package com.pcremote.center.core

import android.app.Application
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.ProcessLifecycleOwner
import com.pcremote.center.monitoring.MonitorRepository
import com.pcremote.center.networking.ConnectionManager
import com.pcremote.center.power.PowerController
import com.pcremote.center.security.AppSettings
import com.pcremote.center.security.PcStore
import com.pcremote.center.shortcuts.ShortcutsRepository
import com.pcremote.center.terminal.TerminalRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn

/** Manual dependency container (app scoped singletons). */
class AppContainer(val app: Application) {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    val store = PcStore(app)
    val settings = AppSettings(app)
    val connection = ConnectionManager(store, scope)

    /** A `pcrc://pair?...` link opened from outside the app (phone camera, message, browser); consumed by the connect screen. */
    val pendingPairLink = kotlinx.coroutines.flow.MutableStateFlow<String?>(null)

    /** Id of the PC the app is currently using. */
    val activePcId = combine(store.pcs, store.activeId) { pcs, id -> (pcs.firstOrNull { it.id == id } ?: pcs.firstOrNull())?.id }
        .stateIn(scope, SharingStarted.Eagerly, store.active()?.id)

    val monitor = MonitorRepository(connection, settings, scope, activePcId)
    val terminal = TerminalRepository(connection, scope)
    val power = PowerController(connection, scope)
    val shortcuts = ShortcutsRepository(connection, scope)
    val processes = com.pcremote.center.processes.ProcessRepository(connection)

    init {
        // Connect when the app is visible (ON_START), disconnect politely when it is not (ON_STOP).
        ProcessLifecycleOwner.get().lifecycle.addObserver(LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> connection.onAppStart()
                Lifecycle.Event.ON_STOP -> connection.onAppStop()
                else -> {}
            }
        })
    }
}
