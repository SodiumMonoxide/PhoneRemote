using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteControl;

internal sealed record RemoteClientProfile(string Mode, string Address, string Fingerprint, string? DeviceToken = null)
{
    internal static RemoteClientProfile Create(string mode, string address, string fingerprint, string? deviceToken = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (mode is not ("lan" or "tailscale"))
            throw new ArgumentException("请选择局域网或 Tailscale。");
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("请输入电脑显示的 HTTPS IPv4 地址，不要附加路径、账户或参数。");
        var octets = ip.GetAddressBytes();
        bool tailscale = octets[0] == 100 && octets[1] is >= 64 and <= 127;
        bool lan = octets[0] is 10 or 127 || octets[0] == 172 && octets[1] is >= 16 and <= 31 ||
            octets[0] == 192 && octets[1] == 168;
        if (mode == "tailscale" ? !tailscale : !lan)
            throw new ArgumentException(mode == "tailscale" ? "Tailscale 模式需要电脑的 100.64–100.127 开头地址。" : "局域网模式需要电脑的内网 IPv4 地址。");
        string hex = string.Concat(fingerprint.Where(c => c != ':' && !char.IsWhiteSpace(c))).ToUpperInvariant();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("请填写完整的 SHA-256 证书指纹（64 个十六进制字符）。");
        if (deviceToken is not null && !IsToken(deviceToken))
            throw new ArgumentException("保存的配对凭据无效，请重新配对。");
        bool hasPort = uri.OriginalString[(uri.OriginalString.IndexOf("://", StringComparison.Ordinal) + 3)..]
            .TrimEnd('/').Contains(':');
        var canonical = new UriBuilder(uri) { Host = ip.ToString(), Port = hasPort ? uri.Port : 8443 };
        if (canonical.Port < 1)
            throw new ArgumentException("连接端口无效。");
        return new(mode, $"https://{ip}:{canonical.Port}",
            string.Join(":", Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2))), deviceToken);
    }

    internal static RemoteClientProfile ParseConnectionInfo(string mode, string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2)
            throw new ArgumentException("连接信息应为两行：HTTPS 地址和证书指纹。请在被控电脑点「复制连接信息」。");
        return Create(mode, lines[0], lines[1]);
    }

    internal static bool IsToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);
}

internal sealed class WindowsClientProfiles
{
    private readonly string path;
    private readonly object gate = new();

    internal WindowsClientProfiles(string? storagePath = null) => path = storagePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneRemote", "windows-client.dpapi");

    internal IReadOnlyDictionary<string, RemoteClientProfile> Load()
    {
        lock (gate)
            return Read();
    }

    internal RemoteClientProfile? Get(string mode) => Load().GetValueOrDefault(mode);

    internal void Save(RemoteClientProfile profile)
    {
        profile = RemoteClientProfile.Create(profile.Mode, profile.Address, profile.Fingerprint, profile.DeviceToken);
        lock (gate)
        {
            var profiles = Read();
            profiles[profile.Mode] = profile;
            Write(profiles);
        }
    }

    // Only this controlling computer's selected profile is removed; host pairings are separate files.
    internal void Forget(string mode)
    {
        lock (gate)
        {
            var profiles = Read();
            profiles.Remove(mode);
            Write(profiles);
        }
    }

    private Dictionary<string, RemoteClientProfile> Read()
    {
        if (!File.Exists(path))
            return new(StringComparer.Ordinal);
        if (new FileInfo(path).Length > 32 * 1024)
            throw new InvalidDataException("Windows 控制端连接设置文件过大，无法读取。");
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            var profiles = JsonSerializer.Deserialize<RemoteClientProfile[]>(plain)
                ?? throw new InvalidDataException("Windows 控制端连接设置内容无效。");
            if (profiles.Length > 2)
                throw new InvalidDataException("Windows 控制端连接设置内容无效。");
            var result = new Dictionary<string, RemoteClientProfile>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                if (profile is null || !result.TryAdd(profile.Mode,
                    RemoteClientProfile.Create(profile.Mode, profile.Address, profile.Fingerprint, profile.DeviceToken)))
                    throw new InvalidDataException("Windows 控制端连接设置重复或无效。");
            }
            return result;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidDataException("无法解密 Windows 控制端连接设置。请使用保存它的 Windows 账户；原有手机配对不受影响。", error);
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Write(Dictionary<string, RemoteClientProfile> profiles)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(profiles.Values.ToArray());
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
