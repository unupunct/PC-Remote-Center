package com.pcremote.center

import com.pcremote.center.networking.PairUri
import com.pcremote.center.processes.ProcessRepository
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class ProcessTest {
    @Test fun parses_process_list_with_missing_cpu() {
        val json = Json.parseToJsonElement(
            """{"total":412,"sort":"memory","processes":[
                {"pid":1234,"name":"chrome","cpu":12.5,"memoryMb":812.4,"threads":38,"window":"Inbox - Mail"},
                {"pid":88,"name":"lsass","memoryMb":20.0,"threads":9},
                {"name":"no-pid"}]}""",
        ) as JsonObject
        val list = ProcessRepository.parse(json)
        assertEquals(412, list.total)
        assertEquals(2, list.rows.size) // the entry without a pid is dropped
        assertEquals(12.5, list.rows[0].cpu!!, 0.0)
        assertEquals("Inbox - Mail", list.rows[0].window)
        assertNull(list.rows[1].cpu) // never shown as 0
        assertNull(list.rows[1].window)
    }

    @Test fun empty_payload_is_an_empty_list() {
        val list = ProcessRepository.parse(Json.parseToJsonElement("{}") as JsonObject)
        assertEquals(0, list.rows.size)
        assertEquals(0, list.total)
    }

    /** The exact link format the server builds (checked by a decoder test on the PC side). */
    @Test fun pairing_link_from_the_server_parses() {
        val fp = "A".repeat(64)
        val link = "pcrc://pair?v=1&h=192.168.1.9%2C100.77.104.0&p=47821&c=12345678&f=$fp&n=DESKTOP-4BL01TS"
        val p = PairUri.parse(link)
        assertNotNull(p)
        assertEquals(listOf("192.168.1.9", "100.77.104.0"), p!!.hosts)
        assertEquals("12345678", p.code)
        assertEquals("DESKTOP-4BL01TS", p.name)
        // surrounding whitespace / newline from a pasted message is tolerated
        assertNotNull(PairUri.parse("  $link\n"))
    }
}
