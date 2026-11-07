@file:OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)

package com.pcremote.center.ui.screens

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewmodel.compose.viewModel
import com.pcremote.center.BuildConfig
import com.pcremote.center.PcRemoteApp
import com.pcremote.center.core.Permissions
import com.pcremote.center.core.Protocol
import com.pcremote.center.core.SavedPc
import com.pcremote.center.networking.LinkState
import com.pcremote.center.security.Fingerprint
import com.pcremote.center.ui.components.Panel
import com.pcremote.center.ui.components.Pill
import com.pcremote.center.ui.components.SectionHeader
import com.pcremote.center.ui.theme.Pc

class SettingsViewModel(app: Application) : AndroidViewModel(app) {
    private val container = (app as PcRemoteApp).container
    val pcs = container.store.pcs
    val activeId = container.store.activeId
    val values = container.settings.values
    val status = container.connection.status
    val permissions = container.connection.permissions
    val computerName = container.connection.computerName

    fun connect(pc: SavedPc) = container.connection.connectTo(pc.id)
    fun forget(pc: SavedPc) {
        container.store.remove(pc.id)
        container.connection.reconnectNow()
    }
    fun setThresholds(amber: Int, red: Int) = container.settings.setThresholds(amber, red)
    fun setHistory(seconds: Int) = container.settings.setHistorySeconds(seconds)
}

@Composable
fun SettingsScreen(onAddPc: () -> Unit, vm: SettingsViewModel = viewModel()) {
    val pcs by vm.pcs.collectAsState()
    val activeId by vm.activeId.collectAsState()
    val values by vm.values.collectAsState()
    val status by vm.status.collectAsState()
    val perms by vm.permissions.collectAsState()
    val name by vm.computerName.collectAsState()
    var forget by remember { mutableStateOf<SavedPc?>(null) }

    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item { Text("Settings", style = MaterialTheme.typography.headlineSmall) }

        item { SectionHeader("Saved PCs") }
        if (pcs.isEmpty()) item { Text("No PC paired yet.", color = Pc.TextSecondary) }
        items(pcs, key = { it.id }) { pc ->
            val isActive = pc.id == activeId || (activeId == null && pc == pcs.first())
            Panel {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(pc.name, style = MaterialTheme.typography.titleMedium)
                            Text("${pc.orderedHosts().joinToString(", ")}:${pc.port}", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                        }
                        if (isActive) Pill(
                            when (status.state) {
                                LinkState.Connected -> "Connected"
                                LinkState.Connecting -> "Connecting"
                                LinkState.AuthFailed -> "Auth failed"
                                LinkState.Disconnected -> "Offline"
                            },
                            when (status.state) { LinkState.Connected -> Pc.Green; LinkState.Connecting -> Pc.Amber; else -> Pc.Red },
                        )
                    }
                    Text(
                        "Pinned certificate: " + Fingerprint.pretty(pc.fingerprint).take(23) + "...",
                        style = MaterialTheme.typography.labelSmall, color = Pc.TextDim,
                    )
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        if (!isActive || status.state != LinkState.Connected) Button(onClick = { vm.connect(pc) }) { Text(if (isActive) "Reconnect" else "Connect") }
                        OutlinedButton(onClick = onAddPc) { Text("Re-pair") }
                        TextButton(onClick = { forget = pc }) { Text("Forget", color = Pc.Red) }
                    }
                }
            }
        }
        item { OutlinedButton(onClick = onAddPc, modifier = Modifier.fillMaxWidth()) { Text("Add PC") } }

        item { SectionHeader("This device's permissions") }
        item {
            Panel {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (status.state != LinkState.Connected) {
                        Text("Connect to a PC to see what it allows this device to do.", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                    } else {
                        Text("On ${name ?: "the PC"}:", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                        for (p in Permissions.ALL) {
                            val has = p in perms
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Pill(if (has) "granted" else "denied", if (has) Pc.Green else Pc.TextDim, Modifier.padding(end = 10.dp))
                                Column {
                                    Text(p, style = MaterialTheme.typography.titleSmall)
                                    Text(Permissions.describe(p), style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                                }
                            }
                        }
                    }
                }
            }
        }

        item { SectionHeader("Warning thresholds") }
        item {
            var amber by remember(values.amber) { mutableStateOf(values.amber.toFloat()) }
            var red by remember(values.red) { mutableStateOf(values.red.toFloat()) }
            Panel {
                Column {
                    Text("Amber from ${amber.toInt()} %", style = MaterialTheme.typography.titleSmall, color = Pc.Amber)
                    Slider(
                        value = amber, onValueChange = { amber = it.coerceAtMost(red - 1f) },
                        onValueChangeFinished = { vm.setThresholds(amber.toInt(), red.toInt()) },
                        valueRange = 40f..98f,
                        colors = SliderDefaults.colors(thumbColor = Pc.Amber, activeTrackColor = Pc.Amber),
                    )
                    Text("Red from ${red.toInt()} %", style = MaterialTheme.typography.titleSmall, color = Pc.Red)
                    Slider(
                        value = red, onValueChange = { red = it.coerceAtLeast(amber + 1f) },
                        onValueChangeFinished = { vm.setThresholds(amber.toInt(), red.toInt()) },
                        valueRange = 41f..100f,
                        colors = SliderDefaults.colors(thumbColor = Pc.Red, activeTrackColor = Pc.Red),
                    )
                    Text("Gauges turn amber / red when CPU, GPU, RAM or disk usage reaches these values.", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                }
            }
        }

        item { SectionHeader("History length") }
        item {
            Panel {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        for (sec in listOf(60, 180, 300, 600, 900)) {
                            FilterChip(selected = values.historySeconds == sec, onClick = { vm.setHistory(sec) }, label = { Text("${sec / 60} min") })
                        }
                    }
                    Text("How much metric history the charts keep in memory (cleared when the app closes).", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                }
            }
        }

        item { SectionHeader("About") }
        item {
            Panel {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("PC Remote Center", style = MaterialTheme.typography.titleMedium)
                    Text("Version ${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE}) - protocol ${Protocol.VERSION}", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                    Text(
                        "Connections use TLS with the certificate fingerprint pinned at pairing. Device tokens are stored encrypted with an Android Keystore key.",
                        style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary,
                    )
                }
            }
        }
    }

    forget?.let { pc ->
        AlertDialog(
            onDismissRequest = { forget = null },
            title = { Text("Forget ${pc.name}?") },
            text = { Text("The device token and pinned certificate are deleted from this phone. Pair again to reconnect.") },
            confirmButton = { TextButton(onClick = { vm.forget(pc); forget = null }) { Text("Forget", color = Pc.Red) } },
            dismissButton = { TextButton(onClick = { forget = null }) { Text("Cancel") } },
        )
    }
}
