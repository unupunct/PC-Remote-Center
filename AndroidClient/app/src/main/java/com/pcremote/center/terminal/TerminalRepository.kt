package com.pcremote.center.terminal

import com.pcremote.center.core.Permissions
import com.pcremote.center.networking.ConnectionManager
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

object Shells {
    const val CMD = "cmd"
    const val POWERSHELL = "powershell"
    val ALL = listOf(CMD, POWERSHELL)

    fun title(shell: String) = if (shell == CMD) "Command Prompt" else "PowerShell"
}

/** Immutable view of one shell for the UI. */
data class TerminalUiState(
    val shell: String,
    /** True once `terminal.open` succeeded on the current connection. */
    val opened: Boolean = false,
    val running: Boolean = false,
    val exitCode: Int? = null,
    val lines: List<TRow> = emptyList(),
    /** Absolute index of lines[0] (stable list keys). */
    val base: Long = 0,
    val screen: List<TRow> = emptyList(),
    val cursor: Cursor = Cursor(0, 0, false),
    val cols: Int = 0,
    val rows: Int = 0,
    val title: String? = null,
    val alt: Boolean = false,
    val version: Long = 0,
    val plainText: () -> String = { "" },
)

/** Per-shell state machine on top of the connection: open/subscribe, frames, input, resize, restart. */
class TerminalRepository(
    private val connection: ConnectionManager,
    scope: CoroutineScope,
) {
    private class ShellModel(val shell: String) {
        val buffer = TerminalBuffer()
        val state = MutableStateFlow(TerminalUiState(shell))
        var opened = false
        var lastSeq = -1L
        var version = 0L
        val queued = ArrayList<TerminalFrame>()
    }

    private val models = Shells.ALL.associateWith { ShellModel(it) }

    fun state(shell: String): StateFlow<TerminalUiState> = models.getValue(shell).state.asStateFlow()

    init {
        scope.launch {
            connection.pushes.filter { it.type == "terminal.output" }.collect { env ->
                val frame = TerminalFrame.parse(env.payload)
                models[frame.shell]?.let { m -> synchronized(m) { onPush(m, frame) } }
            }
        }
        // a new connection has no subscriptions: forget "opened" so the UI opens the shells again
        scope.launch {
            connection.sessionId.collect {
                for (m in models.values) synchronized(m) {
                    m.opened = false
                    m.queued.clear()
                    publish(m, opened = false)
                }
            }
        }
    }

    private fun onPush(m: ShellModel, f: TerminalFrame) {
        if (!m.opened) {
            if (m.queued.size < 200) m.queued.add(f)
            return
        }
        if (f.seq <= m.lastSeq && !f.full) return
        m.lastSeq = f.seq
        m.buffer.apply(f)
        publish(m, opened = true)
    }

    private fun publish(m: ShellModel, opened: Boolean) {
        val b = m.buffer
        m.version++
        m.state.value = TerminalUiState(
            shell = m.shell,
            opened = opened,
            running = b.running && opened,
            exitCode = b.exitCode,
            lines = b.linesSnapshot(),
            base = b.base,
            screen = b.screen,
            cursor = b.cursor,
            cols = b.cols,
            rows = b.rows,
            title = b.title,
            alt = b.alt,
            version = m.version,
            plainText = { synchronized(m) { b.plainText() } },
        )
    }

    private fun adopt(m: ShellModel, reply: JsonObject) {
        synchronized(m) {
            val f = TerminalFrame.parse(reply)
            m.buffer.reset()
            m.buffer.apply(f)
            m.lastSeq = f.seq
            m.opened = true
            val q = m.queued.filter { it.seq > f.seq }.sortedBy { it.seq }
            m.queued.clear()
            for (p in q) { m.lastSeq = p.seq; m.buffer.apply(p) }
            publish(m, opened = true)
        }
    }

    /** `terminal.open`: starts the shell on first use and subscribes this connection to output pushes. */
    suspend fun open(shell: String, cols: Int? = null, rows: Int? = null): Boolean {
        val m = models.getValue(shell)
        if (!connection.hasPermission(Permissions.TERMINAL)) return false
        synchronized(m) { m.opened = false; m.lastSeq = -1; m.queued.clear() }
        val reply = connection.requestOrReport("terminal.open", shellPayload(shell, cols, rows)) ?: return false
        adopt(m, reply)
        return true
    }

    /** `terminal.restart` for an ended session. */
    suspend fun restart(shell: String, cols: Int? = null, rows: Int? = null) {
        val m = models.getValue(shell)
        synchronized(m) { m.opened = false; m.lastSeq = -1; m.queued.clear() }
        val reply = connection.requestOrReport("terminal.restart", shellPayload(shell, cols, rows))
        if (reply != null) adopt(m, reply)
    }

    /** Unsubscribes from pushes (screen left). The shell keeps running on the PC. */
    suspend fun close(shell: String) {
        val m = models.getValue(shell)
        synchronized(m) { m.opened = false; m.queued.clear() }
        try { connection.request("terminal.close", buildJsonObject { put("shell", JsonPrimitive(shell)) }, 5000) }
        catch (e: CancellationException) { throw e } catch (_: Exception) { }
    }

    /** Types [command] followed by Enter. An empty command just presses Enter. */
    suspend fun execute(shell: String, command: String) {
        val cmd = command.replace("\r\n", "\n").replace('\r', '\n').trimEnd('\n')
        if (cmd.isEmpty()) {
            input(shell, "\r")
            return
        }
        connection.requestOrReport("terminal.execute", buildJsonObject {
            put("shell", JsonPrimitive(shell))
            put("command", JsonPrimitive(cmd))
        })
    }

    /** Raw keystrokes / escape sequences (`"\t"`, `"\u001b"`, `"\u001b[A"`). */
    suspend fun input(shell: String, data: String) {
        connection.requestOrReport("terminal.input", buildJsonObject {
            put("shell", JsonPrimitive(shell))
            put("data", JsonPrimitive(data))
        })
    }

    suspend fun interrupt(shell: String) {
        connection.requestOrReport("terminal.interrupt", buildJsonObject { put("shell", JsonPrimitive(shell)) })
    }

    suspend fun clear(shell: String) {
        connection.requestOrReport("terminal.clear", buildJsonObject { put("shell", JsonPrimitive(shell)) })
    }

    suspend fun resize(shell: String, cols: Int, rows: Int) {
        connection.requestOrReport("terminal.resize", buildJsonObject {
            put("shell", JsonPrimitive(shell))
            put("cols", JsonPrimitive(cols.coerceIn(20, 400)))
            put("rows", JsonPrimitive(rows.coerceIn(5, 200)))
        })
    }

    private fun shellPayload(shell: String, cols: Int?, rows: Int?) = buildJsonObject {
        put("shell", JsonPrimitive(shell))
        if (cols != null) put("cols", JsonPrimitive(cols.coerceIn(20, 400)))
        if (rows != null) put("rows", JsonPrimitive(rows.coerceIn(5, 200)))
    }
}

/** Per-shell local command history with up/down navigation. */
class CommandHistory(private val max: Int = 100) {
    private val items = ArrayList<String>()
    private var cursor = 0 // == items.size when not navigating
    private var draft = ""

    fun add(cmd: String) {
        val c = cmd.trim()
        if (c.isEmpty()) return
        if (items.lastOrNull() != c) items.add(c)
        while (items.size > max) items.removeAt(0)
        cursor = items.size
        draft = ""
    }

    /** Older entry (stores the current text as draft when leaving the bottom). Returns null when there is none. */
    fun previous(current: String): String? {
        if (items.isEmpty()) return null
        if (cursor == items.size) draft = current
        if (cursor > 0) cursor--
        return items[cursor]
    }

    /** Newer entry; at the end returns the draft that was being typed. */
    fun next(): String? {
        if (cursor >= items.size) return null
        cursor++
        return if (cursor == items.size) draft else items[cursor]
    }

    val size: Int get() = items.size
}
