using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

internal sealed class CloseOptionsForm : Form
{
    public CloseOptionsForm()
    {
        Text = "关闭操作界面";
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = MinimizeBox = false;
        BackColor = Color.White;
        ClientSize = new Size(480, 202);

        var title = new Label
        {
            Text = "是否继续在后台运行？",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(22, 20, 22, 10)
        };
        var message = new Label
        {
            Text = "后台运行：已启动的连接保持运行，右下角托盘显示状态。\n退出应用：停止两种服务，结束手机遥控，保留已有配对。",
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 4, 22, 8),
            AutoSize = false
        };
        var background = MakeChoice("后台运行", DialogResult.Yes);
        var exit = MakeChoice("退出应用", DialogResult.No);
        var cancel = MakeChoice("取消", DialogResult.Cancel);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            Padding = new Padding(16, 8, 16, 16),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.AddRange([cancel, exit, background]);
        Controls.Add(message);
        Controls.Add(title);
        Controls.Add(actions);
        AcceptButton = background;
        CancelButton = cancel;
    }

    private static Button MakeChoice(string title, DialogResult result) => new()
    {
        Text = title,
        DialogResult = result,
        Size = new Size(112, 34),
        Margin = new Padding(6, 0, 0, 0),
        UseVisualStyleBackColor = true
    };
}
