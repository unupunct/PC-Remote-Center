package com.pcremote.center.security

import okhttp3.OkHttpClient
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** Thrown when the server certificate does not match the pinned SHA-256 fingerprint. */
class FingerprintMismatchException(val expected: String, val actual: String) :
    CertificateException("Server certificate fingerprint does not match the pinned value")

object Fingerprint {
    private val HEX = "0123456789ABCDEF".toCharArray()

    fun toHex(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (i in bytes.indices) {
            val v = bytes[i].toInt() and 0xFF
            out[i * 2] = HEX[v ushr 4]
            out[i * 2 + 1] = HEX[v and 0x0F]
        }
        return String(out)
    }

    /** SHA-256 over the certificate DER bytes, upper-case hex. */
    fun ofDer(der: ByteArray): String = toHex(MessageDigest.getInstance("SHA-256").digest(der))

    fun of(cert: X509Certificate): String = ofDer(cert.encoded)

    /** Removes separators/whitespace and upper-cases ("ab:cd" -> "ABCD"). */
    fun normalize(s: String): String = s.filter { it != ':' && it != '-' && !it.isWhitespace() }.uppercase()

    fun isValid(s: String): Boolean {
        val n = normalize(s)
        return n.length == 64 && n.all { it in '0'..'9' || it in 'A'..'F' }
    }

    /** Groups as "AB:CD:..." for display. */
    fun pretty(s: String): String = normalize(s).chunked(2).joinToString(":")

    /** Constant-time comparison of two fingerprints after normalisation. */
    fun matches(a: String, b: String): Boolean =
        MessageDigest.isEqual(normalize(a).toByteArray(), normalize(b).toByteArray())
}

/**
 * Trust manager that accepts exactly one certificate: the one whose SHA-256 hash equals [expected].
 * No CA chain or host name validation happens - pinning replaces it.
 */
class PinnedTrustManager(expected: String) : X509TrustManager {
    private val expectedNorm = Fingerprint.normalize(expected)

    init {
        require(Fingerprint.isValid(expectedNorm)) { "Invalid pinned fingerprint" }
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("Client certificates are not supported")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        if (chain.isNullOrEmpty()) throw CertificateException("Empty certificate chain")
        val actual = Fingerprint.of(chain[0])
        if (!Fingerprint.matches(actual, expectedNorm)) throw FingerprintMismatchException(expectedNorm, actual)
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

object PinnedClients {
    /** OkHttp client that only talks TLS to a server presenting the pinned certificate. */
    fun create(fingerprint: String): OkHttpClient {
        val tm = PinnedTrustManager(fingerprint)
        val ctx = SSLContext.getInstance("TLS")
        ctx.init(null, arrayOf(tm), SecureRandom())
        return OkHttpClient.Builder()
            .sslSocketFactory(ctx.socketFactory, tm)
            .hostnameVerifier { _, _ -> true } // pinning replaces host name verification
            .connectTimeout(6, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .writeTimeout(10, TimeUnit.SECONDS)
            .retryOnConnectionFailure(false)
            .build()
    }
}

/**
 * Reads the certificate fingerprint a server presents, WITHOUT trusting it and without sending any data:
 * the handshake is aborted right after the certificate is seen. The user must then confirm the fingerprint
 * (compare it with the one shown on the PC) before it is pinned and pairing is attempted.
 */
object FingerprintProbe {
    fun probe(host: String, port: Int, timeoutMs: Int = 5000): String {
        var seen: String? = null
        val capture = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                if (!chain.isNullOrEmpty()) seen = Fingerprint.of(chain[0])
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        val ctx = SSLContext.getInstance("TLS")
        ctx.init(null, arrayOf(capture), SecureRandom())
        val plain = Socket()
        try {
            plain.connect(InetSocketAddress(host, port), timeoutMs)
            plain.soTimeout = timeoutMs
            val ssl = ctx.socketFactory.createSocket(plain, host, port, true) as SSLSocket
            ssl.startHandshake()
        } finally {
            try { plain.close() } catch (_: Exception) {}
        }
        return seen ?: throw CertificateException("The server did not present a certificate")
    }
}
