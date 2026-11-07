package com.pcremote.center

import com.pcremote.center.security.Fingerprint
import com.pcremote.center.security.FingerprintMismatchException
import com.pcremote.center.security.PinnedTrustManager
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate

class SecurityTest {
    // Throw-away self-signed RSA-2048 certificate generated with keytool for this test only.
    private val pem = """
-----BEGIN CERTIFICATE-----
MIICzjCCAbagAwIBAgIJALUzU2zLpEQUMA0GCSqGSIb3DQEBCwUAMBQxEjAQBgNV
BAMTCXBjcmMtdGVzdDAgFw0yNjExMDcxNzM0NTNaGA8yMTI2MTAxNDE3MzQ1M1ow
FDESMBAGA1UEAxMJcGNyYy10ZXN0MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIB
CgKCAQEAv2zvSOKZDBUgMyb3J/gjUgYOzftXcp6F+fyR9aVuIXBW5hV0WWUhNxkz
pe6NUs79Tp1D0dEPfEsCXWW5g4XUfSPvrXl0ScPRtib6fCnL9OykaN/WUvEARUwP
CKI5Zy0GQaMgJwbEtyjPTlktl9vwHX0S/ATxPNyznI/jILx8xSVE2XV9EV7huuLo
3hOQAqrx8ZJl+NDfQ8zNxIuBZi/8kwW8ZzzJO67xly2NsF669Egm+SB7xTG8Vjwx
gdCNbj0Er4R9/TSFMJzobc2+LI1LfkFe94Ycn0lZxwp22S6d7nGpJDeVJIV8i1ma
JOXPKWHjfFkgLWRO4Az0QrcLhItTRQIDAQABoyEwHzAdBgNVHQ4EFgQUDQAK4iiZ
rWHgxj+QCa03g4gCGPEwDQYJKoZIhvcNAQELBQADggEBAC0bAtSinhVPpylxHuTv
V2Ay3EfxWgJkd/SE3GYjUoAz5GvJe+2xlz8DujdQOmdqruYpD+KTGnX8rIPGQ40O
iZ6zJ1qd7V6YD5uLEuN/Hp4/B3ipJfiV/Iuxm+9Jxckq0Nu7ZAJGNcmXlxeBFg6B
wUschU3+xokGuwLWbQfY22G8A+aoOS/JW48cr/f/t9T1y1lSP5mLYjuKal0BWmf6
C6KguoU+hfkC4nCIjBfrbfQHBUyu9b9HB6IfYHNPYlcoiNpV0nS9d1+l9kJCIBFh
JxOlP0MBncY6YtOjBHIfyAokz+NHwko1u6o7/0HmyUvG85rKtk7LuBzERHQIRR86
fDQ=
-----END CERTIFICATE-----
""".trim()

    private val cert: X509Certificate = CertificateFactory.getInstance("X.509")
        .generateCertificate(pem.byteInputStream()) as X509Certificate

    private fun independentSha256Hex(): String =
        MessageDigest.getInstance("SHA-256").digest(cert.encoded).joinToString("") { "%02X".format(it) }

    @Test
    fun fingerprintIsUpperCaseSha256OfDer() {
        val fp = Fingerprint.of(cert)
        assertEquals(64, fp.length)
        assertEquals(independentSha256Hex(), fp)
        assertEquals(fp, fp.uppercase())
    }

    @Test
    fun matchingFingerprintIsAccepted() {
        val tm = PinnedTrustManager(independentSha256Hex())
        tm.checkServerTrusted(arrayOf(cert), "RSA") // must not throw
    }

    @Test
    fun matchingFingerprintIsAcceptedInAnyNotation() {
        val fp = independentSha256Hex()
        PinnedTrustManager(fp.lowercase()).checkServerTrusted(arrayOf(cert), "RSA")
        PinnedTrustManager(fp.chunked(2).joinToString(":")).checkServerTrusted(arrayOf(cert), "RSA")
    }

    @Test
    fun mismatchingFingerprintIsRejected() {
        val other = "00".repeat(32)
        val tm = PinnedTrustManager(other)
        try {
            tm.checkServerTrusted(arrayOf(cert), "RSA")
            fail("expected FingerprintMismatchException")
        } catch (e: FingerprintMismatchException) {
            assertEquals(other, e.expected)
            assertEquals(independentSha256Hex(), e.actual)
        }
    }

    @Test
    fun singleBitDifferenceIsRejected() {
        val fp = independentSha256Hex()
        val flipped = (if (fp[0] == '0') '1' else '0') + fp.substring(1)
        try {
            PinnedTrustManager(flipped).checkServerTrusted(arrayOf(cert), "RSA")
            fail("expected rejection")
        } catch (_: CertificateException) {
        }
    }

    @Test
    fun emptyChainAndClientAuthAreRejected() {
        val tm = PinnedTrustManager(independentSha256Hex())
        try { tm.checkServerTrusted(emptyArray(), "RSA"); fail() } catch (_: CertificateException) {}
        try { tm.checkServerTrusted(null, "RSA"); fail() } catch (_: CertificateException) {}
        try { tm.checkClientTrusted(arrayOf(cert), "RSA"); fail() } catch (_: CertificateException) {}
        assertEquals(0, tm.acceptedIssuers.size)
    }

    @Test
    fun invalidPinIsRejectedAtConstruction() {
        try { PinnedTrustManager("1234"); fail() } catch (_: IllegalArgumentException) {}
    }

    @Test
    fun fingerprintHelpers() {
        assertTrue(Fingerprint.isValid("ab:".repeat(31) + "ab"))
        assertFalse(Fingerprint.isValid("zz".repeat(32)))
        assertFalse(Fingerprint.isValid("ab".repeat(31)))
        assertEquals("AB:CD", Fingerprint.pretty("abcd"))
        assertTrue(Fingerprint.matches("ab:cd", "ABCD"))
        assertFalse(Fingerprint.matches("abcd", "abce"))
    }
}
