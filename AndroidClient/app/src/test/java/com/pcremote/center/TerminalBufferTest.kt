package com.pcremote.center

import com.pcremote.center.core.AppJson
import com.pcremote.center.terminal.CommandHistory
import com.pcremote.center.terminal.Cursor
import com.pcremote.center.terminal.TRow
import com.pcremote.center.terminal.TRun
import com.pcremote.center.terminal.TerminalBuffer
import com.pcremote.center.terminal.TerminalFrame
import kotlinx.serialization.json.JsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalBufferTest {
    private fun row(text: String) = TRow(listOf(TRun(text, -1, -1, 0)))

    private fun frame(
        full: Boolean = false, total: Long, start: Long = 0, from: Long = 0,
        added: List<String> = emptyList(), screen: List<String> = listOf("> "),
        running: Boolean = true, seq: Long = 0,
    ) = TerminalFrame(
        shell = "cmd", seq = seq, full = full, running = running, exitCode = null, title = null, cols = 100, rows = 30,
        cursor = Cursor(2, 0, true), alt = false, scrollbackTotal = total, scrollbackStart = start, addedFrom = from,
        added = added.map(::row), screen = screen.map(::row),
    )

    private fun lines(b: TerminalBuffer) = b.linesSnapshot().map { it.text }

    @Test
    fun fullFrameReplacesBuffer() {
        val b = TerminalBuffer()
        b.apply(frame(full = true, total = 3, added = listOf("a", "b", "c")))
        assertEquals(listOf("a", "b", "c"), lines(b))
        assertEquals(3, b.knownTotal)
        b.apply(frame(full = true, total = 2, added = listOf("x", "y")))
        assertEquals(listOf("x", "y"), lines(b))
        assertEquals(2, b.knownTotal)
    }

    @Test
    fun appendedFramesAddOnlyNewLines() {
        val b = TerminalBuffer()
        b.apply(frame(full = true, total = 2, added = listOf("a", "b")))
        b.apply(frame(total = 4, from = 2, added = listOf("c", "d")))
        assertEquals(listOf("a", "b", "c", "d"), lines(b))
        // overlapping frame: entries below the known total are skipped
        b.apply(frame(total = 5, from = 3, added = listOf("d", "e")))
        assertEquals(listOf("a", "b", "c", "d", "e"), lines(b))
        // identical frame again changes nothing
        b.apply(frame(total = 5, from = 5, added = emptyList()))
        assertEquals(5, b.size)
        assertEquals(5, b.knownTotal)
    }

    @Test
    fun frameWithoutAddedKeepsBuffer() {
        val b = TerminalBuffer()
        b.apply(frame(full = true, total = 2, added = listOf("a", "b")))
        b.apply(frame(total = 2, from = 2, added = emptyList(), screen = listOf("> dir")))
        assertEquals(listOf("a", "b"), lines(b))
        assertEquals("> dir", b.screen[0].text)
    }

    @Test
    fun clearViaScrollbackStartDropsOldLines() {
        val b = TerminalBuffer()
        b.apply(frame(full = true, total = 10, added = (0 until 10).map { "l$it" }))
        // terminal.clear: start jumps to total, nothing added
        b.apply(frame(total = 10, start = 10, from = 10, added = emptyList()))
        assertEquals(0, b.size)
        assertEquals(10, b.base)
        // new output after the clear lands at absolute index 10
        b.apply(frame(total = 11, start = 10, from = 10, added = listOf("fresh")))
        assertEquals(listOf("fresh"), lines(b))
        assertEquals(10, b.base)
    }

    @Test
    fun scrollbackLimitDropsLeadingLines() {
        val b = TerminalBuffer()
        b.apply(frame(full = true, total = 5, added = listOf("a", "b", "c", "d", "e")))
        b.apply(frame(total = 7, start = 3, from = 5, added = listOf("f", "g")))
        assertEquals(listOf("d", "e", "f", "g"), lines(b))
        assertEquals(3, b.base)
        assertEquals(7, b.knownTotal)
    }

    @Test
    fun totalDecreaseReplacesBuffer() {
        val b = TerminalBuffer()
        b.apply(frame(full = true, total = 8, added = (0 until 8).map { "o$it" }))
        // restart: server counters start over and the frame is not flagged full
        b.apply(frame(total = 2, start = 0, from = 0, added = listOf("n0", "n1")))
        assertEquals(listOf("n0", "n1"), lines(b))
        assertEquals(2, b.knownTotal)
    }

    @Test
    fun gapAfterKnownTotalReplacesBuffer() {
        val b = TerminalBuffer()
        b.apply(frame(full = true, total = 3, added = listOf("a", "b", "c")))
        b.apply(frame(total = 20, from = 15, added = listOf("p", "q", "r", "s", "t")))
        assertEquals(listOf("p", "q", "r", "s", "t"), lines(b))
        assertEquals(15, b.base)
    }

    @Test
    fun runningFlagAndPlainText() {
        val b = TerminalBuffer()
        b.apply(frame(full = true, total = 1, added = listOf("hello"), screen = listOf("C:\\> ", ""), running = true))
        assertTrue(b.running)
        assertEquals("hello\nC:\\>", b.plainText())
        b.apply(frame(total = 1, from = 1, screen = listOf("done"), running = false))
        assertFalse(b.running)
    }

    @Test
    fun parsesWireFrameWithColourRuns() {
        val json = """{"shell":"powershell","seq":7,"full":true,"running":true,"exitCode":null,"title":"t","cols":100,"rows":2,
            "cursor":{"x":3,"y":1,"visible":true},"alt":false,"scrollbackTotal":1,"scrollbackStart":0,"addedFrom":0,
            "added":[[["PS ",65280,-1,1],["C:\\>",-1,-1,0]]],
            "screen":[[],[["x",16711680,255,12]]]}"""
        val f = TerminalFrame.parse(AppJson.parseToJsonElement(json) as JsonObject)
        assertEquals("powershell", f.shell)
        assertEquals(7, f.seq)
        assertTrue(f.full)
        assertEquals(1, f.added.size)
        assertEquals(2, f.added[0].runs.size)
        assertEquals(0x00FF00, f.added[0].runs[0].fg)
        assertTrue(f.added[0].runs[0].bold)
        assertEquals(0, f.screen[0].runs.size)
        val r = f.screen[1].runs[0]
        assertEquals(0xFF0000, r.fg)
        assertEquals(255, r.bg)
        assertTrue(r.italic && r.underline && !r.bold)
        assertEquals(3, f.cursor.x)
        assertNull(f.exitCode)
        assertEquals("PS C:\\>", f.added[0].text)
    }

    @Test
    fun commandHistoryNavigation() {
        val h = CommandHistory()
        assertNull(h.previous("x"))
        h.add("dir"); h.add("cd ..") ; h.add("cd ..")
        assertEquals(2, h.size)
        assertEquals("cd ..", h.previous("typing"))
        assertEquals("dir", h.previous("typing"))
        assertEquals("dir", h.previous("typing")) // stays at the oldest
        assertEquals("cd ..", h.next())
        assertEquals("typing", h.next()) // back to the draft
        assertNull(h.next())
    }
}
