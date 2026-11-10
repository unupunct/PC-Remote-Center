package com.pcremote.center.networking

import com.pcremote.center.core.SavedPc
import com.pcremote.center.core.arr
import com.pcremote.center.core.str
import kotlinx.serialization.json.JsonObject

/**
 * Which network path a PC address belongs to (home LAN, Tailscale, WireGuard ...). The PC reports its own addresses in
 * `server.info.networks`; addresses typed by the user (for example a Tailscale MagicDNS name) are classified by pattern.
 */
object NetworkPaths {
    const val LAN = "lan"
    const val TAILSCALE = "tailscale"
    const val WIREGUARD = "wireguard"
    const val VPN = "vpn"
    const val OTHER = "other"

    fun label(kind: String?): String = when (kind) {
        LAN -> "LAN"
        TAILSCALE -> "Tailscale"
        WIREGUARD -> "WireGuard"
        VPN -> "VPN"
        else -> "Other network"
    }

    private fun ipv4(host: String): IntArray? {
        val p = host.split('.')
        if (p.size != 4) return null
        val n = p.map { it.toIntOrNull()?.takeIf { v -> v in 0..255 } ?: return null }
        return n.toIntArray()
    }

    /** [known] holds kinds reported by the PC (exact address match); everything else is guessed from the address. */
    fun classify(host: String, known: Map<String, String> = emptyMap()): String {
        known[host]?.let { return it }
        val h = host.lowercase().trim('[', ']')
        if (h.endsWith(".ts.net")) return TAILSCALE
        if (h.startsWith("fd7a:115c:a1e0:")) return TAILSCALE
        val v4 = ipv4(h)
        if (v4 != null) {
            if (v4[0] == 100 && v4[1] in 64..127) return TAILSCALE // 100.64.0.0/10 (CGNAT range Tailscale uses)
            if (v4[0] == 10 || (v4[0] == 172 && v4[1] in 16..31) || (v4[0] == 192 && v4[1] == 168) || (v4[0] == 169 && v4[1] == 254)) return LAN
        }
        return OTHER
    }

    /** Address -> kind pairs from a `server.info` payload (`networks`: [{address, kind}]). */
    fun parseNetworks(payload: JsonObject): Map<String, String> {
        val out = LinkedHashMap<String, String>()
        payload.arr("networks")?.forEach { el ->
            val o = el as? JsonObject ?: return@forEach
            val a = o.str("address")?.trim().orEmpty()
            val k = o.str("kind")?.trim().orEmpty()
            if (a.isNotEmpty() && k in setOf(LAN, TAILSCALE, WIREGUARD, VPN)) out[a] = k
        }
        return out
    }

    /**
     * Adds addresses the PC reported (for instance its Tailscale address) so the app can reach it away from home.
     * Existing addresses keep their order, so the last working one still comes first; nothing is ever removed.
     */
    fun merge(pc: SavedPc, reported: Map<String, String>): SavedPc {
        if (reported.isEmpty()) return pc
        val hosts = pc.hosts + reported.keys.filter { it !in pc.hosts }
        val kinds = pc.hostKinds + reported
        return if (hosts == pc.hosts && kinds == pc.hostKinds) pc else pc.copy(hosts = hosts, hostKinds = kinds)
    }

    /** Accepts an IPv4/IPv6 address or a host name (e.g. my-pc.tailnet-name.ts.net); returns null for anything else. */
    fun normalizeHostInput(input: String): String? {
        var s = input.trim().lowercase().removePrefix("wss://").removePrefix("https://").trimEnd('/')
        if (s.isEmpty() || s.length > 253) return null
        if (s.startsWith("[") && s.endsWith("]")) s = s.substring(1, s.length - 1)
        val validIpv6 = s.contains(':') && s.all { it.isDigit() || it in 'a'..'f' || it == ':' || it == '.' }
        val validName = s.all { it.isLetterOrDigit() || it == '-' || it == '.' } && !s.startsWith('.') && !s.startsWith('-') && !s.endsWith('.')
        return if (validIpv6 || validName) s else null
    }
}
