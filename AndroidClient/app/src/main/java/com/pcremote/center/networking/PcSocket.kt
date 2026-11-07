package com.pcremote.center.networking

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString

/** Why a socket ended. [error] is set for transport failures, [code] for close frames. */
data class CloseInfo(val code: Int, val reason: String, val error: Throwable? = null)

/** Thin coroutine-friendly wrapper around one OkHttp WebSocket. */
class PcSocket(
    private val client: OkHttpClient,
    private val onText: (String) -> Unit,
) {
    @Volatile private var ws: WebSocket? = null
    val closed = CompletableDeferred<CloseInfo>()

    /** Opens `wss://host:port/ws`; returns when the TLS + WebSocket handshake finished (or throws). */
    suspend fun open(host: String, port: Int, timeoutMs: Long) {
        val opened = CompletableDeferred<Unit>()
        val hostPart = if (host.contains(':') && !host.startsWith("[")) "[$host]" else host
        val request = Request.Builder().url("https://$hostPart:$port/ws").build()
        val socket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                opened.complete(Unit)
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                onText(text)
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) { /* binary frames are not part of the protocol */ }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(1000, null)
                closed.complete(CloseInfo(code, reason))
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                closed.complete(CloseInfo(code, reason))
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                opened.completeExceptionally(t)
                closed.complete(CloseInfo(-1, t.message.orEmpty(), t))
            }
        })
        ws = socket
        try {
            withTimeout(timeoutMs) { opened.await() }
        } catch (e: Throwable) {
            socket.cancel()
            throw e
        }
    }

    fun send(text: String): Boolean = ws?.send(text) ?: false

    /** Polite close (close frame). */
    fun close(code: Int = 1000, reason: String = "bye") {
        try { ws?.close(code, reason) } catch (_: Exception) {}
    }

    /** Hard abort without waiting for the peer. */
    fun abort() {
        try { ws?.cancel() } catch (_: Exception) {}
    }
}
