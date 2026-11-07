package com.pcremote.center.networking

import com.pcremote.center.core.PairResult
import com.pcremote.center.core.Protocol
import com.pcremote.center.core.RequestTimeoutException
import com.pcremote.center.core.RequestTracker
import com.pcremote.center.core.payloadOrThrow
import com.pcremote.center.core.str
import com.pcremote.center.core.strList
import com.pcremote.center.security.PinnedClients
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

/** One-shot `auth.pair` handshake over a pinned TLS connection. */
object PairingClient {
    /**
     * Tries each host in turn and pairs on the first one reachable. Throws [com.pcremote.center.core.ServerError]
     * for server rejections (wrong/expired code, rate limiting) or another exception describing the failure.
     */
    suspend fun pair(
        hosts: List<String>,
        port: Int,
        fingerprint: String,
        code: String,
        deviceName: String,
        deviceModel: String?,
    ): PairResult {
        val client = PinnedClients.create(fingerprint)
        var last: Throwable? = null
        for (host in hosts) {
            val tracker = RequestTracker("p")
            val socket = PcSocket(client) { text -> Protocol.parse(text)?.let { tracker.handle(it) } }
            try {
                socket.open(host, port, 8000)
            } catch (e: CancellationException) {
                if (e is TimeoutCancellationException) { last = e; continue }
                throw e
            } catch (e: Exception) {
                last = e
                if (Errors.isFingerprintMismatch(e)) break
                continue
            }
            try {
                val id = tracker.nextId()
                val reply = tracker.register(id)
                socket.send(
                    Protocol.build(
                        "auth.pair", id,
                        buildJsonObject {
                            put("code", JsonPrimitive(code))
                            put("deviceName", JsonPrimitive(deviceName))
                            if (!deviceModel.isNullOrBlank()) put("deviceModel", JsonPrimitive(deviceModel))
                        }
                    )
                )
                val env = try {
                    withTimeout(12000) { reply.await() }
                } catch (_: TimeoutCancellationException) {
                    throw RequestTimeoutException("auth.pair")
                }
                val p = env.payloadOrThrow()
                return PairResult(
                    deviceId = p.str("deviceId") ?: throw IllegalStateException("Server reply is missing deviceId"),
                    token = p.str("token") ?: throw IllegalStateException("Server reply is missing the device token"),
                    permissions = p.strList("permissions").toSet(),
                    computerName = p.str("computerName") ?: host,
                    host = host,
                )
            } finally {
                socket.close(1000, "paired")
            }
        }
        throw last ?: IllegalStateException("No host to connect to")
    }
}
