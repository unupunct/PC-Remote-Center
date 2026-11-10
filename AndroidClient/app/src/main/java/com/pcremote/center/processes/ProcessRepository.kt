package com.pcremote.center.processes

import com.pcremote.center.core.Permissions
import com.pcremote.center.core.ServerError
import com.pcremote.center.core.arr
import com.pcremote.center.core.dbl
import com.pcremote.center.core.int
import com.pcremote.center.core.str
import com.pcremote.center.networking.ConnectionManager
import com.pcremote.center.networking.Errors
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

/** One row of the PC's process list. [cpu] is null when Windows does not allow reading it (protected process). */
data class ProcRow(val pid: Int, val name: String, val cpu: Double?, val memoryMb: Double, val threads: Int, val window: String?)

data class ProcList(val rows: List<ProcRow>, val total: Int)

sealed interface KillResult {
    data class Ended(val name: String) : KillResult
    data class Failed(val message: String) : KillResult
}

/** `process.list` / `process.kill` (see protocol-spec.md). Killing needs the `admin` permission and is confirmed by the user first. */
class ProcessRepository(private val connection: ConnectionManager) {
    companion object {
        val SORTS = listOf("memory", "cpu", "name")

        fun parse(p: JsonObject): ProcList {
            val rows = p.arr("processes")?.mapNotNull { el ->
                val o = el as? JsonObject ?: return@mapNotNull null
                val pid = o.int("pid") ?: return@mapNotNull null
                ProcRow(pid, o.str("name") ?: "?", o.dbl("cpu"), o.dbl("memoryMb") ?: 0.0, o.int("threads") ?: 0, o.str("window"))
            } ?: emptyList()
            return ProcList(rows, p.int("total") ?: rows.size)
        }
    }

    val canList get() = connection.hasPermission(Permissions.MONITORING)
    val canKill get() = connection.hasPermission(Permissions.ADMIN)

    /** Returns null (and reports nothing) when cancelled; throws no checked errors - failures come back as a message. */
    suspend fun list(sort: String, filter: String): Result<ProcList> = try {
        val p = connection.request("process.list", buildJsonObject {
            put("sort", JsonPrimitive(sort))
            put("limit", JsonPrimitive(120))
            if (filter.isNotBlank()) put("filter", JsonPrimitive(filter.trim()))
        })
        Result.success(parse(p))
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        Result.failure(IllegalStateException(Errors.describe(e), e))
    }

    suspend fun kill(pid: Int, tree: Boolean): KillResult {
        if (!canKill) return KillResult.Failed("Ending processes needs the \"admin\" permission. Ask the PC owner to grant it to this device.")
        return try {
            val p = connection.request("process.kill", buildJsonObject {
                put("pid", JsonPrimitive(pid))
                put("tree", JsonPrimitive(tree))
                put("confirmed", JsonPrimitive(true)) // the user confirmed in the dialog
            })
            KillResult.Ended(p.str("name") ?: "process")
        } catch (e: CancellationException) {
            throw e
        } catch (e: ServerError) {
            KillResult.Failed(Errors.describe(e))
        } catch (e: Exception) {
            KillResult.Failed(Errors.describe(e))
        }
    }
}
