using System;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteControl;

internal sealed partial class HostForm : Form
{
    private static readonly Color Ink = Color.FromArgb(27, 43, 60);
    private static readonly Color Muted = Color.FromArgb(76, 92, 108);
    private static readonly Color Accent = Color.FromArgb(18, 101, 86);
    private static readonly Color Surface = Color.FromArgb(240, 245, 245);

    private readonly ComboBox _addresses = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _refreshAddresses = MakeButton("刷新", 78);
    private readonly Button _startStop = MakeButton("启动连接", 106);
    private readonly Button _guide = MakeButton("使用指南", 88);
    private readonly Button _controlComputer = MakeButton("控制 Windows 或 Mac", 180);
    private WindowsClientForm? _clientWindow;
    private readonly Button _copyConnection = MakeButton("复制连接信息", 132);
    private readonly Button _copyAddress = MakeButton("复制", 78);
    private readonly Button _copyFingerprint = MakeButton("复制", 78);
    private readonly Button _copyCode = MakeButton("复制配对码", 106);
    private readonly Button _refreshCode = MakeButton("生成新码", 106);
    private readonly Button _disconnect = MakeButton("断开设备", 106);
    private readonly Button _forgetDevice = MakeButton("忘记所有设备", 132);
    private readonly TextBox _address = MakeReadOnlyBox();
    private readonly TextBox _fingerprint = MakeReadOnlyBox();
    private readonly Label _connectionHint = MakeLabel("", Muted);
    private readonly Label _status = MakeLabel("服务未启动", Ink);
    private readonly Label _code = MakeLabel("—", Accent);
    private readonly Label _expiration = MakeLabel("", Muted);
    private readonly Label _notice = MakeLabel("", Muted);
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 500 };
    private readonly RemoteControlSession _control = new();
    private readonly ModePanel _lan = new("局域网", false);
    private readonly ModePanel _tailscale = new("Tailscale", true);

    private ModePanel _mode;
    private bool _busy;
    private bool _closeRequested;
    private bool _shutdownComplete;

    public HostForm()
    {
        _mode = _lan;
        Text = "PhoneRemote · 电脑遥控";
        Font = new Font("Microsoft YaHei UI", 10F);
        ForeColor = Ink;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(692, 740);
        MinimumSize = new Size(565, 490);
        StartPosition = FormStartPosition.CenterScreen;

        _startStop.BackColor = Accent;
        _startStop.ForeColor = Color.White;
        _startStop.FlatStyle = FlatStyle.Flat;
        _startStop.FlatAppearance.BorderSize = 0;
        _status.Font = new Font(Font, FontStyle.Bold);
        _code.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
        _addresses.AccessibleName = "电脑的连接地址";
        _address.AccessibleName = "控制端访问地址";
        _fingerprint.AccessibleName = "证书 SHA-256 指纹";
        _copyAddress.AccessibleName = "复制控制端访问地址";
        _copyFingerprint.AccessibleName = "复制证书指纹";
        _fingerprint.Multiline = true;
        _fingerprint.WordWrap = false;
        _fingerprint.Height = 43;
        _fingerprint.Font = new Font("Consolas", 9F);

        var content = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 0, 24, 18)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 12, 24, 14)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = MakeLabel("PhoneRemote", Ink);
        title.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
        title.Anchor = AnchorStyles.Left;
        title.Margin = new Padding(0, 0, 0, 5);
        _guide.Anchor = AnchorStyles.Right;
        _controlComputer.Anchor = AnchorStyles.Right;
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(_controlComputer, 1, 0);
        header.Controls.Add(_guide, 2, 0);
        var subtitle = MakeLabel("允许其他设备控制本机", Ink);
        subtitle.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
        AddRow(content, subtitle, 8);
        var modes = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 12, 0, 0)
        };
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _lan.Selector.Margin = new Padding(0, 0, 5, 0);
        _tailscale.Selector.Margin = new Padding(5, 0, 0, 0);
        modes.Controls.Add(_lan.Selector, 0, 0);
        modes.Controls.Add(_tailscale.Selector, 1, 0);
        AddRow(content, modes, 12);

        AddRow(content, MakeLabel("电脑的连接地址", Ink), 3);
        AddRow(content, MakeFieldRow(_addresses, _refreshAddresses, _startStop), 4);
        AddRow(content, _connectionHint, 12);

        var pairing = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Surface,
            Padding = new Padding(14, 10, 14, 10)
        };
        pairing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pairing.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pairing.Controls.Add(_status, 0, 0);
        pairing.SetColumnSpan(_status, 2);
        pairing.Controls.Add(_code, 0, 1);
        pairing.Controls.Add(MakeActions(_copyCode, _refreshCode), 1, 1);
        pairing.Controls.Add(_expiration, 0, 2);
        pairing.Controls.Add(MakeActions(_disconnect, _forgetDevice), 1, 2);
        var deviceHint = MakeLabel("添加电脑时先断开当前控制，点「生成新码」。原有配对会保留。", Muted);
        pairing.Controls.Add(deviceHint, 0, 3);
        pairing.SetColumnSpan(deviceHint, 2);
        _expiration.Anchor = AnchorStyles.Left;
        AddRow(content, pairing, 12);

        AddRow(content, MakeLabel("控制端访问地址", Ink), 3);
        AddRow(content, MakeFieldRow(_address, _copyAddress, _copyConnection), 8);
        AddRow(content, MakeLabel("在 Android、Windows 或 Mac 控制端粘贴连接信息。", Muted), 12);

        AddRow(content, MakeLabel("证书 SHA-256 指纹", Ink), 3);
        AddRow(content, MakeFieldRow(_fingerprint, _copyFingerprint), 8);
        AddRow(content, MakeLabel("首次配对须核对完整指纹。每种模式最多记住 16 台设备。", Muted), 8);
        AddRow(content, _notice, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(content);
        Controls.Add(scroll);
        Controls.Add(CreateWindowActions());
        Controls.Add(header);

        _guide.Click += (_, _) => ShowGuide();
        _controlComputer.Click += (_, _) => ShowComputerClient();
        _lan.Selector.CheckedChanged += (_, _) => SelectMode(_lan);
        _tailscale.Selector.CheckedChanged += (_, _) => SelectMode(_tailscale);
        _lan.Selector.Checked = true;
        _refreshAddresses.Click += (_, _) => LoadAddresses();
        _addresses.SelectedIndexChanged += (_, _) =>
        {
            _mode.SelectedAddress = _addresses.SelectedItem as IPAddress;
            UpdateDisplay();
        };
        _startStop.Click += async (_, _) => await ToggleServerAsync();
        _copyAddress.Click += (_, _) => CopyValue(_address.Text, "已复制访问地址。");
        _copyConnection.Click += (_, _) =>
        {
            if (_mode.Server is { } server)
                CopyValue(server.Address.AbsoluteUri + Environment.NewLine + server.Fingerprint, "已复制地址和证书指纹，各占一行。");
        };
        _copyFingerprint.Click += (_, _) =>
        {
            if (_mode.Server is { } server)
                CopyValue(server.Fingerprint, "已复制证书指纹。");
        };
        _copyCode.Click += (_, _) =>
        {
            if (_mode.Server?.GetStatus().Code is { } code)
                CopyValue(code, "已复制当前模式的配对码。");
            else
                UpdateDisplay();
        };
        _refreshCode.Click += (_, _) =>
        {
            if (_mode.Server?.RefreshCode() == true)
                _notice.Text = "";
            else
                _notice.Text = "暂时无法生成新码：仍有设备连接，或已达 16 台配对上限。";
            UpdateDisplay();
        };
        _disconnect.Click += (_, _) =>
        {
            _mode.Server?.Disconnect();
            _notice.Text = "本次连接已断开。记住的设备可再次连接。";
            UpdateDisplay();
        };
        _forgetDevice.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"撤销{_mode.Name}模式下所有手机和电脑的配对？之后这些设备都需要重新配对。",
                "忘记所有设备", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;
            try
            {
                _mode.Server?.ForgetDevice();
                _notice.Text = "已取消配对。再次连接时请生成新码。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法取消设备配对", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateDisplay();
        };
        _statusTimer.Tick += (_, _) => UpdateDisplay();
        FormClosing += ShutDown;
        InitializeTray();

        LoadAddresses();
        _statusTimer.Start();
    }

    private void ShowComputerClient()
    {
        if (_closeRequested) return;
        if (_clientWindow is null || _clientWindow.IsDisposed)
            _clientWindow = new WindowsClientForm();
        _clientWindow.Show();
        if (_clientWindow.WindowState == FormWindowState.Minimized)
            _clientWindow.WindowState = FormWindowState.Normal;
        _clientWindow.Activate();
    }

    private void SelectMode(ModePanel mode)
    {
        if (!mode.Selector.Checked || mode == _mode)
            return;
        _mode = mode;
        LoadAddresses();
    }

    private void LoadAddresses()
    {
        var previous = _mode.SelectedAddress;
        _addresses.BeginUpdate();
        try
        {
            _addresses.Items.Clear();
            foreach (var ip in RemoteServer.GetLocalAddresses())
            {
                if (IsTailscaleAddress(ip) == _mode.IsTailscale)
                    _addresses.Items.Add(ip);
            }
            if (_mode.Server is not null && previous is not null && !_addresses.Items.Contains(previous))
                _addresses.Items.Add(previous);
            if (previous is not null && _addresses.Items.Contains(previous))
                _addresses.SelectedItem = previous;
            else if (_addresses.Items.Count > 0)
                _addresses.SelectedIndex = 0;
            _mode.SelectedAddress = _addresses.SelectedItem as IPAddress;
            _notice.Text = "";
        }
        finally
        {
            _addresses.EndUpdate();
        }
        UpdateDisplay();
    }

    private async Task ToggleServerAsync()
    {
        if (_busy)
            return;

        var mode = _mode;
        _busy = true;
        _notice.Text = "";
        UpdateDisplay();
        try
        {
            if (mode.Server is not null)
            {
                await StopModeAsync(mode);
            }
            else if (mode.SelectedAddress is { } selected)
            {
                mode.Server = await RemoteServer.StartAsync(selected, _control);
            }
        }
        catch (Exception ex)
        {
            if (!_closeRequested)
                MessageBox.Show(this, ex.Message, "无法切换连接状态", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            UpdateDisplay();
            if (_closeRequested)
                Close();
        }
    }

    private async void ShutDown(object? sender, FormClosingEventArgs e)
    {
        if (_shutdownComplete)
            return;

        // Windows must be able to log off or shut down without a modal prompt or
        // an async FormClosing cancellation. Release held input synchronously.
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            _closeRequested = true;
            _lan.Server?.Disconnect();
            _tailscale.Server?.Disconnect();
            return;
        }

        if (!_closeRequested && e.CloseReason == CloseReason.UserClosing)
        {
            using var choice = new CloseOptionsForm();
            var result = choice.ShowDialog(this);
            if (result != DialogResult.No)
            {
                e.Cancel = true;
                if (result == DialogResult.Yes)
                    HideToTray();
                return;
            }
        }

        _closeRequested = true;
        if (!_busy && _lan.Server is null && _tailscale.Server is null && (_clientWindow is null || _clientWindow.IsDisposed))
            return;

        e.Cancel = true;
        if (_busy)
        {
            UpdateDisplay();
            return;
        }

        _busy = true;
        _statusTimer.Stop();
        UpdateDisplay();
        try
        {
            if (_clientWindow is { IsDisposed: false })
                await _clientWindow.ShutdownAsync();
            await Task.WhenAll(StopModeAsync(_lan), StopModeAsync(_tailscale));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Remote service shutdown: {ex}");
        }
        finally
        {
            _shutdownComplete = true;
            BeginInvoke(new Action(Close));
        }
    }

    private static async Task StopModeAsync(ModePanel mode)
    {
        var server = mode.Server;
        mode.Server = null;
        if (server is not null)
            await server.DisposeAsync();
    }

    private void UpdateDisplay()
    {
        ServerStatus? lanStatus;
        ServerStatus? tailscaleStatus;
        lock (_control.StateLock)
        {
            lanStatus = _lan.Server?.GetStatus();
            tailscaleStatus = _tailscale.Server?.GetStatus();
        }
        var server = _mode.Server;
        var state = _mode == _lan ? lanStatus : tailscaleStatus;
        var connected = state?.Connected == true;
        var remembered = state?.RememberedDevice == true;
        var code = state?.Code;
        var selected = _mode.SelectedAddress;
        var tailscale = _mode.IsTailscale;

        UpdateModeSelector(_lan, lanStatus);
        UpdateModeSelector(_tailscale, tailscaleStatus);
        UpdateTray(lanStatus, tailscaleStatus);
        _lan.Selector.Enabled = _tailscale.Selector.Enabled = !_busy && !_closeRequested;

        _addresses.Enabled = !_busy && server is null;
        _refreshAddresses.Enabled = _addresses.Enabled;
        _startStop.Enabled = !_busy && (server is not null || selected is not null);
        _startStop.Text = server is null ? "启动连接" : "停止连接";
        _connectionHint.Text = tailscale
            ? selected is null ? "连接电脑上的 Tailscale 后，点击刷新。" : "控制端与本机的 Tailscale 须加入同一个网络。"
            : selected is null ? "让电脑接入局域网后，点击刷新。" : "控制端与本机须接入同一个可信的局域网。";
        SetTextIfChanged(_address, server?.Address.AbsoluteUri ?? "");
        SetTextIfChanged(_fingerprint, FormatFingerprint(server?.Fingerprint));
        _copyAddress.Enabled = _copyFingerprint.Enabled = _copyConnection.Enabled = !_busy && server is not null;
        _copyCode.Enabled = !_busy && code is not null;
        _refreshCode.Enabled = !_busy && server is not null && !connected;
        _controlComputer.Enabled = !_closeRequested;
        _disconnect.Enabled = !_busy && connected;
        _forgetDevice.Enabled = !_busy && remembered;

        if (_closeRequested)
            _status.Text = "正在退出应用…";
        else if (_busy)
            _status.Text = "正在切换连接状态…";
        else if (server is null)
            _status.Text = "服务未启动";
        else
        {
            var deviceStatus = connected ? "控制设备已连接" : code is not null ? "等待新设备配对" : remembered ? "已配对，等待设备连接" : "等待设备配对";
            _status.Text = $"{_mode.Name} · {deviceStatus}";
        }

        _code.Text = code ?? "—";
        if (code is not null && state?.CodeExpiresAt is DateTimeOffset expiry)
        {
            var remaining = Math.Max(0, (int)Math.Ceiling((expiry - DateTimeOffset.UtcNow).TotalSeconds));
            _expiration.Text = $"配对码剩余 {remaining / 60}:{remaining % 60:00}";
        }
        else if (connected)
            _expiration.Text = "后台运行可继续遥控";
        else if (remembered)
            _expiration.Text = "已配对设备可直接连接";
        else if (server is not null)
            _expiration.Text = "点击生成新码开始配对";
        else
            _expiration.Text = "启动后显示一次性配对码";
    }

    private static void UpdateModeSelector(ModePanel mode, ServerStatus? status)
    {
        var summary = status?.Connected == true ? "设备已连接"
            : status?.RememberedDevice == true ? "已配对"
            : status is not null ? "运行中" : "未启动";
        mode.Selector.Text = $"{mode.Name} · {summary}";
    }

    private void CopyValue(string value, string notice)
    {
        try
        {
            Clipboard.SetText(value);
            _notice.Text = notice;
        }
        catch (ExternalException)
        {
            _notice.Text = "剪贴板正忙，请稍后再试。";
        }
    }

    private static void SetTextIfChanged(TextBox field, string value)
    {
        if (field.Text != value)
            field.Text = value;
    }

    private static string FormatFingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var middle = (value.Length + 1) / 2;
        return value.Insert(middle, Environment.NewLine);
    }

    private static Label MakeLabel(string text, Color color) => new()
    {
        Text = text,
        ForeColor = color,
        AutoSize = true,
        Margin = new Padding(0, 3, 0, 3)
    };

    private static TextBox MakeReadOnlyBox() => new()
    {
        ReadOnly = true,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static Button MakeButton(string text, int width) => new()
    {
        Text = text,
        Size = new Size(width, 32),
        Margin = new Padding(8, 2, 0, 2),
        UseVisualStyleBackColor = true
    };

    private static FlowLayoutPanel MakeActions(params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0)
        };
        row.Controls.AddRange(buttons);
        return row;
    }

    private static TableLayoutPanel MakeFieldRow(Control field, params Button[] buttons)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1 + buttons.Length,
            Dock = DockStyle.Top
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Margin = new Padding(0);
        row.Controls.Add(field, 0, 0);
        for (var i = 0; i < buttons.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(buttons[i], i + 1, 0);
        }
        return row;
    }

    private static void AddRow(TableLayoutPanel table, Control child, int spaceBelow)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        child.Margin = new Padding(0, 0, 0, spaceBelow);
        table.Controls.Add(child, 0, row);
    }

    private static bool IsTailscaleAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private sealed class ModePanel(string name, bool isTailscale)
    {
        public string Name { get; } = name;
        public bool IsTailscale { get; } = isTailscale;
        public IPAddress? SelectedAddress { get; set; }
        public RemoteServer? Server { get; set; }
        public RadioButton Selector { get; } = new()
        {
            Appearance = Appearance.Button,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = false,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter
        };
    }
}
