using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteControl;

if (!OperatingSystem.IsMacOSVersionAtLeast(14))
{
    Console.Error.WriteLine("Mac 被控端需要 macOS 14 或更高版本。");
    return 1;
}

try
{
    // Resolve next to the executable even in a self-contained single-file publish.
    NativeLibrary.SetDllImportResolver(typeof(Quartz).Assembly, (name, _, _) =>
        name == Quartz.Library ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,
            "libPhoneRemoteQuartz.dylib")) : IntPtr.Zero);
    var addresses = ConnectionAddress.Find();
    Console.WriteLine("允许另一台电脑控制此 Mac");
    if (addresses.Count == 0)
    {
        Console.WriteLine("未找到可用地址。请连接局域网或 Tailscale，再重新打开被控端。");
        return 1;
    }
    for (int i = 0; i < addresses.Count; i++)
        Console.WriteLine($"{i + 1}  {(addresses[i].IsTailscale ? "Tailscale" : "局域网")}  {addresses[i].Address}");
    Console.Write("选择本次允许连接的地址编号，或回车退出：");
    string? selection = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(selection)) return 0;
    if (!int.TryParse(selection, out var index) || index < 1 || index > addresses.Count)
    {
        Console.Error.WriteLine("地址编号无效。");
        return 1;
    }
    int permissions = Quartz.RequestPermissions();
    if (permissions != 3)
    {
        Console.Error.WriteLine("请在「系统设置 → 隐私与安全性」中为系统提示的应用授权，然后重新打开被控端。");
        if ((permissions & 1) == 0) Console.Error.WriteLine("需要屏幕录制权限以共享主屏幕。");
        if ((permissions & 2) == 0) Console.Error.WriteLine("需要辅助功能权限以使用键盘和鼠标。");
        return 1;
    }

    string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "PhoneRemote", "Host");
    Directory.CreateDirectory(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    using var instance = new FileStream(Path.Combine(dataDir, "host.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);
    await using var host = await RemoteServer.StartAsync(addresses[index - 1].Address, dataDir, new RemoteControlSession());
    using var stopped = new CancellationTokenSource();
    void Stop(PosixSignalContext signal) { signal.Cancel = true; stopped.Cancel(); }
    using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop);
    using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
    string info = $"{host.Address.AbsoluteUri}\n{host.Fingerprint}";
    Console.WriteLine($"\n将以下两行连接信息提供给控制电脑：\n{info}");
    while (!stopped.IsCancellationRequested)
    {
        var state = host.GetStatus();
        Console.WriteLine($"\n当前{(state.Connected ? "已连接" : "等待连接")}。" +
            (state.Code is null ? "" : $"配对码 {state.Code}（{state.CodeExpiresAt?.ToLocalTime():HH:mm:ss} 到期）。"));
        Console.Write("回车刷新  c 复制连接信息  n 新配对码  d 断开  f 忘记设备  q 停止并退出\n> ");
        string? action = await Task.Run(Console.ReadLine).WaitAsync(stopped.Token);
        switch (action?.Trim().ToLowerInvariant())
        {
            case null or "q": return 0;
            case "c":
                using (var copy = Process.Start(new ProcessStartInfo("/usr/bin/pbcopy") { RedirectStandardInput = true }))
                {
                    if (copy is null) break;
                    await copy.StandardInput.WriteAsync(info);
                    copy.StandardInput.Close();
                    await copy.WaitForExitAsync(stopped.Token);
                    Console.WriteLine(copy.ExitCode == 0 ? "已复制地址和证书指纹。" : "复制失败，请选中上方两行信息复制。");
                }
                break;
            case "n":
                if (!host.RefreshCode()) Console.WriteLine("请先断开当前连接；如已记满 16 台设备，请先忘记设备。");
                break;
            case "d": host.Disconnect(); break;
            case "f":
                Console.Write("忘记此网络模式下所有配对？输入 YES 确认：");
                if (await Task.Run(Console.ReadLine).WaitAsync(stopped.Token) == "YES")
                    host.ForgetDevice();
                break;
        }
    }
}
catch (OperationCanceledException) { }
catch (Exception error)
{
    Console.Error.WriteLine($"无法启动或继续共享：{error.Message}");
    return 1;
}
return 0;
