using System;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

internal sealed class GuideForm : Form
{
    public GuideForm()
    {
        Text = "使用指南";
        Font = new Font("Microsoft YaHei UI", 10.5F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 550);
        MinimumSize = new Size(440, 330);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = Color.White;

        var guide = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Font = this.Font,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(27, 43, 60),
            ReadOnly = true,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            AccessibleName = "使用指南正文"
        };

        using var heading = new Font(Font.FontFamily, 12.5F, FontStyle.Bold);
        AddSection(guide, heading, "用 Windows 控制另一台电脑", """
            要控制 Windows 或 Mac，在对方电脑上启动 PhoneRemote 被控服务，在本机点「控制 Windows 或 Mac」。Mac 作为被控端时需要允许屏幕录制和辅助功能。
            从对方电脑取得 HTTPS 地址和证书指纹，将两行内容传到本机，选择相同的局域网或 Tailscale 模式，点「粘贴连接信息」，再核对地址和指纹。
            首次连接填写被控电脑显示的 8 位配对码，勾选记住后连接。已经配对手机时，先结束当前控制会话，再在被控端点「生成新码」添加电脑；不要点「忘记所有设备」。原来的手机配对会保留。
            控制窗口支持直接使用键盘鼠标。顶部工具栏默认折叠，可以拖动位置；展开后可进入全屏、退出键鼠控制或准备粘贴本机文字。全屏时只保留远程画面与浮动工具栏。
            Ctrl+Alt+F12 或工具栏「退出键鼠控制」会同时恢复普通窗口，保留画面连接。Ctrl+Alt+Enter 切换全屏。粘贴本机文本会先退出控制，在预览确认后才发送。
            两种模式的配对独立保存。仅使用控制端不需要开放入站端口。
            不同网络时两台电脑均须连接同一个 Tailscale 网络。被控电脑需要开机、联网并保持普通桌面可用，不能控制锁屏或 UAC 安全桌面。
            连接本机地址会进入只读预览，防止鼠标输入在本机循环。单机预览和自动测试不能代替真实双机跨网络验收。
            """);
        AddSection(guide, heading, "从 Mac 连接这台 Windows 电脑", """
            在此窗口选择局域网或 Tailscale 地址，点「启动连接」，再点「复制连接信息」。在 Mac 的 PhoneRemote 控制端填入地址和证书指纹，核对后输入此窗口的 8 位配对码。
            两台 Mac 之间连接时，在被控 Mac 启动服务并获取连接信息，在另一台 Mac 上打开 PhoneRemote 控制端，使用相同的配对流程。
            """);
        AddSection(guide, heading, "选择连接方式", """
            手机和电脑在同一个可信的 Wi-Fi 时，在电脑窗口上方选「局域网」。电脑用网线接入同一个路由器也可以。
            跨 Wi-Fi 或用移动数据时，两端都打开 Tailscale 并加入同一个网络，然后在电脑窗口上方选「Tailscale」。
            如果两种都要用，在电脑的两个页面分别点「启动连接」。换到另一个页面时，已经启动的模式会继续运行。
            """);
        AddSection(guide, heading, "首次配对", """
            1. 在电脑选好地址后点「启动连接」。点「复制连接信息」可一次复制 HTTPS 地址和证书指纹，两项各占一行。配对码在电脑窗口单独显示，可用「复制配对码」。
            2. 将这两行传到手机上并复制。在安卓「手机遥控电脑」点「设置 局域网」或「设置 Tailscale」，然后点「粘贴连接信息」。也可手动填入电脑地址（含端口 8443）和完整指纹。核对无误后点「保存并连接」。
            3. 输入电脑同一模式下的 8 位配对码，点「连接」。勾选「记住这部手机，之后直接连接」可保留该模式的配对。两种模式分别设置和配对，各用各的地址、指纹和配对码。
            """);
        AddSection(guide, heading, "在手机上切换", """
            两种模式都配对并记住后，在手机顶部点「局域网」或「Tailscale」即可切换。电脑保持开机、联网、桌面未锁定，两种服务保持运行。同一时刻只允许一个连接操作电脑。
            出门前可关闭手机 Wi-Fi，用移动数据和 Tailscale 试连一次。若手机显示「连接已配对的电脑」，点它即可重试。
            """);
        AddSection(guide, heading, "常见问题", """
            看不到 Tailscale 地址时，先在电脑登录并连接 Tailscale，再点「刷新」。
            手机连接超时时，确认电脑开机、联网且桌面未锁定，服务正在运行；窗口关闭后也可在右下角托盘查看状态。同网时检查是否为同一个可信局域网，访客 Wi-Fi 可能隔离设备。异地时检查两端 Tailscale 已连到同一网络，手机使用的是电脑的 Tailscale 地址。仍连不上时，查看随附《使用说明》中的防火墙排查。
            配对码有效期为 5 分钟。过期后在电脑点「生成新码」，再重新填写。
            如指纹不一致，先停下配对，在电脑确认当前地址和完整指纹，再修正手机上的「连接设置」。
            """);
        AddSection(guide, heading, "后台运行与退出", """
            点窗口底部「后台运行」可收起操作界面，已经启动的局域网和 Tailscale 连接会继续运行。点窗口右上角 × 时，可以选择「后台运行」「退出应用」或「取消」。
            鼠标停在 Windows 右下角托盘图标上可查看两种模式的状态。右键图标可以查看状态、打开对应模式、控制另一台电脑、打开使用指南或退出。灰点表示服务未启动，绿点表示等待设备，蓝点表示设备已连接。找不到图标时，点任务栏右下角的向上箭头展开隐藏图标。
            双击托盘图标或再次打开 PhoneRemote.exe 可以恢复操作界面。后台运行期间电脑仍需保持开机、联网且桌面未锁定；后台运行不会阻止电脑休眠。
            「退出应用」会同时关闭电脑控制窗口、结束当前遥控、停止两种服务并移除托盘图标，保留已有配对。控制窗口单独关闭只断开对另一台电脑的控制。下次打开应用后，在需要的模式中重新点「启动连接」。
            """);
        AddSection(guide, heading, "结束连接", """
            「断开设备」结束所选模式的当前会话，保留所有已记住的配对。「忘记所有设备」在确认后撤销所选模式的全部手机和电脑配对。「停止连接」仅停止所选模式。
            """);
        guide.Select(0, 0);

        var close = new Button
        {
            Text = "关闭",
            Size = new Size(92, 32),
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right
        };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 18, 20, 16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(guide, 0, 0);
        layout.Controls.Add(close, 0, 1);
        Controls.Add(layout);
        CancelButton = close;
    }

    private static void AddSection(RichTextBox guide, Font headingFont, string heading, string body)
    {
        var offset = guide.TextLength;
        guide.AppendText(heading + Environment.NewLine + body + Environment.NewLine + Environment.NewLine);
        guide.Select(offset, heading.Length);
        guide.SelectionFont = headingFont;
        guide.SelectionColor = Color.FromArgb(18, 101, 86);
    }
}
