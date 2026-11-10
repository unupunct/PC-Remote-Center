package com.pcremote.center

import com.pcremote.center.core.SavedPc
import com.pcremote.center.networking.NetworkPaths
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class NetworkPathsTest {
    private fun pc(hosts: List<String>, kinds: Map<String, String> = emptyMap(), last: String? = null) = SavedPc(
        id = "1", name = "PC", hosts = hosts, fingerprint = "AA", deviceId = "d", token = "t", lastHost = last, hostKinds = kinds,
    )

    @Test fun classifies_by_pattern() {
        assertEquals(NetworkPaths.LAN, NetworkPaths.classify("192.168.1.9"))
        assertEquals(NetworkPaths.LAN, NetworkPaths.classify("10.0.0.7"))
        assertEquals(NetworkPaths.LAN, NetworkPaths.classify("172.20.1.1"))
        assertEquals(NetworkPaths.OTHER, NetworkPaths.classify("172.32.0.1"))
        assertEquals(NetworkPaths.TAILSCALE, NetworkPaths.classify("100.77.104.0"))
        assertEquals(NetworkPaths.TAILSCALE, NetworkPaths.classify("100.127.255.255"))
        assertEquals(NetworkPaths.OTHER, NetworkPaths.classify("100.128.0.1"))
        assertEquals(NetworkPaths.OTHER, NetworkPaths.classify("100.63.0.1"))
        assertEquals(NetworkPaths.TAILSCALE, NetworkPaths.classify("my-pc.tail1234.ts.net"))
        assertEquals(NetworkPaths.TAILSCALE, NetworkPaths.classify("[fd7a:115c:a1e0::dd35:6801]"))
        assertEquals(NetworkPaths.OTHER, NetworkPaths.classify("example.com"))
    }

    @Test fun reported_kind_wins_over_the_guess() {
        assertEquals(NetworkPaths.WIREGUARD, NetworkPaths.classify("10.8.0.2", mapOf("10.8.0.2" to NetworkPaths.WIREGUARD)))
    }

    @Test fun parses_server_info_networks() {
        val json = Json.parseToJsonElement(
            """{"networks":[{"address":"192.168.1.9","kind":"lan"},{"address":"100.77.104.0","kind":"tailscale"},{"address":"1.2.3.4","kind":"bogus"},{"kind":"lan"}]}""",
        ) as JsonObject
        assertEquals(mapOf("192.168.1.9" to "lan", "100.77.104.0" to "tailscale"), NetworkPaths.parseNetworks(json))
        assertEquals(emptyMap<String, String>(), NetworkPaths.parseNetworks(Json.parseToJsonElement("{}") as JsonObject))
    }

    @Test fun merge_adds_new_addresses_keeps_order_and_never_removes() {
        val base = pc(listOf("192.168.1.9", "192.168.1.20"), last = "192.168.1.20")
        val merged = NetworkPaths.merge(base, mapOf("192.168.1.9" to "lan", "100.77.104.0" to "tailscale"))
        assertEquals(listOf("192.168.1.9", "192.168.1.20", "100.77.104.0"), merged.hosts)
        assertEquals("tailscale", merged.hostKinds["100.77.104.0"])
        // last working host still comes first
        assertEquals("192.168.1.20", merged.orderedHosts().first())
        // nothing to add -> same object
        assertEquals(merged, NetworkPaths.merge(merged, mapOf("100.77.104.0" to "tailscale")))
        assertEquals(base, NetworkPaths.merge(base, emptyMap()))
    }

    @Test fun host_input_is_validated() {
        assertEquals("my-pc.tail1234.ts.net", NetworkPaths.normalizeHostInput("  My-PC.tail1234.ts.net  "))
        assertEquals("100.77.104.0", NetworkPaths.normalizeHostInput("100.77.104.0"))
        assertEquals("fd7a:115c:a1e0::1", NetworkPaths.normalizeHostInput("[fd7a:115c:a1e0::1]"))
        assertEquals("host.example", NetworkPaths.normalizeHostInput("wss://host.example/"))
        assertNull(NetworkPaths.normalizeHostInput(""))
        assertNull(NetworkPaths.normalizeHostInput("bad host"))
        assertNull(NetworkPaths.normalizeHostInput("a/b"))
        assertNull(NetworkPaths.normalizeHostInput("-leading.example"))
        assertNull(NetworkPaths.normalizeHostInput("evil;rm -rf"))
    }

    @Test fun saved_pc_from_older_versions_still_loads() {
        val old = """{"id":"1","name":"PC","hosts":["192.168.1.9"],"fingerprint":"AA","deviceId":"d","token":"t"}"""
        val decoded = Json { ignoreUnknownKeys = true }.decodeFromString(SavedPc.serializer(), old)
        assertEquals(emptyMap<String, String>(), decoded.hostKinds)
    }
}
