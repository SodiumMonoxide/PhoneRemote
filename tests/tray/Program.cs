using System.Net;
using System.Reflection;

namespace RemoteControl;

internal sealed class WindowsClientForm : Form
{
    internal bool ShutdownCalled { get; private set; }
    public Task ShutdownAsync() { ShutdownCalled = true; Close(); return Task.CompletedTask; }
}

internal sealed record ServerStatus(string? Code, DateTimeOffset? CodeExpiresAt, bool Connected, bool RememberedDevice);
internal sealed class RemoteControlSession
{
    internal object StateLock { get; } = new();
}

internal sealed class RemoteServer(IPAddress address, RemoteControlSession control)
{
    internal static readonly Dictionary<bool, RemoteServer> Active = [];
    internal static TaskCompletionSource? Starting;
    internal static TaskCompletionSource? Stopping;
    public bool IsTailscale { get; } = address.ToString().StartsWith("100.");
    public RemoteControlSession Control { get; } = control;
    public Uri Address => new($"https://{address}:8443/");
    public string Fingerprint => new('A', 64);
    public int Disconnects { get; private set; }
    private ServerStatus status = new("12345678", DateTimeOffset.UtcNow.AddMinutes(5), false, false);
    public static IReadOnlyList<IPAddress> GetLocalAddresses() => [IPAddress.Parse("100.64.0.10"), IPAddress.Parse("192.0.2.10")];
    public static async Task<RemoteServer> StartAsync(IPAddress address, RemoteControlSession control)
    {
        if (Starting is not null) await Starting.Task;
        var server = new RemoteServer(address, control);
        Active.Add(server.IsTailscale, server);
        return server;
    }
    public ServerStatus GetStatus() => status;
    public bool RefreshCode() => true;
    public void Pair() => status = new(null, null, true, true);
    public void Disconnect() { Disconnects++; status = status with { Connected = false }; }
    public void ForgetDevice() => status = new(null, null, false, false);
    public async ValueTask DisposeAsync()
    {
        Disconnect();
        if (Stopping is not null) await Stopping.Task;
        Active.Remove(IsTailscale);
    }
}

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string CaptureDirectory = Path.Combine(Path.GetTempPath(), "PhoneRemote-tests", "tray-" + Guid.NewGuid().ToString("N"));
    [STAThread] private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var driver = new Form { Opacity = 0, ShowInTaskbar = false };
        driver.Shown += async (_, _) =>
        {
            try
            {
                await CheckLifecycle();
                await CheckBusyExit();
                await CheckSystemShutdown();
                CheckScaledLayouts();
                Console.WriteLine("PASS tray lifecycle, close choices, status refresh, restore, graceful exit, busy-start exit, OS shutdown");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { driver.Close(); }
        };
        Application.Run(driver);
    }

    private static async Task CheckLifecycle()
    {
        using var host = NewHost();
        Button(host, "启动连接").PerformClick();
        Modes(host).Single(m => m.Text.StartsWith("Tailscale")).Checked = true;
        Button(host, "启动连接").PerformClick();
        Check(RemoteServer.Active.Count == 2, "Both services running");
        RemoteServer.Active[false].Pair();
        await Task.Delay(600);
        var icon = Field<NotifyIcon>(host, "_tray");
        var menu = Field<ContextMenuStrip>(host, "_trayMenu");
        Check(icon.Visible && (icon.Text ?? "").Contains("局域网：设备已连接") && !(icon.Text ?? "").Contains("12345678"), "Safe accurate tray tooltip");
        Check(menu.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == "Tailscale · 运行中，等待配对"), "Independent Tailscale tray state");

        await CloseWithChoice(host, "取消", capture: true);
        Check(host.Visible && !host.IsDisposed && RemoteServer.Active.Count == 2, "Cancel preserves window and servers");
        await CloseWithChoice(host, "后台运行");
        Check(!host.Visible && !host.ShowInTaskbar && icon.Visible && RemoteServer.Active.Count == 2, "X background preserves both services");
        Check(RemoteServer.Active[false].Disconnects == 0, "Background does not disconnect phone");
        RemoteServer.Active[false].Disconnect();
        RemoteServer.Active[true].Pair();
        await Task.Delay(650);
        Check((icon.Text ?? "").Contains("局域网：已配对，等待连接") && (icon.Text ?? "").Contains("Tailscale：设备已连接"), "Hidden status timer updates both modes");
        host.RestoreWindow();
        Check(host.Visible && host.ShowInTaskbar, "External-launch restoration");
        Button(host, "后台运行").PerformClick();
        Check(!host.Visible, "Direct background button hides");
        menu.Items.OfType<ToolStripMenuItem>().Single(i => (i.Text ?? "").StartsWith("局域网 ·")).PerformClick();
        Check(host.Visible && Modes(host).Single(m => m.Text.StartsWith("局域网")).Checked, "Tray mode action restores correct panel");

        host.WindowState = FormWindowState.Minimized;
        host.RestoreWindow();
        Check(host.WindowState == FormWindowState.Normal, "Restore minimized window");
        Capture(host, "host-preview.png");
        Button(host, "控制另一台电脑").PerformClick();
        var clientWindow = Field<WindowsClientForm>(host, "_clientWindow");
        Button(host, "控制另一台电脑").PerformClick();
        Check(ReferenceEquals(clientWindow, Field<WindowsClientForm>(host, "_clientWindow")), "Client window reused");
        var disposed = Closed(host);
        RemoteServer.Stopping = new TaskCompletionSource();
        await CloseWithChoice(host, "退出应用");
        Check(!host.IsDisposed && !Button(host, "后台运行").Enabled, "Graceful exit waits and disables reentry");
        RemoteServer.Stopping.SetResult();
        await disposed.WaitAsync(TimeSpan.FromSeconds(3));
        RemoteServer.Stopping = null;
        Check(RemoteServer.Active.Count == 0 && host.IsDisposed && !icon.Visible, "Exit releases both servers and tray");
        Check(clientWindow.ShutdownCalled && clientWindow.IsDisposed, "Exit disconnects and closes Windows controller");
        using var noServices = NewHost();
        var noServicesClosed = Closed(noServices);
        Button(noServices, "退出应用").PerformClick();
        await noServicesClosed.WaitAsync(TimeSpan.FromSeconds(2));
        Check(noServices.IsDisposed, "Explicit exit with no services");
    }

    private static async Task CheckBusyExit()
    {
        using var host = NewHost();
        RemoteServer.Starting = new TaskCompletionSource();
        Button(host, "启动连接").PerformClick();
        var closed = Closed(host);
        Button(host, "退出应用").PerformClick();
        Check(!host.IsDisposed, "Wait for ongoing start before exit");
        RemoteServer.Starting.SetResult();
        await closed.WaitAsync(TimeSpan.FromSeconds(3));
        RemoteServer.Starting = null;
        Check(RemoteServer.Active.Count == 0 && host.IsDisposed, "Late-started server is stopped on exit");
    }

    private static async Task CheckSystemShutdown()
    {
        using var host = NewHost();
        Button(host, "启动连接").PerformClick();
        var server = RemoteServer.Active[false];
        server.Pair();
        var args = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
        typeof(Form).GetMethod("OnFormClosing", Private)!.Invoke(host, [args]);
        Check(!args.Cancel && server.Disconnects == 1, "OS shutdown never prompts/cancels and releases active input");
        await server.DisposeAsync();
    }

    private static HostForm NewHost()
    {
        var host = new HostForm { Opacity = 0, ShowInTaskbar = false };
        host.Show();
        return host;
    }

    private static void CheckScaledLayouts()
    {
        foreach (var scale in new[] { 1F, 1.25F, 1.5F })
        {
            using var host = NewHost();
            ScaleForLayoutTest(host, scale);
            host.Size = host.MinimumSize;
            host.PerformLayout();
            Console.WriteLine($"Layout check: actual DPI {host.DeviceDpi}, scale {scale}, minimum size {host.Size}");
            var background = Button(host, "后台运行");
            var exit = Button(host, "退出应用");
            Check(background.Bounds.Right <= background.Parent!.ClientSize.Width && exit.Bounds.Right <= exit.Parent!.ClientSize.Width,
                $"Footer buttons fit at scale {scale}");
            Check(exit.Parent!.Bounds.Right <= exit.Parent.Parent!.ClientSize.Width, $"Footer actions fit at scale {scale}");
            Capture(host, $"host-minimum-{scale * 100:0}.png");
            using var close = new CloseOptionsForm { Opacity = 0 };
            close.Show();
            ScaleForLayoutTest(close, scale);
            close.PerformLayout();
            foreach (var button in Descendants(close).OfType<Button>())
                Check(button.Bounds.Right <= button.Parent!.ClientSize.Width && button.Bounds.Bottom <= button.Parent.ClientSize.Height,
                    $"Close choice fits at scale {scale}");
            Capture(close, $"close-scaled-{scale * 100:0}.png");
            close.Close();
            Button(host, "退出应用").PerformClick();
        }
    }

    private static void ScaleForLayoutTest(Control root, float scale)
    {
        // The test monitor is 96 DPI. Scale geometry and fonts to exercise the
        // 125% / 150% layout without changing the user's Windows display setting.
        var fonts = Descendants(root).Prepend(root).Select(c => (Control: c, Font: c.Font)).ToArray();
        root.SuspendLayout();
        root.Scale(new SizeF(scale, scale));
        foreach (var entry in fonts)
            entry.Control.Font = new Font(entry.Font.FontFamily, entry.Font.Size * scale, entry.Font.Style, entry.Font.Unit);
        root.ResumeLayout(true);
    }

    private static Task Closed(Form host)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.FormClosed += (_, _) => closed.SetResult();
        return closed.Task;
    }

    private static async Task CloseWithChoice(HostForm host, string choice, bool capture = false)
    {
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<CloseOptionsForm>().SingleOrDefault();
            if (dialog is null) return;
            timer.Stop();
            if (capture) Capture(dialog, "close-preview.png");
            Button(dialog, choice).PerformClick();
        };
        timer.Start();
        host.Close();
        await Task.Delay(30);
    }

    private static void Capture(Form form, string file)
    {
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        Directory.CreateDirectory(CaptureDirectory);
        image.Save(Path.Combine(CaptureDirectory, file));
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Button Button(Control parent, string text) => Descendants(parent).OfType<Button>().Single(b => b.Text == text);
    private static IEnumerable<RadioButton> Modes(Control parent) => Descendants(parent).OfType<RadioButton>();
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

