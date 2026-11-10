package com.pcremote.center.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.State
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.pcremote.center.R
import com.pcremote.center.networking.LinkState
import com.pcremote.center.networking.LinkStatus
import com.pcremote.center.networking.Quality
import com.pcremote.center.ui.theme.Pc
import kotlinx.coroutines.delay

/** Wall clock ticking every [intervalMs] (for countdowns and "synced x s ago"). */
@Composable
fun rememberNowMs(intervalMs: Long = 1000): State<Long> {
    val now = remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(intervalMs) {
        while (true) {
            now.longValue = System.currentTimeMillis()
            delay(intervalMs)
        }
    }
    return now
}

fun qualityColor(q: Quality): Color = when (q) {
    Quality.Good -> Pc.Green
    Quality.Fair -> Pc.Amber
    Quality.Poor -> Pc.Red
    Quality.Unknown -> Pc.TextDim
}

fun qualityLabel(q: Quality, rtt: Long?): String = when (q) {
    Quality.Unknown -> "Quality n/a"
    else -> q.name + (if (rtt != null) " · $rtt ms" else "")
}

fun agoText(thenMs: Long?, nowMs: Long): String {
    if (thenMs == null) return "never"
    val s = ((nowMs - thenMs) / 1000).coerceAtLeast(0)
    return when {
        s < 2 -> "just now"
        s < 60 -> "${s}s ago"
        s < 3600 -> "${s / 60}m ago"
        else -> "${s / 3600}h ago"
    }
}

/** Dashboard header: logo, computer name, connection state, quality, last sync, settings button. */
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
fun ConnectionHeader(
    title: String,
    status: LinkStatus,
    quality: Quality,
    rttMs: Long?,
    lastSyncMs: Long?,
    onSettings: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val now by rememberNowMs(1000)
    Row(modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Box(
            Modifier.size(46.dp).clip(RoundedCornerShape(14.dp)).background(Pc.Panel),
            contentAlignment = Alignment.Center,
        ) {
            androidx.compose.foundation.Image(
                painterResource(R.drawable.ic_launcher_foreground), contentDescription = "PC Remote Center",
                modifier = Modifier.size(64.dp),
            )
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            // FlowRow: the three badges wrap to a second line on narrow phones instead of being squeezed
            FlowRow(
                Modifier.padding(top = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                val (txt, col) = when (status.state) {
                    LinkState.Connected -> "Connected" to Pc.Green
                    LinkState.Connecting -> "Connecting" to Pc.Amber
                    LinkState.Disconnected -> "Offline" to Pc.Red
                    LinkState.AuthFailed -> (if (status.revoked) "Revoked" else "Auth failed") to Pc.Red
                }
                Pill(txt, col)
                if (status.state == LinkState.Connected) Pill(qualityLabel(quality, rttMs), qualityColor(quality))
                // which path the link uses: home LAN, Tailscale, WireGuard ...
                if (status.state == LinkState.Connected && status.path != null) {
                    Pill(
                        com.pcremote.center.networking.NetworkPaths.label(status.path),
                        if (status.path == com.pcremote.center.networking.NetworkPaths.LAN) Pc.TextSecondary else Pc.Purple,
                    )
                }
            }
            Text(
                "Last sync: ${agoText(lastSyncMs, now)}",
                style = MaterialTheme.typography.labelSmall, color = Pc.TextDim, modifier = Modifier.padding(top = 4.dp),
            )
        }
        if (onSettings != null) {
            IconButton(onClick = onSettings) { Icon(Icons.Filled.Settings, "Settings", tint = Pc.TextSecondary) }
        }
    }
}
