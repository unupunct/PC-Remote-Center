package com.pcremote.center.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.ClearAll
import androidx.compose.material.icons.filled.CloseFullscreen
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.FitScreen
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.OpenInFull
import androidx.compose.material.icons.filled.RestartAlt
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.isShiftPressed
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.pcremote.center.core.Permissions
import com.pcremote.center.networking.LinkState
import com.pcremote.center.terminal.Shells
import com.pcremote.center.terminal.TRow
import com.pcremote.center.terminal.TerminalUiState
import com.pcremote.center.ui.components.EmptyState
import com.pcremote.center.ui.components.Pill
import com.pcremote.center.ui.theme.Mono
import com.pcremote.center.ui.theme.Pc
import kotlinx.coroutines.flow.filter

private enum class ViewMode { Split, Cmd, PowerShell }

private val TermFg = Color(0xFFCCCCCC)
private val TermBg = Color(0xFF050607)
private val FontSize = 11.sp
private val LineHeight = 14.sp

@Composable
fun TerminalScreen(vm: TerminalViewModel = viewModel()) {
    val status by vm.status.collectAsState()
    val perms by vm.permissions.collectAsState()
    val sessionId by vm.sessionId.collectAsState()
    var mode by rememberSaveable { mutableStateOf(ViewMode.Split) }
    val connected = status.state == LinkState.Connected
    val permitted = Permissions.TERMINAL in perms

    // Open both sessions when the screen is shown and again after every reconnect / permission grant.
    LaunchedEffect(sessionId, connected, permitted) {
        if (connected && permitted) vm.openAll()
    }
    DisposableEffect(Unit) { onDispose { vm.closeAll() } }

    Column(Modifier.fillMaxSize().statusBarsPadding().padding(horizontal = 10.dp)) {
        Row(Modifier.fillMaxWidth().padding(top = 8.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            SingleChoiceSegmentedButtonRow(Modifier.weight(1f)) {
                val modes = listOf(ViewMode.Split to "Split", ViewMode.Cmd to "CMD", ViewMode.PowerShell to "PowerShell")
                modes.forEachIndexed { i, (m, label) ->
                    SegmentedButton(
                        selected = mode == m, onClick = { mode = m },
                        shape = SegmentedButtonDefaults.itemShape(i, modes.size),
                    ) { Text(label, maxLines = 1) }
                }
            }
        }
        when {
            !connected -> EmptyState(
                "Not connected",
                if (status.state == LinkState.AuthFailed) "The PC rejected this device. Re-pair it in Settings." else "The terminals appear as soon as the connection to the PC is established.",
            )
            !permitted -> EmptyState(
                "Terminal not permitted",
                "This device has no \"terminal\" permission. Ask the PC owner to grant it in PC Remote Center on the PC.",
            )
            else -> Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (mode != ViewMode.PowerShell) {
                    ShellPanel(
                        Shells.CMD, vm,
                        expanded = mode == ViewMode.Cmd,
                        onToggleExpand = { mode = if (mode == ViewMode.Cmd) ViewMode.Split else ViewMode.Cmd },
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                    )
                }
                if (mode != ViewMode.Cmd) {
                    ShellPanel(
                        Shells.POWERSHELL, vm,
                        expanded = mode == ViewMode.PowerShell,
                        onToggleExpand = { mode = if (mode == ViewMode.PowerShell) ViewMode.Split else ViewMode.PowerShell },
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                    )
                }
            }
        }
    }
}

private fun rgb(v: Int) = Color(0xFF000000.toInt() or (v and 0xFFFFFF))

/** Converts a server row (colour runs) to a styled string; optionally paints the cursor cell. */
private fun TRow.toAnnotated(cursorX: Int? = null): AnnotatedString = buildAnnotatedString {
    for (r in runs) {
        val fg = when {
            r.fg != -1 -> rgb(r.fg).let { c -> if (r.dim) c.copy(alpha = 0.65f) else c }
            r.dim -> TermFg.copy(alpha = 0.6f)
            else -> Color.Unspecified
        }
        pushStyle(
            SpanStyle(
                color = fg,
                background = if (r.bg != -1) rgb(r.bg) else Color.Unspecified,
                fontWeight = if (r.bold) FontWeight.Bold else null,
                fontStyle = if (r.italic) FontStyle.Italic else null,
                textDecoration = if (r.underline) TextDecoration.Underline else null,
            )
        )
        append(r.text)
        pop()
    }
    if (cursorX != null && cursorX >= 0) {
        while (length <= cursorX) append(' ')
        addStyle(SpanStyle(background = Pc.Cyan.copy(alpha = 0.85f), color = Color.Black), cursorX, cursorX + 1)
    }
}

@Composable
private fun ShellPanel(
    shell: String,
    vm: TerminalViewModel,
    expanded: Boolean,
    onToggleExpand: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val state by vm.state(shell).collectAsState()
    val clipboard = LocalClipboardManager.current
    val density = LocalDensity.current
    val measurer = rememberTextMeasurer()
    val monoStyle = remember { TextStyle(fontFamily = Mono, fontSize = FontSize, lineHeight = LineHeight, color = TermFg) }
    val charWidthPx = remember { measurer.measure("M", monoStyle).size.width.toFloat().coerceAtLeast(1f) }
    val lineHeightPx = with(density) { LineHeight.toPx() }

    var input by remember { mutableStateOf("") }
    var viewport by remember { mutableStateOf(androidx.compose.ui.unit.IntSize.Zero) }

    // rows shown: scrollback + screen (trailing blank screen rows trimmed unless alt screen)
    val rows: List<TRow> = remember(state.version) {
        val screen = state.screen
        val keep = if (state.alt) screen.size else {
            var last = screen.indexOfLast { it.runs.isNotEmpty() }
            if (state.cursor.visible && state.cursor.y > last) last = state.cursor.y
            (last + 1).coerceIn(0, screen.size)
        }
        state.lines + screen.take(keep)
    }
    val cursorRowIndex = state.lines.size + state.cursor.y
    val showCursor = state.running && state.cursor.visible && !state.alt

    val listState = rememberLazyListState()
    var follow by remember { mutableStateOf(true) }
    LaunchedEffect(listState) {
        snapshotFlow { listState.isScrollInProgress }.filter { !it }.collect { follow = !listState.canScrollForward }
    }
    LaunchedEffect(state.version, follow, rows.size) {
        if (follow && rows.isNotEmpty()) listState.scrollToItem(rows.size - 1)
    }

    fun send() {
        if (!state.running) return
        val text = input
        vm.send(shell, text)
        input = ""
        follow = true
    }

    Column(
        modifier
            .clip(RoundedCornerShape(16.dp))
            .background(Pc.Panel)
            .border(1.dp, Pc.Outline, RoundedCornerShape(16.dp))
    ) {
        // ---- header ----
        Row(Modifier.fillMaxWidth().padding(start = 12.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(Shells.title(shell), style = MaterialTheme.typography.titleSmall, maxLines = 1)
            Box(Modifier.width(8.dp))
            when {
                !state.opened -> Pill("Opening", Pc.Amber)
                state.running -> Pill("Running", Pc.Green)
                else -> Pill("Ended" + (state.exitCode?.let { " ($it)" } ?: ""), Pc.Red)
            }
            Box(Modifier.weight(1f))
            PanelIcon(Icons.Filled.FitScreen, "Fit to phone width") {
                val cols = ((viewport.width - with(density) { 16.dp.toPx() }) / charWidthPx).toInt()
                val rowsFit = (viewport.height / lineHeightPx).toInt()
                if (cols >= 20) vm.resize(shell, cols, rowsFit.coerceAtLeast(5)) else vm.info("The panel is too narrow to fit a terminal.")
            }
            PanelIcon(Icons.Filled.ContentCopy, "Copy output") {
                val text = state.plainText()
                clipboard.setText(AnnotatedString(text))
                vm.info(if (text.isEmpty()) "Nothing to copy yet." else "Output copied (${text.length} characters).")
            }
            PanelIcon(Icons.Filled.ClearAll, "Clear") { vm.clear(shell) }
            PanelIcon(Icons.Filled.Stop, "Stop (Ctrl+C)", tint = Pc.Red) { vm.interrupt(shell) }
            PanelIcon(if (expanded) Icons.Filled.CloseFullscreen else Icons.Filled.OpenInFull, if (expanded) "Collapse" else "Expand", onClick = onToggleExpand)
        }

        // ---- output ----
        BoxWithConstraints(
            Modifier
                .weight(1f)
                .fillMaxWidth()
                .padding(horizontal = 4.dp)
                .background(TermBg, RoundedCornerShape(10.dp))
                .clip(RoundedCornerShape(10.dp))
                .onSizeChanged { viewport = it }
        ) {
            val colsWidth = with(density) { (state.cols * charWidthPx).toDp() } + 16.dp
            val contentWidth = if (colsWidth > maxWidth) colsWidth else maxWidth
            Box(Modifier.fillMaxSize().horizontalScroll(rememberScrollState())) {
                LazyColumn(Modifier.width(contentWidth).fillMaxHeight(), state = listState) {
                    itemsIndexed(
                        rows,
                        key = { i, _ -> if (i < state.lines.size) state.base + i else -(i - state.lines.size).toLong() - 1 },
                    ) { i, row ->
                        val text = if (showCursor && i == cursorRowIndex) row.toAnnotated(state.cursor.x)
                        else remember(row) { row.toAnnotated() }
                        Text(
                            text, style = monoStyle, softWrap = false, maxLines = 1, overflow = TextOverflow.Clip,
                            modifier = Modifier.padding(horizontal = 8.dp),
                        )
                    }
                }
            }
            if (!follow) {
                IconButton(
                    onClick = { follow = true },
                    modifier = Modifier.align(Alignment.BottomEnd).padding(6.dp).size(34.dp)
                        .background(Pc.Cyan.copy(alpha = 0.9f), RoundedCornerShape(50)),
                ) { Icon(Icons.Filled.KeyboardArrowDown, "Jump to bottom", tint = Color.Black) }
            }
        }

        // ---- input area ----
        if (state.opened && !state.running) {
            Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    "Session ended" + (state.exitCode?.let { " (exit code $it)" } ?: ""),
                    color = Pc.TextSecondary, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f),
                )
                Button(onClick = { vm.restart(shell) }) {
                    Icon(Icons.Filled.RestartAlt, null, Modifier.size(18.dp))
                    Box(Modifier.width(6.dp))
                    Text("Restart")
                }
            }
        } else {
            QuickKeys(
                onKey = { vm.key(shell, it) },
                onHistoryUp = { vm.historyPrevious(shell, input)?.let { input = it } },
                onHistoryDown = { vm.historyNext(shell)?.let { input = it } },
            )
            Row(Modifier.fillMaxWidth().padding(start = 6.dp, end = 2.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = input,
                    onValueChange = { new ->
                        // Enter on the soft keyboard sends; pasted multi-line text stays in the field
                        if (new.length == input.length + 1 && new.endsWith("\n") && new.dropLast(1) == input) send()
                        else input = new
                    },
                    modifier = Modifier
                        .weight(1f)
                        .onPreviewKeyEvent { ev ->
                            if (ev.key == Key.Enter && ev.type == KeyEventType.KeyDown && !ev.isShiftPressed) { send(); true } else false
                        },
                    textStyle = monoStyle.copy(color = Pc.TextPrimary),
                    placeholder = { Text("Command", style = monoStyle.copy(color = Pc.TextDim)) },
                    maxLines = 4,
                    enabled = state.opened,
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.None, autoCorrectEnabled = false, imeAction = ImeAction.Default),
                    shape = RoundedCornerShape(14.dp),
                )
                IconButton(onClick = ::send, enabled = state.opened && state.running) {
                    Icon(Icons.AutoMirrored.Filled.Send, "Send", tint = Pc.Cyan)
                }
            }
        }
    }
}

@Composable
private fun PanelIcon(icon: androidx.compose.ui.graphics.vector.ImageVector, desc: String, tint: Color = Pc.TextSecondary, onClick: () -> Unit) {
    IconButton(onClick = onClick, modifier = Modifier.size(36.dp)) { Icon(icon, desc, tint = tint, modifier = Modifier.size(20.dp)) }
}

/** Tab / Esc / arrow keys are sent as raw input (the shell's own history answers the arrow keys). */
@Composable
private fun QuickKeys(onKey: (String) -> Unit, onHistoryUp: () -> Unit, onHistoryDown: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 6.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        KeyChip("Tab") { onKey("\t") }
        KeyChip("Esc") { onKey("\u001b") }
        KeyChip("↑") { onKey("\u001b[A") }
        KeyChip("↓") { onKey("\u001b[B") }
        KeyChip("←") { onKey("\u001b[D") }
        KeyChip("→") { onKey("\u001b[C") }
        KeyChip("Hist ▲", Pc.Purple, onHistoryUp)
        KeyChip("Hist ▼", Pc.Purple, onHistoryDown)
    }
}

@Composable
private fun KeyChip(label: String, color: Color = Pc.Cyan, onClick: () -> Unit) {
    Text(
        label,
        style = MaterialTheme.typography.labelMedium,
        color = color,
        modifier = Modifier
            .clip(RoundedCornerShape(8.dp))
            .background(color.copy(alpha = 0.12f))
            .clickable(onClick = onClick)
            .widthIn(min = 34.dp)
            .padding(horizontal = 10.dp, vertical = 6.dp),
        maxLines = 1,
    )
}
