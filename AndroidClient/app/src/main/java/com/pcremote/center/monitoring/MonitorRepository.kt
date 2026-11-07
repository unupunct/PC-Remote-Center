package com.pcremote.center.monitoring

import com.pcremote.center.core.Permissions
import com.pcremote.center.core.obj
import com.pcremote.center.networking.ConnectionManager
import com.pcremote.center.networking.LinkState
import com.pcremote.center.security.AppSettings
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import java.util.concurrent.atomic.AtomicInteger

/**
 * Holds the latest [Snapshot] and the in-memory history. Screens call [acquire]/[release] while they are visible;
 * the repository subscribes (`monitor.subscribe`) only while somebody watches, the connection is authenticated and
 * the device has the `monitoring` permission.
 */
class MonitorRepository(
    private val connection: ConnectionManager,
    settings: AppSettings,
    private val scope: CoroutineScope,
    private val activePcId: StateFlow<String?>,
) {
    private val settingsFlow = settings.values

    private val _snapshot = MutableStateFlow<Snapshot?>(null)
    val snapshot: StateFlow<Snapshot?> = _snapshot.asStateFlow()

    private val _history = MutableStateFlow<List<Sample>>(emptyList())
    val history: StateFlow<List<Sample>> = _history.asStateFlow()

    private val _lastSyncMs = MutableStateFlow<Long?>(null)
    val lastSyncMs: StateFlow<Long?> = _lastSyncMs.asStateFlow()

    private val _selectedDisk = MutableStateFlow<String?>(null)
    val selectedDisk: StateFlow<String?> = _selectedDisk.asStateFlow()

    private val watchers = AtomicInteger(0)
    private val watching = MutableStateFlow(false)

    private data class Want(val session: Int, val connected: Boolean, val watching: Boolean, val permitted: Boolean)

    init {
        // push handler
        scope.launch {
            connection.pushes.filter { it.type == "monitor.snapshot" }.collect { env ->
                val o = env.payload.obj("snapshot") ?: return@collect
                apply(Snapshot.parse(o))
            }
        }
        // PC switch -> drop data of the previous PC
        scope.launch {
            activePcId.collect {
                _snapshot.value = null
                _history.value = emptyList()
                _lastSyncMs.value = null
                _selectedDisk.value = null
            }
        }
        // subscription management
        scope.launch {
            combine(
                connection.sessionId,
                connection.status.map { it.state == LinkState.Connected }.distinctUntilChanged(),
                watching,
                connection.permissions.map { Permissions.MONITORING in it }.distinctUntilChanged(),
            ) { s, c, w, p -> Want(s, c, w, p) }.collectLatest { want ->
                if (want.connected && want.watching && want.permitted) {
                    try {
                        val reply = connection.requestOrReport(
                            "monitor.subscribe",
                            buildJsonObject { put("intervalMs", JsonPrimitive(1500)) },
                        )
                        reply?.obj("snapshot")?.let { apply(Snapshot.parse(it)) }
                        awaitCancellation()
                    } finally {
                        withContext(NonCancellable) {
                            withTimeoutOrNull(2000) {
                                try { connection.request("monitor.unsubscribe") } catch (e: CancellationException) { throw e } catch (_: Exception) {}
                            }
                        }
                    }
                }
            }
        }
    }

    private fun apply(s: Snapshot) {
        val now = System.currentTimeMillis()
        _snapshot.value = s
        _lastSyncMs.value = now
        _history.value = History.append(_history.value, History.sampleOf(s, now), settingsFlow.value.historySeconds)
        // keep a valid drive selection
        val sel = _selectedDisk.value
        if ((sel == null || s.disks.none { it.id == sel }) && s.disks.isNotEmpty()) {
            _selectedDisk.value = (s.disks.firstOrNull { it.id.equals("C:", true) } ?: s.disks.first()).id
        }
    }

    fun selectDisk(id: String) {
        _selectedDisk.value = id
    }

    /** A screen that shows live metrics became visible. */
    fun acquire() {
        if (watchers.incrementAndGet() > 0) watching.value = true
    }

    fun release() {
        if (watchers.decrementAndGet() <= 0) watching.value = false
    }
}
