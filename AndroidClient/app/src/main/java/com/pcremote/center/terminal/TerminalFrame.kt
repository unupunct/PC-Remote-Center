package com.pcremote.center.terminal

import com.pcremote.center.core.arr
import com.pcremote.center.core.bool
import com.pcremote.center.core.int
import com.pcremote.center.core.lng
import com.pcremote.center.core.obj
import com.pcremote.center.core.str
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.longOrNull

/** One styled text run. fg/bg are -1 (theme default) or 0xRRGGBB. flags: 1 bold, 2 dim, 4 italic, 8 underline. */
data class TRun(val text: String, val fg: Int, val bg: Int, val flags: Int) {
    val bold get() = flags and 1 != 0
    val dim get() = flags and 2 != 0
    val italic get() = flags and 4 != 0
    val underline get() = flags and 8 != 0
}

/** A rendered terminal row (empty list = blank row). */
data class TRow(val runs: List<TRun>) {
    val text: String by lazy(LazyThreadSafetyMode.NONE) { runs.joinToString("") { it.text } }

    companion object {
        val EMPTY = TRow(emptyList())
    }
}

data class Cursor(val x: Int, val y: Int, val visible: Boolean)

/** A frame as sent by `terminal.open` / `terminal.restart` replies and `terminal.output` pushes. */
data class TerminalFrame(
    val shell: String,
    val seq: Long,
    val full: Boolean,
    val running: Boolean,
    val exitCode: Int?,
    val title: String?,
    val cols: Int,
    val rows: Int,
    val cursor: Cursor,
    val alt: Boolean,
    val scrollbackTotal: Long,
    val scrollbackStart: Long,
    val addedFrom: Long,
    val added: List<TRow>,
    val screen: List<TRow>,
) {
    companion object {
        fun parseRow(el: kotlinx.serialization.json.JsonElement): TRow {
            val arr = el as? JsonArray ?: return TRow.EMPTY
            if (arr.isEmpty()) return TRow.EMPTY
            val runs = ArrayList<TRun>(arr.size)
            for (r in arr) {
                val a = r as? JsonArray ?: continue
                if (a.isEmpty()) continue
                val text = (a[0] as? JsonPrimitive)?.contentOrNull ?: ""
                val fg = ((a.getOrNull(1) as? JsonPrimitive)?.longOrNull ?: -1L).toInt()
                val bg = ((a.getOrNull(2) as? JsonPrimitive)?.longOrNull ?: -1L).toInt()
                val fl = ((a.getOrNull(3) as? JsonPrimitive)?.longOrNull ?: 0L).toInt()
                runs.add(TRun(text, fg, bg, fl))
            }
            return TRow(runs)
        }

        fun parse(o: JsonObject): TerminalFrame {
            val cur = o.obj("cursor")
            return TerminalFrame(
                shell = o.str("shell") ?: "",
                seq = o.lng("seq") ?: 0,
                full = o.bool("full") ?: false,
                running = o.bool("running") ?: true,
                exitCode = o.int("exitCode"),
                title = o.str("title"),
                cols = o.int("cols") ?: 0,
                rows = o.int("rows") ?: 0,
                cursor = Cursor(cur?.int("x") ?: 0, cur?.int("y") ?: 0, cur?.bool("visible") ?: false),
                alt = o.bool("alt") ?: false,
                scrollbackTotal = o.lng("scrollbackTotal") ?: 0,
                scrollbackStart = o.lng("scrollbackStart") ?: 0,
                addedFrom = o.lng("addedFrom") ?: 0,
                added = o.arr("added")?.map { parseRow(it) } ?: emptyList(),
                screen = o.arr("screen")?.map { parseRow(it) } ?: emptyList(),
            )
        }
    }
}
