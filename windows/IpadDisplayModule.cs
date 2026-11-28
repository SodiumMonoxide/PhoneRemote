using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RemoteControl;

internal sealed class IpadDisplayModule
{
    internal static Uri ConsoleUri { get; } = new("http://127.0.0.1:8442/");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "modules", "ipad-display");
    private readonly string _state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPadWiFiDisplay", "session");

    internal bool OwnsService { get; private set; }
    internal int VideoPort { get; private set; }
    internal bool DisplayAvailable { get; private set; }

    internal async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (await IsRunningAsync()) return;
            var output = await RunScriptAsync("Start-Display.ps1", ["-NoBrowser"]);
            var started = output.Split('\n').Any(line => line.Trim() == "PHONEREMOTE_IPAD_STARTED");
            OwnsService = started;
            if (!await IsRunningAsync())
            {
                if (started) await StopWorkersAsync(includeExisting: false);
                throw new InvalidOperationException("副屏服务未就绪。请查看副屏模块的启动日志，检查 Wi-Fi 后重试。");
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task StopAsync(bool includeExisting = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (includeExisting && !OwnsService && !await IsRunningAsync())
                throw new InvalidOperationException("无法核实原副屏服务的身份，请检查电脑控制台后重试。");
            if (OwnsService || includeExisting) await StopWorkersAsync(includeExisting);
        }
        finally { _gate.Release(); }
    }

    private async Task StopWorkersAsync(bool includeExisting)
    {
        await RunScriptAsync("Stop-Display.ps1", includeExisting ? ["-IncludeLegacy"] : []);
        OwnsService = false;
        VideoPort = 0;
        DisplayAvailable = false;
    }

    internal async Task ConfigureAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var address = SavedAddress();
            if (address is null)
            {
                var wifi = LocalAddresses(wifiOnly: true).Distinct().ToArray();
                if (wifi.Length != 1)
                    throw new InvalidOperationException("请连接用于副屏的 Wi-Fi；如果有多个 Wi-Fi 地址，请按模块指南用 -Address 指定地址。");
                address = wifi[0];
            }
            // Resolve the user-level Python environment before elevating only the driver/firewall setup.
            await RunScriptAsync("Initialize-Runtime.ps1", []);
            await RunScriptAsync("Configure-PC.ps1", ["-Address", address.ToString()], elevated: true);
            if (VideoPort != 0)
                await IsRunningAsync();
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> IsRunningAsync()
    {
        VideoPort = 0;
        DisplayAvailable = false;
        try
        {
            var address = SavedAddress();
            if (address is null) return false;
            var token = (await File.ReadAllTextAsync(Path.Combine(_state, "console-token"))).Trim();
            var portFile = Path.Combine(_state, "video-port.txt");
            // Existing iPad Web Clips point to 8443. New installations persist their selected port.
            int port = 8443;
            if (File.Exists(portFile) && (!int.TryParse((await File.ReadAllTextAsync(portFile)).Trim(), out port) ||
                port is not (8443 or 8453))) return false;

            using var leaf = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(Path.Combine(_state, "server.pem")));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && certificate.RawDataMemory.Span.SequenceEqual(leaf.RawDataMemory.Span);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var console = await client.GetAsync(ConsoleUri, HttpCompletionOption.ResponseHeadersRead);
            if (console.StatusCode != HttpStatusCode.SeeOther || token.Length < 32 ||
                console.Headers.Location?.OriginalString != "/c/" + token) return false;
            using var response = await client.GetAsync($"https://{address}:{port}/api/status");
            if (!response.IsSuccessStatusCode) return false;
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!status.RootElement.TryGetProperty("mode", out var mode) || mode.GetString() != "display" ||
                !status.RootElement.TryGetProperty("ready", out var ready) ||
                ready.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            VideoPort = port;
            DisplayAvailable = ready.GetBoolean();
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or
            CryptographicException or JsonException or NetworkInformationException)
        {
            return false;
        }
    }

    private IPAddress? SavedAddress()
    {
        var file = Path.Combine(_state, "address.txt");
        if (!File.Exists(file) || !IPAddress.TryParse(File.ReadAllText(file).Trim(), out var saved))
            return null;
        return LocalAddresses().Contains(saved) ? saved : null;
    }

    private static IEnumerable<IPAddress> LocalAddresses(bool wifiOnly = false) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                (!wifiOnly || adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(entry => entry.Address)
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip));

    private async Task<string> RunScriptAsync(string name, IReadOnlyList<string> arguments, bool elevated = false)
    {
        var script = Path.Combine(_directory, name);
        if (!File.Exists(script))
            throw new FileNotFoundException("找不到副屏组件。请从完整的 PhoneRemote Windows 软件包启动。", script);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : "",
            WorkingDirectory = _directory,
            CreateNoWindow = !elevated,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = !elevated,
            RedirectStandardOutput = !elevated
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script }.Concat(arguments))
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动副屏组件。");
            var output = elevated ? Task.FromResult("") : process.StandardOutput.ReadToEndAsync();
            var errors = elevated ? Task.FromResult("") : process.StandardError.ReadToEndAsync();
            await Task.WhenAll(output, errors, process.WaitForExitAsync());
            if (process.ExitCode == 0) return await output;

            var reason = (await errors).Trim();
            if (elevated && File.Exists(Path.Combine(_state, "setup-result.txt")))
                reason = (await File.ReadAllTextAsync(Path.Combine(_state, "setup-result.txt"))).Trim();
            if (reason.Contains("8443", StringComparison.Ordinal) &&
                (reason.Contains("in use", StringComparison.OrdinalIgnoreCase) || reason.Contains("occupied", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("原 iPad 配对使用的 8443 端口已被占用。如果「局域网」正在该地址上运行，请先停止该连接，再回到「iPad 副屏」重试。");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
                ? $"副屏组件执行失败（{process.ExitCode}）。" : reason.Split('\n')[0].Trim());
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员配置。需要安装驱动或设置防火墙时可再试。", ex);
        }
    }
}
