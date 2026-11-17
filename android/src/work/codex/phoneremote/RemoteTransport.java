package work.codex.phoneremote;

import android.util.Base64;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Proxy;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.Collection;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.net.ssl.HostnameVerifier;
import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;
import org.json.JSONObject;

final class RemoteTransport {
    private static final int MAX_REQUEST = 4096;
    private static final int MAX_JSON = 16 * 1024;
    private static final int MAX_FRAME = 2 * 1024 * 1024;
    private final RemoteProfile profile;
    private final ProfileStore store;
    private final SSLSocketFactory sockets;
    private final HostnameVerifier hostnameVerifier;
    private final AtomicBoolean switchOnResume;

    RemoteTransport(RemoteProfile profile, ProfileStore store, boolean explicitModeSwitch) throws GeneralSecurityException {
        this.profile = profile;
        this.store = store;
        switchOnResume = new AtomicBoolean(explicitModeSwitch);
        SSLContext tls = SSLContext.getInstance("TLS");
        tls.init(null, new TrustManager[]{new PinnedCertificate(profile)}, null);
        sockets = tls.getSocketFactory();
        hostnameVerifier = HttpsURLConnection.getDefaultHostnameVerifier();
    }

    static boolean allows(String path, String method) {
        if (path == null || method == null) return false;
        if ("GET".equals(method)) return "/api/frame".equals(path);
        if (!"POST".equals(method)) return false;
        return "/api/pair".equals(path) || "/api/resume".equals(path) ||
            "/api/input".equals(path) || "/api/disconnect".equals(path);
    }

    Result request(String path, String method, String body, String bearer) throws Exception {
        if (!allows(path, method) || (body != null && body.length() > MAX_REQUEST)) {
            return new Result(400, "");
        }
        boolean pairing = "/api/pair".equals(path);
        boolean resuming = "/api/resume".equals(path);
        boolean frame = "/api/frame".equals(path);

        if (pairing || resuming) {
            JSONObject supplied = new JSONObject(body == null ? "{}" : body);
            boolean switchedBySelector = resuming && switchOnResume.getAndSet(false);
            JSONObject fields = new JSONObject()
                .put("switchMode", switchedBySelector || supplied.optBoolean("switchMode", false));
            if (resuming) {
                String deviceToken = store.deviceToken(profile);
                if (!isToken(deviceToken)) return new Result(401, "");
                fields.put("deviceToken", deviceToken);
            } else {
                fields.put("code", supplied.optString("code", ""))
                    .put("remember", supplied.optBoolean("remember", false));
            }
            body = fields.toString();
        }
        if (!pairing && !resuming && !isToken(bearer)) return new Result(401, "");

        byte[] bodyBytes = body == null ? new byte[0] : body.getBytes(StandardCharsets.UTF_8);
        if (bodyBytes.length > MAX_REQUEST) return new Result(400, "");
        HttpsURLConnection connection = (HttpsURLConnection) new URL(profile.origin + path)
            .openConnection(Proxy.NO_PROXY);
        try {
            connection.setSSLSocketFactory(sockets);
            connection.setHostnameVerifier(hostnameVerifier);
            connection.setInstanceFollowRedirects(false);
            connection.setUseCaches(false);
            connection.setConnectTimeout(2800);
            connection.setReadTimeout(frame ? 2300 : 4000);
            connection.setRequestMethod(method);
            connection.setRequestProperty("Accept", frame ? "image/jpeg" : "application/json");

            if (!pairing && !resuming)
                connection.setRequestProperty("Authorization", "Bearer " + bearer);
            if ("POST".equals(method)) {
                connection.setRequestProperty("Origin", profile.origin);
                connection.setRequestProperty("Content-Type", "application/json");
                connection.setDoOutput(true);
                connection.setFixedLengthStreamingMode(bodyBytes.length);
                try (OutputStream stream = connection.getOutputStream()) {
                    stream.write(bodyBytes);
                }
            }

            int status = connection.getResponseCode();
            if (resuming && status == 401) store.forgetDevice(profile);
            if (status != 200) return new Result(status, "");
            if (frame) {
                String mime = connection.getContentType();
                if (mime == null || !mime.equalsIgnoreCase("image/jpeg"))
                    throw new IOException("Unexpected frame media type");
                byte[] image = readBody(connection, MAX_FRAME);
                return new Result(status, Base64.encodeToString(image, Base64.NO_WRAP));
            }
            if (pairing || resuming) {
                JSONObject object = new JSONObject(new String(readBody(connection, MAX_JSON), StandardCharsets.UTF_8));
                String token = object.optString("token", "");
                if (!isToken(token)) throw new IOException("Invalid session token");
                String deviceToken = object.optString("deviceToken", "");
                boolean saveFailed = isToken(deviceToken) && !store.rememberDevice(profile, deviceToken);
                String response = new JSONObject().put("token", token).toString();
                return new Result(status, response, saveFailed);
            }
            return new Result(status, "");
        } finally {
            connection.disconnect();
        }
    }

    private static boolean isToken(String value) {
        return value != null && value.matches("[A-Fa-f0-9]{64}");
    }

    private static byte[] readBody(HttpsURLConnection connection, int limit) throws IOException {
        if (connection.getContentLengthLong() > limit) throw new IOException("Response is too large");
        try (InputStream stream = connection.getInputStream()) {
            ByteArrayOutputStream data = new ByteArrayOutputStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = stream.read(buffer)) != -1) {
                if (data.size() + count > limit) throw new IOException("Response is too large");
                data.write(buffer, 0, count);
            }
            return data.toByteArray();
        }
    }

    static final class Result {
        final int status;
        final String payload;
        final boolean rememberFailed;

        Result(int status, String payload) {
            this(status, payload, false);
        }

        Result(int status, String payload, boolean rememberFailed) {
            this.status = status;
            this.payload = payload;
            this.rememberFailed = rememberFailed;
        }
    }

    private static final class PinnedCertificate implements X509TrustManager {
        private final RemoteProfile profile;

        PinnedCertificate(RemoteProfile profile) {
            this.profile = profile;
        }

        @Override
        public void checkClientTrusted(X509Certificate[] certificates, String algorithm)
            throws CertificateException {
            throw new CertificateException("Client certificates are not supported");
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }

        @Override
        public void checkServerTrusted(X509Certificate[] certificates, String algorithm)
            throws CertificateException {
            if (certificates == null || certificates.length == 0)
                throw new CertificateException("Missing server certificate");
            X509Certificate leaf = certificates[0];
            leaf.checkValidity();
            try {
                byte[] fingerprint = MessageDigest.getInstance("SHA-256").digest(leaf.getEncoded());
                if (!MessageDigest.isEqual(profile.fingerprintBytes, fingerprint))
                    throw new CertificateException("Server certificate fingerprint mismatch");
            } catch (GeneralSecurityException error) {
                throw new CertificateException("Server certificate verification failed", error);
            }
            Collection<List<?>> alternatives = leaf.getSubjectAlternativeNames();
            if (alternatives != null) {
                for (List<?> alternative : alternatives) {
                    if (alternative.size() >= 2 && Integer.valueOf(7).equals(alternative.get(0)) &&
                        profile.host.equals(alternative.get(1))) return;
                }
            }
            throw new CertificateException("Server certificate does not cover this IP address");
        }
    }
}
