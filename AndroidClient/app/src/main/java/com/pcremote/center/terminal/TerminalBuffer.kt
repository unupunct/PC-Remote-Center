package com.pcremote.center.terminal

/**
 * Client-side terminal model following the protocol's client algorithm:
 *
 * keep `lines` (scrollback, absolute index = position + [base]). On a frame: if `full` OR `scrollbackTotal` < known
 * total OR `addedFrom` > known total, replace the buffer with `added` (starting at index `addedFrom`); otherwise append
 * the entries of `added` whose absolute index (`addedFrom + i`) >= known total. Then drop lines with index <
 * `scrollbackStart`. Known total = `scrollbackTotal`. The visible screen is rendered after the scrollback.
 */
class TerminalBuffer {
    private val lines = ArrayList<TRow>()

    /** Absolute index of lines[0]. */
    var base: Long = 0
        private set

    var knownTotal: Long = 0
        private set

    var screen: List<TRow> = emptyList()
        private set
    var cursor: Cursor = Cursor(0, 0, false)
        private set
    var running: Boolean = true
        private set
    var exitCode: Int? = null
        private set
    var cols: Int = 0
        private set
    var rows: Int = 0
        private set
    var title: String? = null
        private set
    var alt: Boolean = false
        private set

    val size: Int get() = lines.size

    fun reset() {
        lines.clear()
        base = 0
        knownTotal = 0
        screen = emptyList()
    }

    fun apply(f: TerminalFrame) {
        val replace = f.full || f.scrollbackTotal < knownTotal || f.addedFrom > knownTotal
        if (replace) {
            lines.clear()
            lines.addAll(f.added)
            base = f.addedFrom
        } else {
            for ((i, row) in f.added.withIndex()) {
                if (f.addedFrom + i >= knownTotal) lines.add(row)
            }
        }
        // drop everything the server no longer keeps (clear / scrollback limit)
        val drop = (f.scrollbackStart - base).coerceIn(0, lines.size.toLong()).toInt()
        if (drop > 0) {
            lines.subList(0, drop).clear()
            base += drop
        }
        if (lines.isEmpty() && base < f.scrollbackStart) base = f.scrollbackStart
        knownTotal = f.scrollbackTotal

        screen = f.screen
        cursor = f.cursor
        running = f.running
        exitCode = f.exitCode
        cols = f.cols
        rows = f.rows
        title = f.title
        alt = f.alt
    }

    /** Immutable copy of the scrollback for the UI. */
    fun linesSnapshot(): List<TRow> = ArrayList(lines)

    /** Plain text of scrollback + screen with trailing blank lines removed (for the Copy action). */
    fun plainText(): String {
        val all = ArrayList<String>(lines.size + screen.size)
        for (l in lines) all.add(l.text.trimEnd())
        for (r in screen) all.add(r.text.trimEnd())
        var end = all.size
        while (end > 0 && all[end - 1].isEmpty()) end--
        return all.subList(0, end).joinToString("\n")
    }
}
