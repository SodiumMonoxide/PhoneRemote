using System.Globalization;
using System.Net;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteControl;

internal sealed record ServerStatus(string? Code, DateTimeOffset? CodeExpiresAt, bool Connected, bool RememberedDevice);

internal sealed class RemoteServer : IAsyncDisposable
{
    private const int Port = 8443;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly WebAsset Page = LoadAsset("index.html", "text/html; charset=utf-8");
    private static readonly WebAsset Style = LoadAsset("styles.css", "text/css; charset=utf-8");
    private static readonly WebAsset Script = LoadAsset("app.js", "text/javascript; charset=utf-8");

    private readonly RemoteControlSession control;
    private object state => control.StateLock;
    private readonly ConnectionAddress endpoint;
    private readonly LocalIdentity identity;
    private readonly X509Certificate2 certificate;
    private readonly WebApplication app;
    private readonly System.Threading.Timer timer;
    private string? code;
    private DateTimeOffset codeExpiresAt;
    private int failedAttempts;
    private DateTimeOffset retryAt;
    private SavedDevice? remembered;
    private bool closing;

    private RemoteServer(ConnectionAddress address, string storageDirectory, RemoteControlSession control)
    {
        this.control = control;
        endpoint = address;
        IPAddress localAddress = address.Address;
        Address = new UriBuilder(Uri.UriSchemeHttps, localAddress.ToString(), Port).Uri;
        identity = new LocalIdentity(storageDirectory, address);
        remembered = identity.LoadDevice();
        certificate = identity.LoadCertificate();
        Fingerprint = string.Join(":", SHA256.HashData(certificate.RawData).Select(b => b.ToString("X2")));
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxConcurrentConnections = 24;
                options.Limits.MaxRequestBodySize = 4096;
                options.Limits.MaxRequestHeadersTotalSize = 8192;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                options.Listen(localAddress, Port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    listen.UseHttps(certificate, https =>
                        https.HandshakeTimeout = TimeSpan.FromSeconds(5));
                });
            });
            app = builder.Build();
            app.Run(HandleAsync);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
        RefreshCode();
        timer = new System.Threading.Timer(_ => MaintainSession(), null, 350, 350);
    }

    public Uri Address { get; }
    public string Fingerprint { get; }
    public bool IsTailscale => endpoint.IsTailscale;

    public static IReadOnlyList<IPAddress> GetLocalAddresses() =>
        ConnectionAddress.Find().Select(entry => entry.Address).Distinct().ToArray();

    public static Task<RemoteServer> StartAsync(IPAddress address, RemoteControlSession control) =>
        StartAsync(address, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneRemote"), control);

    public static Task<RemoteServer> StartAsync(IPAddress address) => StartAsync(address, new RemoteControlSession());

    internal static Task<RemoteServer> StartAsync(IPAddress address, string storageDirectory) =>
        StartAsync(address, storageDirectory, new RemoteControlSession());

    internal static async Task<RemoteServer> StartAsync(IPAddress address, string storageDirectory,
        RemoteControlSession control)
    {
        ConnectionAddress local = ConnectionAddress.Find().FirstOrDefault(entry => entry.Address.Equals(address))
            ?? throw new InvalidOperationException("所选地址已不可用，请刷新地址列表。");
        return await Task.Run(async () =>
        {
            var server = new RemoteServer(local, storageDirectory, control);
            try
            {
                await server.app.StartAsync();
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        });
    }

    public ServerStatus GetStatus()
    {
        lock (state)
        {
            MaintainSessionLocked();
            return new ServerStatus(code, code is null ? null : codeExpiresAt, control.IsConnectedLocked(this),
                remembered is not null);
        }
    }

    public bool RefreshCode()
    {
        lock (state)
        {
            MaintainSessionLocked();
            if (closing || control.IsConnectedLocked(this) || remembered is not null)
                return false;
            code = RandomNumberGenerator.GetInt32(100_000_000).ToString("D8", CultureInfo.InvariantCulture);
            codeExpiresAt = DateTimeOffset.UtcNow + CodeLifetime;
            return true;
        }
    }

    public void Disconnect()
    {
        lock (state)
        {
            code = null;
            control.DisconnectLocked(this);
        }
    }

    public void ForgetDevice()
    {
        lock (state)
        {
            if (remembered is null)
                return;
            identity.ForgetDevice();
            remembered = null;
            code = null;
            control.DisconnectLocked(this);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (state)
        {
            if (closing)
                return;
            closing = true;
            code = null;
            control.DisconnectLocked(this);
        }
        await timer.DisposeAsync();
        try
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(stopDeadline.Token);
        }
        catch (OperationCanceledException)
        {
            // Continue disposal once the shutdown deadline elapses.
        }
        finally
        {
            try
            {
                await app.DisposeAsync();
            }
            finally
            {
                certificate.Dispose();
            }
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        SetResponseHeaders(context.Response);
        var peer = context.Connection.RemoteIpAddress;
        if (!string.Equals(context.Request.Host.Value, Address.Authority, StringComparison.Ordinal) ||
            peer is null || !endpoint.AllowsPeer(peer))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await ServeGetAsync(context, peer);
                return;
            }
            if (HttpMethods.IsPost(context.Request.Method))
            {
                await ServePostAsync(context, peer);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error)
        {
            context.Response.StatusCode = error.StatusCode;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private async Task ServeGetAsync(HttpContext context, IPAddress peer)
    {
        WebAsset? asset = context.Request.Path.Value switch
        {
            "/" => Page,
            "/styles.css" => Style,
            "/app.js" => Script,
            _ => null
        };
        if (asset is not null)
        {
            await WriteBytesAsync(context, asset.Bytes, asset.ContentType);
            return;
        }
        if (context.Request.Path.Value != "/api/frame")
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var token = ReadToken(context.Request);
        lock (state)
        {
            if (!AuthorizeLocked(token, peer))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        byte[]? frame = control.Capture();
        lock (state)
        {
            if (!AuthorizeLocked(token, peer))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        if (frame is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        await WriteBytesAsync(context, frame, "image/jpeg");
    }

    private async Task ServePostAsync(HttpContext context, IPAddress peer)
    {
        HttpRequest request = context.Request;
        if (!string.Equals(request.Headers.Origin.ToString(), Address.GetLeftPart(UriPartial.Authority),
                StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!request.HasJsonContentType())
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }
        switch (request.Path.Value)
        {
            case "/api/pair":
                var pairing = await JsonSerializer.DeserializeAsync<PairRequest>(request.Body,
                    JsonOptions, context.RequestAborted);
                PairResponse? paired = TryPair(pairing, peer, context.Response);
                if (paired is not null)
                    await context.Response.WriteAsJsonAsync(paired, context.RequestAborted);
                break;
            case "/api/resume":
                var resume = await JsonSerializer.DeserializeAsync<ResumeRequest>(request.Body,
                    JsonOptions, context.RequestAborted);
                string? token = TryResume(resume, peer, context.Response);
                if (token is not null)
                    await context.Response.WriteAsJsonAsync(new { token }, context.RequestAborted);
                break;
            case "/api/input":
                var command = await JsonSerializer.DeserializeAsync<ControlInput>(request.Body,
                    JsonOptions, context.RequestAborted);
                context.Response.StatusCode = Apply(ReadToken(request), peer, command);
                break;
            case "/api/disconnect":
                lock (state)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    if (AuthorizeLocked(ReadToken(request), peer))
                    {
                        control.DisconnectLocked(this);
                        context.Response.StatusCode = StatusCodes.Status204NoContent;
                    }
                }
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private PairResponse? TryPair(PairRequest? request, IPAddress peer, HttpResponse response)
    {
        lock (state)
        {
            MaintainSessionLocked();
            if (control.IsConnectedLocked(this) || remembered is not null)
            {
                response.StatusCode = StatusCodes.Status409Conflict;
                return null;
            }
            if (closing || code is null)
            {
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return null;
            }
            var now = DateTimeOffset.UtcNow;
            if (now < retryAt)
            {
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.RetryAfter = Math.Ceiling((retryAt - now).TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture);
                return null;
            }
            if (request?.Code is not { Length: 8 } entered || !RemoteControlSession.FixedEquals(entered, code))
            {
                if (++failedAttempts >= 5)
                {
                    retryAt = now.AddSeconds(20);
                    failedAttempts = 0;
                }
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return null;
            }
            if (!control.CanOpenLocked(this, request.SwitchMode))
            {
                response.StatusCode = StatusCodes.Status409Conflict;
                return null;
            }
            string? deviceToken = null;
            if (request.Remember)
            {
                deviceToken = NewToken();
                var device = new SavedDevice(HashToken(deviceToken), peer.ToString(), Address.Host);
                try
                {
                    identity.SaveDevice(device);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return null;
                }
                remembered = device;
            }
            var token = NewToken();
            control.OpenLocked(this, token, peer);
            code = null;
            failedAttempts = 0;
            return new PairResponse(token, deviceToken);
        }
    }

    private string? TryResume(ResumeRequest? request, IPAddress peer, HttpResponse response)
    {
        lock (state)
        {
            MaintainSessionLocked();
            if (closing || remembered is not { } device || request?.DeviceToken is not { Length: 64 } deviceToken ||
                device.Host != Address.Host || (endpoint.IsTailscale && device.Peer != peer.ToString()) ||
                !RemoteControlSession.FixedEquals(HashToken(deviceToken), device.TokenHash))
            {
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return null;
            }
            if (!control.CanOpenLocked(this, request.SwitchMode))
            {
                response.StatusCode = StatusCodes.Status409Conflict;
                return null;
            }
            var token = NewToken();
            control.OpenLocked(this, token, peer);
            return token;
        }
    }

    private int Apply(string? token, IPAddress peer, ControlInput? command)
    {
        lock (state)
        {
            if (!AuthorizeLocked(token, peer))
                return StatusCodes.Status401Unauthorized;
            return control.ApplyLocked(command);
        }
    }

    private bool AuthorizeLocked(string? token, IPAddress peer)
    {
        MaintainSessionLocked();
        return control.AuthorizeLocked(this, token, peer);
    }

    private void MaintainSession()
    {
        lock (state)
            MaintainSessionLocked();
    }

    private void MaintainSessionLocked()
    {
        if (code is not null && DateTimeOffset.UtcNow >= codeExpiresAt)
            code = null;
        control.MaintainLocked();
    }

    private static string? ReadToken(HttpRequest request)
    {
        const string prefix = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        return header.Length == prefix.Length + 64 && header.StartsWith(prefix, StringComparison.Ordinal)
            ? header[prefix.Length..]
            : null;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static void SetResponseHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; " +
            "style-src 'self'; img-src 'self' blob:; connect-src 'self'; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }

    private static Task WriteBytesAsync(HttpContext context, byte[] bytes, string contentType)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        return context.Response.Body.WriteAsync(bytes.AsMemory(), context.RequestAborted).AsTask();
    }

    private static WebAsset LoadAsset(string file, string contentType)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web." + file)
            ?? throw new InvalidOperationException("手机界面资源缺失，请重新解压程序。");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return new WebAsset(bytes.ToArray(), contentType);
    }

    private sealed record WebAsset(byte[] Bytes, string ContentType);
    private sealed record PairRequest(string? Code, bool Remember, bool SwitchMode);
    private sealed record PairResponse(string Token, string? DeviceToken);
    private sealed record ResumeRequest(string? DeviceToken, bool SwitchMode);
}
