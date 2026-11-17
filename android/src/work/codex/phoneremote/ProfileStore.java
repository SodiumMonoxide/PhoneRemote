package work.codex.phoneremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.KeyStore;
import java.util.Arrays;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

final class ProfileStore {
    private static final String KEY_ALIAS = "phoneremote.device.aes";
    private static final int IV_LENGTH = 12;
    private static final String SELECTED_MODE = "selected-mode";
    private final SharedPreferences preferences;

    ProfileStore(Context context) {
        this(context.getSharedPreferences("connection", Context.MODE_PRIVATE));
    }

    ProfileStore(SharedPreferences preferences) {
        this.preferences = preferences;
        migrateLegacyProfile();
    }

    private RemoteProfile readProfile(String originKey, String fingerprintKey) {
        try {
            return RemoteProfile.parse(preferences.getString(originKey, ""),
                preferences.getString(fingerprintKey, ""));
        } catch (IllegalArgumentException error) {
            return null;
        }
    }

    private void migrateLegacyProfile() {
        if (!preferences.contains("origin") && !preferences.contains("fingerprint") &&
            !preferences.contains("device")) return;
        RemoteProfile legacy = readProfile("origin", "fingerprint");
        SharedPreferences.Editor migration = preferences.edit()
            .remove("origin").remove("fingerprint").remove("device");
        if (legacy != null && profile(legacy.mode) == null) {
            migration.putString(slotKey(legacy.mode, "origin"), legacy.origin)
                .putString(slotKey(legacy.mode, "fingerprint"), legacy.fingerprint);
            // The key and authenticated profile scope are unchanged. Preserve the stored ciphertext.
            String encrypted = preferences.getString("device", null);
            if (encrypted != null) migration.putString(slotKey(legacy.mode, "device"), encrypted);
            if (!preferences.contains(SELECTED_MODE)) migration.putString(SELECTED_MODE, legacy.mode.key);
        }
        migration.apply();
    }

    private static String slotKey(RemoteProfile.Mode mode, String field) {
        return mode.key + "." + field;
    }

    synchronized RemoteProfile profile() {
        RemoteProfile.Mode selected = RemoteProfile.Mode.fromKey(preferences.getString(SELECTED_MODE, null));
        RemoteProfile preferred = selected == null ? null : profile(selected);
        if (preferred != null) return preferred;
        RemoteProfile lan = profile(RemoteProfile.Mode.LAN);
        return lan == null ? profile(RemoteProfile.Mode.TAILSCALE) : lan;
    }

    synchronized RemoteProfile profile(RemoteProfile.Mode mode) {
        RemoteProfile saved = readProfile(slotKey(mode, "origin"), slotKey(mode, "fingerprint"));
        return saved != null && saved.mode == mode ? saved : null;
    }

    synchronized RemoteProfile selectProfile(RemoteProfile.Mode mode) {
        RemoteProfile selected = profile(mode);
        if (selected != null) preferences.edit().putString(SELECTED_MODE, mode.key).apply();
        return selected;
    }

    synchronized void saveProfile(RemoteProfile profile) {
        RemoteProfile.Mode mode = profile.mode;
        SharedPreferences.Editor editor = preferences.edit().putString(SELECTED_MODE, mode.key);
        if (!profile.matches(profile(mode))) {
            editor.putString(slotKey(mode, "origin"), profile.origin)
                .putString(slotKey(mode, "fingerprint"), profile.fingerprint)
                .remove(slotKey(mode, "device"));
        }
        editor.apply();
    }

    synchronized String deviceToken(RemoteProfile expected) {
        if (!expected.matches(profile(expected.mode))) return null;
        String stored = preferences.getString(slotKey(expected.mode, "device"), null);
        if (stored == null) return null;
        try {
            byte[] encoded = Base64.decode(stored, Base64.NO_WRAP);
            if (encoded.length <= IV_LENGTH) throw new GeneralSecurityException("Invalid credential");
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(false),
                new GCMParameterSpec(128, Arrays.copyOf(encoded, IV_LENGTH)));
            cipher.updateAAD(profileScope(expected));
            byte[] plaintext = cipher.doFinal(encoded, IV_LENGTH, encoded.length - IV_LENGTH);
            return new String(plaintext, StandardCharsets.US_ASCII);
        } catch (Exception error) {
            forgetDevice(expected);
            return null;
        }
    }

    synchronized boolean rememberDevice(RemoteProfile expected, String token) {
        if (!expected.matches(profile(expected.mode))) return false;
        try {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.ENCRYPT_MODE, key(true));
            cipher.updateAAD(profileScope(expected));
            byte[] iv = cipher.getIV();
            if (iv.length != IV_LENGTH) return false;
            byte[] encrypted = cipher.doFinal(token.getBytes(StandardCharsets.US_ASCII));
            byte[] data = ByteBuffer.allocate(iv.length + encrypted.length).put(iv).put(encrypted).array();
            return preferences.edit().putString(slotKey(expected.mode, "device"),
                Base64.encodeToString(data, Base64.NO_WRAP)).commit();
        } catch (Exception error) {
            return false;
        }
    }

    synchronized void forgetDevice(RemoteProfile expected) {
        if (expected.matches(profile(expected.mode)))
            preferences.edit().remove(slotKey(expected.mode, "device")).apply();
    }

    private static byte[] profileScope(RemoteProfile profile) {
        return (profile.origin + "|" + profile.fingerprint).getBytes(StandardCharsets.US_ASCII);
    }

    private static SecretKey key(boolean create) throws Exception {
        KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
        keyStore.load(null);
        SecretKey existing = (SecretKey) keyStore.getKey(KEY_ALIAS, null);
        if (existing != null) return existing;
        if (!create) throw new GeneralSecurityException("Missing device key");
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(
            KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .build());
        return generator.generateKey();
    }
}
