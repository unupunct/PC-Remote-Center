package com.pcremote.center.monitoring

import com.pcremote.center.core.arr
import com.pcremote.center.core.asDouble
import com.pcremote.center.core.dbl
import com.pcremote.center.core.int
import com.pcremote.center.core.lng
import com.pcremote.center.core.obj
import com.pcremote.center.core.str
import kotlinx.serialization.json.JsonObject

/** All metric fields are nullable: the server omits anything the hardware does not expose and the UI shows "n/a". */
data class CpuInfo(
    val model: String?,
    val usage: Double?,
    val cores: List<Double>,
    val logicalCores: Int?,
    val physicalCores: Int?,
    val frequencyMhz: Double?,
    val temperatureC: Double?,
)

data class GpuInfo(
    val name: String?,
    val vendor: String?,
    val usage: Double?,
    val vramUsedMb: Double?,
    val vramTotalMb: Double?,
    val vramPercent: Double?,
    val temperatureC: Double?,
) {
    /** VRAM percentage from the server value, or derived from used/total when only those are present. */
    val vramPct: Double?
        get() = vramPercent ?: if (vramUsedMb != null && vramTotalMb != null && vramTotalMb > 0) vramUsedMb / vramTotalMb * 100 else null
}

data class RamInfo(val totalBytes: Long?, val usedBytes: Long?, val availableBytes: Long?, val percent: Double?)

data class DiskInfo(
    val id: String,
    val label: String?,
    val fileSystem: String?,
    val model: String?,
    val totalBytes: Long?,
    val usedBytes: Long?,
    val freeBytes: Long?,
    val percent: Double?,
    val readBps: Double?,
    val writeBps: Double?,
    val temperatureC: Double?,
)

data class Snapshot(
    val timeUtc: String?,
    val uptimeSeconds: Long?,
    val cpu: CpuInfo?,
    val gpus: List<GpuInfo>,
    val ram: RamInfo?,
    val disks: List<DiskInfo>,
) {
    companion object {
        fun parse(o: JsonObject): Snapshot = Snapshot(
            timeUtc = o.str("timeUtc"),
            uptimeSeconds = o.lng("uptimeSeconds"),
            cpu = o.obj("cpu")?.let { c ->
                CpuInfo(
                    model = c.str("model"),
                    usage = c.dbl("usage"),
                    cores = c.arr("cores")?.mapNotNull { it.asDouble() } ?: emptyList(),
                    logicalCores = c.int("logicalCores"),
                    physicalCores = c.int("physicalCores"),
                    frequencyMhz = c.dbl("frequencyMhz"),
                    temperatureC = c.dbl("temperatureC"),
                )
            },
            gpus = o.arr("gpus")?.mapNotNull { (it as? JsonObject)?.let { g ->
                GpuInfo(
                    name = g.str("name"), vendor = g.str("vendor"), usage = g.dbl("usage"),
                    vramUsedMb = g.dbl("vramUsedMb"), vramTotalMb = g.dbl("vramTotalMb"),
                    vramPercent = g.dbl("vramPercent"), temperatureC = g.dbl("temperatureC"),
                )
            } } ?: emptyList(),
            ram = o.obj("ram")?.let { r ->
                RamInfo(r.lng("totalBytes"), r.lng("usedBytes"), r.lng("availableBytes"), r.dbl("percent"))
            },
            disks = o.arr("disks")?.mapNotNull { (it as? JsonObject)?.let { d ->
                DiskInfo(
                    id = d.str("id") ?: "?", label = d.str("label"), fileSystem = d.str("fileSystem"), model = d.str("model"),
                    totalBytes = d.lng("totalBytes"), usedBytes = d.lng("usedBytes"), freeBytes = d.lng("freeBytes"),
                    percent = d.dbl("percent"), readBps = d.dbl("readBps"), writeBps = d.dbl("writeBps"),
                    temperatureC = d.dbl("temperatureC"),
                )
            } } ?: emptyList(),
        )
    }
}

/** One point of the in-memory history (about five minutes by default). Percentages; null = not reported. */
data class Sample(
    val timeMs: Long,
    val cpu: Float?,
    val gpu: Float?,
    val vram: Float?,
    val ram: Float?,
)

object History {
    fun sampleOf(s: Snapshot, nowMs: Long): Sample = Sample(
        timeMs = nowMs,
        cpu = s.cpu?.usage?.toFloat(),
        gpu = s.gpus.firstOrNull()?.usage?.toFloat(),
        vram = s.gpus.firstOrNull()?.vramPct?.toFloat(),
        ram = s.ram?.percent?.toFloat(),
    )

    /** Appends a sample and drops those older than [windowSeconds]. */
    fun append(list: List<Sample>, sample: Sample, windowSeconds: Int): List<Sample> {
        val cutoff = sample.timeMs - windowSeconds * 1000L
        val out = ArrayList<Sample>(list.size + 1)
        for (x in list) if (x.timeMs >= cutoff) out.add(x)
        out.add(sample)
        return out
    }
}
