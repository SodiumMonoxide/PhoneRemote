using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RemoteControl;

internal sealed record SavedDevice(string TokenHash, string Peer, string Host);

internal sealed class LocalIdentity(string directory, ConnectionAddress endpoint)
{
    private string DevicePath => Path.Combine(directory, endpoint.IsTailscale
        ? "device-tailscale.json" : "device-lan.json");

    public SavedDevice? LoadDevice()
    {
        try
        {
            var device = JsonSerializer.Deserialize<SavedDevice>(File.ReadAllBytes(DevicePath));
            if (device?.TokenHash is not { Length: 64 } || !device.TokenHash.All(Uri.IsHexDigit) ||
                !IPAddress.TryParse(device.Peer, out IPAddress? peer) ||
                !IPAddress.TryParse(device.Host, out IPAddress? host))
                throw new InvalidDataException("已记住的手机记录无法读取。");
            return endpoint.Address.Equals(host) && endpoint.AllowsPeer(peer) ? device : null;
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

    public void ForgetDevice() => File.Delete(DevicePath);

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
