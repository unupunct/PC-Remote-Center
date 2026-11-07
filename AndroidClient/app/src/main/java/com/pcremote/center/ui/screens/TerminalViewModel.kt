package com.pcremote.center.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.pcremote.center.PcRemoteApp
import com.pcremote.center.terminal.CommandHistory
import com.pcremote.center.terminal.Shells
import com.pcremote.center.terminal.TerminalUiState
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class TerminalViewModel(app: Application) : AndroidViewModel(app) {
    private val container = (app as PcRemoteApp).container
    private val repo = container.terminal

    val status = container.connection.status
    val sessionId = container.connection.sessionId
    val permissions = container.connection.permissions

    private val histories = Shells.ALL.associateWith { CommandHistory() }

    fun state(shell: String): StateFlow<TerminalUiState> = repo.state(shell)

    /** Opens (and subscribes to) both sessions; called whenever the screen is shown and after every reconnect. */
    fun openAll() {
        for (s in Shells.ALL) viewModelScope.launch { repo.open(s) }
    }

    /** Unsubscribes from pushes when the screen is left (the shells keep running on the PC). */
    fun closeAll() {
        container.scope.launch(NonCancellable) {
            for (s in Shells.ALL) repo.close(s)
        }
    }

    fun send(shell: String, text: String) {
        histories.getValue(shell).add(text)
        viewModelScope.launch { repo.execute(shell, text) }
    }

    /** Raw keystrokes: Tab, Esc, arrow keys (server-side history) ... */
    fun key(shell: String, data: String) {
        viewModelScope.launch { repo.input(shell, data) }
    }

    fun interrupt(shell: String) { viewModelScope.launch { repo.interrupt(shell) } }
    fun clear(shell: String) { viewModelScope.launch { repo.clear(shell) } }
    fun restart(shell: String) { viewModelScope.launch { repo.restart(shell) } }
    fun resize(shell: String, cols: Int, rows: Int) { viewModelScope.launch { repo.resize(shell, cols, rows) } }

    fun historyPrevious(shell: String, current: String): String? = histories.getValue(shell).previous(current)
    fun historyNext(shell: String): String? = histories.getValue(shell).next()

    fun info(text: String) = container.connection.notify(text)
}
