package com.pcremote.center.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.pcremote.center.PcRemoteApp
import com.pcremote.center.core.Permissions
import com.pcremote.center.core.SavedPc
import com.pcremote.center.networking.Wol
import com.pcremote.center.power.PowerActions
import com.pcremote.center.power.PowerRequestInfo
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Stages of the two-step power confirmation as seen by the user. */
sealed interface PowerDialog {
    /** Restart / shutdown: choose the delay before anything is requested. */
    data class ChooseDelay(val action: String) : PowerDialog
    /** Waiting for `power.request`. */
    data class Requesting(val action: String) : PowerDialog
    /** Token received: explicit Confirm button with a visible expiry countdown. */
    data class Confirm(val info: PowerRequestInfo, val busy: Boolean = false) : PowerDialog
}

/** Shared by the dashboard and the detail screens: connection state, metrics and power actions. */
class DashboardViewModel(app: Application) : AndroidViewModel(app) {
    private val container = (app as PcRemoteApp).container
    private val connection = container.connection
    private val monitor = container.monitor

    val status = connection.status
    val quality = connection.quality
    val rttMs = connection.rttMs
    val permissions = connection.permissions
    val computerName = connection.computerName
    val snapshot = monitor.snapshot
    val history = monitor.history
    val lastSyncMs = monitor.lastSyncMs
    val selectedDisk = monitor.selectedDisk
    val settings = container.settings.values
    val pending = container.power.pending

    /** The active PC (from the store), for the header fallback name and the WoL data. */
    val activePc: StateFlow<SavedPc?> = combine(container.store.pcs, container.store.activeId) { list, id ->
        list.firstOrNull { it.id == id } ?: list.firstOrNull()
    }.stateIn(viewModelScope, SharingStarted.Eagerly, container.store.active())

    private val _powerDialog = MutableStateFlow<PowerDialog?>(null)
    val powerDialog: StateFlow<PowerDialog?> = _powerDialog.asStateFlow()

    fun acquireMonitor() = monitor.acquire()
    fun releaseMonitor() = monitor.release()
    fun selectDisk(id: String) = monitor.selectDisk(id)
    fun reconnect() = connection.reconnectNow()
    fun tell(text: String) = connection.notify(text, isError = true)

    fun hasPower(perms: Set<String>) = Permissions.POWER in perms

    // ---------------------------------------------------------------- power

    fun startPower(action: String) {
        if (action == PowerActions.SLEEP) requestPower(action, null) else _powerDialog.value = PowerDialog.ChooseDelay(action)
    }

    fun requestPower(action: String, delaySeconds: Int?) {
        _powerDialog.value = PowerDialog.Requesting(action)
        viewModelScope.launch {
            val info = container.power.request(action, delaySeconds)
            _powerDialog.value = if (info != null) PowerDialog.Confirm(info) else null
        }
    }

    fun confirmPower() {
        val d = _powerDialog.value as? PowerDialog.Confirm ?: return
        if (d.busy) return
        _powerDialog.value = d.copy(busy = true)
        viewModelScope.launch {
            container.power.confirm(d.info)
            _powerDialog.value = null
        }
    }

    fun dismissPowerDialog() {
        _powerDialog.value = null
    }

    fun cancelPower() {
        viewModelScope.launch { container.power.cancel() }
    }

    fun lockPc() {
        viewModelScope.launch { container.power.lock() }
    }

    // ---------------------------------------------------------------- Wake on LAN

    /**
     * Sends the magic packet from the phone (works while the PC is off) and stores MAC + broadcast address for this PC.
     * Returns an error message or null on success.
     */
    suspend fun sendWol(mac: String, broadcast: String): String? {
        val pc = activePc.value
        if (Wol.parseMac(mac) == null) return "Enter a valid MAC address (for example AA:BB:CC:DD:EE:FF)."
        val bc = broadcast.trim()
        if (bc.isNotEmpty() && !Wol.isIpv4(bc)) return "Enter a valid IPv4 broadcast address (for example 192.168.1.255)."
        return try {
            val n = withContext(Dispatchers.IO) { Wol.send(mac, bc) }
            if (pc != null) {
                container.store.update(pc.id) {
                    it.copy(
                        wolMac = Wol.formatMac(Wol.parseMac(mac)!!),
                        wolBroadcast = bc,
                        wolUserEdited = it.wolUserEdited || Wol.formatMac(Wol.parseMac(mac)!!) != Wol.formatMac(Wol.parseMac(it.wolMac) ?: ByteArray(6)) || bc != it.wolBroadcast,
                    )
                }
            }
            connection.notify("Magic packet sent ($n datagrams to ${bc.ifEmpty { "255.255.255.255" }}). The PC wakes only if Wake-on-LAN is enabled.")
            null
        } catch (e: Exception) {
            "Could not send the packet: ${e.message ?: e.javaClass.simpleName}"
        }
    }
}
