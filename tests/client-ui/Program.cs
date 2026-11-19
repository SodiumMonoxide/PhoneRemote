using System.Drawing.Imaging;
using System.Reflection;
using RemoteControl;

internal static class UiCheck
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        try
        {
            Geometry(); QueueAndRelease().GetAwaiter().GetResult(); TextChunks();
            Forms(args.FirstOrDefault(a => !a.StartsWith('-'))); TextDialog();
            Console.WriteLine($"PASS {checks} Windows client UI checks (no OS input injection or visible windows)"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool pass, string name)
    { if (!pass) throw new Exception(name); checks++; }
    private static void Geometry()
    {
        var wide = WindowsRemoteView.FitRectangle(new Size(1000, 800), new Size(1920, 1080));
        Check(wide == new Rectangle(0, 119, 1000, 562), "widescreen letterbox");
        Check(!WindowsRemoteView.TryMap(wide, new Point(500, 50), false, out _, out _), "letterbox cannot send input");
        Check(WindowsRemoteView.TryMap(wide, wide.Location, false, out var x, out var y) && x == 0 && y == 0, "top-left maps to zero");
        Check(WindowsRemoteView.TryMap(wide, new Point(wide.Right - 1, wide.Bottom - 1), false, out x, out y) && x == 1 && y == 1, "bottom-right reaches full desktop");
        Check(WindowsRemoteView.TryMap(wide, new Point(-200, 900), true, out x, out y) && x == 0 && y == 1, "drag clamps beyond frame");
        Check(!WindowsRemoteView.TryMap(Rectangle.Empty, Point.Empty, true, out _, out _), "empty frame cannot send input");
        var tall = WindowsRemoteView.FitRectangle(new Size(1600, 900), new Size(900, 1600));
        Check(tall.Height == 900 && tall.Width == 506 && tall.X == 547, "portrait desktop fits without stretching");
        Check(WindowsClientForm.IsLocalAddress("https://127.0.0.1:8443"), "loopback protected");
        Check(!WindowsClientForm.IsLocalAddress("https://192.0.2.1:8443"), "remote address not self");
    }
    private static async Task QueueAndRelease()
    {
        var queue = new WindowsInputQueue(4);
        Check(queue.TryEnqueue(new() { Action = "down" }), "queue down");
        bool accepted = true;
        for (int i = 0; i < 1000; i++) accepted &= queue.TryEnqueue(new() { Action = "move", X = i / 999.0 });
        Check(accepted, "rapid moves coalesce");
        Check(queue.TryEnqueue(new() { Action = "up" }) && queue.TryEnqueue(new() { Action = "keyDown", VirtualKey = 65 }), "release and raw key retained");
        Check(!queue.TryEnqueue(new() { Action = "text", Text = "overflow" }), "discrete overload rejected");
        Check((await queue.TakeAsync(default)).Action == "down", "ordered down");
        var lastMove = await queue.TakeAsync(default);
        Check(lastMove.Action == "move" && lastMove.X == 1, "latest pointer retained");
        Check((await queue.TakeAsync(default)).Action == "up" && (await queue.TakeAsync(default)).VirtualKey == 65, "ordered release and raw key");
        using var stop = new CancellationTokenSource(); using var connection = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>(); CancellationToken activeToken = default;
        queue = new WindowsInputQueue(); queue.TryEnqueue(new() { Action = "keyDown" }); queue.TryEnqueue(new() { Action = "text" });
        var worker = RemoteInputFlow.PumpAsync(queue, async (input, token) =>
        { activeToken = token; log.Add(input.Action!); firstStarted.SetResult(); await finishFirst.Task; log.Add("ack"); }, stop.Token, connection.Token);
        await firstStarted.Task; stop.Cancel();
        var released = RemoteInputFlow.ReleaseAsync(Task.FromResult(true), worker, () => { log.Add("releaseAll"); return Task.CompletedTask; });
        Check(!activeToken.IsCancellationRequested && !released.IsCompleted, "pausing waits for current request without cancelling HTTP");
        finishFirst.SetResult();
        Check(await released && log.SequenceEqual(new[] { "keyDown", "ack", "releaseAll" }), "release follows last acknowledged key, pending queue discarded");
        Check(!await RemoteInputFlow.ReleaseAsync(Task.FromResult(true), Task.FromException(new IOException("uncertain key")), () => Task.CompletedTask), "uncertain key blocks resume even if release succeeds");
        Check(!await RemoteInputFlow.ReleaseAsync(Task.FromResult(true), Task.CompletedTask, () => Task.FromException(new IOException("release failed"))), "unacknowledged release blocks resume and paste");
        Check(!await RemoteInputFlow.ReleaseAsync(Task.FromResult(false), Task.CompletedTask, () => Task.CompletedTask), "failed release chain cannot become trusted again");
        using var textStop = new CancellationTokenSource();
        var finishText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        log.Clear();
        var sending = RemoteInputFlow.SendTextAsync(new[] { "first", "second" }, async (input, token) =>
        { activeToken = token; log.Add(input.Text!); await finishText.Task; }, textStop.Token, connection.Token);
        textStop.Cancel();
        Check(!activeToken.IsCancellationRequested && !sending.IsCompleted, "closing text preview waits for current chunk acknowledgement");
        finishText.SetResult();
        try { await sending; throw new Exception("text cancellation ignored"); }
        catch (OperationCanceledException) { Check(log.SequenceEqual(new[] { "first" }), "cancelled paste sends no next chunk"); }
        log.Clear();
        await RemoteInputFlow.SendTextAsync(new[] { "一", "二", "三" }, (input, _) => { log.Add(input.Text!); return Task.CompletedTask; }, default, default);
        Check(log.SequenceEqual(new[] { "一", "二", "三" }), "text chunk order is sequential");
    }
    private static void TextChunks()
    {
        string text = new string('中', 255) + "😀\r\n" + new string('x', 255) + "\r\n尾";
        var chunks = RemoteTextChunks.Split(text);
        Check(string.Concat(chunks) == text && chunks.All(c => c.Length <= 256), "text chunks preserve content and API length bound");
        Check(chunks.All(c => !char.IsHighSurrogate(c[^1]) && !char.IsLowSurrogate(c[0])), "Unicode scalar never splits between chunks");
        Check(chunks.All(c => !c.EndsWith('\r')), "CRLF remains together");
        Check(RemoteTextChunks.Split(new string('a', 8192)).Count == 32, "maximum text size supported");
        foreach (string invalid in new[] { "", new string('x', 8193), "x\0y", "\ud800" })
        {
            try { RemoteTextChunks.Split(invalid); throw new Exception("invalid paste accepted"); }
            catch (ArgumentException) { Check(true, "invalid or oversized text rejected before sending"); }
        }
    }
    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Forms(string? screenshots)
    {
        var temp = Path.Combine(Path.GetTempPath(), "PhoneRemoteUiCheck-" + Guid.NewGuid().ToString("N"));
        foreach (var size in new[] { new Size(1080, 800), new Size(750, 600) })
        {
            using var form = new WindowsClientForm(Path.Combine(temp, "profiles.dpapi")) { ClientSize = size };
            _ = form.Handle; form.PerformLayout();
            var view = Field<WindowsRemoteView>(form, "view"); var toolbar = Field<RemoteToolbar>(form, "tools");
            var setup = Field<Panel>(form, "setup"); var address = Field<TextBox>(form, "address"); var fingerprint = Field<TextBox>(form, "fingerprint");
            Check(view.Height >= 300 && view.Top == setup.Bottom, "connection setup leaves usable viewport");
            Check(address.Right <= setup.Width && address.Width > 200 && fingerprint.Width == address.Width, "address fields fit normal and minimum window widths");
            Check(address.Bottom <= fingerprint.Top, "address rows never overlap");
            Check(!view.InputEnabled && !toolbar.Expanded, "disconnected starts readonly with collapsed toolbar");
            Invoke(form, "SetCanvasMode", true); form.PerformLayout();
            Check(view.Bounds == form.ClientRectangle, "connected canvas occupies entire client area");
            Check(toolbar.Parent == view && toolbar.Top == 0 && toolbar.Left == (view.Width - toolbar.Width) / 2, "toolbar overlays viewer at top center");
            var handle = Field<Label>(toolbar, "handle"); Point start = toolbar.Location;
            Invoke(handle, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 100, 12, 0));
            Invoke(handle, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 40, 72, 0));
            Invoke(handle, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 40, 72, 0));
            Check(toolbar.Location == new Point(start.X - 60, start.Y + 60) && !toolbar.Expanded, "real drag handlers use event coordinates and do not toggle menu");
            Invoke(handle, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 100, 12, 0));
            Invoke(handle, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, -10000, -10000, 0));
            Invoke(handle, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 100, 12, 0));
            Check(toolbar.Location == Point.Empty && !toolbar.Expanded, "actual drag handler clamps before releasing capture");
            Rectangle canvas = view.Bounds; toolbar.SetExpanded(true);
            Check(toolbar.Expanded && view.Bounds == canvas, "expanded toolbar does not reserve canvas space");
            toolbar.MoveWithinParent(new Point(-999, -999)); Check(toolbar.Location == Point.Empty, "toolbar clamps top left");
            toolbar.MoveWithinParent(new Point(99999, 99999));
            Check(toolbar.Right <= view.Width && toolbar.Bottom <= view.Height, "expanded drag clamps bottom right");
            toolbar.SetExpanded(false); Check(toolbar.Height <= 48 && view.Bounds == canvas, "collapse restores slim overlay");
            Invoke(form, "SetCanvasMode", false); Rectangle original = form.Bounds;
            Invoke(form, "SetFullscreen", true); form.PerformLayout();
            Check(form.FormBorderStyle == FormBorderStyle.None && view.Bounds == form.ClientRectangle, "fullscreen removes all normal window chrome and setup rows");
            Check(form.Bounds == Screen.FromControl(form).Bounds, "fullscreen uses full monitor bounds");
            Invoke(form, "ExitControl", true);
            Check(!Field<bool>(form, "isFullscreen") && form.FormBorderStyle == FormBorderStyle.Sizable && form.WindowState == FormWindowState.Normal, "explicit exit control leaves fullscreen for normal window");
            Check(form.Bounds == original && !view.InputEnabled && !Field<bool>(form, "controlRequested"), "exit restores normal bounds and requires explicit resume");
            form.WindowState = FormWindowState.Maximized; Invoke(form, "SetFullscreen", true); Invoke(form, "ExitControl", true);
            Check(form.WindowState == FormWindowState.Normal && form.Bounds == original, "exit from maximized to fullscreen restores ordinary window bounds");
            if (screenshots is not null)
            {
                Directory.CreateDirectory(screenshots); using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(screenshots, $"windows-client-{size.Width}x{size.Height}.png"), ImageFormat.Png);
            }
        }
        Check(!Directory.Exists(temp), "launch and fullscreen preview do not create profile data");
        Check(WindowsClientForm.GuideText.Contains("Ctrl+Alt+F12") && WindowsClientForm.GuideText.Contains("普通窗口") && WindowsClientForm.GuideText.Contains("第二台电脑"), "guide explains exit shortcut and test limitation");
        using var guideBody = new TextBox { Multiline = true, Text = WindowsClientForm.GuideDisplayText };
        Check(guideBody.Lines.Length >= 15 && WindowsClientForm.GuideDisplayText.Contains("\r\n\r\n"), "native guide retains paragraph breaks");
    }
    private static void TextDialog()
    {
        int calls = 0; string received = "";
        using (var dialog = new RemoteTextDialog((chunks, _) => { calls++; received = string.Concat(chunks); return Task.CompletedTask; }))
        {
            var editor = Field<TextBox>(dialog, "editor");
            Check(calls == 0 && editor.Text.Length == 0, "text dialog neither reads clipboard nor sends on construction");
            ((Task)Invoke(dialog, "SendAsync")!).GetAwaiter().GetResult(); Check(calls == 0, "empty preview cannot send");
            editor.Text = "中文 😀\r\nexplicit preview"; ((Task)Invoke(dialog, "SendAsync")!).GetAwaiter().GetResult();
            Check(calls == 1 && received == "中文 😀\r\nexplicit preview" && editor.Text == "", "explicit send transmits exact preview once and clears it");
        }
        calls = 0;
        using (var dialog = new RemoteTextDialog((_, _) => { calls++; return Task.FromException(new IOException("uncertain")); }))
        {
            Field<TextBox>(dialog, "editor").Text = "do not duplicate";
            ((Task)Invoke(dialog, "SendAsync")!).GetAwaiter().GetResult(); ((Task)Invoke(dialog, "SendAsync")!).GetAwaiter().GetResult();
            Check(calls == 1 && !Field<Button>(dialog, "send").Enabled, "uncertain paste cannot retry implicitly");
        }
        var finish = new TaskCompletionSource(); CancellationToken stop = default;
        using (var dialog = new RemoteTextDialog((_, token) => { stop = token; return finish.Task; }))
        {
            Field<TextBox>(dialog, "editor").Text = "wait for acknowledgement";
            var sending = (Task)Invoke(dialog, "SendAsync")!;
            var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
            Invoke(dialog, "OnFormClosing", closing);
            Check(closing.Cancel && stop.IsCancellationRequested && !sending.IsCompleted, "text dialog cannot close or release input gate while its chunk is pending");
            finish.SetResult();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!sending.IsCompleted && DateTime.UtcNow < deadline) Application.DoEvents();
            Check(sending.IsCompletedSuccessfully, "pending dialog close finishes after acknowledged chunk");
        }
    }
}

namespace RemoteControl
{
    internal sealed class ControlInput
    {
        public string? Action { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public int? Delta { get; set; }
        public string? Text { get; set; }
        public string? Key { get; set; }
        public int? VirtualKey { get; set; }
        public bool Extended { get; set; }
    }
}
