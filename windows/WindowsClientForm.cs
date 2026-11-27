using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
namespace RemoteControl;

internal sealed class WindowsClientForm : Form
{
    private readonly WindowsClientProfiles profiles;
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly TextBox address = new() { PlaceholderText = "被控电脑的 HTTPS 地址（含端口）", AccessibleName = "远程电脑地址" };
    private readonly TextBox fingerprint = new() { PlaceholderText = "证书 SHA-256 指纹", AccessibleName = "远程电脑证书指纹" };
    private readonly TextBox code = new() { Width = 130, MaxLength = 8, PlaceholderText = "首次配对码", AccessibleName = "远程电脑配对码" };
    private readonly CheckBox remember = new() { Text = "记住此电脑", Checked = true, AutoSize = true };
    private readonly Button connect = MakeButton("连接"), disconnect = MakeButton("断开"), paste = MakeButton("粘贴连接信息"),
        forget = MakeButton("清除本机保存"), fullscreen = MakeButton("全屏预览");
    private readonly Label status = new() { AccessibleName = "控制端连接状态", AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = Color.FromArgb(66, 89, 101) };
    private readonly TableLayoutPanel setup = new() { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Padding = new Padding(18, 10, 18, 10) };
    private readonly WindowsRemoteView view = new() { Dock = DockStyle.Fill };
    private readonly RemoteToolbar tools = new() { Visible = false };
    private WindowsRemoteClient? client;
    private CancellationTokenSource? connectionLifetime, inputLifetime;
    private Task? frameTask, inputTask, shutdownTask;
    private Task<bool> releaseTask = Task.FromResult(true);
    private WindowsInputQueue? inputQueue;
    private bool busy, updatingMode, closingAllowed, shuttingDown, isLocalPreview, isFullscreen, controlRequested, pasteOpen, resuming;
    private string selectedMode = "lan";
    private Rectangle normalBounds;
    private RemoteTextDialog? textDialog;

    public WindowsClientForm(string? profilePath = null)
    {
        profiles = new WindowsClientProfiles(profilePath);
        Text = "PhoneRemote · 控制另一台电脑"; Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White; ForeColor = Color.FromArgb(27, 43, 60); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(960, 680); MinimumSize = new Size(650, 475);
        mode.Items.AddRange(["局域网", "Tailscale"]); mode.SelectedIndex = 0; mode.AccessibleName = "连接方式";
        var guide = MakeButton("使用指南");
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 6) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(new Label { Text = "控制 Windows 或 Mac", AutoSize = true,
            Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Anchor = AnchorStyles.Left }, 0, 0);
        heading.Controls.Add(fullscreen, 1, 0);
        heading.Controls.Add(guide, 2, 0);
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setup.Controls.Add(heading, 0, 0);
        setup.SetColumnSpan(heading, 2);
        AddSetting("连接方式", Flow(mode, paste, forget), 1);
        AddSetting("HTTPS 地址", address, 2);
        AddSetting("证书指纹", fingerprint, 3);
        AddSetting("配对码", Flow(code, remember, connect, disconnect), 4);
        connect.BackColor = Color.FromArgb(18, 101, 86);
        connect.ForeColor = Color.White;
        connect.FlatStyle = FlatStyle.Flat;
        connect.FlatAppearance.BorderSize = 0;
        setup.Controls.Add(status, 0, 5);
        setup.SetColumnSpan(status, 2);
        AcceptButton = connect;
        Controls.Add(view); Controls.Add(setup); view.Controls.Add(tools);
        view.SizeChanged += (_, _) => { tools.PlaceInitially(); tools.ClampToParent(); };
        tools.ToggleControlRequested += async () => { if (controlRequested) ExitControl(true); else await ResumeControlAsync(); };
        tools.Opening += SuspendInput;
        tools.InteractionEnded += async () => { if (controlRequested && !pasteOpen) await ResumeControlAsync(); };
        tools.PasteRequested += async () => await PrepareTextAsync();
        tools.FullscreenRequested += ToggleFullscreen;
        tools.DisconnectRequested += async () => await StopAsync("已断开，保存的配对仍保留。");
        tools.SettingsRequested += () => { ExitControl(true); SetCanvasMode(false); };
        tools.GuideRequested += () => { SuspendInput(); ShowGuide(); };
        paste.Click += (_, _) => PasteConnection(); guide.Click += (_, _) => ShowGuide();
        connect.Click += async (_, _) => await ConnectAsync(); disconnect.Click += async (_, _) => await StopAsync("已断开，保存的配对仍保留。");
        mode.SelectedIndexChanged += async (_, _) => await SwitchModeAsync(); forget.Click += (_, _) => ForgetProfile();
        fullscreen.Click += (_, _) => ToggleFullscreen(); view.RemoteInput += Enqueue;
        view.ExitControlRequested += () => ExitControl(true);
        view.KeyboardCaptureFailed += message => { ExitControl(false); status.Text = message; UpdateControls(); };
        view.CaptureLost += () => ExitControl(false);
        view.LocalCommand = key => { if (key == (Keys.Control | Keys.Alt | Keys.Enter)) { ToggleFullscreen(); return true; } return false; };
        view.LostFocus += (_, _) =>
        {
            if (tools.Visible && tools.ClientRectangle.Contains(tools.PointToClient(Cursor.Position))) SuspendInput();
            else ExitControl(false);
        };
        Deactivate += (_, _) => ExitControl(false);
        FormClosing += async (_, e) =>
        {
            if (closingAllowed) return;
            if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
            { controlRequested = false; view.ReleaseAllInput(); connectionLifetime?.Cancel(); return; }
            e.Cancel = true; await ShutdownAsync();
        };
        status.TextChanged += (_, _) => UpdateToolbar(); LoadProfile(); UpdateControls();
    }
    private void AddSetting(string title, Control field, int row)
    {
        setup.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 3, 14, 3) }, 0, row);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        setup.Controls.Add(field, 1, row);
    }
    private static Button MakeButton(string title) => new() { Text = title, AutoSize = true,
        MinimumSize = new Size(76, 31), Padding = new Padding(6, 0, 6, 0), Margin = new Padding(3) };
    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = new Padding(0) };
        foreach (var child in controls) { child.Anchor = AnchorStyles.Left; panel.Controls.Add(child); }
        return panel;
    }

    private void SetCanvasMode(bool canvasOnly)
    {
        setup.Visible = !canvasOnly; tools.Visible = canvasOnly || client?.IsConnected == true;
        PerformLayout(); tools.PlaceInitially(); tools.BringToFront();
    }
    private void LoadProfile()
    {
        try
        {
            var saved = profiles.Get(selectedMode); address.Text = saved?.Address ?? ""; fingerprint.Text = saved?.Fingerprint ?? ""; code.Clear();
            status.Text = saved?.DeviceToken is not null ? "已保存配对，点「连接」即可。" : "首次连接：粘贴连接信息并填写 8 位配对码。";
        }
        catch (Exception error) { status.Text = "无法读取保存的连接：" + error.Message; }
    }
    private async Task SwitchModeAsync()
    {
        if (updatingMode) return;
        string next = mode.SelectedIndex == 1 ? "tailscale" : "lan"; if (next == selectedMode) return;
        if (busy || shuttingDown || pasteOpen)
        { updatingMode = true; mode.SelectedIndex = selectedMode == "tailscale" ? 1 : 0; updatingMode = false; return; }
        bool reconnect = client?.IsConnected == true; await StopAsync("正在切换连接方式…"); if (shuttingDown) return;
        selectedMode = next; LoadProfile(); UpdateControls();
        try { if (reconnect && profiles.Get(selectedMode)?.DeviceToken is not null) await ConnectAsync(); }
        catch (Exception error) { status.Text = error.Message; }
    }
    private void PasteConnection()
    {
        try
        {
            var parsed = RemoteClientProfile.ParseConnectionInfo(selectedMode, Clipboard.GetText());
            address.Text = parsed.Address; fingerprint.Text = parsed.Fingerprint; code.Clear();
            status.Text = "已填入地址和指纹。请核对，首次连接还需填写配对码。";
            code.Focus();
        }
        catch (Exception error) { status.Text = "无法粘贴：" + error.Message; }
    }
    private void ForgetProfile()
    {
        try { profiles.Forget(selectedMode); code.Clear(); status.Text = "已清除此模式在本机保存的连接；被控电脑授权不变。"; }
        catch (Exception error) { status.Text = "无法清除：" + error.Message; }
    }
    private async Task ConnectAsync()
    {
        if (busy || client is not null || shuttingDown) return;
        busy = true; UpdateControls(); releaseTask = Task.FromResult(true); connectionLifetime = new CancellationTokenSource(); var lifetime = connectionLifetime;
        try
        {
            var profile = RemoteClientProfile.Create(selectedMode, address.Text, fingerprint.Text); var saved = profiles.Get(selectedMode);
            if (saved is not null && saved.Address == profile.Address && saved.Fingerprint == profile.Fingerprint) profile = profile with { DeviceToken = saved.DeviceToken };
            if (string.IsNullOrWhiteSpace(code.Text) && profile.DeviceToken is null)
                throw new InvalidOperationException("请填写被控电脑的 8 位配对码。添加电脑可生成新码，保留原手机配对。");
            client = new WindowsRemoteClient(profile); status.Text = "正在验证证书并连接…";
            if (string.IsNullOrWhiteSpace(code.Text)) await client.ResumeAsync(false, lifetime.Token);
            else await client.PairAsync(code.Text.Trim(), remember.Checked, false, lifetime.Token);
            string? saveError = null;
            try { profiles.Save(client.Profile); } catch (Exception error) { saveError = " 配对未保存：" + error.Message; }
            await client.RequireKeyboardSupportAsync(lifetime.Token); if (lifetime.IsCancellationRequested) return;
            isLocalPreview = IsLocalAddress(client.Profile.Address); code.Clear();
            status.Text = (isLocalPreview ? "本机只读预览 · 键鼠不可回送本机" : "已连接 · " + (selectedMode == "tailscale" ? "Tailscale" : "局域网")) + saveError;
            SetCanvasMode(true); tools.SetExpanded(false); frameTask = ReadFramesAsync(client, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            status.Text = error.Message;
            if (client is { } failed)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await failed.DisconnectAsync(deadline.Token); } catch { }
                failed.Dispose(); client = null;
            }
            lifetime.Cancel(); lifetime.Dispose(); if (connectionLifetime == lifetime) connectionLifetime = null;
        }
        finally { busy = false; UpdateControls(); }
        if (client?.IsConnected == true && !isLocalPreview && !shuttingDown) await ResumeControlAsync();
    }
    internal static bool IsLocalAddress(string url)
    {
        var host = new Uri(url).Host;
        if (!IPAddress.TryParse(host, out var ip)) return host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        return IPAddress.IsLoopback(ip) || NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(entry => entry.Address.Equals(ip));
    }
    private async Task ReadFramesAsync(WindowsRemoteClient connected, CancellationToken token)
    {
        string previousStatus = status.Text;
        bool waitingForDesktop = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                Bitmap frame;
                try { frame = await Task.Run(async () => DecodeFrame(await connected.GetFrameAsync(token)), token); }
                catch (RemoteClientException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    if (!waitingForDesktop)
                    {
                        previousStatus = status.Text;
                        waitingForDesktop = true;
                    }
                    ExitControl(false);
                    status.Text = "远程桌面暂不可用，请检查解锁状态及屏幕录制权限。";
                    view.ClearFrame(status.Text);
                    await Task.Delay(700, token);
                    continue;
                }
                if (token.IsCancellationRequested || client != connected) { frame.Dispose(); return; }
                view.SetFrame(frame);
                if (waitingForDesktop)
                {
                    status.Text = previousStatus;
                    waitingForDesktop = false;
                }
                var interval = TimeSpan.FromMilliseconds(WindowState == FormWindowState.Minimized ? 1500 : 150);
                var remaining = interval - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { ScheduleFailure(connected, "画面中断：" + error.Message); }
    }
    private static Bitmap DecodeFrame(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes, false);
        using var decoded = Image.FromStream(buffer);
        if (decoded.Width > 4096 || decoded.Height > 4096) throw new InvalidDataException("远程画面尺寸过大。");
        return new Bitmap(decoded);
    }
    private void Enqueue(ControlInput input)
    {
        if (client?.IsConnected != true || isLocalPreview || inputQueue is null || inputLifetime?.IsCancellationRequested != false) return;
        if (!inputQueue.TryEnqueue(input)) ScheduleFailure(client, "输入处理速度不足，已断开，请重新连接。");
    }
    private async Task SendInputsAsync(WindowsRemoteClient connected, WindowsInputQueue queue, CancellationToken stopQueue, CancellationToken connection)
    {
        try { await RemoteInputFlow.PumpAsync(queue, connected.SendInputAsync, stopQueue, connection); }
        catch (Exception error) { ScheduleFailure(connected, "操作中断：" + error.Message); throw; }
    }
    private void ScheduleFailure(WindowsRemoteClient connected, string message)
    {
        if (IsDisposed || !IsHandleCreated || shuttingDown) return;
        BeginInvoke(new Action(async () => { if (client == connected && !busy) await StopAsync(message); }));
    }
    // Stop dequeuing, finish any active request, then require releaseAll acknowledgement.
    private void SuspendInput()
    {
        view.ReleaseAllInput();
        var lifetime = inputLifetime; var worker = inputTask; var connected = client;
        inputLifetime = null; inputTask = null; inputQueue = null; lifetime?.Cancel();
        if (lifetime is not null) releaseTask = ReleaseInputAsync(releaseTask, worker, lifetime, connected);
        UpdateToolbar();
    }
    private async Task<bool> ReleaseInputAsync(Task<bool> previous, Task? worker, CancellationTokenSource lifetime, WindowsRemoteClient? connected)
    {
        try
        {
            bool acknowledged = await RemoteInputFlow.ReleaseAsync(previous, worker, async () =>
            {
                if (connected?.IsConnected != true) throw new InvalidOperationException("连接已结束。");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await connected.SendInputAsync(new ControlInput { Action = "releaseAll" }, deadline.Token);
            });
            if (!acknowledged && connected is not null) ScheduleFailure(connected, "输入释放未获确认，已断开；请重新连接后继续。");
            return acknowledged;
        }
        finally { lifetime.Dispose(); }
    }
    private void ExitControl(bool leaveFullscreen)
    { controlRequested = false; SuspendInput(); if (leaveFullscreen && isFullscreen) SetFullscreen(false); UpdateToolbar(); }
    private async Task ResumeControlAsync()
    {
        if (resuming || busy || pasteOpen || shuttingDown || isLocalPreview || client?.IsConnected != true) return;
        resuming = true; controlRequested = true;
        try
        {
            if (!await releaseTask) { controlRequested = false; await StopAsync("输入释放未获确认，已断开；请重新连接后继续。"); return; }
            if (!controlRequested || busy || pasteOpen || shuttingDown || client?.IsConnected != true) return;
            if (inputLifetime is null)
            {
                inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime!.Token);
                inputQueue = new WindowsInputQueue(); inputTask = SendInputsAsync(client, inputQueue, inputLifetime.Token, connectionLifetime.Token);
            }
            tools.SetExpanded(false); SetCanvasMode(true); view.InputEnabled = true; view.Focus(); UpdateToolbar();
        }
        finally { resuming = false; }
    }
    private async Task PrepareTextAsync()
    {
        if (pasteOpen || busy || isLocalPreview || client?.IsConnected != true) return;
        ExitControl(true); pasteOpen = true; tools.Editing = true; UpdateControls();
        if (!await releaseTask)
        { pasteOpen = false; tools.Editing = false; await StopAsync("输入释放未获确认，未读取或发送文本；请重新连接。"); return; }
        var connected = client;
        if (connected?.IsConnected != true || shuttingDown) { pasteOpen = false; tools.Editing = false; UpdateControls(); return; }
        try
        {
            using var dialog = new RemoteTextDialog(async (chunks, cancellation) =>
            {
                var connectionToken = connectionLifetime!.Token;
                try
                {
                    await RemoteInputFlow.SendTextAsync(chunks, connected.SendInputAsync, cancellation, connectionToken);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !connectionToken.IsCancellationRequested) { throw; }
                catch (Exception) { ScheduleFailure(connected, "文本传输未获确认，已断开；检查远程内容后再连接，避免重复。"); throw; }
            });
            textDialog = dialog; dialog.ReadClipboard(); dialog.ShowDialog(this);
        }
        finally { textDialog = null; pasteOpen = false; tools.Editing = false; UpdateControls(); }
    }
    private async Task StopAsync(string message)
    {
        if (busy && !shuttingDown) return;
        ExitControl(true); busy = true; textDialog?.Close(); UpdateControls(); var old = client; connectionLifetime?.Cancel();
        try
        {
            if (frameTask is not null) await frameTask; await releaseTask;
            if (old is not null) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)); try { await old.DisconnectAsync(deadline.Token); } catch { } }
        }
        finally
        {
            old?.Dispose(); client = null; frameTask = null; connectionLifetime?.Dispose(); connectionLifetime = null;
            isLocalPreview = false; view.ClearFrame(); status.Text = message; busy = false; SetCanvasMode(false); UpdateControls();
        }
    }
    internal Task ShutdownAsync() => shutdownTask ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        shuttingDown = true; textDialog?.Close(); connectionLifetime?.Cancel(); while (busy) await Task.Delay(20);
        await StopAsync("已断开。"); closingAllowed = true; if (!IsDisposed) Close();
    }
    private void UpdateToolbar() => tools.UpdateState(status.Text + (controlRequested ? " · Ctrl+Alt+F12 退出键鼠" : " · 键鼠已退出"),
        controlRequested, client?.IsConnected == true, isLocalPreview, isFullscreen, busy || pasteOpen || shuttingDown);
    private void UpdateControls()
    {
        bool edit = !busy && client?.IsConnected != true && !shuttingDown && !pasteOpen;
        address.Enabled = fingerprint.Enabled = code.Enabled = remember.Enabled = paste.Enabled = forget.Enabled = connect.Enabled = edit;
        disconnect.Enabled = !busy && client?.IsConnected == true && !shuttingDown; mode.Enabled = !busy && !shuttingDown && !pasteOpen; UpdateToolbar();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Alt | Keys.F12)) { ExitControl(true); return true; }
        if (keyData == (Keys.Control | Keys.Alt | Keys.Enter)) { ToggleFullscreen(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private void ToggleFullscreen()
    {
        bool resume = controlRequested; SuspendInput(); SetFullscreen(!isFullscreen); tools.SetExpanded(false);
        if (resume) _ = ResumeControlAsync();
    }
    private void SetFullscreen(bool value)
    {
        if (value == isFullscreen) return;
        if (value)
        {
            normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None;
            isFullscreen = true; SetCanvasMode(true); Bounds = Screen.FromControl(this).Bounds;
        }
        else { isFullscreen = false; WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.Sizable; Bounds = normalBounds; SetCanvasMode(client?.IsConnected == true); }
        tools.ClampToParent(); UpdateToolbar();
    }
    private void ShowGuide()
    {
        using var guide = new Form { Text = "Windows 控制端使用指南", Font = Font, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(660, 550), MinimumSize = new Size(450, 350), AutoScaleMode = AutoScaleMode.Dpi, ShowInTaskbar = false };
        var body = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BackColor = Color.White, BorderStyle = BorderStyle.None, AccessibleName = "Windows 控制端指南正文", Text = GuideDisplayText };
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) }; panel.Controls.Add(body);
        var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom, Height = 36 };
        guide.Controls.Add(panel); guide.Controls.Add(close); guide.CancelButton = close;
        guide.Shown += (_, _) => { body.Select(0, 0); close.Focus(); }; guide.ShowDialog(this);
    }
    internal static string GuideDisplayText => GuideText.ReplaceLineEndings(Environment.NewLine);
    internal const string GuideText = """
        一、准备与连接
        被控电脑可以是 Windows 或 Mac。在对方的 PhoneRemote 启动局域网或 Tailscale 服务，粘贴其 HTTPS 地址与证书指纹，核对后输入配对码。Mac 被控端需要允许屏幕录制和辅助功能。添加电脑可保留原手机配对，生成新码即可。
        跨 Wi-Fi 时两端都连接同一个 Tailscale 网络。两种模式独立保存，下次选择对应模式连接。被控电脑需要开机联网、桌面未锁定。

        二、直接使用键盘鼠标
        连接后直接在画面上操作鼠标和键盘，包括 Tab、Esc、功能键、方向键和组合键。键盘捕获只在远程画面处于前台并启用控制时生效。
        Ctrl+Alt+F12 退出键鼠控制，同时退出全屏回到普通窗口，连接与画面继续保留。Ctrl+Alt+Enter 切换全屏。系统安全桌面和 Ctrl+Alt+Delete 不支持，中文也可使用本机文本功能。

        三、顶部折叠工具栏
        工具栏默认折叠在画面顶部。点击小条展开，按住条上的拖动区域可移动位置。展开期间暂停键鼠，避免操作工具栏影响远程电脑。
        展开后有退出或恢复键鼠、准备粘贴本机文本、全屏、断开连接、连接设置及使用指南。退出后点击画面不会自动恢复，须明确选「恢复键鼠控制」。
        全屏时仅显示被控画面和折叠工具栏。画面按比例显示，黑边不发送鼠标操作。

        四、从本机粘贴文本
        先在被控电脑选好输入位置，再选「准备粘贴本机文本」。这会退出键鼠和全屏，显示可编辑预览。
        可切到主控电脑其他应用复制文本，返回点「重新读取本机剪贴板」。核对后点「发送到被控电脑」才传输。每次最多 8192 个字符，顺序分段发送；关闭不会发送。
        发送后仍保持退出键鼠状态，请明确恢复。发送失败可能已有部分内容到达，先检查远程内容，避免重复。普通 Ctrl+V 使用被控电脑自己的剪贴板，不会自动同步本机剪贴板。

        五、断开与排查
        关闭控制窗口或点「断开连接」结束会话，保存的配对保留。主机服务独立运行。同一时刻只有一个有效控制者。
        超时请检查服务、防火墙和 Tailscale。证书不匹配时请在被控端重新核对。Windows 被控端使用 2.5.0 或更新版本，Mac 被控端使用带 Mac 服务的版本。

        本机预览与双机验证
        连接本机强制只读预览，防止输入循环。可检查画面、全屏和工具栏；实际异机键鼠、缩放和跨网络仍需第二台电脑实测。
        """;
    protected override void Dispose(bool disposing)
    {
        if (disposing) { controlRequested = false; view.ReleaseAllInput(); inputLifetime?.Cancel(); connectionLifetime?.Cancel(); client?.Dispose(); }
        base.Dispose(disposing);
    }
}
