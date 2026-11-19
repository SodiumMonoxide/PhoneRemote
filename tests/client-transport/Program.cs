using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RemoteControl;

await Checks.RunAsync();

static class Checks
{
    private static int passed;
    private static readonly string Pin = new('A', 64);
    private static readonly string Device = new('B', 64);

    internal static async Task RunAsync()
    {
        var parsed = RemoteClientProfile.ParseConnectionInfo("lan", "https://192.168.10.20:8443/\r\n" + Pin);
        Check(parsed.Address == "https://192.168.10.20:8443" && parsed.Fingerprint.Length == 95, "paste normalizes address and fingerprint");
        Check(RemoteClientProfile.Create("lan", "https://10.0.0.1", Pin).Address.EndsWith(":8443"), "default service port");
        var explicitHttpsPort = RemoteClientProfile.Create("lan", "https://10.0.0.1:443", Pin);
        Check(explicitHttpsPort.Address.EndsWith(":443") && RemoteClientProfile.Create(explicitHttpsPort.Mode,
            explicitHttpsPort.Address, explicitHttpsPort.Fingerprint) == explicitHttpsPort, "explicit HTTPS default port retained across validation");
        foreach (string invalid in new[] { "http://10.0.0.1:8443", "https://user@10.0.0.1", "https://10.0.0.1/a", "https://10.0.0.1?q=1", "https://10.0.0.1#x", "https://8.8.8.8", "https://localhost", "https://[::1]", "https://10.0.0.1:0" })
            Reject(() => RemoteClientProfile.Create("lan", invalid, Pin), "reject unsuitable origin");
        Reject(() => RemoteClientProfile.Create("tailscale", "https://10.0.0.1:8443", Pin), "mode mismatch rejected");
        Reject(() => RemoteClientProfile.Create("lan", "https://10.0.0.1:8443", "abcd"), "short pin rejected");
        Reject(() => RemoteClientProfile.Create("lan", null!, Pin), "missing address rejected");
        Reject(() => RemoteClientProfile.Create("lan", "https://10.0.0.1:8443", null!), "missing fingerprint rejected");

        string scratch = Path.Combine(Path.GetTempPath(), "PhoneRemote-client-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string file = Path.Combine(scratch, "client.dpapi");
        var store = new WindowsClientProfiles(file);
        Check(store.Load().Count == 0, "empty client profile store");
        store.Save(parsed with { DeviceToken = Device });
        var tail = RemoteClientProfile.Create("tailscale", "https://100.64.0.2:8443", Pin, new string('C', 64));
        store.Save(tail);
        Check(new WindowsClientProfiles(file).Get("lan")?.DeviceToken == Device && store.Load().Count == 2, "independent profiles survive reopen");
        byte[] protectedBytes = File.ReadAllBytes(file);
        Check(!Encoding.UTF8.GetString(protectedBytes).Contains(Device, StringComparison.Ordinal) &&
            !Encoding.UTF8.GetString(protectedBytes).Contains("192.168", StringComparison.Ordinal), "stored credentials and address are encrypted");
        store.Forget("lan");
        Check(store.Get("lan") is null && store.Get("tailscale") == tail, "forget one mode preserves the other");
        File.WriteAllBytes(file, [1, 2, 3]);
        try { store.Save(parsed); throw new Exception("corruption overwritten"); }
        catch (InvalidDataException) { Check(File.ReadAllBytes(file).SequenceEqual(new byte[] { 1, 2, 3 }), "corrupt store fails without overwrite"); }
        File.Delete(file);
        Directory.Delete(scratch);

        await using var host = await MockHost.StartAsync();
        using var client = new WindowsRemoteClient(host.Profile);
        var paired = await client.PairAsync("12345678", true, false);
        Check(client.IsConnected && paired.DeviceToken == MockHost.DeviceToken, "pair returns remembered token");
        await client.RequireKeyboardSupportAsync();
        Check(true, "authenticated keyboard capability accepted");
        host.Behavior = "oldhost";
        await RejectRemote(() => client.RequireKeyboardSupportAsync(), HttpStatusCode.NotFound, "older host rejected before capture");
        host.Behavior = "nocapability";
        await RejectRemote(() => client.RequireKeyboardSupportAsync(), null, "missing keyboard capability rejected");
        host.Behavior = "";
        Check(host.LastPair?.GetProperty("clientType").GetString() == "windows" &&
            host.LastPair?.GetProperty("switchMode").GetBoolean() == false, "Windows client identity and takeover intent sent");
        Check((await client.GetFrameAsync()).SequenceEqual(MockHost.Frame), "authenticated frame retrieval");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => client.SendInputAsync(new ControlInput { Action = "text", Text = i.ToString() })));
        Check(host.Inputs.SequenceEqual(Enumerable.Range(0, 12).Select(i => i.ToString())), "input commands preserve ordering");
        await client.DisconnectAsync();
        Check(!client.IsConnected && client.Profile.DeviceToken == MockHost.DeviceToken, "disconnect preserves saved pair");
        using var resumed = new WindowsRemoteClient(paired);
        await resumed.ResumeAsync(true);
        Check(resumed.IsConnected && host.ResumeSwitched, "saved pair reconnect and explicit takeover");

        using var wrongPin = new WindowsRemoteClient(host.Profile with { Fingerprint = new string('0', 64) });
        await RejectRemote(() => wrongPin.PairAsync("12345678", true, false), null, "wrong pin rejected");
        Check(host.PairCount == 1, "pin failure sends no authentication request");
        host.Behavior = "redirect";
        await RejectRemote(() => resumed.GetFrameAsync(), HttpStatusCode.Found, "redirect rejected");
        Check(host.TrapRequests == 0, "redirect target never requested");
        host.Behavior = "mime";
        await RejectRemote(() => resumed.GetFrameAsync(), null, "unexpected frame type rejected");
        host.Behavior = "large";
        await RejectRemote(() => resumed.GetFrameAsync(), null, "oversized declared frame rejected");
        host.Behavior = "chunked";
        await RejectRemote(() => resumed.GetFrameAsync(), null, "oversized streamed frame rejected");
        host.Behavior = "slow";
        using (var cancel = new CancellationTokenSource(80))
        {
            try { await resumed.GetFrameAsync(cancel.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { Check(true, "caller cancellation preserved"); }
        }
        await RejectRemote(() => resumed.GetFrameAsync(), null, "request deadline terminates stalled server");
        host.Behavior = "unauthorized";
        await RejectRemote(() => resumed.GetFrameAsync(), HttpStatusCode.Unauthorized, "expired session reported");
        Check(!resumed.IsConnected && resumed.Profile.DeviceToken == MockHost.DeviceToken, "expired session keeps remembered credential");
        host.Behavior = "badtoken";
        using var badToken = new WindowsRemoteClient(host.Profile);
        await RejectRemote(() => badToken.PairAsync("12345678", false, false), null, "malformed authentication response rejected");
        Check(!badToken.IsConnected, "malformed auth never establishes session");

        await using var mismatched = await MockHost.StartAsync(wrongName: true);
        using var wrongName = new WindowsRemoteClient(mismatched.Profile);
        await RejectRemote(() => wrongName.PairAsync("12345678", false, false), null, "correct pin with wrong certificate name rejected");
        Check(mismatched.PairCount == 0, "name failure sends no pairing code");
        await using var expired = await MockHost.StartAsync(expired: true);
        using var stale = new WindowsRemoteClient(expired.Profile);
        await RejectRemote(() => stale.PairAsync("12345678", false, false), null, "expired pinned certificate rejected");
        Check(expired.PairCount == 0, "expired certificate receives no pairing code");
        Console.WriteLine($"PASS: {passed} client transport/profile checks; isolated loopback HTTPS, no real device records touched.");
    }

    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception("FAIL: " + description);
        passed++;
        Console.WriteLine("PASS " + description);
    }

    private static void Reject(Action action, string description)
    {
        try { action(); throw new Exception("accepted invalid value"); }
        catch (ArgumentException) { Check(true, description); }
    }

    private static async Task RejectRemote(Func<Task> action, HttpStatusCode? code, string description)
    {
        try { await action(); throw new Exception("accepted invalid response"); }
        catch (RemoteClientException error) { Check(error.StatusCode == code, description); }
    }
}

sealed class MockHost(WebApplication app, X509Certificate2 cert) : IAsyncDisposable
{
    internal const string SessionToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    internal const string DeviceToken = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    internal static readonly byte[] Frame = [0xff, 0xd8, 0xff, 0xd9];
    internal RemoteClientProfile Profile { get; private set; } = null!;
    internal string Behavior = "normal";
    internal int PairCount;
    internal int TrapRequests;
    internal JsonElement? LastPair;
    internal bool ResumeSwitched;
    internal List<string> Inputs = [];

    internal static async Task<MockHost> StartAsync(bool wrongName = false, bool expired = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PhoneRemote isolated test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(wrongName ? "127.0.0.2" : "127.0.0.1"));
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1));
        var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert)));
        var app = builder.Build();
        var mock = new MockHost(app, cert);
        app.Run(mock.HandleAsync);
        await app.StartAsync();
        mock.Profile = RemoteClientProfile.Create("lan", app.Urls.Single(), Convert.ToHexString(SHA256.HashData(cert.RawData)));
        return mock;
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Path == "/trap") { TrapRequests++; return; }
        if (context.Request.Method == "POST" && context.Request.Headers.Origin != Profile.Address)
        {
            context.Response.StatusCode = 403; return;
        }
        string path = context.Request.Path.Value!;
        if (path == "/api/pair")
        {
            PairCount++;
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            LastPair = doc.RootElement.Clone();
            await context.Response.WriteAsJsonAsync(new { token = Behavior == "badtoken" ? "short" : SessionToken, deviceToken = DeviceToken });
            return;
        }
        if (path == "/api/resume")
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            if (doc.RootElement.GetProperty("deviceToken").GetString() != DeviceToken)
            { context.Response.StatusCode = 401; return; }
            ResumeSwitched = doc.RootElement.GetProperty("switchMode").GetBoolean();
            await context.Response.WriteAsJsonAsync(new { token = SessionToken });
            return;
        }
        if (context.Request.Headers.Authorization != "Bearer " + SessionToken || Behavior == "unauthorized")
        { context.Response.StatusCode = 401; return; }
        if (path == "/api/capabilities")
        {
            if (Behavior == "oldhost") { context.Response.StatusCode = 404; return; }
            await context.Response.WriteAsJsonAsync(new { rawKeyboard = Behavior != "nocapability" });
            return;
        }
        if (path == "/api/input")
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            Inputs.Add(doc.RootElement.GetProperty("text").GetString()!);
            await Task.Delay(5);
            context.Response.StatusCode = 204; return;
        }
        if (path == "/api/disconnect") { context.Response.StatusCode = 204; return; }
        if (path != "/api/frame") { context.Response.StatusCode = 404; return; }
        if (Behavior == "redirect") { context.Response.Redirect(Profile.Address + "/trap"); return; }
        if (Behavior == "slow")
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted); }
            catch (OperationCanceledException) { }
            return;
        }
        context.Response.ContentType = Behavior == "mime" ? "text/plain" : "image/jpeg";
        byte[] bytes = Behavior is "large" or "chunked" ? new byte[2 * 1024 * 1024 + 1] : Frame;
        if (Behavior != "chunked") context.Response.ContentLength = bytes.Length;
        try { await context.Response.Body.WriteAsync(bytes, context.RequestAborted); }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        cert.Dispose();
    }
}
