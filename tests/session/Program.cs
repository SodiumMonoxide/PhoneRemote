using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteControl;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
var storage = Path.Combine(Path.GetTempPath(), "PhoneRemote-tests",
    "state-" + Guid.NewGuid().ToString("N"));
var control = new RemoteControlSession();
string lanDevice;
string tailscaleDevice;
string lanPin;
string tailscalePin;
await using (var lan = await RemoteServer.StartAsync(ConnectionAddress.Lan, storage, control))
await using (var tailscale = await RemoteServer.StartAsync(ConnectionAddress.Tailscale, storage, control))
using (var local = new EndpointClient(lan))
using (var remote = new EndpointClient(tailscale))
{
    lanPin = lan.Fingerprint;
    tailscalePin = tailscale.Fingerprint;
    Check(lanPin != tailscalePin && await local.Get("/") == HttpStatusCode.OK &&
          await remote.Get("/") == HttpStatusCode.OK, "two independent pinned HTTPS listeners");
    var localPair = await local.Post("/api/pair", new { code = lan.GetStatus().Code, remember = true });
    string oldSession = localPair.Token();
    lanDevice = localPair.Token("deviceToken");
    Check(lan.GetStatus().Connected && !tailscale.GetStatus().Connected, "LAN owns desktop after pairing");
    var refused = await remote.Post("/api/pair", new { code = tailscale.GetStatus().Code, remember = true });
    Check(refused.Status == HttpStatusCode.Conflict && !tailscale.GetStatus().RememberedDevice,
        "failed unrequested takeover preserves code and enrollment state");
    var remotePair = await remote.Post("/api/pair",
        new { code = tailscale.GetStatus().Code, remember = true, switchMode = true });
    tailscaleDevice = remotePair.Token("deviceToken");
    string remoteSession = remotePair.Token();
    Check(lanDevice != tailscaleDevice && lan.GetStatus().RememberedDevice && tailscale.GetStatus().RememberedDevice,
        "both mode enrollments are independent");
    Check(await local.Get("/api/frame", oldSession) == HttpStatusCode.Unauthorized &&
          (await local.Post("/api/input", new { action = "up" }, oldSession)).Status == HttpStatusCode.Unauthorized,
        "mode switch rejects both frame and input from old session");
    Check(await local.Get("/api/frame", remoteSession) == HttpStatusCode.Unauthorized,
        "live token is restricted to its listener");
    Check((await local.Post("/api/resume", new { deviceToken = tailscaleDevice, switchMode = true })).Status ==
          HttpStatusCode.Unauthorized && tailscale.GetStatus().Connected,
        "invalid takeover cannot disturb current controller");
    Check((await local.Post("/api/resume", new { deviceToken = lanDevice })).Status == HttpStatusCode.Conflict,
        "background resume cannot evict a different mode");
    var restored = await local.Post("/api/resume", new { deviceToken = lanDevice, switchMode = true });
    Check(lan.GetStatus().Connected && !tailscale.GetStatus().Connected &&
        (await local.Post("/api/input", new { action = "up" }, restored.Token())).Status == HttpStatusCode.NoContent,
        "explicit LAN selection restores control without a code");
    Check((await remote.Post("/api/disconnect", new { }, remoteSession)).Status == HttpStatusCode.Unauthorized &&
        lan.GetStatus().Connected, "late disconnect on old mode cannot terminate new session");
}

await using (var lan = await RemoteServer.StartAsync(ConnectionAddress.Lan, storage, control))
await using (var tailscale = await RemoteServer.StartAsync(ConnectionAddress.Tailscale, storage, control))
using (var local = new EndpointClient(lan))
using (var remote = new EndpointClient(tailscale))
{
    Check(lan.Fingerprint == lanPin && tailscale.Fingerprint == tailscalePin && lan.GetStatus().RememberedDevice &&
          tailscale.GetStatus().RememberedDevice, "both pins and enrollments survive app restart");
    var automatic = await Task.WhenAll(
        local.Post("/api/resume", new { deviceToken = lanDevice }),
        remote.Post("/api/resume", new { deviceToken = tailscaleDevice }));
    Check(automatic.Count(result => result.Status == HttpStatusCode.OK) == 1 &&
        automatic.Count(result => result.Status == HttpStatusCode.Conflict) == 1,
        "simultaneous background resumes elect one controller");

    using var changedIp = new EndpointClient(lan, IPAddress.Parse("127.0.0.3"));
    var relocated = await changedIp.Post("/api/resume", new { deviceToken = lanDevice, switchMode = true });
    string relocatedSession = relocated.Token();
    Check(await changedIp.Get("/api/frame", relocatedSession) == HttpStatusCode.OK &&
          await local.Get("/api/frame", relocatedSession) == HttpStatusCode.Unauthorized,
        "LAN device resumes after same-subnet IP change and new session remains IP scoped");
    tailscale.ForgetDevice();
    Check(lan.GetStatus().Connected && lan.GetStatus().RememberedDevice && !tailscale.GetStatus().RememberedDevice,
        "forgetting the other mode leaves the current session and pairing");
    await tailscale.DisposeAsync();
    Check(await changedIp.Get("/api/frame", relocatedSession) == HttpStatusCode.OK,
        "stopping other listener leaves active connection intact");
}
await CheckAdditionalPairing(ConnectionAddress.Lan, false);
await CheckAdditionalPairing(ConnectionAddress.Tailscale, true);
Console.WriteLine("PASS live device settings were isolated from the smoke test");

static async Task CheckAdditionalPairing(IPAddress address, bool isTailscale)
{
    string mode = isTailscale ? "tailscale" : "lan";
    string stateDirectory = Path.Combine(Path.GetTempPath(), "PhoneRemote-tests",
        "additional-" + mode + "-" + Guid.NewGuid().ToString("N"));
    var identity = new LocalIdentity(stateDirectory, new ConnectionAddress(address, 8, isTailscale));
    string phoneCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    string phoneHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phoneCredential)));
    identity.SaveDevice(new SavedDevice(phoneHash, IPAddress.Loopback.ToString(), address.ToString()));
    string legacyPath = Path.Combine(stateDirectory, "device-" + mode + ".json");
    byte[] originalBytes = File.ReadAllBytes(legacyPath);
    string windowsCredential;
    string pin;

    await using (var server = await RemoteServer.StartAsync(address, stateDirectory))
    using (var client = new EndpointClient(server))
    {
        pin = server.Fingerprint;
        Check(server.GetStatus().RememberedDevice && server.GetStatus().Code is null,
            mode + ": existing phone pairing starts with enrollment closed");
        var phone = await client.Post("/api/resume", new { deviceToken = phoneCredential });
        Check(phone.Status == HttpStatusCode.OK && !server.RefreshCode(),
            mode + ": existing phone resumes and active control blocks new enrollment");
        Check((await client.Post("/api/pair", new { code = "00000000", remember = true })).Status ==
            HttpStatusCode.Conflict, mode + ": new pair cannot disturb active phone");
        await client.Post("/api/disconnect", new { }, phone.Token());
        Check(server.RefreshCode() && server.GetStatus().Code is { Length: 8 },
            mode + ": explicit new code permits additional device without forgetting phone");
        var windows = await client.Post("/api/pair", new { code = server.GetStatus().Code, remember = true });
        windowsCredential = windows.Token("deviceToken");
        Check(File.ReadAllBytes(legacyPath).SequenceEqual(originalBytes) &&
            identity.LoadDevices().Count == 2 && !File.ReadAllText(Path.Combine(stateDirectory,
                "devices-additional-" + mode + ".json")).Contains(windowsCredential, StringComparison.Ordinal),
            mode + ": Windows pairing appends only a token hash and leaves legacy file unchanged");
        Check((await client.Post("/api/resume", new { deviceToken = phoneCredential })).Status ==
            HttpStatusCode.Conflict, mode + ": different saved device cannot silently take over same mode");
        Check((await client.Post("/api/resume", new { deviceToken = windowsCredential })).Status ==
            HttpStatusCode.OK, mode + ": same device can reconnect to its own active mode");
        var phoneAgain = await client.Post("/api/resume", new { deviceToken = phoneCredential, switchMode = true });
        Check(phoneAgain.Status == HttpStatusCode.OK &&
            await client.Get("/api/frame", windows.Token()) == HttpStatusCode.Unauthorized,
            mode + ": explicit handover works and invalidates previous session");
    }

    await using (var server = await RemoteServer.StartAsync(address, stateDirectory))
    using (var client = new EndpointClient(server))
    {
        Check(server.Fingerprint == pin && server.GetStatus().Code is null &&
            File.ReadAllBytes(legacyPath).SequenceEqual(originalBytes),
            mode + ": restart preserves certificate and original pairing without opening enrollment");
        var phone = await client.Post("/api/resume", new { deviceToken = phoneCredential });
        Check(phone.Status == HttpStatusCode.OK, mode + ": legacy phone still resumes after restart");
        await client.Post("/api/disconnect", new { }, phone.Token());
        var windows = await client.Post("/api/resume", new { deviceToken = windowsCredential });
        Check(windows.Status == HttpStatusCode.OK, mode + ": added Windows device resumes after restart");
        using var movedClient = new EndpointClient(server, IPAddress.Parse("127.0.0.3"));
        var moved = await movedClient.Post("/api/resume", new { deviceToken = windowsCredential, switchMode = true });
        Check(moved.Status == (isTailscale ? HttpStatusCode.Unauthorized : HttpStatusCode.OK),
            mode + ": added pairing preserves mode-specific source-address policy");
        server.ForgetDevice();
        Check(!server.GetStatus().RememberedDevice && !server.GetStatus().Connected &&
            (await client.Post("/api/resume", new { deviceToken = phoneCredential })).Status == HttpStatusCode.Unauthorized &&
            (await client.Post("/api/resume", new { deviceToken = windowsCredential })).Status == HttpStatusCode.Unauthorized &&
            !File.Exists(legacyPath), mode + ": forget all revokes both legacy and additional credentials");
    }

    for (int index = 0; index < LocalIdentity.MaximumDevices; index++)
        identity.AddDevice(new SavedDevice(index.ToString("X64"), IPAddress.Loopback.ToString(), address.ToString()));
    bool capped = false;
    try { identity.AddDevice(new SavedDevice(new string('F', 64), IPAddress.Loopback.ToString(), address.ToString())); }
    catch (IOException) { capped = true; }
    Check(capped && identity.LoadDevices().Count == LocalIdentity.MaximumDevices,
        mode + ": enrollment capacity limit preserves every existing record");
}

static void Check(bool success, string name)
{
    if (!success) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
}

sealed class EndpointClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string origin;

    public EndpointClient(RemoteServer server, IPAddress? source = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null &&
            Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == server.Fingerprint.Replace(":", "");
        if (source is not null)
        {
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        http = new HttpClient(handler) { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(5) };
        origin = server.Address.GetLeftPart(UriPartial.Authority);
    }

    public async Task<HttpStatusCode> Get(string path, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    public async Task<Reply> Post(string path, object data, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(data) };
        request.Headers.Add("Origin", origin);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => http.Dispose();

    public sealed record Reply(HttpStatusCode Status, string Body)
    {
        public string Token(string property = "token")
        {
            if (Status != HttpStatusCode.OK) throw new InvalidOperationException("Expected token, HTTP " + Status);
            using var json = JsonDocument.Parse(Body);
            return json.RootElement.GetProperty(property).GetString()!;
        }
    }
}

