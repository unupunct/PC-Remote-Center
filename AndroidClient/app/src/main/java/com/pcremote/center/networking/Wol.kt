package com.pcremote.center.networking

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/** Wake-on-LAN magic packet helpers. The packet is sent from the phone, so it works while the PC is off. */
object Wol {
    val PORTS = intArrayOf(9, 7)

    /** Parses "AA:BB:CC:DD:EE:FF", "AA-BB-..." or "AABBCCDDEEFF". Returns null when invalid. */
    fun parseMac(text: String): ByteArray? {
        val hex = text.trim().filter { it != ':' && it != '-' && it != '.' }
        if (hex.length != 12 || !hex.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }) return null
        return ByteArray(6) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }

    fun formatMac(mac: ByteArray): String = mac.joinToString(":") { "%02X".format(it.toInt() and 0xFF) }

    /** 6 x 0xFF followed by the MAC repeated 16 times (102 bytes). */
    fun buildMagicPacket(mac: ByteArray): ByteArray {
        require(mac.size == 6) { "MAC must be 6 bytes" }
        val out = ByteArray(6 + 16 * 6)
        for (i in 0 until 6) out[i] = 0xFF.toByte()
        for (r in 0 until 16) System.arraycopy(mac, 0, out, 6 + r * 6, 6)
        return out
    }

    /** True for a dotted-quad IPv4 address. */
    fun isIpv4(s: String): Boolean {
        val p = s.trim().split('.')
        return p.size == 4 && p.all { it.toIntOrNull()?.let { n -> n in 0..255 } == true && it.length <= 3 }
    }

    /** Sends the magic packet to [broadcast] on UDP ports 9 and 7. Returns the number of datagrams sent. */
    fun send(macText: String, broadcast: String): Int {
        val mac = parseMac(macText) ?: throw IllegalArgumentException("Invalid MAC address")
        val target = broadcast.trim().ifEmpty { "255.255.255.255" }
        require(isIpv4(target)) { "Invalid broadcast address" }
        val data = buildMagicPacket(mac)
        val addr = InetAddress.getByName(target)
        var sent = 0
        DatagramSocket().use { sock ->
            sock.broadcast = true
            for (port in PORTS) {
                sock.send(DatagramPacket(data, data.size, addr, port))
                sent++
            }
        }
        return sent
    }
}
