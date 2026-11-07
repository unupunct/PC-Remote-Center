package com.pcremote.center.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.pcremote.center.PcRemoteApp
import com.pcremote.center.core.Permissions
import com.pcremote.center.shortcuts.EditResult
import com.pcremote.center.shortcuts.RunResult
import com.pcremote.center.shortcuts.Shortcut
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

class ShortcutsViewModel(app: Application) : AndroidViewModel(app) {
    private val container = (app as PcRemoteApp).container
    private val repo = container.shortcuts
    private val connection = container.connection

    val shortcuts = repo.shortcuts
    val loaded = repo.loaded
    val elevatedAllowed = repo.elevatedAllowed
    val permissions = connection.permissions
    val status = connection.status

    private val _confirm = MutableStateFlow<Shortcut?>(null)
    /** Shortcut waiting for the user's confirmation before it is run with `confirmed=true`. */
    val confirm: StateFlow<Shortcut?> = _confirm.asStateFlow()

    private val _running = MutableStateFlow<String?>(null)
    val running: StateFlow<String?> = _running.asStateFlow()

    fun refresh() { viewModelScope.launch { repo.refresh() } }

    fun tap(s: Shortcut) {
        if (s.confirm) _confirm.value = s else run(s, confirmed = false)
    }

    fun dismissConfirm() { _confirm.value = null }

    fun runConfirmed(s: Shortcut) {
        _confirm.value = null
        run(s, confirmed = true)
    }

    private fun run(s: Shortcut, confirmed: Boolean) {
        viewModelScope.launch {
            _running.value = s.id
            try {
                when (val r = repo.run(s.id, confirmed)) {
                    is RunResult.Launched -> connection.notify("Launched ${r.name} on the PC.")
                    RunResult.NeedsConfirmation -> _confirm.value = s
                    is RunResult.Failed -> connection.notify(r.message, isError = true)
                }
            } finally {
                _running.value = null
            }
        }
    }

    fun hasAdmin(perms: Set<String>) = Permissions.ADMIN in perms

    fun tell(text: String) = connection.notify(text, isError = true)

    /** Saves (add when id is empty). [onDone] receives the server's message on failure. */
    fun save(s: Shortcut, onDone: (EditResult) -> Unit) {
        viewModelScope.launch {
            val r = repo.save(s)
            if (r.ok) connection.notify("Shortcut saved.")
            onDone(r)
        }
    }

    fun delete(s: Shortcut) {
        viewModelScope.launch {
            val r = repo.delete(s.id)
            if (r.ok) connection.notify("Deleted ${s.name}.") else connection.notify(r.message ?: "Could not delete.", isError = true)
        }
    }

    fun move(s: Shortcut, delta: Int) {
        val ids = shortcuts.value.map { it.id }.toMutableList()
        val i = ids.indexOf(s.id)
        val j = i + delta
        if (i < 0 || j !in ids.indices) return
        ids.removeAt(i)
        ids.add(j, s.id)
        repo.localOrder(ids)
        viewModelScope.launch {
            val r = repo.reorder(ids)
            if (!r.ok) {
                connection.notify(r.message ?: "Could not reorder.", isError = true)
                repo.refresh(silent = true)
            }
        }
    }
}
