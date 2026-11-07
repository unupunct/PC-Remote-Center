package com.pcremote.center.networking

import com.pcremote.center.core.Envelope
import com.pcremote.center.core.NotConnectedException
import com.pcremote.center.core.Permissions
import com.pcremote.center.core.Protocol
import com.pcremote.center.core.RequestTimeoutException
import com.pcremote.center.core.RequestTracker
import com.pcremote.center.core.SavedPc
import com.pcremote.center.core.ServerError
import com.pcremote.center.core.TimeUtil
import com.pcremote.center.core.UserMessage
import com.pcremote.center.core.payloadOrThrow
import com.pcremote.center.core.str
import com.pcremote.center.core.strList
import com.pcremote.center.security.PcStore
import com.pcremote.center.security.PinnedClients
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

enum class LinkState { Disconnected, Connecting, Connected, AuthFailed }

enum class Quality {
    Unknown, Good, Fair, Poor;

    companion object {
        fun forRtt(ms: Long): Quality = when {
            ms < 150 -> Good
            ms < 400 -> Fair
            else -> Poor
        }
    }
}

data class LinkStatus(
    val state: LinkState = LinkState.Disconnected,
    val detail: String? = null,
    /** AuthFailed because the PC owner revoked this device (or the token is no longer valid). */
    val revoked: Boolean = false,
    /** Wall clock time of the next automatic retry while Disconnected. */
    val retryAtMs: Long? = null,
    val host: String? = null,
)

/**
 * App-scoped connection to the active PC. Owns the WebSocket, authentication (auth.login), request/response
 * correlation, heartbeat, reconnect with exponential backoff and the permission set. Everything observable is a
 * StateFlow / SharedFlow.
 */
class ConnectionManager(
    private val store: PcStore,
    private val scope: CoroutineScope,
) {
    private val _status = MutableStateFlow(LinkStatus())
    val status: StateFlow<LinkStatus> = _status.asStateFlow()

    private val _quality = MutableStateFlow(Quality.Unknown)
    val quality: StateFlow<Quality> = _quality.asStateFlow()

    private val _rttMs = MutableStateFlow<Long?>(null)
    val rttMs: StateFlow<Long?> = _rttMs.asStateFlow()

    private val _permissions = MutableStateFlow<Set<String>>(emptySet())
    val permissions: StateFlow<Set<String>> = _permissions.asStateFlow()

    private val _computerName = MutableStateFlow<String?>(null)
    val computerName: StateFlow<String?> = _computerName.asStateFlow()

    /** Incremented after every successful authentication; screens use it to re-open subscriptions. */
    private val _sessionId = MutableStateFlow(0)
    val sessionId: StateFlow<Int> = _sessionId.asStateFlow()

    private val _pushes = MutableSharedFlow<Envelope>(extraBufferCapacity = 1024)
    /** Server pushes that are not handled here (`monitor.snapshot`, `terminal.output`). */
    val pushes: SharedFlow<Envelope> = _pushes.asSharedFlow()

    private val _messages = MutableSharedFlow<UserMessage>(extraBufferCapacity = 32)
    val messages: SharedFlow<UserMessage> = _messages.asSharedFlow()

    /** Server clock minus phone clock (ms), from the last ping. Used to convert `atUtc` values. */
    @Volatile var clockSkewMs: Long = 0
        private set

    private class SessionCtx {
        val tracker = RequestTracker()
        @Volatile var expired = false
    }

    private class Live(val socket: PcSocket, val ctx: SessionCtx)

    private sealed interface Outcome {
        data class Rejected(val message: String, val revoked: Boolean) : Outcome
        data class Ended(
            val wasConnected: Boolean,
            val reason: String?,
            val immediate: Boolean = false,
            val minDelayMs: Long = 0,
        ) : Outcome
    }

    @Volatile private var live: Live? = null
    @Volatile private var foreground = false
    private var loop: Job? = null
    private val lock = Any()

    // ---------------------------------------------------------------- lifecycle

    /** App moved to the foreground (process lifecycle ON_START). */
    fun onAppStart() {
        foreground = true
        startLoop()
    }

    /** App moved to the background: disconnect politely and stop retrying. */
    fun onAppStop() {
        foreground = false
        synchronized(lock) {
            loop?.cancel()
            loop = null
        }
        live?.socket?.close(1000, "app stopped")
        live = null
        resetSession()
        _status.value = LinkStatus(LinkState.Disconnected, "Paused while the app is in the background")
    }

    /** Switches to another saved PC and connects to it. */
    fun connectTo(id: String) {
        store.setActive(id)
        startLoop()
    }

    /** Restarts the connection loop (retry now, or re-evaluate after a PC was added/removed). */
    fun reconnectNow() = startLoop()

    private fun startLoop() {
        synchronized(lock) {
            loop?.cancel()
            loop = null
            live?.socket?.abort()
            live = null
            resetSession()
            val pc = store.active()
            if (pc == null) {
                _status.value = LinkStatus(LinkState.Disconnected, "No PC selected")
                return
            }
            if (!foreground) return
            _status.value = LinkStatus(LinkState.Connecting)
            loop = scope.launch { runLoop(pc.id) }
        }
    }

    private fun resetSession() {
        _permissions.value = emptySet()
        _computerName.value = null
        _quality.value = Quality.Unknown
        _rttMs.value = null
    }

    // ---------------------------------------------------------------- connection loop

    private suspend fun runLoop(pcId: String) {
        var attempt = 0
        var everLoggedIn = false
        while (currentCoroutineContext().isActive) {
            val pc = store.get(pcId)
            if (pc == null) {
                _status.value = LinkStatus(LinkState.Disconnected, "PC was removed")
                return
            }
            _status.value = LinkStatus(
                LinkState.Connecting,
                detail = if (attempt > 0) "Reconnecting (attempt ${attempt + 1})" else null,
            )
            val outcome = try {
                connectOnce(pc, everLoggedIn)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                Outcome.Ended(false, Errors.describe(e))
            }
            resetSession()
            when (outcome) {
                is Outcome.Rejected -> {
                    _status.value = LinkStatus(LinkState.AuthFailed, outcome.message, outcome.revoked)
                    _messages.tryEmit(UserMessage(outcome.message))
                    return // never retry on auth failure
                }
                is Outcome.Ended -> {
                    if (outcome.wasConnected) {
                        everLoggedIn = true
                        attempt = 0
                    }
                    val wait = if (outcome.immediate) 300L else maxOf(backoffMs(attempt++), outcome.minDelayMs)
                    _status.value = LinkStatus(
                        LinkState.Disconnected,
                        outcome.reason ?: "Disconnected",
                        retryAtMs = System.currentTimeMillis() + wait,
                    )
                    delay(wait)
                }
            }
        }
    }

    private suspend fun connectOnce(pc: SavedPc, everLoggedIn: Boolean): Outcome {
        val client = PinnedClients.create(pc.fingerprint)
        val ctx = SessionCtx()
        var socket: PcSocket? = null
        var host: String? = null
        var mismatch = false
        var lastErr: String? = null

        for (h in pc.orderedHosts()) {
            val s = PcSocket(client) { text -> onText(ctx, text) }
            try {
                s.open(h, pc.port, 8000)
                socket = s
                host = h
                break
            } catch (e: CancellationException) {
                s.abort()
                if (e is TimeoutCancellationException) { lastErr = "Connection timed out"; continue }
                throw e
            } catch (e: Exception) {
                s.abort()
                if (Errors.isFingerprintMismatch(e)) mismatch = true
                lastErr = Errors.describe(e)
            }
        }
        if (socket == null || host == null) {
            return if (mismatch) Outcome.Rejected(lastErr ?: "Certificate mismatch", revoked = false)
            else Outcome.Ended(false, lastErr ?: "Could not connect")
        }

        val me = Live(socket, ctx)
        live = me
        var pingJob: Job? = null
        try {
            val login = try {
                rawRequest(
                    me, "auth.login",
                    buildJsonObject {
                        put("deviceId", JsonPrimitive(pc.deviceId))
                        put("token", JsonPrimitive(pc.token))
                    },
                    10_000,
                )
            } catch (e: ServerError) {
                return when (e.code) {
                    "auth_failed" -> Outcome.Rejected(
                        if (everLoggedIn) "This device was revoked or its token is no longer valid on the PC. Re-pair to continue."
                        else "The PC rejected this device. Re-pair it with a new code.",
                        revoked = everLoggedIn,
                    )
                    "rate_limited" -> Outcome.Ended(false, Errors.describe(e), minDelayMs = 60_000)
                    else -> Outcome.Ended(false, Errors.describe(e))
                }
            }

            _permissions.value = login.strList("permissions").toSet()
            val name = login.str("computerName")
            _computerName.value = name ?: pc.name
            store.update(pc.id) { it.copy(lastHost = host, name = name?.takeIf { n -> n.isNotBlank() } ?: it.name) }
            _sessionId.value = _sessionId.value + 1
            _status.value = LinkStatus(LinkState.Connected, host = host)
            pingJob = scope.launch { pingLoop(me) }
            scope.launch { refreshServerInfo(pc.id, me) }

            val info = socket.closed.await()
            val reason = when {
                ctx.expired -> "Session expired - signing in again"
                info.error != null -> Errors.describe(info.error)
                info.reason.isNotBlank() -> "Connection closed by the PC: ${info.reason}"
                else -> "Connection closed by the PC"
            }
            return Outcome.Ended(true, reason, immediate = ctx.expired)
        } finally {
            pingJob?.cancel()
            ctx.tracker.failAll(NotConnectedException("Connection closed"))
            if (live === me) live = null
            socket.close(1000, "bye")
        }
    }

    private fun backoffMs(attempt: Int): Long {
        val base = 1000L shl attempt.coerceIn(0, 5) // 1,2,4,8,16,32 s
        return minOf(base, 30_000L) + (0..400).random()
    }

    private fun onText(ctx: SessionCtx, text: String) {
        val env = Protocol.parse(text) ?: return
        if (ctx.tracker.handle(env)) return
        when (env.type) {
            "auth.permissions" -> _permissions.value = env.payload.strList("permissions").toSet()
            "session.expired" -> {
                ctx.expired = true
                _messages.tryEmit(UserMessage("Session expired - signing in again", isError = false))
            }
            "response", "error" -> { /* reply to a request that already timed out */ }
            else -> _pushes.tryEmit(env)
        }
    }

    // ---------------------------------------------------------------- requests

    private suspend fun rawRequest(l: Live, type: String, payload: JsonObject, timeoutMs: Long): JsonObject {
        val id = l.ctx.tracker.nextId()
        val text = Protocol.build(type, id, payload)
        if (text.length > Protocol.MAX_MESSAGE_BYTES / 2) throw ServerError("too_large", "The message is too large to send.")
        val reply = l.ctx.tracker.register(id)
        try {
            if (!l.socket.send(text)) throw NotConnectedException()
            val env = try {
                withTimeout(timeoutMs) { reply.await() }
            } catch (_: TimeoutCancellationException) {
                throw RequestTimeoutException(type)
            }
            return env.payloadOrThrow()
        } finally {
            l.ctx.tracker.cancel(id)
        }
    }

    /** Sends a request on the live connection. Throws [ServerError], [NotConnectedException] or [RequestTimeoutException]. */
    suspend fun request(type: String, payload: JsonObject = JsonObject(emptyMap()), timeoutMs: Long = 15_000): JsonObject {
        val l = live ?: throw NotConnectedException()
        return rawRequest(l, type, payload, timeoutMs)
    }

    /** Like [request] but shows failures to the user (snackbar) and returns null instead of throwing. */
    suspend fun requestOrReport(type: String, payload: JsonObject = JsonObject(emptyMap()), timeoutMs: Long = 15_000): JsonObject? =
        try {
            request(type, payload, timeoutMs)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            report(e)
            null
        }

    fun report(e: Throwable) {
        _messages.tryEmit(UserMessage(Errors.describe(e)))
    }

    fun notify(text: String, isError: Boolean = false) {
        _messages.tryEmit(UserMessage(text, isError))
    }

    fun hasPermission(p: String): Boolean = p in _permissions.value

    // ---------------------------------------------------------------- heartbeat

    private suspend fun pingLoop(l: Live) {
        var fails = 0
        while (currentCoroutineContext().isActive) {
            val wall0 = System.currentTimeMillis()
            val t0 = System.nanoTime()
            try {
                val p = rawRequest(l, "ping", JsonObject(emptyMap()), 8_000)
                val rtt = (System.nanoTime() - t0) / 1_000_000
                fails = 0
                _rttMs.value = rtt
                _quality.value = Quality.forRtt(rtt)
                TimeUtil.parseIsoMillis(p.str("serverTimeUtc"))?.let { clockSkewMs = it - (wall0 + rtt / 2) }
            } catch (e: CancellationException) {
                throw e
            } catch (e: ServerError) {
                // the server answered, so the link is alive
            } catch (_: Exception) {
                fails++
                _quality.value = Quality.Poor
                _rttMs.value = null
                if (fails >= 2) {
                    l.socket.abort() // triggers the reconnect logic
                    return
                }
            }
            delay(10_000)
        }
    }

    // ---------------------------------------------------------------- Wake-on-LAN data

    /** Reads adapters from `server.info` / `wol.info` and stores MAC + broadcast for this PC (unless edited by the user). */
    private suspend fun refreshServerInfo(pcId: String, l: Live) {
        try {
            val info = rawRequest(l, "server.info", JsonObject(emptyMap()), 10_000)
            var adapter = WolAdapter.pick(WolAdapter.parseList(info, "macAddresses"))
            var hint: String? = null
            if (Permissions.MONITORING in _permissions.value) {
                try {
                    val wol = rawRequest(l, "wol.info", JsonObject(emptyMap()), 10_000)
                    val list = WolAdapter.parseList(wol, "adapters")
                    WolAdapter.pick(list)?.let { adapter = it }
                    hint = WolAdapter.hintFor(adapter, wol.str("note"))
                } catch (e: CancellationException) {
                    throw e
                } catch (_: Exception) {
                }
            }
            val a = adapter
            store.update(pcId) { pc ->
                var next = pc
                if (!pc.wolUserEdited && a != null) {
                    next = next.copy(wolMac = a.mac, wolBroadcast = a.broadcast ?: pc.wolBroadcast)
                }
                if (hint != null) next = next.copy(wolHint = hint)
                next
            }
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // optional data - the WoL dialog still works with manual input
        }
    }
}
