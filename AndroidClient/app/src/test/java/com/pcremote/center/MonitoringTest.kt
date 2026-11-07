package com.pcremote.center

import com.pcremote.center.core.AppJson
import com.pcremote.center.core.Format
import com.pcremote.center.core.Level
import com.pcremote.center.core.Thresholds
import com.pcremote.center.monitoring.History
import com.pcremote.center.monitoring.Snapshot
import com.pcremote.center.shortcuts.Shortcut
import kotlinx.serialization.json.JsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MonitoringTest {
    private fun parse(json: String) = Snapshot.parse(AppJson.parseToJsonElement(json) as JsonObject)

    // ---- thresholds ----

    @Test
    fun defaultThresholdColouring() {
        assertEquals(Level.Normal, Thresholds.levelFor(0.0))
        assertEquals(Level.Normal, Thresholds.levelFor(79.9))
        assertEquals(Level.Warning, Thresholds.levelFor(80.0))
        assertEquals(Level.Warning, Thresholds.levelFor(89.9))
        assertEquals(Level.Critical, Thresholds.levelFor(90.0))
        assertEquals(Level.Critical, Thresholds.levelFor(100.0))
    }

    @Test
    fun customThresholdsAndMissingValues() {
        assertEquals(Level.Normal, Thresholds.levelFor(60.0, amber = 70, red = 85))
        assertEquals(Level.Warning, Thresholds.levelFor(70.0, amber = 70, red = 85))
        assertEquals(Level.Critical, Thresholds.levelFor(85.0, amber = 70, red = 85))
        assertEquals(Level.Normal, Thresholds.levelFor(null))
    }

    // ---- snapshot parsing: omitted metrics stay null ----

    @Test
    fun parsesFullSnapshot() {
        val s = parse(
            """{"timeUtc":"2026-01-01T00:00:00Z","uptimeSeconds":123,
            "cpu":{"model":"Ryzen","usage":12.5,"cores":[1,2.5,3],"logicalCores":8,"physicalCores":4,"frequencyMhz":3400,"temperatureC":55.0},
            "gpus":[{"name":"RTX","vendor":"NVIDIA","usage":7,"vramUsedMb":3439,"vramTotalMb":8188,"vramPercent":42,"temperatureC":50}],
            "ram":{"totalBytes":17179869184,"usedBytes":8589934592,"availableBytes":8589934592,"percent":50},
            "disks":[{"id":"C:","label":"System","fileSystem":"NTFS","model":"SSD","totalBytes":1000,"usedBytes":330,"freeBytes":670,"percent":33,"readBps":0,"writeBps":10,"temperatureC":35}]}"""
        )
        assertEquals(12.5, s.cpu!!.usage!!, 0.0)
        assertEquals(listOf(1.0, 2.5, 3.0), s.cpu!!.cores)
        assertEquals(55.0, s.cpu!!.temperatureC!!, 0.0)
        assertEquals(42.0, s.gpus[0].vramPct!!, 0.0)
        assertEquals(17179869184L, s.ram!!.totalBytes)
        assertEquals("C:", s.disks[0].id)
        assertEquals(10.0, s.disks[0].writeBps!!, 0.0)
        assertEquals(123L, s.uptimeSeconds)
    }

    @Test
    fun omittedMetricsAreNullAndRenderNa() {
        val s = parse("""{"cpu":{"model":"X","logicalCores":4},"gpus":[{"name":"iGPU"}],"disks":[{"id":"D:"}]}""")
        assertNull(s.cpu!!.usage)
        assertNull(s.cpu!!.temperatureC)
        assertNull(s.cpu!!.frequencyMhz)
        assertNull(s.ram)
        assertNull(s.gpus[0].usage)
        assertNull(s.gpus[0].vramPct)
        assertNull(s.disks[0].readBps)
        assertEquals("n/a", Format.percent(s.cpu!!.usage))
        assertEquals("n/a", Format.temperature(s.cpu!!.temperatureC))
        assertEquals("n/a", Format.bytes(s.ram?.usedBytes))
        assertEquals("n/a", Format.speed(s.disks[0].readBps))
        assertEquals("n/a", Format.mhz(null))
    }

    @Test
    fun vramPercentIsDerivedOnlyFromRealValues() {
        val s = parse("""{"gpus":[{"vramUsedMb":2048,"vramTotalMb":8192}]}""")
        assertEquals(25.0, s.gpus[0].vramPct!!, 0.001)
    }

    @Test
    fun emptyObjectParses() {
        val s = parse("{}")
        assertNull(s.cpu)
        assertTrue(s.gpus.isEmpty() && s.disks.isEmpty())
    }

    // ---- history ----

    @Test
    fun historyKeepsOnlyTheWindow() {
        val snap = parse("""{"cpu":{"usage":10}}""")
        var list = emptyList<com.pcremote.center.monitoring.Sample>()
        for (i in 0..400) list = History.append(list, History.sampleOf(snap, i * 1500L), 300)
        // 300 s window at 1.5 s per sample -> 201 samples (inclusive of both ends)
        assertEquals(201, list.size)
        assertEquals(400 * 1500L, list.last().timeMs)
        assertTrue(list.first().timeMs >= 400 * 1500L - 300_000)
        assertNotNull(list.first().cpu)
        assertNull(list.first().gpu)
    }

    // ---- formatting ----

    @Test
    fun formatting() {
        assertEquals("0 B", Format.bytes(0))
        assertEquals("1.0 KB", Format.bytes(1024))
        assertEquals("16.0 GB", Format.bytes(16L * 1024 * 1024 * 1024))
        assertEquals("3.40 GHz", Format.mhz(3400.0))
        assertEquals("0:05", Format.countdown(5))
        assertEquals("2:00", Format.countdown(120))
        assertEquals("1d 2h 3m", Format.duration(86400 + 7200 + 180))
    }

    // ---- shortcuts ----

    @Test
    fun shortcutJsonRoundTrip() {
        val s = Shortcut("id1", "Task Manager", "desc", "monitor", "taskmgr.exe", "/7", "C:\\", "minimized", true)
        val back = Shortcut.parse(s.toJson())
        assertEquals(s, back)
        // no id -> field omitted so the server treats it as an add
        assertTrue(!Shortcut(name = "n").toJson().containsKey("id"))
    }
}
