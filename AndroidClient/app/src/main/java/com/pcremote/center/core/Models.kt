package com.pcremote.center.core

import kotlinx.serialization.Serializable

/** A paired PC. Persisted encrypted (contains the device token and the pinned fingerprint). */
@Serializable
data class SavedPc(
    val id: String,
    val name: String,
    val hosts: List<String>,
    val port: Int = Protocol.DEFAULT_PORT,
    /** Pinned SHA-256 fingerprint of the server certificate (upper-case hex, no separators). */
    val fingerprint: String,
    val deviceId: String,
    val token: String,
    val lastHost: String? = null,
    /** Network path of known addresses (address -> lan|tailscale|wireguard|vpn), reported by the PC in server.info. */
    val hostKinds: Map<String, String> = emptyMap(),
    /** Wake-on-LAN data, stored per PC. [wolUserEdited] stops server data from overwriting manual edits. */
    val wolMac: String = "",
    val wolBroadcast: String = "",
    val wolUserEdited: Boolean = false,
    val wolHint: String? = null,
) {
    /** Hosts with the last working one first. */
    fun orderedHosts(): List<String> {
        val last = lastHost
        return if (last != null && last in hosts) listOf(last) + hosts.filter { it != last } else hosts
    }
}

/** A server found by UDP discovery or entered manually; not trusted until paired. */
data class ServerCandidate(
    val name: String,
    val host: String,
    val port: Int,
    /** Fingerprint advertised in the UDP reply (untrusted hint) or null for manual entry. */
    val advertisedFingerprint: String?,
)

/** Result of the pairing handshake. */
data class PairResult(
    val deviceId: String,
    val token: String,
    val permissions: Set<String>,
    val computerName: String,
    val host: String,
)

/** Warning thresholds shared by the gauges. */
enum class Level { Normal, Warning, Critical }

object Thresholds {
    const val DEFAULT_AMBER = 80
    const val DEFAULT_RED = 90

    /** Colour class for a percentage; null (metric not available) is always [Level.Normal]. */
    fun levelFor(percent: Double?, amber: Int = DEFAULT_AMBER, red: Int = DEFAULT_RED): Level = when {
        percent == null -> Level.Normal
        percent >= red -> Level.Critical
        percent >= amber -> Level.Warning
        else -> Level.Normal
    }
}

object Permissions {
    const val MONITORING = "monitoring"
    const val TERMINAL = "terminal"
    const val LAUNCH = "launch"
    const val POWER = "power"
    const val ADMIN = "admin"
    val ALL = listOf(MONITORING, TERMINAL, LAUNCH, POWER, ADMIN)

    fun describe(p: String): String = when (p) {
        MONITORING -> "View CPU, GPU, RAM and disk metrics"
        TERMINAL -> "Use Command Prompt and PowerShell"
        LAUNCH -> "Run custom shortcuts"
        POWER -> "Restart, shut down, sleep and lock the PC"
        ADMIN -> "Edit shortcuts and administration"
        else -> p
    }
}

/** A user-visible message produced by the connection layer (snackbar). */
data class UserMessage(val text: String, val isError: Boolean = true)
