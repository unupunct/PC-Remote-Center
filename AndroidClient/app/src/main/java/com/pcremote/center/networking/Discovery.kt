package com.pcremote.center.networking

import android.content.Context
import android.net.wifi.WifiManager
import com.pcremote.center.core.AppJson
import com.pcremote.center.core.Protocol
import com.pcremote.center.core.ServerCandidate
import com.pcremote.center.core.int
import com.pcremote.center.core.str
import com.pcremote.center.security.Fingerprint
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

/** UDP LAN discovery (`PCRC_DISCOVER_V1` broadcast -> JSON reply). Replies are untrusted hints. */
object Discovery {
    /** Parses a discovery reply; [senderHost] is the datagram's source address. */
    fun parseReply(text: String, senderHost: String): ServerCandidate? {
        val o = try { AppJson.parseToJsonElement(text) as? JsonObject } catch (_: Exception) { null } ?: return null
        if (o.str("app") != "pc-remote-center") return null
        val name = o.str("name") ?: senderHost
        val port = o.int("port") ?: Protocol.DEFAULT_PORT
        val fp = o.str("fingerprint")?.takeIf { Fingerprint.isValid(it) }?.let { Fingerprint.normalize(it) }
        return ServerCandidate(name, senderHost, port, fp)
    }

    /** All IPv4 broadcast addresses of the phone's interfaces plus the limited broadcast. */
    private fun broadcastTargets(): List<InetAddress> {
        val out = LinkedHashSet<InetAddress>()
        try {
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) ia.broadcast?.let { out.add(it) }
            }
        } catch (_: Exception) {}
        out.add(InetAddress.getByName("255.255.255.255"))
        return out.toList()
    }

    /**
     * Broadcasts a probe (repeated a few times, UDP is lossy) and emits each distinct server that answers
     * within [listenMs].
     */
    fun scan(context: Context, listenMs: Long = 3000): Flow<ServerCandidate> = channelFlow {
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        val lock = try { wifi?.createMulticastLock("pcrc-discovery")?.apply { setReferenceCounted(false); acquire() } } catch (_: Exception) { null }
        try {
            DatagramSocket().use { sock ->
                sock.broadcast = true
                sock.soTimeout = 300
                val probe = Protocol.DISCOVERY_PROBE.toByteArray(Charsets.US_ASCII)
                val targets = broadcastTargets()
                val seen = HashSet<String>()
                val deadline = System.currentTimeMillis() + listenMs
                var nextProbe = 0L
                val buf = ByteArray(2048)
                while (isActive && System.currentTimeMillis() < deadline) {
                    val now = System.currentTimeMillis()
                    if (now >= nextProbe) {
                        for (t in targets) {
                            try { sock.send(DatagramPacket(probe, probe.size, t, Protocol.DISCOVERY_PORT)) } catch (_: Exception) {}
                        }
                        nextProbe = now + 1000
                    }
                    try {
                        val p = DatagramPacket(buf, buf.size)
                        sock.receive(p)
                        val host = p.address.hostAddress ?: continue
                        val c = parseReply(String(p.data, 0, p.length, Charsets.UTF_8), host) ?: continue
                        if (seen.add("$host:${c.port}")) send(c)
                    } catch (_: SocketTimeoutException) {
                    }
                }
            }
        } finally {
            try { lock?.release() } catch (_: Exception) {}
        }
    }.flowOn(Dispatchers.IO)

    /** Convenience for one-shot use. */
    suspend fun scanList(context: Context, listenMs: Long = 3000): List<ServerCandidate> = withContext(Dispatchers.IO) {
        val out = ArrayList<ServerCandidate>()
        scan(context, listenMs).collect { out.add(it) }
        out
    }
}
