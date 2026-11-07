package com.pcremote.center.security

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import com.pcremote.center.core.AppJson
import com.pcremote.center.core.SavedPc
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.builtins.ListSerializer
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** AES-256-GCM with a key that lives in the Android Keystore (never leaves the secure hardware/TEE). */
class KeystoreCipher(private val alias: String = "pcrc_master_key_v1") {
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(alias, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    /** Returns Base64(iv || ciphertext+tag). */
    fun encrypt(plain: ByteArray): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val iv = c.iv
        val ct = c.doFinal(plain)
        return Base64.encodeToString(iv + ct, Base64.NO_WRAP)
    }

    fun decrypt(encoded: String): ByteArray {
        val raw = Base64.decode(encoded, Base64.NO_WRAP)
        require(raw.size > 12 + 16) { "ciphertext too short" }
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, raw.copyOfRange(0, 12)))
        return c.doFinal(raw, 12, raw.size - 12)
    }
}

/**
 * Persistent list of paired PCs. The whole list (including device tokens and pinned fingerprints) is stored as
 * Keystore-encrypted ciphertext in SharedPreferences. Plain data (active id) is not secret.
 */
class PcStore(context: Context) {
    private val prefs: SharedPreferences = context.getSharedPreferences("pcrc_secure", Context.MODE_PRIVATE)
    private val cipher = KeystoreCipher()
    private val serializer = ListSerializer(SavedPc.serializer())
    private val lock = Any()

    private val _pcs = MutableStateFlow(load())
    val pcs: StateFlow<List<SavedPc>> = _pcs.asStateFlow()

    private val _activeId = MutableStateFlow(prefs.getString(KEY_ACTIVE, null))
    val activeId: StateFlow<String?> = _activeId.asStateFlow()

    private fun load(): List<SavedPc> {
        val enc = prefs.getString(KEY_PCS, null) ?: return emptyList()
        return try {
            AppJson.decodeFromString(serializer, String(cipher.decrypt(enc), Charsets.UTF_8))
        } catch (_: Exception) {
            emptyList() // key invalidated / data corrupt: the user has to pair again
        }
    }

    private fun persist(list: List<SavedPc>) {
        val enc = cipher.encrypt(AppJson.encodeToString(serializer, list).toByteArray(Charsets.UTF_8))
        prefs.edit().putString(KEY_PCS, enc).apply()
        _pcs.value = list
    }

    fun get(id: String?): SavedPc? = _pcs.value.firstOrNull { it.id == id }

    fun active(): SavedPc? = get(_activeId.value) ?: _pcs.value.firstOrNull()

    fun newId(): String = UUID.randomUUID().toString()

    /** Adds a PC (or replaces one with the same id) and makes it active. */
    fun upsert(pc: SavedPc, makeActive: Boolean = false) = synchronized(lock) {
        val list = _pcs.value.filter { it.id != pc.id } + pc
        persist(list)
        if (makeActive) setActive(pc.id)
    }

    /** Atomically modifies one PC. */
    fun update(id: String, f: (SavedPc) -> SavedPc) = synchronized(lock) {
        val cur = get(id) ?: return@synchronized
        val next = f(cur)
        if (next != cur) persist(_pcs.value.map { if (it.id == id) next else it })
    }

    fun remove(id: String) = synchronized(lock) {
        persist(_pcs.value.filter { it.id != id })
        if (_activeId.value == id) setActive(_pcs.value.firstOrNull()?.id)
    }

    fun setActive(id: String?) {
        prefs.edit().putString(KEY_ACTIVE, id).apply()
        _activeId.value = id
    }

    private companion object {
        const val KEY_PCS = "pcs_v1"
        const val KEY_ACTIVE = "active_id"
    }
}

/** Non-secret app settings. */
class AppSettings(context: Context) {
    private val prefs = context.getSharedPreferences("pcrc_settings", Context.MODE_PRIVATE)

    data class Values(val amber: Int, val red: Int, val historySeconds: Int)

    private val _values = MutableStateFlow(
        Values(
            amber = prefs.getInt("amber", 80),
            red = prefs.getInt("red", 90),
            historySeconds = prefs.getInt("history", 300),
        )
    )
    val values: StateFlow<Values> = _values.asStateFlow()

    fun setThresholds(amber: Int, red: Int) {
        val a = amber.coerceIn(1, 98)
        val r = red.coerceIn(a + 1, 100)
        prefs.edit().putInt("amber", a).putInt("red", r).apply()
        _values.value = _values.value.copy(amber = a, red = r)
    }

    fun setHistorySeconds(seconds: Int) {
        val s = seconds.coerceIn(60, 1800)
        prefs.edit().putInt("history", s).apply()
        _values.value = _values.value.copy(historySeconds = s)
    }
}
