package com.pcremote.center.power

import com.pcremote.center.core.Permissions
import com.pcremote.center.core.TimeUtil
import com.pcremote.center.core.bool
import com.pcremote.center.core.int
import com.pcremote.center.core.str
import com.pcremote.center.networking.ConnectionManager
import com.pcremote.center.networking.LinkState
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

/** Result of `power.request`: nothing happens on the PC until the token is confirmed. */
data class PowerRequestInfo(
    val token: String,
    val action: String,
    val delaySeconds: Int,
    /** Phone wall-clock time at which the confirmation token expires. */
    val expiresAtMs: Long,
)

/** A scheduled restart/shutdown. [atMs] is expressed on the phone clock. */
data class PendingPower(val action: String, val atMs: Long)

object PowerActions {
    const val RESTART = "restart"
    const val SHUTDOWN = "shutdown"
    const val SLEEP = "sleep"
}

class PowerController(
    private val connection: ConnectionManager,
    scope: CoroutineScope,
) {
    private val _pending = MutableStateFlow<PendingPower?>(null)
    val pending: StateFlow<PendingPower?> = _pending.asStateFlow()

    init {
        // restore a scheduled action after (re)connecting
        scope.launch {
            connection.sessionId.collect { sid ->
                if (sid == 0) return@collect
                if (connection.status.value.state != LinkState.Connected && sid == 0) return@collect
                refreshStatus()
            }
        }
    }

    suspend fun refreshStatus() {
        if (!connection.hasPermission(Permissions.MONITORING)) return
        try {
            val p = connection.request("power.status")
            if (p.bool("scheduled") == true) {
                val action = p.str("action") ?: "shutdown"
                val at = TimeUtil.parseIsoMillis(p.str("atUtc"))?.minus(connection.clockSkewMs)
                _pending.value = PendingPower(action, at ?: (System.currentTimeMillis() + 30_000))
            } else {
                _pending.value = null
            }
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
        }
    }

    /** Step 1: ask the server for a confirmation token. Returns null (after showing the error) on failure. */
    suspend fun request(action: String, delaySeconds: Int?): PowerRequestInfo? {
        val p = connection.requestOrReport("power.request", buildJsonObject {
            put("action", JsonPrimitive(action))
            if (delaySeconds != null) put("delaySeconds", JsonPrimitive(delaySeconds))
        }) ?: return null
        val token = p.str("confirmationToken") ?: run {
            connection.notify("The PC did not return a confirmation token.", isError = true)
            return null
        }
        val expires = (p.int("expiresInSeconds") ?: 30).coerceAtLeast(5)
        return PowerRequestInfo(
            token = token,
            action = p.str("action") ?: action,
            delaySeconds = p.int("delaySeconds") ?: delaySeconds ?: 30,
            expiresAtMs = System.currentTimeMillis() + expires * 1000L,
        )
    }

    /** Step 2: confirm. Returns true when accepted. */
    suspend fun confirm(info: PowerRequestInfo): Boolean {
        val p = connection.requestOrReport("power.confirm", buildJsonObject { put("token", JsonPrimitive(info.token)) })
            ?: return false
        if (p.bool("sleeping") == true) {
            connection.notify("The PC is going to sleep.")
            return true
        }
        if (p.bool("scheduled") == true) {
            val delay = p.int("delaySeconds") ?: info.delaySeconds
            val at = TimeUtil.parseIsoMillis(p.str("atUtc"))?.minus(connection.clockSkewMs)
                ?: (System.currentTimeMillis() + delay * 1000L)
            _pending.value = PendingPower(p.str("action") ?: info.action, at)
            return true
        }
        connection.notify("The PC accepted the request.")
        return true
    }

    /** Cancels a scheduled restart/shutdown and shows the server's message. */
    suspend fun cancel() {
        val p = connection.requestOrReport("power.cancel") ?: return
        _pending.value = null
        val msg = p.str("message")
        val cancelled = p.bool("cancelled")
        connection.notify(msg ?: if (cancelled == false) "Nothing was scheduled." else "Action cancelled.", isError = cancelled == false)
    }

    suspend fun lock() {
        val p = connection.requestOrReport("power.lock") ?: return
        if (p.bool("locked") == true) connection.notify("PC locked.") else connection.notify("The PC did not confirm the lock.", isError = true)
    }

    fun clearPending() {
        _pending.value = null
    }
}
