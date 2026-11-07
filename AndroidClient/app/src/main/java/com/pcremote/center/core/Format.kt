package com.pcremote.center.core

import java.util.Locale

/** Display formatting helpers. A null input always renders as "n/a" - values are never invented. */
object Format {
    const val NA = "n/a"

    fun percent(v: Double?): String = if (v == null) NA else String.format(Locale.US, "%.0f%%", v)

    fun bytes(v: Long?): String {
        if (v == null) return NA
        val units = arrayOf("B", "KB", "MB", "GB", "TB", "PB")
        var d = v.toDouble()
        var i = 0
        while (d >= 1024 && i < units.size - 1) { d /= 1024; i++ }
        return if (i == 0) "$v B" else String.format(Locale.US, if (d >= 100) "%.0f %s" else "%.1f %s", d, units[i])
    }

    fun megabytes(mb: Double?): String = if (mb == null) NA else bytes((mb * 1024 * 1024).toLong())

    fun speed(bps: Double?): String = if (bps == null) NA else bytes(bps.toLong()) + "/s"

    fun temperature(c: Double?): String = if (c == null) NA else String.format(Locale.US, "%.0f°C", c)

    fun mhz(v: Double?): String = when {
        v == null -> NA
        v >= 1000 -> String.format(Locale.US, "%.2f GHz", v / 1000)
        else -> String.format(Locale.US, "%.0f MHz", v)
    }

    fun duration(totalSeconds: Long): String {
        val s = totalSeconds.coerceAtLeast(0)
        val d = s / 86400
        val h = (s % 86400) / 3600
        val m = (s % 3600) / 60
        val sec = s % 60
        return when {
            d > 0 -> "${d}d ${h}h ${m}m"
            h > 0 -> "${h}h ${m}m ${sec}s"
            m > 0 -> "${m}m ${sec}s"
            else -> "${sec}s"
        }
    }

    fun countdown(seconds: Long): String {
        val s = seconds.coerceAtLeast(0)
        return String.format(Locale.US, "%d:%02d", s / 60, s % 60)
    }
}
