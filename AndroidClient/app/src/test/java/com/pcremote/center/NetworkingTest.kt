package com.pcremote.center

import com.pcremote.center.networking.Discovery
import com.pcremote.center.networking.PairUri
import com.pcremote.center.networking.Quality
import com.pcremote.center.networking.Wol
import com.pcremote.center.networking.WolAdapter
import com.pcremote.center.core.AppJson
import kotlinx.serialization.json.JsonObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class NetworkingTest {
    private val fp = "AB".repeat(32)

    // ---- magic packet ----

    @Test
    fun magicPacketLayout() {
        val mac = Wol.parseMac("01:23:45:67:89:AB")!!
        val p = Wol.buildMagicPacket(mac)
        assertEquals(102, p.size)
        for (i in 0 until 6) assertEquals(0xFF.toByte(), p[i])
        for (r in 0 until 16) assertArrayEquals(mac, p.copyOfRange(6 + r * 6, 12 + r * 6))
    }

    @Test
    fun macParsingAcceptsCommonFormats() {
        val expected = byteArrayOf(0x01, 0x23, 0x45, 0x67, 0x89.toByte(), 0xAB.toByte())
        assertArrayEquals(expected, Wol.parseMac("01:23:45:67:89:ab"))
        assertArrayEquals(expected, Wol.parseMac("01-23-45-67-89-AB"))
        assertArrayEquals(expected, Wol.parseMac(" 0123456789AB "))
        assertNull(Wol.parseMac("01:23:45:67:89"))
        assertNull(Wol.parseMac("zz:23:45:67:89:ab"))
        assertNull(Wol.parseMac(""))
        assertEquals("01:23:45:67:89:AB", Wol.formatMac(expected))
    }

    @Test
    fun ipv4Validation() {
        assertTrue(Wol.isIpv4("192.168.1.255"))
        assertTrue(Wol.isIpv4("255.255.255.255"))
        assertFalse(Wol.isIpv4("192.168.1"))
        assertFalse(Wol.isIpv4("192.168.1.256"))
        assertFalse(Wol.isIpv4("host.local"))
    }

    @Test
    fun wolPortsAreNineAndSeven() {
        assertArrayEquals(intArrayOf(9, 7), Wol.PORTS)
    }

    // ---- pcrc:// URI ----

    @Test
    fun parsesPairingUri() {
        val u = PairUri.parse("pcrc://pair?v=1&h=192.168.1.20,10.0.0.5&p=47821&c=12345678&f=$fp&n=My%20Gaming%20PC")
        assertNotNull(u)
        assertEquals(listOf("192.168.1.20", "10.0.0.5"), u!!.hosts)
        assertEquals(47821, u.port)
        assertEquals("12345678", u.code)
        assertEquals(fp, u.fingerprint)
        assertEquals("My Gaming PC", u.name)
    }

    @Test
    fun uriNormalisesFingerprintAndDefaultsPort() {
        val colon = fp.chunked(2).joinToString(":").lowercase()
        val u = PairUri.parse("pcrc://pair?h=pc.local&c=00000001&f=$colon")!!
        assertEquals(fp, u.fingerprint)
        assertEquals(47821, u.port)
        assertNull(u.name)
    }

    @Test
    fun rejectsInvalidPairingUris() {
        assertNull(PairUri.parse("https://example.com"))
        assertNull(PairUri.parse("pcrc://other?h=a&c=12345678&f=$fp"))
        assertNull(PairUri.parse("pcrc://pair?h=a&c=1234&f=$fp"))              // code too short
        assertNull(PairUri.parse("pcrc://pair?h=a&c=1234567x&f=$fp"))          // code not numeric
        assertNull(PairUri.parse("pcrc://pair?h=a&c=12345678&f=ABCD"))         // bad fingerprint
        assertNull(PairUri.parse("pcrc://pair?c=12345678&f=$fp"))              // no host
        assertNull(PairUri.parse("pcrc://pair?v=2&h=a&c=12345678&f=$fp"))      // unsupported version
        assertNull(PairUri.parse("pcrc://pair?h=a&p=99999&c=12345678&f=$fp"))  // bad port
        assertNull(PairUri.parse("pcrc://pair"))
        assertNull(PairUri.parse(""))
    }

    // ---- discovery ----

    @Test
    fun parsesDiscoveryReply() {
        val c = Discovery.parseReply("""{"app":"pc-remote-center","version":1,"name":"DESKTOP-1","port":47821,"fingerprint":"$fp"}""", "192.168.1.9")
        assertNotNull(c)
        assertEquals("DESKTOP-1", c!!.name)
        assertEquals("192.168.1.9", c.host)
        assertEquals(47821, c.port)
        assertEquals(fp, c.advertisedFingerprint)
    }

    @Test
    fun ignoresForeignDiscoveryReplies() {
        assertNull(Discovery.parseReply("""{"app":"other"}""", "1.1.1.1"))
        assertNull(Discovery.parseReply("garbage", "1.1.1.1"))
    }

    // ---- misc ----

    @Test
    fun qualityFromRtt() {
        assertEquals(Quality.Good, Quality.forRtt(20))
        assertEquals(Quality.Fair, Quality.forRtt(200))
        assertEquals(Quality.Poor, Quality.forRtt(900))
    }

    @Test
    fun wolAdapterSelectionPrefersWiredWithBroadcast() {
        val json = """{"adapters":[
            {"name":"Wi-Fi","mac":"AA:AA:AA:AA:AA:AA","ipv4":"192.168.1.5","broadcast":"192.168.1.255","wireless":true},
            {"name":"Ethernet","mac":"BB:BB:BB:BB:BB:BB","ipv4":"192.168.1.6","broadcast":"192.168.1.255","wireless":false,"wakeOnLanEnabled":false},
            {"name":"nomac","ipv4":"1.2.3.4"}],"note":"n"}"""
        val list = WolAdapter.parseList(AppJson.parseToJsonElement(json) as JsonObject, "adapters")
        assertEquals(2, list.size)
        val a = WolAdapter.pick(list)!!
        assertEquals("Ethernet", a.name)
        assertEquals(false, a.wakeOnLanEnabled)
        val hint = WolAdapter.hintFor(a, "n")!!
        assertTrue(hint.contains("DISABLED"))
        try {
            Wol.send("nonsense", "192.168.1.255")
            fail("expected IllegalArgumentException")
        } catch (_: IllegalArgumentException) {
        }
    }
}
