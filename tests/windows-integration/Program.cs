using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteControl;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
await CheckEndpoint(ConnectionAddress.Lan, false);
await CheckEndpoint(ConnectionAddress.Tailscale, true);
Console.WriteLine("PASS isolated real-server integration; no valid OS input was injected");

static async Task CheckEndpoint(IPAddress address, bool tailscale)
{
    string mode = tailscale ? "tailscale" : "lan";
    string directory = Path.Combine(Path.GetTempPath(), "PhoneRemote-tests", "windows-real-" +
        mode + "-" + Guid.NewGuid().ToString("N"));
    var identity = new LocalIdentity(directory, new ConnectionAddress(address, 8, tailscale));
    string phoneCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    identity.SaveDevice(new SavedDevice(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phoneCredential))),
        IPAddress.Loopback.ToString(), address.ToString()));
    string legacyFile = Path.Combine(directory, "device-" + mode + ".json");
    byte[] legacy = File.ReadAllBytes(legacyFile);
    string settingsFile = Path.Combine(directory, "windows-test-client.dpapi");
    var profiles = new WindowsClientProfiles(settingsFile);
    RemoteClientProfile saved;
    string fingerprint;

    await using (var server = await RemoteServer.StartAsync(address, directory))
    using (var phone = new LegacyPhone(server))
    {
        fingerprint = server.Fingerprint;
        string phoneSession = await phone.Resume(phoneCredential);
        await phone.Disconnect(phoneSession);
        Check(server.RefreshCode(), mode + ": host opens additional enrollment explicitly");
        // Both synthetic endpoints are loopback. The client uses its LAN validator;
        // the server's Tailscale policy is independently exercised by the second binding.
        var profile = RemoteClientProfile.Create("lan", server.Address.AbsoluteUri, server.Fingerprint);
        using (var rejected = new WindowsRemoteClient(profile with { Fingerprint = new string('0', 64) }))
        {
            bool failed = false;
            try { await rejected.PairAsync(server.GetStatus().Code!, true, false); }
            catch (RemoteClientException) { failed = true; }
            Check(failed && !server.GetStatus().Connected && identity.LoadDevices().Count == 1,
                mode + ": wrong certificate pin prevents enrollment against real server");
        }
        using var windows = new WindowsRemoteClient(profile);
        saved = await windows.PairAsync(server.GetStatus().Code!, true, false);
        Check(windows.IsConnected && RemoteClientProfile.IsToken(saved.DeviceToken),
            mode + ": production Windows client pairs with production server");
        profiles.Save(saved);
        await windows.RequireKeyboardSupportAsync();
        Check(true, mode + ": authenticated new keyboard protocol advertised by real server");
        await windows.SendInputAsync(new ControlInput { Action = "heartbeat" });
        await windows.SendInputAsync(new ControlInput { Action = "releaseAll" });
        Check(true, mode + ": heartbeat and empty release accepted without OS input");
        Check(File.ReadAllBytes(legacyFile).SequenceEqual(legacy) && identity.LoadDevices().Count == 2,
            mode + ": client enrollment retains original phone pairing bytes");
        await CheckFrame(windows, mode + ": authenticated JPEG is decoded from real desktop capture");
        await ExpectStatus(() => windows.SendInputAsync(new ControlInput { Action = "invalid-test-command" }),
            HttpStatusCode.BadRequest, mode + ": real input endpoint rejects invalid command without OS injection");
        await CheckFrame(windows, mode + ": rejected input leaves authenticated frame stream usable");
        await windows.DisconnectAsync();
        Check(!windows.IsConnected && !server.GetStatus().Connected,
            mode + ": Windows disconnect ends active server session");
        await ExpectStatus(() => windows.GetFrameAsync(), HttpStatusCode.Unauthorized,
            mode + ": disconnected Windows client cannot read frames");
        await windows.ResumeAsync(false);
        Check(server.GetStatus().Connected, mode + ": Windows resumes without another pairing code");
        Check(await phone.TryResume(phoneCredential) == HttpStatusCode.Conflict,
            mode + ": old phone protocol cannot silently evict active Windows controller");
        phoneSession = await phone.Resume(phoneCredential, switchMode: true);
        await ExpectStatus(() => windows.SendInputAsync(new ControlInput { Action = "invalid-test-command" }),
            HttpStatusCode.Unauthorized, mode + ": explicit legacy phone takeover invalidates Windows input token");
        await phone.Disconnect(phoneSession);
    }

    await using (var server = await RemoteServer.StartAsync(address, directory))
    using (var phone = new LegacyPhone(server))
    using (var windows = new WindowsRemoteClient(new WindowsClientProfiles(settingsFile).Get("lan")!))
    {
        Check(server.Fingerprint == fingerprint && File.ReadAllBytes(legacyFile).SequenceEqual(legacy) &&
            server.GetStatus().Code is null, mode + ": host restart preserves both legacy identity and closed enrollment");
        await windows.ResumeAsync(false);
        await CheckFrame(windows, mode + ": DPAPI-restored Windows credential resumes after server restart");
        await windows.DisconnectAsync();
        string phoneSession = await phone.Resume(phoneCredential);
        await phone.Disconnect(phoneSession);
        Check(!server.GetStatus().Connected && identity.LoadDevices().Count == 2,
            mode + ": unchanged Android protocol still resumes after Windows upgrade and restart");
    }
}

static async Task CheckFrame(WindowsRemoteClient client, string name)
{
    byte[] jpeg = await client.GetFrameAsync();
    using var bytes = new MemoryStream(jpeg);
    using var image = Image.FromStream(bytes);
    Check(jpeg.Length > 100 && image.Width > 0 && image.Width <= 1600 && image.Height > 0 && image.Height <= 1000, name);
}

static async Task ExpectStatus(Func<Task> action, HttpStatusCode status, string name)
{
    bool found = false;
    try { await action(); }
    catch (RemoteClientException error) when (error.StatusCode == status) { found = true; }
    Check(found, name);
}

static void Check(bool success, string name)
{
    if (!success) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
}

sealed class LegacyPhone : IDisposable
{
    private readonly HttpClient client;
    private readonly string origin;

    internal LegacyPhone(RemoteServer server)
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            Convert.ToHexString(SHA256.HashData(certificate.RawData)) == server.Fingerprint.Replace(":", "");
        client = new HttpClient(handler) { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(5) };
        origin = server.Address.GetLeftPart(UriPartial.Authority);
    }

    internal async Task<string> Resume(string deviceToken, bool switchMode = false)
    {
        using var result = await Post("/api/resume", new { deviceToken, switchMode });
        result.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    internal async Task<HttpStatusCode> TryResume(string deviceToken)
    {
        using var result = await Post("/api/resume", new { deviceToken, switchMode = false });
        return result.StatusCode;
    }

    internal async Task Disconnect(string token)
    {
        using var result = await Post("/api/disconnect", new { }, token);
        result.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> Post(string path, object body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", origin);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public void Dispose() => client.Dispose();
}
