using System.Diagnostics;
using System.Net;

namespace RemoteControl;

internal static class MacNetwork
{
    // utun names change and other VPNs can use the same address range. Ask
    // Tailscale for its own address and intersect it with local interfaces.
    internal static IPAddress? GetTailscaleAddress()
    {
        string[] candidates = ["/Applications/Tailscale.app/Contents/MacOS/Tailscale",
            "/opt/homebrew/bin/tailscale", "/usr/local/bin/tailscale"];
        foreach (string path in candidates.Where(File.Exists))
        {
            var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["TAILSCALE_BE_CLI"] = "1";
            start.ArgumentList.Add("ip");
            start.ArgumentList.Add("-4");
            try
            {
                using var cli = Process.Start(start);
                if (cli is null) continue;
                var output = cli.StandardOutput.ReadToEndAsync();
                var errors = cli.StandardError.ReadToEndAsync();
                if (!cli.WaitForExit(2000))
                {
                    cli.Kill();
                    continue;
                }
                if (cli.ExitCode == 0 && IPAddress.TryParse(output.GetAwaiter().GetResult().Trim(), out var ip))
                    return ip;
                errors.GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // LAN hosting is independent of the optional Tailscale client.
            }
        }
        return null;
    }
}
