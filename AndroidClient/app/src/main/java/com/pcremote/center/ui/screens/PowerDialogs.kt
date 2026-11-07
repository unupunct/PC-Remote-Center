package com.pcremote.center.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.pcremote.center.core.Format
import com.pcremote.center.core.SavedPc
import com.pcremote.center.power.PowerActions
import com.pcremote.center.ui.components.rememberNowMs
import com.pcremote.center.ui.theme.Pc
import kotlinx.coroutines.launch

private fun verb(action: String) = when (action) {
    PowerActions.RESTART -> "Restart"
    PowerActions.SHUTDOWN -> "Shut down"
    else -> "Put to sleep"
}

@Composable
fun PowerDialogs(d: PowerDialog, vm: DashboardViewModel) {
    when (d) {
        is PowerDialog.ChooseDelay -> {
            var delay by remember { mutableIntStateOf(30) }
            AlertDialog(
                onDismissRequest = vm::dismissPowerDialog,
                title = { Text("${verb(d.action)} the PC?") },
                text = {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text(
                            "Choose how long the PC waits before it ${d.action}s. Nothing happens until you confirm in the next step, and you can cancel while the countdown runs.",
                            style = MaterialTheme.typography.bodyMedium,
                        )
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            for (s in listOf(10, 30, 60)) {
                                FilterChip(selected = delay == s, onClick = { delay = s }, label = { Text("$s s") })
                            }
                        }
                    }
                },
                confirmButton = { TextButton(onClick = { vm.requestPower(d.action, delay) }) { Text("Continue") } },
                dismissButton = { TextButton(onClick = vm::dismissPowerDialog) { Text("Cancel") } },
            )
        }

        is PowerDialog.Requesting -> AlertDialog(
            onDismissRequest = {},
            title = { Text("${verb(d.action)}...") },
            text = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.size(12.dp))
                    Text("Asking the PC for a confirmation token...")
                }
            },
            confirmButton = {},
        )

        is PowerDialog.Confirm -> {
            val now by rememberNowMs(250)
            val remainingMs = (d.info.expiresAtMs - now).coerceAtLeast(0)
            val expired = remainingMs <= 0
            val danger = d.info.action != PowerActions.SLEEP
            LaunchedEffect(expired) {
                // keep the dialog so the user sees the expiry; it is dismissed by the user
            }
            AlertDialog(
                onDismissRequest = { if (!d.busy) vm.dismissPowerDialog() },
                title = { Text("Confirm ${d.info.action}") },
                text = {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text(
                            when (d.info.action) {
                                PowerActions.SLEEP -> "The PC will go to sleep immediately after you confirm."
                                else -> "The PC will ${d.info.action} in ${d.info.delaySeconds} seconds after you confirm. You can still cancel during the countdown."
                            },
                            style = MaterialTheme.typography.bodyMedium,
                        )
                        Text(
                            if (expired) "Confirmation expired - start again." else "Confirmation expires in ${Format.countdown((remainingMs + 999) / 1000)}",
                            color = if (expired) Pc.Red else Pc.Amber,
                            style = MaterialTheme.typography.titleSmall,
                        )
                        LinearProgressIndicator(
                            progress = { (remainingMs / 30_000f).coerceIn(0f, 1f) },
                            modifier = Modifier.fillMaxWidth(),
                            color = if (expired) Pc.Red else Pc.Amber,
                            trackColor = Pc.Outline,
                        )
                    }
                },
                confirmButton = {
                    Button(
                        onClick = vm::confirmPower,
                        enabled = !expired && !d.busy,
                        colors = ButtonDefaults.buttonColors(containerColor = if (danger) Pc.Red else Pc.Purple, contentColor = Color.White),
                    ) { Text(if (d.busy) "Confirming..." else "Confirm") }
                },
                dismissButton = { TextButton(onClick = vm::dismissPowerDialog, enabled = !d.busy) { Text("Abort") } },
            )
        }
    }
}

/** Wake-on-LAN: MAC and broadcast address are pre-filled from the server data stored for this PC and stay editable. */
@Composable
fun WolDialog(pc: SavedPc, vm: DashboardViewModel, onDismiss: () -> Unit) {
    var mac by remember { mutableStateOf(pc.wolMac) }
    var broadcast by remember { mutableStateOf(pc.wolBroadcast) }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text("Wake ${pc.name}") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    "The magic packet is sent from this phone over the local network (UDP ports 9 and 7), so it works while the PC is off - as long as the phone is on the same network.",
                    style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary,
                )
                OutlinedTextField(
                    value = mac, onValueChange = { mac = it; error = null }, singleLine = true,
                    label = { Text("MAC address") }, placeholder = { Text("AA:BB:CC:DD:EE:FF") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = broadcast, onValueChange = { broadcast = it; error = null }, singleLine = true,
                    label = { Text("Broadcast address") }, placeholder = { Text("192.168.1.255") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    modifier = Modifier.fillMaxWidth(),
                )
                if (pc.wolMac.isEmpty()) {
                    Text(
                        "No MAC address is stored yet. It is read from the PC the next time this phone is connected to it; until then enter it manually.",
                        style = MaterialTheme.typography.bodySmall, color = Pc.Amber,
                    )
                }
                pc.wolHint?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary) }
                error?.let { Text(it, color = Pc.Red, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            TextButton(
                enabled = !busy,
                onClick = {
                    busy = true
                    scope.launch {
                        val err = vm.sendWol(mac, broadcast)
                        busy = false
                        if (err == null) onDismiss() else error = err
                    }
                },
            ) { Text(if (busy) "Sending..." else "Send") }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !busy) { Text("Cancel") } },
    )
}
