using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteControl;

internal sealed partial class HostForm
{
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _trayLan = new();
    private readonly ToolStripMenuItem _trayTailscale = new();
    private readonly ToolStripMenuItem _trayExit = new("退出应用");
    private readonly Button _background = MakeButton("后台运行", 106);
    private readonly Button _exit = MakeButton("退出应用", 106);
    private readonly Icon _stoppedIcon = MakeTrayIcon(Color.FromArgb(112, 126, 140));
    private readonly Icon _runningIcon = MakeTrayIcon(Color.FromArgb(18, 101, 86));
    private readonly Icon _connectedIcon = MakeTrayIcon(Color.FromArgb(26, 117, 226));
    private bool _trayNoticeShown;
    private bool _showingGuide;
    private bool _trayDisposed;

    private Control CreateWindowActions()
    {
        var footer = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(24, 10, 24, 14),
            BackColor = Surface
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var hint = MakeLabel("右下角托盘可查看状态或恢复窗口。", Muted);
        hint.MaximumSize = new Size(230, 0);
        hint.Anchor = AnchorStyles.Left;
        footer.Controls.Add(hint, 0, 0);
        footer.Controls.Add(MakeActions(_background, _exit), 1, 0);
        _background.Click += (_, _) => HideToTray();
        _exit.Click += (_, _) => RequestExit();
        return footer;
    }

    private void InitializeTray()
    {
        var restore = new ToolStripMenuItem("显示操作界面", null, (_, _) => RestoreWindow());
        restore.Font = new Font(_trayMenu.Font, FontStyle.Bold);
        _trayMenu.Items.Add(restore);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.AddRange([_trayLan, _trayTailscale]);
        _trayLan.Click += (_, _) => { _lan.Selector.Checked = true; RestoreWindow(); };
        _trayTailscale.Click += (_, _) => { _tailscale.Selector.Checked = true; RestoreWindow(); };
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("控制另一台电脑", null, (_, _) => ShowComputerClient());
        _trayMenu.Items.Add("使用指南", null, (_, _) => ShowGuide());
        _trayMenu.Items.Add(_trayExit);
        _trayExit.Click += (_, _) => RequestExit();
        _tray.ContextMenuStrip = _trayMenu;
        _tray.Icon = _stoppedIcon;
        _tray.Text = "PhoneRemote";
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tray.BalloonTipClicked += (_, _) => RestoreWindow();
        _trayMenu.Opening += (_, _) => UpdateDisplay();
        _tray.Visible = true;
        Icon = _runningIcon;
    }

    internal void RestoreWindow()
    {
        if (IsDisposed || Disposing || _closeRequested)
            return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void HideToTray()
    {
        if (_closeRequested)
            return;
        UpdateDisplay();
        Hide();
        ShowInTaskbar = false;
        if (!_trayNoticeShown)
        {
            _trayNoticeShown = true;
            _tray.ShowBalloonTip(4000, "PhoneRemote 正在后台运行",
                "已启动的连接保持运行。双击右下角托盘图标恢复窗口，右键可查看状态或退出。",
                ToolTipIcon.Info);
        }
    }

    private void RequestExit()
    {
        if (_closeRequested)
            return;
        _closeRequested = true;
        Close();
    }

    private void ShowGuide()
    {
        if (_closeRequested || _showingGuide)
            return;
        RestoreWindow();
        _showingGuide = true;
        try
        {
            using var guide = new GuideForm();
            guide.ShowDialog(this);
        }
        finally
        {
            _showingGuide = false;
        }
    }

    private void UpdateTray(ServerStatus? lan, ServerStatus? tailscale)
    {
        if (_trayDisposed)
            return;
        static string Describe(ServerStatus? state) => state?.Connected == true ? "设备已连接"
            : state?.RememberedDevice == true ? "已配对，等待连接"
            : state is not null ? "运行中，等待配对" : "未启动";
        _trayLan.Text = $"局域网 · {Describe(lan)}";
        _trayTailscale.Text = $"Tailscale · {Describe(tailscale)}";
        _tray.Text = _closeRequested ? "PhoneRemote · 正在退出…"
            : $"PhoneRemote\n局域网：{Describe(lan)}\nTailscale：{Describe(tailscale)}";
        _tray.Icon = lan?.Connected == true || tailscale?.Connected == true ? _connectedIcon
            : lan is not null || tailscale is not null ? _runningIcon : _stoppedIcon;
        _trayExit.Enabled = _exit.Enabled = _background.Enabled = !_closeRequested;
        _trayLan.Enabled = _trayTailscale.Enabled = !_busy && !_closeRequested;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_trayDisposed)
        {
            _trayDisposed = true;
            _statusTimer.Dispose();
            _clientWindow?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _trayMenu.Dispose();
            _stoppedIcon.Dispose();
            _runningIcon.Dispose();
            _connectedIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon MakeTrayIcon(Color status)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var monitor = new Pen(Color.FromArgb(37, 58, 76), 2.6F);
            using var screen = new SolidBrush(Color.FromArgb(233, 245, 244));
            using var dot = new SolidBrush(status);
            canvas.FillRectangle(screen, 3, 4, 25, 18);
            canvas.DrawRectangle(monitor, 3, 4, 25, 18);
            canvas.DrawLine(monitor, 14, 23, 14, 28);
            canvas.DrawLine(monitor, 8, 28, 20, 28);
            canvas.FillEllipse(Brushes.White, 19, 18, 13, 13);
            canvas.FillEllipse(dot, 21, 20, 9, 9);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
