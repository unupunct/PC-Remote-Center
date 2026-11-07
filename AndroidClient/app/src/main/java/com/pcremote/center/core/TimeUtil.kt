package com.pcremote.center.core

import java.time.Instant
import java.time.OffsetDateTime

object TimeUtil {
    /** Parses an ISO-8601 instant ("2026-01-01T10:00:00.1234567Z" or with an offset) to epoch millis; null if invalid. */
    fun parseIsoMillis(text: String?): Long? {
        if (text.isNullOrBlank()) return null
        return try {
            Instant.parse(text).toEpochMilli()
        } catch (_: Exception) {
            try { OffsetDateTime.parse(text).toInstant().toEpochMilli() } catch (_: Exception) { null }
        }
    }
}
