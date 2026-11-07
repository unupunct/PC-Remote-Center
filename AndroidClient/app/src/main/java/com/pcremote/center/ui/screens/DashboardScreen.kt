@file:OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)

package com.pcremote.center.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.RestartAlt
import androidx.compose.material.icons.filled.Bolt
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.pcremote.center.core.Format
import com.pcremote.center.core.Level
import com.pcremote.center.core.Permissions
import com.pcremote.center.core.Thresholds
import com.pcremote.center.monitoring.DiskInfo
import com.pcremote.center.networking.LinkState
import com.pcremote.center.power.PowerActions
import com.pcremote.center.ui.components.CircularGauge
import com.pcremote.center.ui.components.ConnectionHeader
import com.pcremote.center.ui.components.Panel
import com.pcremote.center.ui.components.SectionHeader
import com.pcremote.center.ui.components.rememberNowMs
import com.pcremote.center.ui.theme.Pc

@Composable
fun DashboardScreen(
    onOpenDetail: (String) -> Unit,
    onOpenSettings: () -> Unit,
    onAddPc: () -> Unit,
    vm: DashboardViewModel = viewModel(),
) {
    val status by vm.status.collectAsState()
    val quality by vm.quality.collectAsState()
    val rtt by vm.rttMs.collectAsState()
    val perms by vm.permissions.collectAsState()
    val name by vm.computerName.collectAsState()
    val pc by vm.activePc.collectAsState()
    val snap by vm.snapshot.collectAsState()
    val lastSync by vm.lastSyncMs.collectAsState()
    val selectedDisk by vm.selectedDisk.collectAsState()
    val settings by vm.settings.collectAsState()
    val pending by vm.pending.collectAsState()
    var showWol by remember { mutableStateOf(false) }

    DisposableEffect(Unit) {
        vm.acquireMonitor()
        onDispose { vm.releaseMonitor() }
    }

    val connected = status.state == LinkState.Connected
    val canMonitor = Permissions.MONITORING in perms
    val canPower = Permissions.POWER in perms && connected
    val stale = !connected
    val dim = if (stale) 0.55f else 1f

    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 12.dp, bottom = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            ConnectionHeader(
                title = name ?: pc?.name ?: "PC Remote Center",
                status = status, quality = quality, rttMs = rtt, lastSyncMs = lastSync,
                onSettings = onOpenSettings,
            )
        }

        // ---- connection problems ----
        if (status.state == LinkState.AuthFailed) {
            item {
                Banner(
                    title = if (status.revoked) "This device was revoked" else "Authentication failed",
                    text = status.detail ?: "The PC rejected this device.",
                    color = Pc.Red,
                    actions = {
                        Button(onClick = onAddPc) { Text("Re-pair") }
                        OutlinedButton(onClick = vm::reconnect) { Text("Retry") }
                    },
                )
            }
        } else if (status.state == LinkState.Disconnected && pc != null) {
            item {
                val now by rememberNowMs(1000)
                val retry = status.retryAtMs?.let { ((it - now) / 1000).coerceAtLeast(0) }
                Banner(
                    title = "Not connected",
                    text = (status.detail ?: "Disconnected") + (if (retry != null) " - retrying in ${retry}s" else ""),
                    color = Pc.Amber,
                    actions = { OutlinedButton(onClick = vm::reconnect) { Text("Retry now") } },
                )
            }
        } else if (status.state == LinkState.Connecting) {
            item { Banner("Connecting", status.detail ?: "Contacting ${pc?.name ?: "the PC"}...", Pc.Cyan, null) }
        } else if (pc == null) {
            item {
                Banner("No PC paired", "Pair a PC to see its metrics and control it.", Pc.Cyan) {
                    Button(onClick = onAddPc) { Text("Add PC") }
                }
            }
        }

        if (connected && !canMonitor) {
            item {
                Banner(
                    "Monitoring is not permitted",
                    "This device has no \"monitoring\" permission, so live metrics are unavailable (n/a). Ask the PC owner to grant it.",
                    Pc.Amber, null,
                )
            }
        }

        // ---- scheduled power action ----
        pending?.let { p ->
            item { PendingBanner(p.action, p.atMs, canPower, vm::cancelPower) }
        }

        // ---- metric cards ----
        item {
            val cpu = snap?.cpu
            val gpu = snap?.gpus?.firstOrNull()
            Column(Modifier.alpha(dim), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Row(Modifier.height(IntrinsicSize.Max), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    MetricCard(
                        "CPU", cpu?.usage, settings.amber, settings.red,
                        listOf(
                            cpu?.model ?: "Model n/a",
                            "Temp ${Format.temperature(cpu?.temperatureC)}",
                        ),
                        Modifier.weight(1f).fillMaxHeight(),
                    ) { onOpenDetail("cpu") }
                    MetricCard(
                        "GPU", gpu?.usage, settings.amber, settings.red,
                        listOf(
                            gpu?.name ?: if (snap != null) "No GPU reported" else "GPU n/a",
                            "VRAM " + if (gpu?.vramUsedMb != null && gpu.vramTotalMb != null)
                                "${Format.megabytes(gpu.vramUsedMb)} / ${Format.megabytes(gpu.vramTotalMb)}" else "n/a",
                        ),
                        Modifier.weight(1f).fillMaxHeight(),
                    ) { onOpenDetail("gpu") }
                }
                Row(Modifier.height(IntrinsicSize.Max), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    val ram = snap?.ram
                    MetricCard(
                        "RAM", ram?.percent, settings.amber, settings.red,
                        listOf(
                            "${Format.bytes(ram?.usedBytes)} / ${Format.bytes(ram?.totalBytes)}",
                            "Available ${Format.bytes(ram?.availableBytes)}",
                        ),
                        Modifier.weight(1f).fillMaxHeight(),
                    ) { onOpenDetail("ram") }
                    val disks = snap?.disks.orEmpty()
                    val disk = disks.firstOrNull { it.id == selectedDisk } ?: disks.firstOrNull()
                    MetricCard(
                        "DISK" + (disk?.let { " ${it.id}" } ?: ""), disk?.percent, settings.amber, settings.red,
                        listOf(
                            "${Format.bytes(disk?.usedBytes)} / ${Format.bytes(disk?.totalBytes)}",
                            "Free ${Format.bytes(disk?.freeBytes)}",
                        ),
                        Modifier.weight(1f).fillMaxHeight(),
                        extra = if (disks.size > 1) ({ DiskChips(disks, disk?.id, vm::selectDisk) }) else null,
                    ) { onOpenDetail("disk") }
                }
            }
        }

        // ---- actions ----
        item { SectionHeader("PC actions") }
        item {
            val reason = when {
                !connected -> "Connect to the PC to use power actions."
                Permissions.POWER !in perms -> "This device has no \"power\" permission. Ask the PC owner to grant it."
                else -> null
            }
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                ActionRow(
                    ActionSpec("RESTART", Icons.Filled.RestartAlt, Pc.Cyan, canPower) { vm.startPower(PowerActions.RESTART) },
                    ActionSpec("SHUTDOWN", Icons.Filled.PowerSettingsNew, Pc.Red, canPower) { vm.startPower(PowerActions.SHUTDOWN) },
                    reason, vm::tell,
                )
                ActionRow(
                    ActionSpec("CANCEL ACTION", Icons.Filled.Cancel, Pc.Amber, canPower) { vm.cancelPower() },
                    ActionSpec("LOCK PC", Icons.Filled.Lock, Pc.Blue, canPower) { vm.lockPc() },
                    reason, vm::tell,
                )
                ActionRow(
                    ActionSpec("SLEEP", Icons.Filled.Bedtime, Pc.Purple, canPower) { vm.startPower(PowerActions.SLEEP) },
                    ActionSpec("WAKE ON LAN", Icons.Filled.Bolt, Pc.Green, pc != null) { showWol = true },
                    reason, vm::tell,
                )
                if (reason != null) Text(reason, style = MaterialTheme.typography.bodySmall, color = Pc.TextDim)
            }
        }
        item { Spacer(Modifier.height(8.dp)) }
    }

    // ---- dialogs ----
    val dialog by vm.powerDialog.collectAsState()
    dialog?.let { PowerDialogs(it, vm) }
    if (showWol && pc != null) WolDialog(pc!!, vm) { showWol = false }
}

@Composable
private fun Banner(title: String, text: String, color: Color, actions: (@Composable () -> Unit)?) {
    Panel(borderColor = color.copy(alpha = 0.5f), corner = 16.dp) {
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = color)
            Text(text, style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
            if (actions != null) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) { actions() }
        }
    }
}

@Composable
private fun PendingBanner(action: String, atMs: Long, canCancel: Boolean, onCancel: () -> Unit) {
    val now by rememberNowMs(250)
    val remaining = ((atMs - now) / 1000).coerceAtLeast(0)
    Panel(borderColor = Pc.Red.copy(alpha = 0.6f), corner = 16.dp) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("${action.uppercase()} SCHEDULED", style = MaterialTheme.typography.labelLarge, color = Pc.Red)
                Text(
                    if (remaining > 0) "The PC will ${action} in ${Format.countdown(remaining)}" else "Executing now...",
                    style = MaterialTheme.typography.titleMedium,
                )
            }
            Button(
                onClick = onCancel, enabled = canCancel,
                colors = ButtonDefaults.buttonColors(containerColor = Pc.Amber, contentColor = Color.Black),
            ) { Text("Cancel") }
        }
    }
}

@Composable
private fun MetricCard(
    title: String,
    percent: Double?,
    amber: Int,
    red: Int,
    lines: List<String>,
    modifier: Modifier = Modifier,
    extra: (@Composable () -> Unit)? = null,
    onClick: () -> Unit,
) {
    val level = Thresholds.levelFor(percent, amber, red)
    Panel(modifier, onClick = onClick, contentPadding = PaddingValues(12.dp)) {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(title, style = MaterialTheme.typography.labelLarge, color = Pc.TextSecondary, modifier = Modifier.weight(1f))
                if (level != Level.Normal) {
                    Text(if (level == Level.Critical) "CRITICAL" else "HIGH", style = MaterialTheme.typography.labelSmall, color = Pc.levelColor(level))
                }
            }
            CircularGauge(
                percent = percent?.toFloat(), level = level, centerText = Format.percent(percent),
                modifier = Modifier.size(104.dp), strokeWidth = 9.dp,
            )
            lines.forEachIndexed { i, l ->
                Text(
                    l,
                    style = if (i == 0) MaterialTheme.typography.bodySmall else MaterialTheme.typography.labelMedium,
                    color = if (i == 0) Pc.TextPrimary else Pc.TextSecondary,
                    maxLines = if (i == 0) 2 else 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.fillMaxWidth(),
                    textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                )
            }
            extra?.invoke()
        }
    }
}

@Composable
private fun DiskChips(disks: List<DiskInfo>, selected: String?, onSelect: (String) -> Unit) {
    FlowRow(
        horizontalArrangement = Arrangement.spacedBy(6.dp, Alignment.CenterHorizontally),
        verticalArrangement = Arrangement.spacedBy(4.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        for (d in disks) {
            val sel = d.id == selected
            Text(
                d.id,
                style = MaterialTheme.typography.labelMedium,
                color = if (sel) Color.Black else Pc.TextSecondary,
                modifier = Modifier
                    .clip(RoundedCornerShape(50))
                    .background(if (sel) Pc.Cyan else Pc.Outline)
                    .clickable { onSelect(d.id) }
                    .padding(horizontal = 10.dp, vertical = 4.dp),
            )
        }
    }
}

private data class ActionSpec(
    val label: String,
    val icon: ImageVector,
    val tint: Color,
    val enabled: Boolean,
    val onClick: () -> Unit,
)

@Composable
private fun ActionRow(a: ActionSpec, b: ActionSpec, disabledReason: String?, tell: (String) -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        ActionTile(a, disabledReason, tell, Modifier.weight(1f))
        ActionTile(b, disabledReason, tell, Modifier.weight(1f))
    }
}

@Composable
private fun ActionTile(a: ActionSpec, reason: String?, tell: (String) -> Unit, modifier: Modifier) {
    Panel(
        modifier.alpha(if (a.enabled) 1f else 0.4f),
        onClick = { if (a.enabled) a.onClick() else reason?.let(tell) },
        corner = 18.dp,
        contentPadding = PaddingValues(horizontal = 14.dp, vertical = 16.dp),
        borderColor = if (a.enabled) a.tint.copy(alpha = 0.45f) else Pc.Outline,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Icon(a.icon, null, tint = a.tint, modifier = Modifier.size(26.dp))
            Spacer(Modifier.width(10.dp))
            Text(a.label, style = MaterialTheme.typography.labelLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}
