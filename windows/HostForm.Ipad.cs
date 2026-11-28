using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

internal sealed partial class HostForm
{
    private readonly TabControl _sections = new() { Dock = DockStyle.Fill, AccessibleName = "功能版块" };
    private readonly TabPage _ipadPage = new("iPad 副屏") { BackColor = Color.White, AutoScroll = true };
    private readonly IpadDisplayModule _ipadModule = new();
    private readonly Label _ipadStatus = MakeLabel("等待启动", Ink);
    private readonly Label _ipadDetail = MakeLabel("", Muted);
    private readonly Button _ipadConsole = MakeButton("打开配对控制台", 148);
    private readonly Button _ipadStart = MakeButton("启动副屏", 100);
    private readonly Button _ipadStop = MakeButton("停止副屏", 100);
    private readonly Button _ipadConfigure = MakeButton("配置驱动和网络", 148);
    private bool _ipadBusy;
    private bool _ipadReady;

    private Control CreateModuleTabs(Control remoteContent)
    {
        var remotePage = new TabPage("电脑遥控") { BackColor = Color.White };
        remotePage.Controls.Add(remoteContent);
        var content = new TableLayoutPanel
        {
            ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24, 20, 24, 16)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = MakeLabel("用 iPad 扩展 Windows 桌面", Ink);
        title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
        AddRow(content, title, 12);
        _ipadStatus.Font = new Font(Font, FontStyle.Bold);
        _ipadDetail.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        AddRow(content, _ipadStatus, 4);
        AddRow(content, _ipadDetail, 10);
        var actions = MakeActions(_ipadConsole, _ipadStart, _ipadStop);
        actions.Anchor = AnchorStyles.Left;
        AddRow(content, actions, 20);
        AddRow(content, MakeLabel("连接这台 iPad", Ink), 4);
        var connect = MakeLabel("将 iPad 和电脑接入同一局域网。从 iPad 主屏幕打开「Wi-Fi 副屏」，横放后点「连接屏幕」。新设备请先打开电脑上的配对控制台，按提示安装并允许连接。", Muted);
        connect.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        AddRow(content, connect, 14);
        AddRow(content, MakeLabel("把窗口移到副屏", Ink), 4);
        var move = MakeLabel("在电脑上选中窗口，按 Win + Shift + → 移到右侧副屏。轻点 iPad 画面即可打开全屏工具栏和操作说明。", Muted);
        move.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        AddRow(content, move, 20);
        AddRow(content, MakeLabel("首次配置这台电脑", Ink), 4);
        var setup = MakeLabel("首次设置虚拟显示器或防火墙时，点下方按钮并在 Windows 提示时允许。", Muted);
        setup.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        AddRow(content, setup, 6);
        AddRow(content, _ipadConfigure, 0);
        _ipadPage.Controls.Add(content);
        _sections.TabPages.AddRange([remotePage, _ipadPage]);
        _sections.SelectedIndexChanged += async (_, _) =>
        {
            Text = _sections.SelectedTab == _ipadPage ? "PhoneRemote · iPad 副屏" : "PhoneRemote · 电脑遥控";
            if (_sections.SelectedTab == _ipadPage)
                await SetIpadRunningAsync(true);
        };
        _ipadStart.Click += async (_, _) => await SetIpadRunningAsync(true);
        _ipadStop.Click += async (_, _) => await SetIpadRunningAsync(false);
        _ipadConfigure.Click += async (_, _) =>
        {
            if (_ipadBusy || _closeRequested) return;
            _ipadBusy = true;
            UpdateIpadActions();
            _ipadDetail.Text = "正在配置虚拟显示器和防火墙…";
            try
            {
                await _ipadModule.ConfigureAsync();
                if (_ipadReady)
                    ShowIpadServiceState();
                else
                    _ipadDetail.Text = "配置完成，可以点「启动副屏」。";
            }
            catch (Exception ex)
            {
                _ipadDetail.Text = ex.Message;
            }
            finally
            {
                FinishIpadAction();
            }
        };
        _ipadConsole.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(IpadDisplayModule.ConsoleUri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                _ipadDetail.Text = $"可在本机浏览器打开 {IpadDisplayModule.ConsoleUri} 进入配对控制台。";
            }
        };
        UpdateIpadActions();
        return _sections;
    }

    private async Task SetIpadRunningAsync(bool start)
    {
        if (_ipadBusy || _closeRequested) return;
        _ipadBusy = true;
        _ipadStatus.Text = start ? "正在启动副屏…" : "正在停止副屏…";
        _ipadDetail.Text = "";
        UpdateIpadActions();
        try
        {
            if (start)
                await _ipadModule.StartAsync();
            else
                await _ipadModule.StopAsync(includeExisting: true);
            _ipadReady = start;
            if (start)
                ShowIpadServiceState();
            else
                _ipadStatus.Text = "副屏服务已停止";
        }
        catch (Exception ex)
        {
            if (start) _ipadReady = false;
            _ipadStatus.Text = start ? "副屏启动失败" : "副屏停止失败";
            _ipadDetail.Text = ex.Message;
        }
        finally
        {
            FinishIpadAction();
        }
    }

    private void ShowIpadServiceState()
    {
        if (!_ipadModule.DisplayAvailable)
        {
            _ipadStatus.Text = "服务已启动，等待虚拟显示器";
            _ipadDetail.Text = "请点「配置驱动和网络」设置这台电脑。";
            return;
        }
        _ipadStatus.Text = "副屏服务已就绪";
        if (_ipadModule.VideoPort == 8443)
            _ipadDetail.Text = "当前 iPad 配对与局域网遥控共用 8443 端口。要启动同一地址的遥控，请先点「停止副屏」。";
        else if (_ipadModule.OwnsService)
            _ipadDetail.Text = "切换版块或收起窗口后，副屏仍会运行。";
        else
            _ipadDetail.Text = "已接入原先运行的副屏服务。可以在此停止，或在退出 PhoneRemote 后继续使用。";
    }

    private void FinishIpadAction()
    {
        _ipadBusy = false;
        if (IsDisposed || Disposing) return;
        UpdateIpadActions();
        if (_closeRequested) Close();
    }

    private void UpdateIpadActions()
    {
        _ipadConsole.Enabled = _ipadReady && !_ipadBusy && !_closeRequested;
        _ipadStart.Visible = !_ipadReady;
        _ipadStop.Visible = _ipadReady || _ipadModule.OwnsService;
        _ipadStart.Enabled = _ipadStop.Enabled = _ipadConfigure.Enabled = !_ipadBusy && !_closeRequested;
    }
}
