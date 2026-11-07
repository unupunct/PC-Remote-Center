package com.pcremote.center.networking

import com.pcremote.center.core.NotConnectedException
import com.pcremote.center.core.RequestTimeoutException
import com.pcremote.center.core.ServerError
import com.pcremote.center.security.FingerprintMismatchException
import kotlinx.coroutines.TimeoutCancellationException
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import javax.net.ssl.SSLException

object Errors {
    fun causeChain(t: Throwable): Sequence<Throwable> = generateSequence(t) { it.cause?.takeIf { c -> c !== it } }.take(8)

    fun isFingerprintMismatch(t: Throwable): Boolean = causeChain(t).any { it is FingerprintMismatchException }

    /** Human readable message for an exception raised by the connection layer. Never contains secrets. */
    fun describe(t: Throwable): String {
        if (isFingerprintMismatch(t)) {
            return "Security warning: the PC's certificate does not match the one pinned during pairing. " +
                "Connection refused. Forget and re-pair the PC if you changed its certificate."
        }
        return when (t) {
            is ServerError -> userMessageForCode(t.code, t.message)
            is NotConnectedException -> t.message ?: "Not connected to the PC"
            is RequestTimeoutException -> t.message ?: "The PC did not answer in time"
            is TimeoutCancellationException, is SocketTimeoutException -> "Connection timed out"
            is UnknownHostException -> "Host not found"
            is ConnectException -> "Could not reach the PC (is the server running and the port open?)"
            is NoRouteToHostException -> "No route to the PC - check that both devices are on the same network"
            is SSLException -> "Secure connection failed: ${t.message ?: "TLS error"}"
            else -> t.message?.takeIf { it.isNotBlank() } ?: t.javaClass.simpleName
        }
    }

    fun userMessageForCode(code: String, message: String?): String {
        val m = message?.takeIf { it.isNotBlank() }
        return when (code) {
            "forbidden" -> m ?: "Your device does not have permission for this action."
            "rate_limited" -> m ?: "Too many attempts - wait a few minutes and try again."
            "auth_failed" -> m ?: "Authentication failed."
            "confirmation_invalid" -> m ?: "The confirmation expired or is invalid. Start again."
            "invalid_shortcut" -> m ?: "The shortcut is not valid."
            else -> m ?: "Error: $code"
        }
    }
}
