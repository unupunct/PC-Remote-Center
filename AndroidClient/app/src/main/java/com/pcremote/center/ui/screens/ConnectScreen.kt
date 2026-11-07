package com.pcremote.center.ui.screens

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.QrCodeScanner
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.pcremote.center.core.SavedPc
import com.pcremote.center.security.Fingerprint
import com.pcremote.center.ui.components.EmptyState
import com.pcremote.center.ui.components.Panel
import com.pcremote.center.ui.components.Pill
import com.pcremote.center.ui.components.SectionHeader
import com.pcremote.center.ui.theme.Pc

@Composable
fun ConnectScreen(onBack: (() -> Unit)?, onPaired: () -> Unit, vm: ConnectViewModel = viewModel()) {
    val saved by vm.savedPcs.collectAsState()
    val activeId by vm.activeId.collectAsState()
    val discovered by vm.discovered.collectAsState()
    val scanning by vm.scanning.collectAsState()
    val dialog by vm.dialog.collectAsState()
    val error by vm.error.collectAsState()
    val snackbar = remember { SnackbarHostState() }
    var host by rememberSaveable { mutableStateOf("") }
    var port by rememberSaveable { mutableStateOf("47821") }
    var forgetPc by remember { mutableStateOf<SavedPc?>(null) }

    val scanner = rememberLauncherForActivityResult(ScanContract()) { result -> vm.onQrScanned(result.contents) }

    LaunchedEffect(Unit) { vm.startDiscovery() }
    LaunchedEffect(Unit) { vm.paired.collect { onPaired() } }
    LaunchedEffect(error) {
        error?.let { snackbar.showSnackbar(it); vm.clearError() }
    }

    Box(Modifier.fillMaxSize()) {
        LazyColumn(
            Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding(),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (onBack != null) {
                        IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") }
                    }
                    Column(Modifier.weight(1f)) {
                        Text("Connect a PC", style = MaterialTheme.typography.headlineSmall)
                        Text(
                            "Pair this phone with the PC Remote Center server running on your Windows PC.",
                            style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary,
                        )
                    }
                }
            }

            if (saved.isNotEmpty()) {
                item { SectionHeader("Your PCs") }
                items(saved, key = { it.id }) { pc ->
                    Panel {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Outlined.Computer, null, tint = Pc.Cyan, modifier = Modifier.size(28.dp))
                            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                                Text(pc.name, style = MaterialTheme.typography.titleMedium)
                                Text(
                                    "${pc.orderedHosts().first()}:${pc.port}",
                                    style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary,
                                )
                            }
                            if (pc.id == activeId) Pill("Active", Pc.Green)
                            Spacer(Modifier.size(6.dp))
                            TextButton(onClick = { vm.connect(pc); onPaired() }) { Text("Open") }
                            IconButton(onClick = { forgetPc = pc }) { Icon(Icons.Filled.Delete, "Forget ${pc.name}", tint = Pc.TextDim) }
                        }
                    }
                }
            }

            item { SectionHeader("Pair with QR code") }
            item {
                Panel {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(
                            "On the PC open PC Remote Center and choose Pair a phone. Scan the QR code it shows - " +
                                "it contains the address, the one-time code and the certificate fingerprint.",
                            style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary,
                        )
                        Button(
                            onClick = {
                                scanner.launch(
                                    ScanOptions()
                                        .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                                        .setPrompt("Scan the pairing QR code shown on the PC")
                                        .setBeepEnabled(false)
                                        .setOrientationLocked(false)
                                )
                            },
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Icon(Icons.Outlined.QrCodeScanner, null)
                            Spacer(Modifier.size(8.dp))
                            Text("Scan QR code")
                        }
                    }
                }
            }

            item {
                SectionHeader("PCs on this network") {
                    if (scanning) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    else IconButton(onClick = vm::startDiscovery) { Icon(Icons.Outlined.Refresh, "Scan again", tint = Pc.Cyan) }
                }
            }
            if (discovered.isEmpty()) {
                item {
                    Text(
                        if (scanning) "Searching the local network..." else "No PC answered. Make sure the server is running and both devices share the same Wi-Fi, or enter the address below.",
                        style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary,
                    )
                }
            }
            items(discovered, key = { "${it.host}:${it.port}" }) { c ->
                Panel(onClick = { vm.beginCandidate(c) }) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Outlined.Wifi, null, tint = Pc.Purple, modifier = Modifier.size(26.dp))
                        Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                            Text(c.name, style = MaterialTheme.typography.titleMedium)
                            Text("${c.host}:${c.port}", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                        }
                        val known = saved.any { s -> c.advertisedFingerprint != null && Fingerprint.matches(s.fingerprint, c.advertisedFingerprint) }
                        Pill(if (known) "Paired" else "Online", if (known) Pc.Green else Pc.Cyan)
                    }
                }
            }

            item { SectionHeader("Add manually") }
            item {
                Panel {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedTextField(
                            value = host, onValueChange = { host = it }, singleLine = true,
                            label = { Text("IP address or host name") },
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = port, onValueChange = { port = it.filter(Char::isDigit).take(5) }, singleLine = true,
                            label = { Text("Port") },
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedButton(onClick = { vm.beginManual(host, port) }, modifier = Modifier.fillMaxWidth()) {
                            Text("Continue")
                        }
                    }
                }
            }
            if (saved.isEmpty() && discovered.isEmpty() && !scanning) {
                item { Spacer(Modifier.height(4.dp)) }
            }
            item { Spacer(Modifier.height(24.dp)) }
        }
        SnackbarHost(snackbar, Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(12.dp))
    }

    dialog?.let { d -> PairDialog(d, vm) }

    forgetPc?.let { pc ->
        AlertDialog(
            onDismissRequest = { forgetPc = null },
            title = { Text("Forget ${pc.name}?") },
            text = { Text("The saved device token and pinned certificate are deleted from this phone. You can pair again later with a new code. The phone also stays listed on the PC until the owner removes it there.") },
            confirmButton = { TextButton(onClick = { vm.forget(pc); forgetPc = null }) { Text("Forget", color = Pc.Red) } },
            dismissButton = { TextButton(onClick = { forgetPc = null }) { Text("Cancel") } },
        )
    }
}

/** Shows the server's certificate fingerprint for comparison and collects the 8-digit pairing code. */
@Composable
private fun PairDialog(d: PairDialogState, vm: ConnectViewModel) {
    AlertDialog(
        onDismissRequest = { if (!d.busy) vm.dismissDialog() },
        title = { Text("Pair with ${d.name}") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("${d.hosts.joinToString(", ")}:${d.port}", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                when {
                    d.probing -> Row(verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.size(10.dp))
                        Text("Reading the PC's certificate...")
                    }
                    d.fingerprint != null -> {
                        Text(
                            if (d.fromQr) "Certificate fingerprint (from the QR code, it will be pinned):"
                            else "Certificate fingerprint of the server. Compare it with the one shown on the PC - only continue if it is identical:",
                            style = MaterialTheme.typography.bodySmall,
                        )
                        Panel(contentPadding = androidx.compose.foundation.layout.PaddingValues(10.dp), corner = 12.dp) {
                            Text(
                                formatFingerprint(d.fingerprint),
                                fontFamily = FontFamily.Monospace,
                                style = MaterialTheme.typography.bodySmall,
                                color = Pc.Cyan,
                            )
                        }
                    }
                }
                if (d.warning != null) Text(d.warning, color = Pc.Amber, style = MaterialTheme.typography.bodySmall)
                if (d.fingerprint != null && !d.fromQr) {
                    OutlinedTextField(
                        value = d.code, onValueChange = vm::setCode, singleLine = true, enabled = !d.busy,
                        label = { Text("8-digit pairing code") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
                if (d.busy) Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.size(10.dp))
                    Text("Pairing...")
                }
                if (d.error != null) Text(d.error, color = Pc.Red, style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            if (!d.fromQr || d.error != null) {
                TextButton(onClick = { vm.pair() }, enabled = d.fingerprint != null && !d.busy && d.code.length == 8) {
                    Text("Pair")
                }
            }
        },
        dismissButton = { TextButton(onClick = vm::dismissDialog, enabled = !d.busy) { Text("Cancel") } },
    )
}

/** "AA:BB:..." with 8 bytes per line. */
internal fun formatFingerprint(fp: String): String =
    Fingerprint.pretty(fp).split(':').chunked(8).joinToString("\n") { it.joinToString(":") }
