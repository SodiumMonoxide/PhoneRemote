using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteControl;

internal sealed class RemoteClientException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    internal HttpStatusCode? StatusCode { get; } = statusCode;
}

internal sealed class WindowsRemoteClient : IDisposable
{
    private const int MaxJson = 16 * 1024;
    private const int MaxFrame = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly SemaphoreSlim commands = new(1, 1);
    private string? sessionToken;
    private bool disposed;
    private bool pinRejected;

    internal RemoteClientProfile Profile { get; private set; }
    internal bool IsConnected => sessionToken is not null && !disposed;

    internal WindowsRemoteClient(RemoteClientProfile profile)
    {
        Profile = RemoteClientProfile.Create(profile.Mode, profile.Address, profile.Fingerprint, profile.DeviceToken);
        byte[] fingerprint = Convert.FromHexString(Profile.Fingerprint.Replace(":", "", StringComparison.Ordinal));
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, MaxResponseHeadersLength = 16, MaxConnectionsPerServer = 4 };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            // Exact leaf pin is the trust anchor for the host's self-signed certificate.
            // Name and validity still have to match; accepting arbitrary certificates is never allowed.
            bool valid = certificate is not null && (errors & (SslPolicyErrors.RemoteCertificateNotAvailable |
                SslPolicyErrors.RemoteCertificateNameMismatch)) == 0 && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), fingerprint);
            if (!valid) pinRejected = true;
            return valid;
        };
        http = new HttpClient(handler) { BaseAddress = new Uri(Profile.Address), Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<RemoteClientProfile> PairAsync(string code, bool remember, bool switchMode, CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        if (code.Length != 8 || !code.All(c => c is >= '0' and <= '9'))
            throw new ArgumentException("请输入被控电脑显示的 8 位配对码。");
        await commands.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected) throw new InvalidOperationException("请先断开当前连接。");
            byte[] response = await RequestAsync(HttpMethod.Post, "/api/pair",
                new { code, remember, switchMode, clientType = "windows" }, false, cancellationToken);
            var result = ParseTokens(response);
            if (remember && !RemoteClientProfile.IsToken(result.DeviceToken))
                throw new RemoteClientException("被控电脑没有返回有效的配对凭据。");
            sessionToken = result.Token;
            Profile = Profile with { DeviceToken = remember ? result.DeviceToken : null };
            return Profile;
        }
        finally { commands.Release(); }
    }

    internal async Task ResumeAsync(bool switchMode, CancellationToken cancellationToken = default)
    {
        if (!RemoteClientProfile.IsToken(Profile.DeviceToken))
            throw new RemoteClientException("此模式尚未配对，请输入被控电脑的配对码。", HttpStatusCode.Unauthorized);
        await commands.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected) throw new InvalidOperationException("请先断开当前连接。");
            byte[] response = await RequestAsync(HttpMethod.Post, "/api/resume",
                new { deviceToken = Profile.DeviceToken, switchMode, clientType = "windows" }, false, cancellationToken);
            sessionToken = ParseTokens(response).Token;
        }
        finally { commands.Release(); }
    }

    internal Task<byte[]> GetFrameAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(HttpMethod.Get, "/api/frame", null, true, cancellationToken);

    internal async Task SendInputAsync(ControlInput command, CancellationToken cancellationToken = default)
    {
        await commands.WaitAsync(cancellationToken);
        try { await RequestAsync(HttpMethod.Post, "/api/input", command, true, cancellationToken); }
        finally { commands.Release(); }
    }

    internal async Task RequireKeyboardSupportAsync(CancellationToken cancellationToken = default)
    {
        const string message = "被控电脑需要升级到 Windows 2.5.0 或更新版本，才能使用直接键鼠控制。原有配对可以保留。";
        try
        {
            byte[] bytes = await RequestAsync(HttpMethod.Get, "/api/capabilities", null, true, cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("rawKeyboard", out var capability) || capability.ValueKind != JsonValueKind.True)
                throw new RemoteClientException(message);
        }
        catch (RemoteClientException error) when (error.StatusCode == HttpStatusCode.NotFound)
        { throw new RemoteClientException(message, error.StatusCode, error); }
        catch (JsonException error) { throw new RemoteClientException(message, null, error); }
    }

    internal async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await commands.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
                await RequestAsync(HttpMethod.Post, "/api/disconnect", new { }, true, cancellationToken);
        }
        finally
        {
            sessionToken = null;
            commands.Release();
        }
    }

    private async Task<byte[]> RequestAsync(HttpMethod method, string path, object? body, bool authorized,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string? bearer = sessionToken;
        if (authorized && !RemoteClientProfile.IsToken(bearer))
            throw new RemoteClientException("连接已断开，请重新连接。", HttpStatusCode.Unauthorized);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(method, path);
        bool frame = path == "/api/frame";
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(frame ? "image/jpeg" : "application/json"));
        if (authorized) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (method == HttpMethod.Post)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
            if (json.Length > 4096)
                throw new ArgumentException("本次输入内容过长，请分段发送。");
            request.Headers.Add("Origin", http.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != (method == HttpMethod.Get || !authorized ? HttpStatusCode.OK : HttpStatusCode.NoContent))
            {
                if (authorized && response.StatusCode == HttpStatusCode.Unauthorized && sessionToken == bearer) sessionToken = null;
                throw StatusError(response.StatusCode, path);
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return [];
            string? type = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(type, frame ? "image/jpeg" : "application/json", StringComparison.OrdinalIgnoreCase))
                throw new RemoteClientException("被控电脑返回了无法识别的数据类型。");
            int limit = frame ? MaxFrame : MaxJson;
            if (response.Content.Headers.ContentLength > limit)
                throw new RemoteClientException("被控电脑返回的数据过大。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (output.Length + count > limit) throw new RemoteClientException("被控电脑返回的数据过大。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (HttpRequestException error)
        {
            throw new RemoteClientException(pinRejected ? "证书指纹、地址或有效期不匹配。请在被控电脑重新核对连接信息。" :
                "无法连接电脑。请检查服务、网络、防火墙和两端的 Tailscale 状态。", null, error);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteClientException("连接超时，请检查被控电脑是否开机联网，服务是否已启动。", null, error);
        }
        catch (IOException error)
        {
            throw new RemoteClientException("连接中断，请检查网络后重新连接。", null, error);
        }
    }

    private static TokenResponse ParseTokens(byte[] response)
    {
        try
        {
            var tokens = JsonSerializer.Deserialize<TokenResponse>(response, JsonOptions);
            if (tokens is null || !RemoteClientProfile.IsToken(tokens.Token))
                throw new RemoteClientException("被控电脑返回了无效的会话凭据。");
            return tokens;
        }
        catch (JsonException error) { throw new RemoteClientException("被控电脑返回了无效的连接数据。", null, error); }
    }

    private static RemoteClientException StatusError(HttpStatusCode status, string path) => new(status switch
    {
        HttpStatusCode.Unauthorized => path == "/api/pair" ? "配对码无效或已过期，请在被控电脑生成新码。" :
            path == "/api/resume" ? "已保存的配对失效，请重新输入配对码。" : "连接已结束，请重新连接。",
        HttpStatusCode.Conflict => "电脑正由其他设备控制，或尚未允许新设备配对。请先断开当前连接；首次配对时请在被控电脑点「生成新码」。",
        HttpStatusCode.TooManyRequests => "尝试过于频繁，请等 20 秒后重试。",
        HttpStatusCode.Forbidden => "被控电脑拒绝了此网络地址，请确认连接模式和地址匹配。",
        HttpStatusCode.ServiceUnavailable => "被控电脑桌面暂不可用，请确认已解锁并关闭系统权限提示。",
        HttpStatusCode.BadRequest => "被控电脑不支持本次输入，请检查输入内容或使用工具栏按键。",
        _ => $"连接请求失败（HTTP {(int)status}）。请确认两端使用兼容版本。"
    }, status);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        sessionToken = null;
        http.Dispose();
        // An in-flight command may still be unwinding and releasing the gate.
    }

    private sealed record TokenResponse(string Token, string? DeviceToken);
}
