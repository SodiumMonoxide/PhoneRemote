namespace RemoteControl;

internal static class RemoteTextChunks
{
    internal const int MaximumLength = 8192;
    internal static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumLength)
            throw new ArgumentException($"请填写 1–{MaximumLength} 个字符，较长内容请分次发送。");
        for (int i = 0; i < text.Length;)
        {
            if (!System.Text.Rune.TryGetRuneAt(text, i, out var rune) ||
                (System.Text.Rune.IsControl(rune) && rune.Value is not ('\r' or '\n' or '\t')))
                throw new ArgumentException("文本包含不支持的控制字符或不完整字符，请编辑后重试。");
            i += rune.Utf16SequenceLength;
        }
        var result = new List<string>();
        for (int start = 0; start < text.Length;)
        {
            int count = Math.Min(256, text.Length - start);
            if (start + count < text.Length && (char.IsHighSurrogate(text[start + count - 1]) ||
                (text[start + count - 1] == '\r' && text[start + count] == '\n'))) count--;
            result.Add(text.Substring(start, count)); start += count;
        }
        return result;
    }
}

internal sealed class RemoteTextDialog : Form
{
    private readonly TextBox editor = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true, AcceptsTab = true, MaxLength = RemoteTextChunks.MaximumLength, AccessibleName = "待发送的本机文本" };
    private readonly Label status = new() { AutoSize = false, Height = 58, Dock = DockStyle.Top };
    private readonly Button send = new() { Text = "发送到被控电脑", AutoSize = true, Height = 34 };
    private readonly Button read = new() { Text = "重新读取本机剪贴板", AutoSize = true, Height = 34 };
    private readonly Button cancel = new() { Text = "关闭", AutoSize = true, Height = 34 };
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> transmit;
    private CancellationTokenSource? sending;
    private bool uncertain, closeWhenFinished;

    internal RemoteTextDialog(Func<IReadOnlyList<string>, CancellationToken, Task> transmit)
    {
        this.transmit = transmit;
        Text = "准备粘贴本机文本";
        Font = new Font("Microsoft YaHei UI", 10F);
        ClientSize = new Size(640, 480); MinimumSize = new Size(450, 350);
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; Padding = new Padding(14);
        status.Text = "已退出键鼠控制。先在本机复制文本，再点「重新读取本机剪贴板」。核对后才会发送；关闭不会发送。";
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Height = 42, WrapContents = true };
        actions.Controls.AddRange([read, send, cancel]);
        Controls.Add(editor); Controls.Add(actions); Controls.Add(status);
        read.Click += (_, _) => ReadClipboard();
        send.Click += async (_, _) => await SendAsync();
        cancel.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (sending is null) return;
            closeWhenFinished = true; sending.Cancel(); e.Cancel = true;
            status.Text = "正在结束当前文本片段，确认后关闭；不会发送后续片段。";
        };
    }

    // Called only after the user's toolbar action or the explicit reread button.
    internal void ReadClipboard()
    {
        try
        {
            string text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            text = text.ReplaceLineEndings(Environment.NewLine);
            if (text.Length > RemoteTextChunks.MaximumLength)
            {
                status.Text = "剪贴板文本超过 8192 个字符，请在本机分段复制。未导入或发送该内容。";
                return;
            }
            editor.Text = text;
            uncertain = false; send.Enabled = true;
            status.Text = text.Length == 0 ? "剪贴板没有文本。请先在本机复制，或在下方输入文本。" : "已读取本机文本，请核对内容及远程光标位置，再点「发送到被控电脑」。";
        }
        catch (System.Runtime.InteropServices.ExternalException) { status.Text = "本机剪贴板正忙，请稍后点「重新读取本机剪贴板」。"; }
    }

    private async Task SendAsync()
    {
        if (sending is not null || uncertain) return;
        IReadOnlyList<string> chunks;
        try { chunks = RemoteTextChunks.Split(editor.Text); }
        catch (ArgumentException error) { status.Text = error.Message; return; }
        sending = new CancellationTokenSource();
        var lifetime = sending;
        editor.ReadOnly = true; send.Enabled = read.Enabled = false;
        status.Text = "正在发送，请勿切换远程输入位置…";
        try
        {
            await transmit(chunks, lifetime.Token);
            if (!IsDisposed) { status.Text = "已发送。键鼠仍处于退出状态，返回工具栏可恢复控制。"; editor.Clear(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            uncertain = true;
            if (!IsDisposed) status.Text = "已停止后续片段。请先检查被控电脑已有的文本，避免重复。";
        }
        catch (Exception error)
        {
            uncertain = true;
            if (!IsDisposed) status.Text = "发送中断，可能已有部分内容到达；请先检查远程内容，避免重复。" + error.Message;
        }
        finally
        {
            sending = null; lifetime.Dispose();
            if (!IsDisposed)
            {
                editor.ReadOnly = false; read.Enabled = true; send.Enabled = !uncertain;
                if (closeWhenFinished) Close();
            }
        }
    }
}
