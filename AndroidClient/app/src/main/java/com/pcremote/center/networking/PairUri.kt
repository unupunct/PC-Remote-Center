package com.pcremote.center.networking

import com.pcremote.center.core.Protocol
import com.pcremote.center.security.Fingerprint
import java.net.URLDecoder

/** Parsed `pcrc://pair?v=1&h=a,b&p=47821&c=12345678&f=<sha256>&n=<name>` QR payload. */
data class PairUri(
    val hosts: List<String>,
    val port: Int,
    val code: String,
    val fingerprint: String,
    val name: String?,
) {
    companion object {
        /** Returns null when [text] is not a valid pairing URI. */
        fun parse(text: String): PairUri? {
            val s = text.trim()
            val prefix = "pcrc://pair"
            if (!s.startsWith(prefix, ignoreCase = true)) return null
            val rest = s.substring(prefix.length)
            if (rest.isNotEmpty() && rest[0] != '?' && rest[0] != '/') return null
            val q = rest.substringAfter('?', "")
            if (q.isEmpty()) return null
            val params = HashMap<String, String>()
            for (part in q.substringBefore('#').split('&')) {
                if (part.isEmpty()) continue
                val k = part.substringBefore('=')
                val v = part.substringAfter('=', "")
                try {
                    params[k] = URLDecoder.decode(v, "UTF-8")
                } catch (_: Exception) {
                    return null
                }
            }
            val version = params["v"]
            if (version != null && version != Protocol.VERSION.toString()) return null
            val hosts = params["h"].orEmpty().split(',').map { it.trim() }.filter { it.isNotEmpty() }
            if (hosts.isEmpty()) return null
            val port = params["p"]?.toIntOrNull() ?: Protocol.DEFAULT_PORT
            if (port !in 1..65535) return null
            val code = params["c"].orEmpty()
            if (code.length != 8 || !code.all { it.isDigit() }) return null
            val fp = params["f"].orEmpty()
            if (!Fingerprint.isValid(fp)) return null
            return PairUri(hosts, port, code, Fingerprint.normalize(fp), params["n"]?.takeIf { it.isNotBlank() })
        }
    }
}
