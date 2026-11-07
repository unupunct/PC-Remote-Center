@file:OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)

package com.pcremote.center.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Apps
import androidx.compose.material.icons.filled.BarChart
import androidx.compose.material.icons.filled.Build
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Done
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.Lan
import androidx.compose.material.icons.filled.Memory
import androidx.compose.material.icons.filled.Monitor
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.pcremote.center.core.Permissions
import com.pcremote.center.networking.LinkState
import com.pcremote.center.shortcuts.Shortcut
import com.pcremote.center.ui.components.EmptyState
import com.pcremote.center.ui.components.Panel
import com.pcremote.center.ui.theme.Pc

fun shortcutIcon(id: String): ImageVector = when (id) {
    "monitor" -> Icons.Filled.Monitor
    "memory" -> Icons.Filled.Memory
    "settings" -> Icons.Filled.Settings
    "list" -> Icons.AutoMirrored.Filled.List
    "network" -> Icons.Filled.Lan
    "chart" -> Icons.Filled.BarChart
    "web" -> Icons.Filled.Language
    "code" -> Icons.Filled.Code
    "build" -> Icons.Filled.Build
    else -> Icons.Filled.Apps
}

@Composable
fun ShortcutsScreen(vm: ShortcutsViewModel = viewModel()) {
    val list by vm.shortcuts.collectAsState()
    val loaded by vm.loaded.collectAsState()
    val perms by vm.permissions.collectAsState()
    val status by vm.status.collectAsState()
    val confirm by vm.confirm.collectAsState()
    val running by vm.running.collectAsState()
    val elevated by vm.elevatedAllowed.collectAsState()
    var edit by remember { mutableStateOf(false) }
    var editing by remember { mutableStateOf<Shortcut?>(null) }
    var deleting by remember { mutableStateOf<Shortcut?>(null) }
    val connected = status.state == LinkState.Connected
    val permitted = Permissions.LAUNCH in perms
    val admin = Permissions.ADMIN in perms

    LaunchedEffect(connected, permitted) { if (connected && permitted) vm.refresh() }

    Column(Modifier.fillMaxSize().statusBarsPadding()) {
        Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Shortcuts", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.weight(1f))
            if (connected && permitted) {
                if (edit) IconButton(onClick = { editing = Shortcut() }) { Icon(Icons.Filled.Add, "Add shortcut", tint = Pc.Cyan) }
                IconButton(onClick = {
                    if (!edit && !admin) vm.tell("Editing shortcuts needs the \"admin\" permission. Ask the PC owner to grant it to this device.")
                    else edit = !edit
                }) {
                    Icon(if (edit) Icons.Filled.Done else Icons.Filled.Edit, if (edit) "Done editing" else "Edit shortcuts", tint = if (edit) Pc.Green else Pc.TextSecondary)
                }
            }
        }
        when {
            !connected -> EmptyState("Not connected", "Shortcuts are loaded from the PC once the connection is established.")
            !permitted -> EmptyState("Shortcuts not permitted", "This device has no \"launch\" permission. Ask the PC owner to grant it.")
            !loaded -> Box(Modifier.fillMaxWidth().padding(40.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            list.isEmpty() -> EmptyState(
                "No shortcuts yet",
                if (admin) "Tap the pencil, then + to add the first shortcut (an app, a control panel item or an ms-settings: link)."
                else "The PC has no shortcuts. An administrator device can add them.",
            )
            else -> LazyVerticalGrid(
                GridCells.Fixed(2),
                contentPadding = PaddingValues(16.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                if (edit) {
                    items(list, key = { it.id }, span = { GridItemSpan(maxLineSpan) }) { s ->
                        val idx = list.indexOf(s)
                        Panel {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(shortcutIcon(s.icon), null, tint = Pc.Cyan, modifier = Modifier.size(26.dp))
                                Column(Modifier.weight(1f).padding(horizontal = 10.dp)) {
                                    Text(s.name, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    Text(s.path, style = MaterialTheme.typography.labelSmall, color = Pc.TextDim, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                }
                                IconButton(onClick = { vm.move(s, -1) }, enabled = idx > 0, modifier = Modifier.size(36.dp)) { Icon(Icons.Filled.KeyboardArrowUp, "Move up") }
                                IconButton(onClick = { vm.move(s, 1) }, enabled = idx < list.lastIndex, modifier = Modifier.size(36.dp)) { Icon(Icons.Filled.KeyboardArrowDown, "Move down") }
                                IconButton(onClick = { editing = s }, modifier = Modifier.size(36.dp)) { Icon(Icons.Filled.Edit, "Edit", tint = Pc.Cyan) }
                                IconButton(onClick = { deleting = s }, modifier = Modifier.size(36.dp)) { Icon(Icons.Filled.Delete, "Delete", tint = Pc.Red) }
                            }
                        }
                    }
                } else {
                    items(list, key = { it.id }) { s ->
                        Panel(onClick = { vm.tap(s) }, contentPadding = PaddingValues(14.dp)) {
                            Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                                Box(
                                    Modifier.size(42.dp).background(Pc.Cyan.copy(alpha = 0.12f), RoundedCornerShape(12.dp)),
                                    contentAlignment = Alignment.Center,
                                ) {
                                    if (running == s.id) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                                    else Icon(shortcutIcon(s.icon), null, tint = Pc.Cyan)
                                }
                                Text(s.name, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text(
                                    s.description.ifBlank { s.path },
                                    style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary,
                                    maxLines = 2, overflow = TextOverflow.Ellipsis,
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    confirm?.let { s ->
        AlertDialog(
            onDismissRequest = vm::dismissConfirm,
            title = { Text("Run ${s.name}?") },
            text = { Text("This shortcut asks for confirmation before it starts on the PC.\n\n${s.path}${if (s.arguments.isNotBlank()) " " + s.arguments else ""}") },
            confirmButton = { TextButton(onClick = { vm.runConfirmed(s) }) { Text("Run") } },
            dismissButton = { TextButton(onClick = vm::dismissConfirm) { Text("Cancel") } },
        )
    }
    deleting?.let { s ->
        AlertDialog(
            onDismissRequest = { deleting = null },
            title = { Text("Delete ${s.name}?") },
            text = { Text("The shortcut is removed from the PC.") },
            confirmButton = { TextButton(onClick = { vm.delete(s); deleting = null }) { Text("Delete", color = Pc.Red) } },
            dismissButton = { TextButton(onClick = { deleting = null }) { Text("Cancel") } },
        )
    }
    editing?.let { s -> ShortcutEditor(s, elevated, vm) { editing = null } }
}

@Composable
private fun ShortcutEditor(initial: Shortcut, elevatedAllowed: Boolean, vm: ShortcutsViewModel, onClose: () -> Unit) {
    var s by remember { mutableStateOf(initial) }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val isNew = initial.id.isEmpty()
    AlertDialog(
        onDismissRequest = { if (!busy) onClose() },
        title = { Text(if (isNew) "New shortcut" else "Edit shortcut") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(s.name, { s = s.copy(name = it) }, label = { Text("Name") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(s.description, { s = s.copy(description = it) }, label = { Text("Description") }, modifier = Modifier.fillMaxWidth())
                Text("Icon", style = MaterialTheme.typography.labelMedium, color = Pc.TextSecondary)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (id in Shortcut.ICONS) {
                        FilterChip(
                            selected = s.icon == id, onClick = { s = s.copy(icon = id) },
                            label = { Text(id) },
                            leadingIcon = { Icon(shortcutIcon(id), null, Modifier.size(18.dp)) },
                        )
                    }
                }
                OutlinedTextField(
                    s.path, { s = s.copy(path = it) }, label = { Text("Path (.exe .msc .cpl .lnk or ms-settings:...)") },
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(s.arguments, { s = s.copy(arguments = it) }, label = { Text("Arguments") }, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(s.workingDirectory, { s = s.copy(workingDirectory = it) }, label = { Text("Working directory") }, modifier = Modifier.fillMaxWidth())
                Text("Run mode", style = MaterialTheme.typography.labelMedium, color = Pc.TextSecondary)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (m in Shortcut.RUN_MODES) {
                        FilterChip(
                            selected = s.runMode == m, onClick = { s = s.copy(runMode = m) }, label = { Text(m) },
                            enabled = m != "elevated" || elevatedAllowed || s.runMode == "elevated",
                        )
                    }
                }
                if (!elevatedAllowed) Text("\"elevated\" is disabled on the PC.", style = MaterialTheme.typography.labelSmall, color = Pc.TextDim)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Ask for confirmation before running", Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                    Switch(s.confirm, { s = s.copy(confirm = it) })
                }
                error?.let { Text(it, color = Pc.Red, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            TextButton(
                enabled = !busy,
                onClick = {
                    busy = true; error = null
                    vm.save(s) { r ->
                        busy = false
                        if (r.ok) onClose() else error = r.message ?: "The PC rejected the shortcut."
                    }
                },
            ) { Text(if (busy) "Saving..." else "Save") }
        },
        dismissButton = { TextButton(onClick = onClose, enabled = !busy) { Text("Cancel") } },
    )
}
