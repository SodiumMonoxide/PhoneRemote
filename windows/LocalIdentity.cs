using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RemoteControl;

internal sealed record SavedDevice(string TokenHash, string Peer, string Host);

internal sealed class LocalIdentity(string directory, ConnectionAddress endpoint)
{
    internal const int MaximumDevices = 16;
    private string DevicePath => Path.Combine(directory, endpoint.IsTailscale
        ? "device-tailscale.json" : "device-lan.json");
    private string AdditionalDevicesPath => Path.Combine(directory, endpoint.IsTailscale
        ? "devices-additional-tailscale.json" : "devices-additional-lan.json");

    public SavedDevice? LoadDevice()
    {
        try
        {
            var device = JsonSerializer.Deserialize<SavedDevice>(File.ReadAllBytes(DevicePath));
            Validate(device);
            return MatchesEndpoint(device!) ? device : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void SaveDevice(SavedDevice device) =>
        WriteAtomically(DevicePath, JsonSerializer.SerializeToUtf8Bytes(device));

    public IReadOnlyList<SavedDevice> LoadDevices()
    {
        var devices = LoadAdditionalDevices();
        if (LoadDevice() is { } legacy)
            devices.Insert(0, legacy);
        return devices.Where(MatchesEndpoint).DistinctBy(device => device.TokenHash).ToArray();
    }

    public void AddDevice(SavedDevice device)
    {
        Validate(device);
        var devices = LoadAdditionalDevices();
        // Keep the original single-device file unchanged during upgrades and enrollment.
        // Count all stored records, including addresses that are temporarily unavailable.
        int count = devices.Count + (File.Exists(DevicePath) ? 1 : 0);
        if (count >= MaximumDevices)
            throw new IOException("已记住的设备已达上限，请先在电脑上忘记所有设备后重新配对。");
        devices.Add(device);
        WriteAtomically(AdditionalDevicesPath, JsonSerializer.SerializeToUtf8Bytes(devices));
    }

    public void ForgetDevice()
    {
        File.Delete(AdditionalDevicesPath);
        File.Delete(DevicePath);
    }

    private List<SavedDevice> LoadAdditionalDevices()
    {
        try
        {
            if (new FileInfo(AdditionalDevicesPath).Length > 32 * 1024)
                throw new InvalidDataException("已记住的设备记录过大。");
            var devices = JsonSerializer.Deserialize<List<SavedDevice>>(File.ReadAllBytes(AdditionalDevicesPath));
            if (devices is null || devices.Count > MaximumDevices)
                throw new InvalidDataException("已记住的设备记录无法读取。");
            foreach (var device in devices)
                Validate(device);
            return devices;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static void Validate(SavedDevice? device)
    {
        if (device?.TokenHash is not { Length: 64 } || !device.TokenHash.All(Uri.IsHexDigit) ||
            !IPAddress.TryParse(device.Peer, out _) || !IPAddress.TryParse(device.Host, out _))
            throw new InvalidDataException("已记住的设备记录无法读取。");
    }

    private bool MatchesEndpoint(SavedDevice device) =>
        endpoint.Address.Equals(IPAddress.Parse(device.Host)) && endpoint.AllowsPeer(IPAddress.Parse(device.Peer));

    public X509Certificate2 LoadCertificate()
    {
        string path = Path.Combine(directory, $"https-{endpoint.Address}.dpapi");
        if (File.Exists(path))
        {
            byte[] encrypted = File.ReadAllBytes(path);
            byte[] pfx = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var existing = Import(pfx);
                if (existing.HasPrivateKey && existing.NotBefore <= DateTime.Now && existing.NotAfter > DateTime.Now)
                    return existing;
                existing.Dispose();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }

        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Phone Remote Control", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(endpoint.Address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var now = DateTimeOffset.UtcNow;
        using var generated = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(2));
        byte[] newPfx = generated.Export(X509ContentType.Pfx);
        try
        {
            var cert = Import(newPfx);
            try
            {
                WriteAtomically(path, ProtectedData.Protect(newPfx, null, DataProtectionScope.CurrentUser));
                return cert;
            }
            catch
            {
                cert.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newPfx);
        }
    }

    private static X509Certificate2 Import(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);

    private void WriteAtomically(string path, byte[] data)
    {
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
