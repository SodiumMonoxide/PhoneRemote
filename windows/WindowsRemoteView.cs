using System.Drawing.Drawing2D;

namespace RemoteControl;

internal sealed class WindowsRemoteView : Control
{
    private Image? frame;
    public event Action<ControlInput>? RemoteInput;
    public event Action? ExitControlRequested;
    public event Action<string>? KeyboardCaptureFailed;
    public event Action? CaptureLost;
    private readonly ScopedKeyboardCapture keyboard;
    private bool inputEnabled;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<Keys, bool>? LocalCommand { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool InputEnabled
    {
        get => inputEnabled;
        set
        {
            if (inputEnabled == value) return;
            if (!value) { ReleaseAllInput(); return; }
            inputEnabled = true;
            TryStartKeyboardCapture();
        }
    }
    private bool dragging;
    private bool pressed;
    private Point pressLocation;
    private Point lastPointer;
    private Point? pendingClick;
    private int wheelRemainder;
    private readonly System.Windows.Forms.Timer clickTimer = new();
    private readonly System.Windows.Forms.Timer dragTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer heartbeatTimer = new() { Interval = 500 };
    private long lastMove;

    public WindowsRemoteView()
    {
        keyboard = new ScopedKeyboardCapture(CanCaptureKeyboard, input =>
        {
            if (input.Action is "keyDown" or "keyUp") FlushClick();
            RemoteInput?.Invoke(input);
        }, HandleLocalCommand, message =>
        {
            ReleaseAllInput();
            PostToView(() => KeyboardCaptureFailed?.Invoke(message));
        }, () =>
        {
            ReleaseAllInput();
            PostToView(() => CaptureLost?.Invoke());
        });
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable |
            ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        clickTimer.Interval = SystemInformation.DoubleClickTime;
        clickTimer.Tick += (_, _) => FlushClick();
        dragTimer.Tick += (_, _) => { if (dragging) Pointer("move", lastPointer, true); };
        heartbeatTimer.Tick += (_, _) =>
        {
            if (!CanCaptureKeyboard())
            {
                ReleaseAllInput();
                CaptureLost?.Invoke();
                return;
            }
            RemoteInput?.Invoke(new ControlInput { Action = "heartbeat" });
        };
        BackColor = Color.FromArgb(20, 29, 39);
        ForeColor = Color.White;
        TabStop = true;
        AccessibleName = "远程电脑画面";
        AccessibleDescription = "键鼠控制时直接操作远程桌面；Ctrl+Alt+F12 退出控制，Ctrl+Alt+Enter 切换全屏。";
        ImeMode = ImeMode.Disable;
    }

    private bool CanCaptureKeyboard() => inputEnabled && frame is not null && Focused && Visible && IsHandleCreated &&
        FindForm() is { Visible: true, WindowState: not FormWindowState.Minimized } owner &&
        ScopedKeyboardCapture.IsForeground(owner.Handle);

    private void TryStartKeyboardCapture()
    {
        if (!CanCaptureKeyboard()) return;
        if (keyboard.Start()) heartbeatTimer.Start();
    }

    private void PostToView(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    private void HandleLocalCommand(Keys key)
    {
        if (key == (Keys.Control | Keys.Alt | Keys.F12))
        {
            ReleaseAllInput();
            PostToView(() => ExitControlRequested?.Invoke());
        }
        else PostToView(() => LocalCommand?.Invoke(key));
    }

    public void ReleaseAllInput()
    {
        bool wasEnabled = inputEnabled;
        inputEnabled = false;
        heartbeatTimer.Stop();
        ReleasePointer();
        bool wasRunning = keyboard.Running;
        keyboard.Stop();
        if (wasEnabled && !wasRunning) RemoteInput?.Invoke(new ControlInput { Action = "releaseAll" });
    }

    public void SetFrame(Image image)
    {
        var old = frame;
        frame = image;
        old?.Dispose();
        TryStartKeyboardCapture();
        Invalidate();
    }

    public void ClearFrame()
    {
        ReleaseAllInput();
        clickTimer.Stop(); pendingClick = null; wheelRemainder = 0;
        frame?.Dispose();
        frame = null;
        Invalidate();
    }

    internal static Rectangle FitRectangle(Size viewport, Size image)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || image.Width <= 0 || image.Height <= 0)
            return Rectangle.Empty;
        double scale = Math.Min((double)viewport.Width / image.Width, (double)viewport.Height / image.Height);
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return new Rectangle((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
    }

    internal static bool TryMap(Rectangle rectangle, Point point, bool clamp, out double x, out double y)
    {
        x = y = 0;
        if (rectangle.Width <= 0 || rectangle.Height <= 0 || (!clamp && !rectangle.Contains(point)))
            return false;
        x = Math.Clamp((double)(point.X - rectangle.Left) / Math.Max(1, rectangle.Width - 1), 0, 1);
        y = Math.Clamp((double)(point.Y - rectangle.Top) / Math.Max(1, rectangle.Height - 1), 0, 1);
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (frame is not null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            e.Graphics.DrawImage(frame, FitRectangle(ClientSize, frame.Size));
        }
        else
            TextRenderer.DrawText(e.Graphics, "连接后在这里显示远程电脑画面", Font, ClientRectangle,
                Color.FromArgb(190, 204, 216), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), Color.White, BackColor);
    }

    private bool Pointer(string action, Point point, bool clamp = false, int? delta = null)
    {
        if (!InputEnabled || frame is null || !TryMap(FitRectangle(ClientSize, frame.Size), point, clamp, out var x, out var y))
            return false;
        RemoteInput?.Invoke(new ControlInput { Action = action, X = x, Y = y, Delta = delta });
        return true;
    }

    public void ReleasePointer()
    {
        FlushClick();
        pressed = false;
        dragTimer.Stop();
        bool release = dragging;
        dragging = false;
        // An unconditional release remains valid if resizing moved the pointer outside the image.
        if (release) RemoteInput?.Invoke(new ControlInput { Action = "up" });
        Capture = false;
    }

    private void FlushClick()
    {
        clickTimer.Stop();
        var point = pendingClick;
        pendingClick = null;
        if (point is { } location) Pointer("click", location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        lastPointer = e.Location;
        if (e.Button == MouseButtons.Left && InputEnabled && frame is not null &&
            TryMap(FitRectangle(ClientSize, frame.Size), e.Location, false, out _, out _))
        {
            if (e.Clicks >= 2) { clickTimer.Stop(); pendingClick = null; }
            else FlushClick();
            pressed = true;
            pressLocation = e.Location;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        lastPointer = e.Location;
        var dragArea = new Rectangle(pressLocation.X - SystemInformation.DragSize.Width / 2,
            pressLocation.Y - SystemInformation.DragSize.Height / 2,
            SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
        if (pressed && !dragging && !dragArea.Contains(e.Location) && Pointer("down", pressLocation))
        {
            dragging = true;
            dragTimer.Start();
        }
        if (!Focused || Environment.TickCount64 - lastMove < 40) return;
        lastMove = Environment.TickCount64;
        Pointer("move", e.Location, dragging);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        lastPointer = e.Location;
        if (e.Button == MouseButtons.Left && pressed)
        {
            if (dragging)
            {
                Pointer("move", e.Location, true);
                ReleasePointer();
            }
            else
            {
                pressed = false;
                Capture = false;
                pendingClick = e.Location;
                clickTimer.Start();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            FlushClick();
            Pointer("rightClick", e.Location);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || dragging) return;
        clickTimer.Stop(); pendingClick = null;
        pressed = false; Capture = false;
        Pointer("doubleClick", e.Location);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Focused || !InputEnabled || e.Delta == 0) return;
        FlushClick();
        wheelRemainder += e.Delta;
        int steps = wheelRemainder / 120;
        wheelRemainder %= 120;
        if (steps != 0) Pointer("scroll", e.Location, false, Math.Clamp(steps, -8, 8));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && (pressed || dragging)) ReleasePointer();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        ReleaseAllInput();
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        TryStartKeyboardCapture();
        Invalidate();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseAllInput();
        base.OnHandleDestroyed(e);
    }

    internal static string? MapKey(Keys keyData) => keyData switch
    {
        Keys.Enter => "Enter", Keys.Escape => "Escape", Keys.Back => "Backspace",
        Keys.Delete => "Delete", Keys.Up => "ArrowUp", Keys.Down => "ArrowDown",
        Keys.Left => "ArrowLeft", Keys.Right => "ArrowRight", Keys.Home => "Home", Keys.End => "End",
        Keys.PageUp => "PageUp", Keys.PageDown => "PageDown", Keys.F5 => "F5",
        Keys.Control | Keys.A => "Ctrl+A", Keys.Control | Keys.C => "Ctrl+C",
        Keys.Control | Keys.V => "Ctrl+V", Keys.Control | Keys.X => "Ctrl+X", Keys.Control | Keys.Z => "Ctrl+Z",
        _ => null
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (InputEnabled && keyData == (Keys.Control | Keys.Alt | Keys.F12))
        {
            HandleLocalCommand(keyData);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Alt | Keys.Enter) && LocalCommand?.Invoke(keyData) == true) return true;
        // Real physical keys are consumed by the scoped hook. Do not forward injected
        // or duplicate WinForms messages a second time, or navigate local controls.
        if (InputEnabled) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (InputEnabled) e.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseAllInput();
            keyboard.Dispose();
            heartbeatTimer.Dispose();
            clickTimer.Dispose(); dragTimer.Dispose();
            frame?.Dispose(); frame = null;
        }
        base.Dispose(disposing);
    }
}

// Consecutive pointer moves may collapse; clicks, keys and releases always retain ordering.
internal sealed class WindowsInputQueue
{
    private readonly LinkedList<ControlInput> pending = new();
    private readonly SemaphoreSlim ready = new(0);
    private readonly int capacity;
    internal WindowsInputQueue(int capacity = 64) => this.capacity = capacity;
    internal bool TryEnqueue(ControlInput input)
    {
        lock (pending)
        {
            if (input.Action == "move" && pending.Last?.Value.Action == "move")
            {
                pending.Last.Value = input;
                return true;
            }
            if (pending.Count >= capacity) return false;
            pending.AddLast(input);
            ready.Release();
            return true;
        }
    }
    internal async Task<ControlInput> TakeAsync(CancellationToken token)
    {
        await ready.WaitAsync(token);
        lock (pending)
        {
            var input = pending.First!.Value;
            pending.RemoveFirst();
            return input;
        }
    }
}
