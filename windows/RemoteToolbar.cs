namespace RemoteControl;

// A child of the remote canvas: never topmost outside this application.
internal sealed class RemoteToolbar : Panel
{
    private readonly Label handle = new() { Text = "⋮⋮  遥控工具  ▾", TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Top, Height = 30, Cursor = Cursors.SizeAll, AccessibleName = "拖动或展开遥控工具栏" };
    private readonly FlowLayoutPanel menu = new() { FlowDirection = FlowDirection.TopDown,
        WrapContents = false, Dock = DockStyle.Fill, Padding = new Padding(8), Visible = false };
    private readonly Label state = new() { AutoSize = false, Height = 44, Width = 244 };
    private readonly Button control = MakeButton("退出键鼠控制");
    private readonly Button text = MakeButton("准备粘贴本机文本");
    private readonly Button full = MakeButton("全屏被控画面");
    private readonly Button disconnect = MakeButton("断开连接");
    private readonly System.Windows.Forms.Timer collapseTimer = new() { Interval = 900 };
    private bool expanded, dragging, moved, placed;
    private Point dragOrigin, windowOrigin;

    internal event Action? ToggleControlRequested, PasteRequested, FullscreenRequested, DisconnectRequested,
        SettingsRequested, GuideRequested, Opening, InteractionEnded;
    internal bool Expanded => expanded;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Editing { get; set; }

    internal RemoteToolbar()
    {
        BackColor = Color.FromArgb(238, 245, 246);
        ForeColor = Color.FromArgb(27, 43, 60);
        BorderStyle = BorderStyle.FixedSingle;
        AccessibleName = "可拖动遥控工具栏";
        Size = new Size(270, 32);
        menu.Controls.Add(state);
        menu.Controls.AddRange([control, text, full, disconnect]);
        var settings = MakeButton("连接设置");
        var guide = MakeButton("使用指南");
        menu.Controls.AddRange([settings, guide]);
        Controls.Add(menu); Controls.Add(handle);
        control.Click += (_, _) => ToggleControlRequested?.Invoke();
        text.Click += (_, _) => PasteRequested?.Invoke();
        full.Click += (_, _) => FullscreenRequested?.Invoke();
        disconnect.Click += (_, _) => DisconnectRequested?.Invoke();
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        guide.Click += (_, _) => GuideRequested?.Invoke();
        handle.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            Opening?.Invoke();
            dragging = true; moved = false;
            dragOrigin = handle.PointToScreen(e.Location); windowOrigin = Location;
            handle.Capture = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var current = handle.PointToScreen(e.Location);
            Point delta = new(current.X - dragOrigin.X, current.Y - dragOrigin.Y);
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 4) moved = true;
            if (moved) MoveWithinParent(new Point(windowOrigin.X + delta.X, windowOrigin.Y + delta.Y));
        };
        handle.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false; handle.Capture = false;
            if (!moved) SetExpanded(!expanded);
            else if (!expanded) InteractionEnded?.Invoke();
        };
        handle.MouseCaptureChanged += (_, _) => { if (!handle.Capture) dragging = false; };
        handle.MouseEnter += (_, _) => collapseTimer.Stop();
        foreach (Control child in menu.Controls)
        {
            child.MouseEnter += (_, _) => collapseTimer.Stop();
            child.MouseLeave += (_, _) => collapseTimer.Start();
        }
        MouseLeave += (_, _) => collapseTimer.Start();
        handle.MouseLeave += (_, _) => collapseTimer.Start();
        collapseTimer.Tick += (_, _) =>
        {
            if (!Editing && !dragging && !ContainsFocus && !ClientRectangle.Contains(PointToClient(Cursor.Position)))
                SetExpanded(false);
        };
    }

    private static Button MakeButton(string title) => new() { Text = title, Width = 244, Height = 34, Margin = new Padding(0, 2, 0, 2) };

    internal void UpdateState(string message, bool controlling, bool connected, bool readOnly, bool fullscreen, bool busy)
    {
        state.Text = message;
        handle.Text = controlling ? "⋮⋮  控制中 · 工具 ▾" : "⋮⋮  遥控工具 ▾";
        control.Text = controlling ? "退出键鼠控制" : "恢复键鼠控制";
        control.Enabled = text.Enabled = connected && !readOnly && !busy;
        full.Text = fullscreen ? "退出全屏" : "全屏被控画面";
        disconnect.Enabled = connected && !busy;
    }

    internal void SetExpanded(bool value)
    {
        if (value == expanded) return;
        if (value) Opening?.Invoke();
        expanded = value;
        menu.Visible = value;
        Height = value ? Scale(324) : Scale(32);
        ClampToParent();
        BringToFront();
        if (!value) { collapseTimer.Stop(); InteractionEnded?.Invoke(); }
    }

    internal void PlaceInitially()
    {
        if (Parent is null) return;
        if (!placed)
        {
            Width = Math.Min(Scale(270), Parent.ClientSize.Width);
            Location = new Point(Math.Max(0, (Parent.ClientSize.Width - Width) / 2), 0);
        }
        ClampToParent();
    }

    internal static Point ClampLocation(Point desired, Size toolbar, Size area) => new(
        Math.Clamp(desired.X, 0, Math.Max(0, area.Width - toolbar.Width)),
        Math.Clamp(desired.Y, 0, Math.Max(0, area.Height - toolbar.Height)));

    internal void MoveWithinParent(Point desired)
    {
        if (Parent is null) return;
        placed = true;
        Location = ClampLocation(desired, Size, Parent.ClientSize);
    }

    internal void ClampToParent()
    {
        if (Parent is not null) Location = ClampLocation(Location, Size, Parent.ClientSize);
    }
    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    protected override void Dispose(bool disposing)
    {
        if (disposing) collapseTimer.Dispose();
        base.Dispose(disposing);
    }
}
