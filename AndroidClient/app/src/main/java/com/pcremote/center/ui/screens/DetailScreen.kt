package com.pcremote.center.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.pcremote.center.core.Format
import com.pcremote.center.core.Level
import com.pcremote.center.core.Thresholds
import com.pcremote.center.monitoring.DiskInfo
import com.pcremote.center.monitoring.GpuInfo
import com.pcremote.center.monitoring.Sample
import com.pcremote.center.networking.LinkState
import com.pcremote.center.ui.components.ChartLegend
import com.pcremote.center.ui.components.ChartSeries
import com.pcremote.center.ui.components.CircularGauge
import com.pcremote.center.ui.components.LineChart
import com.pcremote.center.ui.components.Panel
import com.pcremote.center.ui.components.SectionHeader
import com.pcremote.center.ui.components.UsageBar
import com.pcremote.center.ui.components.rememberNowMs
import com.pcremote.center.ui.theme.Pc

@Composable
fun DetailScreen(kind: String, onBack: () -> Unit, vm: DashboardViewModel = viewModel()) {
    val snap by vm.snapshot.collectAsState()
    val history by vm.history.collectAsState()
    val settings by vm.settings.collectAsState()
    val status by vm.status.collectAsState()
    val now by rememberNowMs(1000)
    DisposableEffect(Unit) {
        vm.acquireMonitor()
        onDispose { vm.releaseMonitor() }
    }
    val windowMs = settings.historySeconds * 1000L
    val title = when (kind) { "cpu" -> "CPU"; "gpu" -> "GPU"; "ram" -> "Memory"; else -> "Disks" }

    fun series(label: String, color: androidx.compose.ui.graphics.Color, pick: (Sample) -> Float?) =
        ChartSeries(label, color, history.mapNotNull { s -> pick(s)?.let { s.timeMs to it } })

    Column(Modifier.fillMaxSize().statusBarsPadding()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") }
            Text(title, style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            if (status.state != LinkState.Connected) Text("offline - last values", style = MaterialTheme.typography.labelSmall, color = Pc.Amber)
        }
        LazyColumn(
            Modifier.fillMaxSize().navigationBarsPadding(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            when (kind) {
                "cpu" -> {
                    val cpu = snap?.cpu
                    item {
                        Panel {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                CircularGauge(
                                    cpu?.usage?.toFloat(), Thresholds.levelFor(cpu?.usage, settings.amber, settings.red),
                                    Format.percent(cpu?.usage), Modifier.size(120.dp), 11.dp, 26,
                                )
                                Column(Modifier.padding(start = 16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                                    Text(cpu?.model ?: "Model n/a", style = MaterialTheme.typography.titleSmall)
                                    StatRow("Clock", Format.mhz(cpu?.frequencyMhz))
                                    StatRow("Temperature", Format.temperature(cpu?.temperatureC))
                                    StatRow("Logical cores", cpu?.logicalCores?.toString() ?: Format.NA)
                                    StatRow("Physical cores", cpu?.physicalCores?.toString() ?: Format.NA)
                                }
                            }
                        }
                    }
                    item {
                        val s = listOf(series("CPU usage", Pc.Cyan) { it.cpu })
                        ChartPanel("CPU usage history", s, windowMs, now)
                    }
                    item { SectionHeader("Per-core usage") }
                    val cores = cpu?.cores.orEmpty()
                    if (cores.isEmpty()) item { Text("Per-core data n/a", color = Pc.TextDim) }
                    else item {
                        Panel {
                            Column(verticalArrangement = Arrangement.spacedBy(7.dp)) {
                                cores.forEachIndexed { i, v ->
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Text("Core $i", style = MaterialTheme.typography.labelMedium, color = Pc.TextSecondary, modifier = Modifier.width(56.dp))
                                        UsageBar(v.toFloat(), Thresholds.levelFor(v, settings.amber, settings.red), Modifier.weight(1f))
                                        Text(Format.percent(v), style = MaterialTheme.typography.labelMedium, modifier = Modifier.width(48.dp), textAlign = androidx.compose.ui.text.style.TextAlign.End)
                                    }
                                }
                            }
                        }
                    }
                }

                "gpu" -> {
                    val gpus = snap?.gpus.orEmpty()
                    if (gpus.isEmpty()) item { Panel { Text(if (snap == null) "GPU data n/a" else "The PC did not report a GPU.", color = Pc.TextSecondary) } }
                    gpus.forEachIndexed { idx, g -> item(key = "gpu$idx") { GpuPanel(g, settings.amber, settings.red) } }
                    item {
                        ChartPanel(
                            "GPU history" + if (gpus.size > 1) " (first GPU)" else "",
                            listOf(series("GPU usage", Pc.Purple) { it.gpu }, series("VRAM", Pc.Cyan) { it.vram }),
                            windowMs, now,
                        )
                    }
                }

                "ram" -> {
                    val ram = snap?.ram
                    item {
                        Panel {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                CircularGauge(
                                    ram?.percent?.toFloat(), Thresholds.levelFor(ram?.percent, settings.amber, settings.red),
                                    Format.percent(ram?.percent), Modifier.size(120.dp), 11.dp, 26,
                                )
                                Column(Modifier.padding(start = 16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                                    StatRow("Used", Format.bytes(ram?.usedBytes))
                                    StatRow("Available", Format.bytes(ram?.availableBytes))
                                    StatRow("Total", Format.bytes(ram?.totalBytes))
                                }
                            }
                        }
                    }
                    item { ChartPanel("Memory history", listOf(series("RAM", Pc.Blue) { it.ram }), windowMs, now) }
                }

                else -> {
                    val disks = snap?.disks.orEmpty()
                    if (disks.isEmpty()) item { Panel { Text(if (snap == null) "Disk data n/a" else "The PC did not report any disk.", color = Pc.TextSecondary) } }
                    items(disks, key = { it.id }) { DiskPanel(it, settings.amber, settings.red) }
                }
            }
        }
    }
}

@Composable
private fun StatRow(label: String, value: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(label, style = MaterialTheme.typography.bodySmall, color = Pc.TextDim, modifier = Modifier.width(96.dp))
        Text(value, style = MaterialTheme.typography.bodySmall, color = Pc.TextPrimary)
    }
}

@Composable
private fun ChartPanel(title: String, series: List<ChartSeries>, windowMs: Long, nowMs: Long) {
    Panel {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            LineChart(series, windowMs, endTimeMs = maxOf(nowMs, series.flatMap { it.points }.maxOfOrNull { it.first } ?: nowMs))
            ChartLegend(series)
        }
    }
}

@Composable
private fun GpuPanel(g: GpuInfo, amber: Int, red: Int) {
    Panel {
        Row(verticalAlignment = Alignment.CenterVertically) {
            CircularGauge(g.usage?.toFloat(), Thresholds.levelFor(g.usage, amber, red), Format.percent(g.usage), Modifier.size(110.dp), 10.dp, 24)
            Column(Modifier.padding(start = 16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(g.name ?: "GPU", style = MaterialTheme.typography.titleSmall)
                StatRow("Vendor", g.vendor ?: Format.NA)
                StatRow("Temperature", Format.temperature(g.temperatureC))
                StatRow("VRAM used", Format.megabytes(g.vramUsedMb))
                StatRow("VRAM total", Format.megabytes(g.vramTotalMb))
                StatRow("VRAM", Format.percent(g.vramPct))
            }
        }
    }
}

@Composable
private fun DiskPanel(d: DiskInfo, amber: Int, red: Int) {
    val level: Level = Thresholds.levelFor(d.percent, amber, red)
    Panel {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    d.id + (d.label?.takeIf { it.isNotBlank() }?.let { "  $it" } ?: ""),
                    style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f),
                )
                Text(Format.percent(d.percent), style = MaterialTheme.typography.titleMedium, color = Pc.levelColor(level))
            }
            UsageBar(d.percent?.toFloat(), level)
            StatRow("Used", "${Format.bytes(d.usedBytes)} of ${Format.bytes(d.totalBytes)}")
            StatRow("Free", Format.bytes(d.freeBytes))
            StatRow("File system", d.fileSystem ?: Format.NA)
            StatRow("Model", d.model ?: Format.NA)
            StatRow("Read speed", Format.speed(d.readBps))
            StatRow("Write speed", Format.speed(d.writeBps))
            StatRow("Temperature", Format.temperature(d.temperatureC))
        }
    }
}
