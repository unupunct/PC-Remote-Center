package com.pcremote.center.networking

import com.pcremote.center.core.arr
import com.pcremote.center.core.bool
import com.pcremote.center.core.str
import kotlinx.serialization.json.JsonObject

/** A network adapter reported by `server.info` (macAddresses) or `wol.info` (adapters). */
data class WolAdapter(
    val name: String,
    val mac: String,
    val ipv4: String?,
    val broadcast: String?,
    val wireless: Boolean,
    val wakeOnLanEnabled: Boolean?,
) {
    companion object {
        fun parseList(payload: JsonObject, key: String): List<WolAdapter> =
            payload.arr(key)?.mapNotNull { el ->
                val o = el as? JsonObject ?: return@mapNotNull null
                val mac = o.str("mac")?.takeIf { it.isNotBlank() } ?: return@mapNotNull null
                WolAdapter(
                    name = o.str("name") ?: "adapter",
                    mac = mac,
                    ipv4 = o.str("ipv4"),
                    broadcast = o.str("broadcast"),
                    wireless = o.bool("wireless") ?: false,
                    wakeOnLanEnabled = o.bool("wakeOnLanEnabled"),
                )
            } ?: emptyList()

        /** Prefers a wired adapter that has an IPv4 address and broadcast address (magic packets work best on Ethernet). */
        fun pick(list: List<WolAdapter>): WolAdapter? =
            list.sortedWith(
                compareBy<WolAdapter> { if (it.broadcast.isNullOrBlank()) 1 else 0 }
                    .thenBy { if (it.wireless) 1 else 0 }
            ).firstOrNull()

        fun hintFor(adapter: WolAdapter?, note: String?): String? {
            val state: String? = if (adapter == null) null else when (adapter.wakeOnLanEnabled) {
                true -> "Wake-on-LAN is enabled on \"${adapter.name}\"."
                false -> "Wake-on-LAN is DISABLED on \"${adapter.name}\" in Windows - enable it in the adapter's power settings and the BIOS."
                null -> "Windows did not report the Wake-on-LAN state of \"${adapter.name}\"."
            }
            val parts = listOfNotNull(state, note?.takeIf { it.isNotBlank() })
            return parts.joinToString(" ").ifBlank { null }
        }
    }
}
