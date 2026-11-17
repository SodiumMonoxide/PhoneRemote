package work.codex.phoneremote;

import java.net.URI;
import java.util.Locale;
import java.util.regex.Pattern;

final class RemoteProfile {
    private static final Pattern IPV4 = Pattern.compile(
        "(?:0|[1-9][0-9]{0,2})(?:\\.(?:0|[1-9][0-9]{0,2})){3}");

    final String host;
    final String origin;
    final String fingerprint;
    final byte[] fingerprintBytes;
    final Mode mode;

    private RemoteProfile(String host, String fingerprint, Mode mode) {
        this.host = host;
        this.origin = "https://" + host + ":8443";
        this.fingerprint = fingerprint;
        this.mode = mode;
        fingerprintBytes = new byte[32];
        for (int index = 0; index < fingerprintBytes.length; index++) {
            fingerprintBytes[index] = (byte) Integer.parseInt(
                fingerprint.substring(index * 2, index * 2 + 2), 16);
        }
    }

    static RemoteProfile parse(String address, String fingerprint) {
        String value = address.trim();
        URI url;
        try {
            url = URI.create(value.contains("://") ? value : "https://" + value);
        } catch (IllegalArgumentException error) {
            throw new IllegalArgumentException("请输入电脑端显示的 HTTPS 地址。");
        }
        String host = url.getHost();
        Mode mode = addressMode(host);
        if (!"https".equalsIgnoreCase(url.getScheme()) || host == null ||
            url.getRawUserInfo() != null || url.getRawQuery() != null || url.getRawFragment() != null ||
            !(url.getRawPath().isEmpty() || "/".equals(url.getRawPath())) ||
            !(url.getPort() == -1 || url.getPort() == 8443) || mode == null) {
            throw new IllegalArgumentException("请输入电脑端显示的局域网或 Tailscale IPv4 地址，端口为 8443。");
        }
        String pin = fingerprint.replaceAll("[:\\s]", "").toUpperCase(Locale.ROOT);
        if (!pin.matches("[0-9A-F]{64}")) {
            throw new IllegalArgumentException("请粘贴电脑端显示的完整 SHA-256 证书指纹。");
        }
        return new RemoteProfile(host, pin, mode);
    }

    static RemoteProfile parseConnectionInfo(CharSequence text) {
        if (text != null && text.length() <= 320) {
            String[] lines = text.toString().trim().split("\\r\\n|\\n|\\r", -1);
            if (lines.length == 2)
                return parse(lines[0], lines[1]);
        }
        throw new IllegalArgumentException("请粘贴电脑端复制的两行文字，第一行是电脑地址，第二行是证书指纹。");
    }

    private static Mode addressMode(String host) {
        if (host == null || !IPV4.matcher(host).matches()) return null;
        String[] values = host.split("\\.");
        int first = Integer.parseInt(values[0]);
        int second = Integer.parseInt(values[1]);
        if (second > 255 || Integer.parseInt(values[2]) > 255 || Integer.parseInt(values[3]) > 255)
            return null;
        if (first == 100 && second >= 64 && second <= 127) return Mode.TAILSCALE;
        if (first == 10 || (first == 172 && second >= 16 && second <= 31) ||
            (first == 192 && second == 168)) return Mode.LAN;
        return null;
    }

    boolean matches(RemoteProfile other) {
        return other != null && origin.equals(other.origin) && fingerprint.equals(other.fingerprint);
    }

    enum Mode {
        LAN("lan", "局域网"),
        TAILSCALE("tailscale", "Tailscale");

        final String key;
        final String label;

        Mode(String key, String label) {
            this.key = key;
            this.label = label;
        }

        static Mode fromKey(String key) {
            for (Mode mode : values()) {
                if (mode.key.equals(key)) return mode;
            }
            return null;
        }
    }
}
