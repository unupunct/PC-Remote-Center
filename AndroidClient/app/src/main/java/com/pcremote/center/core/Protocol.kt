package com.pcremote.center.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.longOrNull
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

/** Shared JSON instance (tolerant to unknown keys - the server may add fields). */
val AppJson: Json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

/** One decoded wire message. */
data class Envelope(
    val type: String,
    val requestId: String?,
    val payload: JsonObject,
    val version: Int = Protocol.VERSION,
) {
    val isReply: Boolean get() = type == "response" || type == "error"
}

/** Error reply from the server (`type":"error"`), or a local protocol failure. */
open class ServerError(val code: String, message: String) : Exception(message)

/** There is no live authenticated connection to send on. */
class NotConnectedException(message: String = "Not connected to the PC") : Exception(message)

/** The server did not answer within the timeout. */
class RequestTimeoutException(val type: String) : Exception("The PC did not answer in time ($type)")

object Protocol {
    const val VERSION = 1
    const val DEFAULT_PORT = 47821
    const val DISCOVERY_PORT = 47822
    const val DISCOVERY_PROBE = "PCRC_DISCOVER_V1"
    const val MAX_MESSAGE_BYTES = 256 * 1024

    /** Parses a text frame. Returns null for anything that is not a JSON object with a string `type`. */
    fun parse(text: String): Envelope? {
        return try {
            val root = AppJson.parseToJsonElement(text) as? JsonObject ?: return null
            val type = (root["type"] as? JsonPrimitive)?.contentOrNull ?: return null
            val reqId = (root["requestId"] as? JsonPrimitive)?.contentOrNull
            val payload = root["payload"] as? JsonObject ?: JsonObject(emptyMap())
            val version = (root["version"] as? JsonPrimitive)?.longOrNull?.toInt() ?: VERSION
            Envelope(type, reqId, payload, version)
        } catch (_: Exception) {
            null
        }
    }

    /** Serialises a request envelope. */
    fun build(type: String, requestId: String?, payload: JsonObject = JsonObject(emptyMap())): String {
        val obj = buildJsonObject {
            put("version", JsonPrimitive(VERSION))
            put("type", JsonPrimitive(type))
            if (requestId != null) put("requestId", JsonPrimitive(requestId))
            put("payload", payload)
        }
        return obj.toString()
    }
}

/** Returns the payload of a response or throws [ServerError] for an `error` reply. */
fun Envelope.payloadOrThrow(): JsonObject {
    if (type == "error") {
        throw ServerError(payload.str("code") ?: "error", payload.str("message") ?: "The PC reported an error.")
    }
    return payload
}

/**
 * Request/response correlation. Every request gets an id that is unique for the lifetime of one connection
 * (the server rejects duplicates); replies complete the matching pending request.
 */
class RequestTracker(private val prefix: String = "r") {
    private val counter = AtomicLong(0)
    private val pending = ConcurrentHashMap<String, CompletableDeferred<Envelope>>()

    val pendingCount: Int get() = pending.size

    fun nextId(): String = "$prefix${counter.incrementAndGet()}"

    fun register(id: String): CompletableDeferred<Envelope> {
        val d = CompletableDeferred<Envelope>()
        pending[id] = d
        return d
    }

    fun cancel(id: String) {
        pending.remove(id)
    }

    /** Completes the pending request for a reply. Returns true when the message was consumed. */
    fun handle(env: Envelope): Boolean {
        val id = env.requestId ?: return false
        if (!env.isReply) return false
        val d = pending.remove(id) ?: return false
        d.complete(env)
        return true
    }

    /** Fails every outstanding request (connection lost). */
    fun failAll(cause: Throwable) {
        val ids = pending.keys.toList()
        for (id in ids) pending.remove(id)?.completeExceptionally(cause)
    }
}

// ---- tolerant JSON accessors (missing / null / wrong type -> null; the server omits unknown metrics) ----

private fun JsonObject.prim(key: String): JsonPrimitive? = (this[key] as? JsonPrimitive)?.takeIf { it !is JsonNull }

fun JsonObject.str(key: String): String? = prim(key)?.contentOrNull
fun JsonObject.dbl(key: String): Double? = prim(key)?.doubleOrNull
fun JsonObject.lng(key: String): Long? = prim(key)?.let { it.longOrNull ?: it.doubleOrNull?.toLong() }
fun JsonObject.int(key: String): Int? = lng(key)?.toInt()
fun JsonObject.bool(key: String): Boolean? = prim(key)?.booleanOrNull
fun JsonObject.obj(key: String): JsonObject? = this[key] as? JsonObject
fun JsonObject.arr(key: String): JsonArray? = this[key] as? JsonArray
fun JsonObject.strList(key: String): List<String> = arr(key)?.mapNotNull { (it as? JsonPrimitive)?.contentOrNull } ?: emptyList()

fun JsonElement.asDouble(): Double? = (this as? JsonPrimitive)?.takeIf { it !is JsonNull }?.doubleOrNull
