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
            鼠标停在 Windows 右下角托盘图标上可查看两种模式的状态。右键图标可以查看状态、打开对应模式、打开使用指南或退出。灰点表示服务未启动，绿点表示等待手机，蓝点表示手机已连接。找不到图标时，点任务栏右下角的向上箭头展开隐藏图标。
            双击托盘图标或再次打开 PhoneRemote.exe 可以恢复操作界面。后台运行期间电脑仍需保持开机、联网且桌面未锁定；后台运行不会阻止电脑休眠。
            「退出应用」会结束当前遥控、停止两种服务并移除托盘图标，保留已有配对。下次打开应用后，在需要的模式中重新点「启动连接」。
            """);
        AddSection(guide, heading, "结束连接", """
            「断开手机」结束所选模式的当前会话，保留已记住的配对。「忘记手机」撤销所选模式的配对。「停止连接」仅停止所选模式。
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
