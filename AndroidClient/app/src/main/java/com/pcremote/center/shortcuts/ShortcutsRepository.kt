package com.pcremote.center.shortcuts

import com.pcremote.center.core.Permissions
import com.pcremote.center.core.ServerError
import com.pcremote.center.core.arr
import com.pcremote.center.core.bool
import com.pcremote.center.core.obj
import com.pcremote.center.core.str
import com.pcremote.center.networking.ConnectionManager
import com.pcremote.center.networking.Errors
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

data class Shortcut(
    val id: String = "",
    val name: String = "",
    val description: String = "",
    val icon: String = "apps",
    val path: String = "",
    val arguments: String = "",
    val workingDirectory: String = "",
    val runMode: String = "normal",
    val confirm: Boolean = false,
) {
    fun toJson(): JsonObject = buildJsonObject {
        if (id.isNotEmpty()) put("id", JsonPrimitive(id))
        put("name", JsonPrimitive(name))
        put("description", JsonPrimitive(description))
        put("icon", JsonPrimitive(icon))
        put("path", JsonPrimitive(path))
        put("arguments", JsonPrimitive(arguments))
        put("workingDirectory", JsonPrimitive(workingDirectory))
        put("runMode", JsonPrimitive(runMode))
        put("confirm", JsonPrimitive(confirm))
    }

    companion object {
        val ICONS = listOf("apps", "monitor", "memory", "settings", "list", "network", "chart", "web", "code", "build")
        val RUN_MODES = listOf("normal", "minimized", "maximized", "hidden", "elevated")

        fun parse(o: JsonObject) = Shortcut(
            id = o.str("id") ?: "",
            name = o.str("name") ?: "",
            description = o.str("description") ?: "",
            icon = o.str("icon") ?: "apps",
            path = o.str("path") ?: "",
            arguments = o.str("arguments") ?: "",
            workingDirectory = o.str("workingDirectory") ?: "",
            runMode = o.str("runMode") ?: "normal",
            confirm = o.bool("confirm") ?: false,
        )
    }
}

sealed interface RunResult {
    data class Launched(val name: String) : RunResult
    /** The shortcut requires an explicit confirmation; show a dialog and call again with confirmed = true. */
    data object NeedsConfirmation : RunResult
    data class Failed(val message: String) : RunResult
}

/** Outcome of save/delete/reorder. [message] is the server's text (e.g. `invalid_shortcut` validation message). */
data class EditResult(val ok: Boolean, val message: String? = null)

class ShortcutsRepository(
    private val connection: ConnectionManager,
    scope: CoroutineScope,
) {
    private val _shortcuts = MutableStateFlow<List<Shortcut>>(emptyList())
    val shortcuts: StateFlow<List<Shortcut>> = _shortcuts.asStateFlow()

    private val _elevatedAllowed = MutableStateFlow(false)
    val elevatedAllowed: StateFlow<Boolean> = _elevatedAllowed.asStateFlow()

    private val _loaded = MutableStateFlow(false)
    val loaded: StateFlow<Boolean> = _loaded.asStateFlow()

    init {
        scope.launch {
            connection.sessionId.collect { sid ->
                if (sid == 0) return@collect
                _loaded.value = false
                refresh(silent = true)
            }
        }
        // permission change (auth.permissions push) -> reload / clear
        scope.launch {
            var prev = false
            connection.permissions.collect { perms ->
                val has = Permissions.LAUNCH in perms
                if (has && !prev) refresh(silent = true)
                if (!has) { _shortcuts.value = emptyList(); _loaded.value = false }
                prev = has
            }
        }
    }

    suspend fun refresh(silent: Boolean = false) {
        if (!connection.hasPermission(Permissions.LAUNCH)) return
        try {
            val p = connection.request("shortcuts.list")
            _shortcuts.value = p.arr("shortcuts")?.mapNotNull { (it as? JsonObject)?.let(Shortcut::parse) } ?: emptyList()
            _elevatedAllowed.value = p.bool("elevatedAllowed") ?: false
            _loaded.value = true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (!silent) connection.report(e)
        }
    }

    suspend fun run(id: String, confirmed: Boolean): RunResult {
        return try {
            val p = connection.request("shortcuts.run", buildJsonObject {
                put("id", JsonPrimitive(id))
                if (confirmed) put("confirmed", JsonPrimitive(true))
            })
            RunResult.Launched(p.str("name") ?: _shortcuts.value.firstOrNull { it.id == id }?.name ?: "Shortcut")
        } catch (e: CancellationException) {
            throw e
        } catch (e: ServerError) {
            if (e.code == "confirmation_required") RunResult.NeedsConfirmation else RunResult.Failed(Errors.describe(e))
        } catch (e: Exception) {
            RunResult.Failed(Errors.describe(e))
        }
    }

    suspend fun save(s: Shortcut): EditResult = edit {
        connection.request("shortcuts.save", buildJsonObject { put("shortcut", s.toJson()) })
    }

    suspend fun delete(id: String): EditResult = edit {
        connection.request("shortcuts.delete", buildJsonObject { put("id", JsonPrimitive(id)) })
    }

    suspend fun reorder(ids: List<String>): EditResult = edit {
        connection.request("shortcuts.reorder", buildJsonObject {
            put("ids", JsonArray(ids.map { JsonPrimitive(it) }))
        })
    }

    private suspend fun edit(block: suspend () -> JsonObject): EditResult {
        if (!connection.hasPermission(Permissions.ADMIN)) {
            return EditResult(false, "Editing shortcuts needs the \"admin\" permission. Ask the PC owner to grant it to this device.")
        }
        return try {
            block()
            refresh(silent = true)
            EditResult(true)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            EditResult(false, Errors.describe(e))
        }
    }

    /** Updates the order locally (optimistic) so the editor can show moves immediately. */
    fun localOrder(ids: List<String>) {
        val byId = _shortcuts.value.associateBy { it.id }
        _shortcuts.value = ids.mapNotNull { byId[it] }
    }
}
