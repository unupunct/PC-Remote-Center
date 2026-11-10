package com.pcremote.center.ui.screens

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.pcremote.center.PcRemoteApp
import com.pcremote.center.networking.LinkState
import com.pcremote.center.processes.KillResult
import com.pcremote.center.processes.ProcRow
import com.pcremote.center.processes.ProcessRepository
import com.pcremote.center.ui.components.Panel
import com.pcremote.center.ui.theme.Pc
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.util.Locale

class ProcessesViewModel(app: Application) : AndroidViewModel(app) {
    private val container = (app as PcRemoteApp).container
    private val repo = container.processes
    val status = container.connection.status
    val permissions = container.connection.permissions

    private val _rows = MutableStateFlow<List<ProcRow>>(emptyList())
    val rows = _rows.asStateFlow()
    private val _total = MutableStateFlow(0)
    val total = _total.asStateFlow()
    private val _sort = MutableStateFlow("memory")
    val sort = _sort.asStateFlow()
    private val _filter = MutableStateFlow("")
    val filter = _filter.asStateFlow()
    private val _error = MutableStateFlow<String?>(null)
    val error = _error.asStateFlow()
    private val _notice = MutableStateFlow<String?>(null)
    val notice = _notice.asStateFlow()
    private val _loaded = MutableStateFlow(false)
    val loaded = _loaded.asStateFlow()

    fun setSort(s: String) { _sort.value = s; refresh() }
    fun setFilter(f: String) { _filter.value = f }
    fun clearNotice() { _notice.value = null }

    fun refresh() {
        viewModelScope.launch {
            repo.list(_sort.value, _filter.value).fold(
                onSuccess = { _rows.value = it.rows; _total.value = it.total; _error.value = null; _loaded.value = true },
                onFailure = { _error.value = it.message },
            )
        }
    }

    fun kill(p: ProcRow, tree: Boolean) {
        viewModelScope.launch {
            when (val r = repo.kill(p.pid, tree)) {
                is KillResult.Ended -> { _notice.value = "Ended ${r.name} (pid ${p.pid})."; refresh() }
                is KillResult.Failed -> _notice.value = r.message
            }
        }
    }

    val canKill get() = repo.canKill
}

@Composable
fun ProcessesScreen(onBack: () -> Unit, vm: ProcessesViewModel = viewModel()) {
    val rows by vm.rows.collectAsState()
    val total by vm.total.collectAsState()
    val sort by vm.sort.collectAsState()
    val filter by vm.filter.collectAsState()
    val error by vm.error.collectAsState()
    val notice by vm.notice.collectAsState()
    val loaded by vm.loaded.collectAsState()
    val status by vm.status.collectAsState()
    val perms by vm.permissions.collectAsState()
    var confirm by remember { mutableStateOf<ProcRow?>(null) }
    var tree by remember { mutableStateOf(false) }

    val connected = status.state == LinkState.Connected
    val allowed = connected && "monitoring" in perms

    // live refresh every 3 s while the screen is open and a filter edit settles
    LaunchedEffect(allowed, filter) {
        if (!allowed) return@LaunchedEffect
        delay(if (filter.isEmpty()) 0 else 350)
        while (true) { vm.refresh(); delay(3000) }
    }

    Column(Modifier.fillMaxSize().statusBarsPadding().padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") }
            Column(Modifier.weight(1f)) {
                Text("Processes", style = MaterialTheme.typography.headlineSmall)
                Text(if (allowed) "$total running · live" else "Not available", style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
            }
        }

        if (!allowed) {
            Text(
                if (!connected) "Connect to the PC to see its processes." else "This device has no \"monitoring\" permission.",
                color = Pc.TextSecondary,
            )
            return@Column
        }

        OutlinedTextField(
            value = filter, onValueChange = vm::setFilter, singleLine = true, label = { Text("Search name or window title") },
            modifier = Modifier.fillMaxWidth(),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            for (s in ProcessRepositoryUi.sorts) {
                FilterChip(selected = sort == s.first, onClick = { vm.setSort(s.first) }, label = { Text(s.second) })
            }
        }
        error?.let { Text(it, color = Pc.Red, style = MaterialTheme.typography.bodySmall) }
        notice?.let {
            Text(it, color = Pc.Amber, style = MaterialTheme.typography.bodySmall)
            LaunchedEffect(it) { delay(5000); vm.clearNotice() }
        }
        if (!vm.canKill) Text("Ending processes needs the \"admin\" permission on this device.", style = MaterialTheme.typography.labelSmall, color = Pc.TextDim)

        if (!loaded && error == null) Text("Loading...", color = Pc.TextSecondary)
        LazyColumn(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            items(rows, key = { it.pid }) { p ->
                Panel {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(p.name, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                "pid ${p.pid} · ${p.threads} threads" + (p.window?.let { " · $it" } ?: ""),
                                style = MaterialTheme.typography.labelSmall, color = Pc.TextDim, maxLines = 1, overflow = TextOverflow.Ellipsis,
                            )
                        }
                        Column(horizontalAlignment = Alignment.End) {
                            Text(p.cpu?.let { String.format(Locale.US, "%.1f%% CPU", it) } ?: "CPU n/a", style = MaterialTheme.typography.bodySmall,
                                color = if ((p.cpu ?: 0.0) >= 50) Pc.Amber else Pc.TextSecondary)
                            Text(formatMb(p.memoryMb), style = MaterialTheme.typography.bodySmall, color = Pc.TextSecondary)
                        }
                        Spacer(Modifier.padding(start = 6.dp))
                        TextButton(onClick = { tree = false; confirm = p }, enabled = vm.canKill) { Text("End", color = if (vm.canKill) Pc.Red else Pc.TextDim) }
                    }
                }
            }
        }
    }

    confirm?.let { p ->
        AlertDialog(
            onDismissRequest = { confirm = null },
            title = { Text("End ${p.name}?") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("Process ${p.pid} on the PC is stopped immediately. Unsaved work in it is lost.")
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(checked = tree, onCheckedChange = { tree = it })
                        Text("Also end the processes it started")
                    }
                }
            },
            confirmButton = { TextButton(onClick = { vm.kill(p, tree); confirm = null }) { Text("End process", color = Pc.Red) } },
            dismissButton = { TextButton(onClick = { confirm = null }) { Text("Cancel") } },
        )
    }
}

private object ProcessRepositoryUi {
    val sorts = listOf("memory" to "Memory", "cpu" to "CPU", "name" to "Name")
}

private fun formatMb(mb: Double): String =
    if (mb >= 1024) String.format(Locale.US, "%.2f GB", mb / 1024) else String.format(Locale.US, "%.0f MB", mb)
