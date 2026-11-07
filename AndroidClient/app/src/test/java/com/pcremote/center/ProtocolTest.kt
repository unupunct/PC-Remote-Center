package com.pcremote.center

import com.pcremote.center.core.Protocol
import com.pcremote.center.core.RequestTracker
import com.pcremote.center.core.ServerError
import com.pcremote.center.core.arr
import com.pcremote.center.core.dbl
import com.pcremote.center.core.int
import com.pcremote.center.core.payloadOrThrow
import com.pcremote.center.core.str
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class ProtocolTest {
    @Test
    fun parsesResponseEnvelope() {
        val e = Protocol.parse("""{"version":1,"type":"response","requestId":"r7","payload":{"serverTimeUtc":"2026-01-01T00:00:00Z"}}""")
        assertNotNull(e)
        assertEquals("response", e!!.type)
        assertEquals("r7", e.requestId)
        assertEquals("2026-01-01T00:00:00Z", e.payload.str("serverTimeUtc"))
        assertTrue(e.isReply)
    }

    @Test
    fun parsesPushWithoutRequestId() {
        val e = Protocol.parse("""{"version":1,"type":"monitor.snapshot","payload":{"snapshot":{"cpu":{"usage":12.5}}}}""")!!
        assertNull(e.requestId)
        assertFalse(e.isReply)
        assertEquals("monitor.snapshot", e.type)
    }

    @Test
    fun missingPayloadBecomesEmptyObject() {
        val e = Protocol.parse("""{"type":"session.expired"}""")!!
        assertTrue(e.payload.isEmpty())
    }

    @Test
    fun rejectsGarbage() {
        assertNull(Protocol.parse("not json"))
        assertNull(Protocol.parse("[1,2,3]"))
        assertNull(Protocol.parse("""{"payload":{}}"""))
        assertNull(Protocol.parse(""))
    }

    @Test
    fun buildProducesEnvelopeV1() {
        val text = Protocol.build("terminal.execute", "r1", buildJsonObject {
            put("shell", JsonPrimitive("powershell"))
            put("command", JsonPrimitive("Get-Process"))
        })
        val back = Protocol.parse(text)!!
        assertEquals("terminal.execute", back.type)
        assertEquals("r1", back.requestId)
        assertEquals("powershell", back.payload.str("shell"))
        assertEquals(1, back.version)
    }

    @Test
    fun tolerantAccessorsReturnNullForMissingOrWrongTypes() {
        val o = Protocol.parse("""{"type":"x","payload":{"a":1,"b":"s","c":null,"d":2.5,"list":[1,2]}}""")!!.payload
        assertEquals(1, o.int("a"))
        assertNull(o.int("missing"))
        assertNull(o.str("c"))
        assertNull(o.dbl("b"))
        assertEquals(2.5, o.dbl("d")!!, 0.0)
        assertEquals(2, o.arr("list")!!.size)
    }

    @Test
    fun requestIdsAreUnique() {
        val t = RequestTracker()
        val ids = (1..1000).map { t.nextId() }
        assertEquals(1000, ids.toSet().size)
        assertTrue(ids.all { it.length <= 64 })
    }

    @Test
    fun responseCompletesMatchingRequest() = runBlocking {
        val t = RequestTracker()
        val a = t.nextId()
        val b = t.nextId()
        val da = t.register(a)
        val db = t.register(b)
        assertEquals(2, t.pendingCount)
        assertTrue(t.handle(Protocol.parse("""{"type":"response","requestId":"$b","payload":{"v":2}}""")!!))
        assertFalse(da.isCompleted)
        assertEquals(2, withTimeout(1000) { db.await() }.payload.int("v"))
        assertTrue(t.handle(Protocol.parse("""{"type":"response","requestId":"$a","payload":{"v":1}}""")!!))
        assertEquals(1, withTimeout(1000) { da.await() }.payload.int("v"))
        assertEquals(0, t.pendingCount)
    }

    @Test
    fun unknownOrPushMessagesAreNotConsumed() {
        val t = RequestTracker()
        t.register("r1")
        assertFalse(t.handle(Protocol.parse("""{"type":"response","requestId":"zzz","payload":{}}""")!!))
        // a push that happens to carry a requestId must not complete a request
        assertFalse(t.handle(Protocol.parse("""{"type":"terminal.output","requestId":"r1","payload":{}}""")!!))
        assertFalse(t.handle(Protocol.parse("""{"type":"response","payload":{}}""")!!))
        assertEquals(1, t.pendingCount)
    }

    @Test
    fun errorReplyBecomesServerError() = runBlocking {
        val t = RequestTracker()
        val d = t.register("r1")
        t.handle(Protocol.parse("""{"type":"error","requestId":"r1","payload":{"code":"forbidden","message":"No permission"}}""")!!)
        val env = withTimeout(1000) { d.await() }
        try {
            env.payloadOrThrow()
            fail("expected ServerError")
        } catch (e: ServerError) {
            assertEquals("forbidden", e.code)
            assertEquals("No permission", e.message)
        }
    }

    @Test
    fun failAllCompletesPendingExceptionally() = runBlocking {
        val t = RequestTracker()
        val d = t.register("r1")
        t.failAll(IllegalStateException("closed"))
        try {
            withTimeout(1000) { d.await() }
            fail("expected exception")
        } catch (e: IllegalStateException) {
            assertEquals("closed", e.message)
        }
        assertEquals(0, t.pendingCount)
    }

    @Test
    fun payloadOrThrowReturnsPayloadForResponse() {
        val e = Protocol.parse("""{"type":"response","requestId":"r1","payload":{"ok":true}}""")!!
        assertEquals(JsonObject::class, e.payloadOrThrow()::class)
    }
}
