package com.pcremote.center.ui.screens

import android.app.Application
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.pcremote.center.PcRemoteApp
import com.pcremote.center.core.Protocol
import com.pcremote.center.core.SavedPc
import com.pcremote.center.core.ServerCandidate
import com.pcremote.center.core.ServerError
import com.pcremote.center.networking.Discovery
import com.pcremote.center.networking.Errors
import com.pcremote.center.networking.PairUri
import com.pcremote.center.networking.PairingClient
import com.pcremote.center.security.Fingerprint
import com.pcremote.center.security.FingerprintProbe
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** State of the pairing dialog. [fingerprint] is null while the server certificate is still being read. */
data class PairDialogState(
    val name: String,
    val hosts: List<String>,
    val port: Int,
    val fingerprint: String? = null,
    /** True when the fingerprint came from a scanned QR code (already trusted by the user's scan). */
    val fromQr: Boolean = false,
    val code: String = "",
    val probing: Boolean = false,
    val busy: Boolean = false,
    val error: String? = null,
    val warning: String? = null,
)

class ConnectViewModel(app: Application) : AndroidViewModel(app) {
    private val container = (app as PcRemoteApp).container

    val savedPcs = container.store.pcs
    val activeId = container.store.activeId

    private val _discovered = MutableStateFlow<List<ServerCandidate>>(emptyList())
    val discovered: StateFlow<List<ServerCandidate>> = _discovered.asStateFlow()

    private val _scanning = MutableStateFlow(false)
    val scanning: StateFlow<Boolean> = _scanning.asStateFlow()

    private val _dialog = MutableStateFlow<PairDialogState?>(null)
    val dialog: StateFlow<PairDialogState?> = _dialog.asStateFlow()

    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error.asStateFlow()

    private val _paired = MutableSharedFlow<Unit>(extraBufferCapacity = 1)
    /** Emits once after a PC was paired and the connection to it was started. */
    val paired: SharedFlow<Unit> = _paired.asSharedFlow()

    private var scanJob: Job? = null
    private var probeJob: Job? = null

    fun clearError() { _error.value = null }

    fun startDiscovery() {
        if (scanJob?.isActive == true) return
        _discovered.value = emptyList()
        _scanning.value = true
        scanJob = viewModelScope.launch {
            try {
                Discovery.scan(getApplication(), 3500).collect { c -> _discovered.update { it + c } }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _error.value = "Network discovery failed: ${Errors.describe(e)}"
            } finally {
                _scanning.value = false
            }
        }
    }

    // ---- manual / discovered servers: read the certificate fingerprint, let the user compare it, then pair ----

    fun beginManual(hostText: String, portText: String) {
        val host = hostText.trim()
        if (host.isEmpty()) { _error.value = "Enter the IP address or host name of the PC."; return }
        val port = if (portText.isBlank()) Protocol.DEFAULT_PORT else portText.trim().toIntOrNull()
        if (port == null || port !in 1..65535) { _error.value = "The port must be a number between 1 and 65535."; return }
        beginProbe(host, host, port, null)
    }

    fun beginCandidate(c: ServerCandidate) = beginProbe(c.name, c.host, c.port, c.advertisedFingerprint)

    private fun beginProbe(name: String, host: String, port: Int, advertised: String?) {
        probeJob?.cancel()
        _dialog.value = PairDialogState(name = name, hosts = listOf(host), port = port, probing = true)
        probeJob = viewModelScope.launch {
            try {
                val fp = withContext(Dispatchers.IO) { FingerprintProbe.probe(host, port) }
                val warning = if (advertised != null && !Fingerprint.matches(advertised, fp)) {
                    "The fingerprint announced on the network differs from the certificate the server presents now. " +
                        "Only continue if the fingerprint below matches the one shown on the PC."
                } else null
                _dialog.update { it?.copy(fingerprint = fp, probing = false, warning = warning) }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _dialog.update { it?.copy(probing = false, error = "Could not read the PC's certificate: ${Errors.describe(e)}") }
            }
        }
    }

    // ---- QR code ----

    fun onQrScanned(text: String?) {
        if (text == null) return
        val uri = PairUri.parse(text)
        if (uri == null) {
            _error.value = "This is not a PC Remote Center pairing QR code (or it is malformed)."
            return
        }
        _dialog.value = PairDialogState(
            name = uri.name ?: uri.hosts.first(), hosts = uri.hosts, port = uri.port,
            fingerprint = uri.fingerprint, fromQr = true, code = uri.code, busy = true,
        )
        pair(uri.code)
    }

    fun setCode(code: String) {
        _dialog.update { it?.copy(code = code.filter { ch -> ch.isDigit() }.take(8), error = null) }
    }

    fun dismissDialog() {
        probeJob?.cancel()
        _dialog.value = null
    }

    fun pair(codeOverride: String? = null) {
        val d = _dialog.value ?: return
        val fp = d.fingerprint ?: return
        val code = codeOverride ?: d.code
        if (code.length != 8) { _dialog.update { it?.copy(error = "Enter the 8-digit code shown on the PC.") }; return }
        _dialog.update { it?.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                val deviceName = deviceName()
                val r = PairingClient.pair(d.hosts, d.port, fp, code, deviceName, Build.MODEL)
                val normalized = Fingerprint.normalize(fp)
                // re-pairing the same PC replaces its entry (same certificate fingerprint)
                val existing = container.store.pcs.value.firstOrNull { Fingerprint.matches(it.fingerprint, normalized) }
                val hosts = (listOf(r.host) + d.hosts).distinct()
                val pc = SavedPc(
                    id = existing?.id ?: container.store.newId(),
                    name = r.computerName,
                    hosts = hosts,
                    port = d.port,
                    fingerprint = normalized,
                    deviceId = r.deviceId,
                    token = r.token,
                    lastHost = r.host,
                    wolMac = existing?.wolMac.orEmpty(),
                    wolBroadcast = existing?.wolBroadcast.orEmpty(),
                    wolUserEdited = existing?.wolUserEdited ?: false,
                    wolHint = existing?.wolHint,
                )
                container.store.upsert(pc, makeActive = true)
                container.connection.connectTo(pc.id)
                _dialog.value = null
                _paired.tryEmit(Unit)
            } catch (e: CancellationException) {
                throw e
            } catch (e: ServerError) {
                _dialog.update { it?.copy(busy = false, error = Errors.describe(e)) }
            } catch (e: Exception) {
                _dialog.update { it?.copy(busy = false, error = Errors.describe(e)) }
            }
        }
    }

    // ---- saved PCs ----

    fun connect(pc: SavedPc) = container.connection.connectTo(pc.id)

    fun forget(pc: SavedPc) {
        container.store.remove(pc.id)
        container.connection.reconnectNow()
    }

    private fun deviceName(): String {
        val maker = Build.MANUFACTURER.orEmpty().replaceFirstChar { it.uppercase() }
        val model = Build.MODEL.orEmpty()
        return if (model.startsWith(maker, ignoreCase = true)) model else "$maker $model".trim()
    }
}
