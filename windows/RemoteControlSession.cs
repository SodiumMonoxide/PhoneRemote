using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace RemoteControl;

internal sealed class RemoteControlSession
{
    private static readonly TimeSpan SessionIdle = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DragTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(150);

    private readonly object frames = new();
    private readonly Desktop desktop = new();
    private ActiveSession? active;
    private long? dragActivity;
    private bool releasePending;
    private long lastCapture;
    private byte[]? cachedFrame;

    internal object StateLock { get; } = new();

    // Each listener holds StateLock while reading or changing the shared controller.
    internal bool IsConnectedLocked(RemoteServer endpoint) => active?.Endpoint == endpoint;

    internal bool CanOpenLocked(RemoteServer endpoint, bool switchMode) =>
        active is null || active.Endpoint == endpoint || switchMode;

    internal void OpenLocked(RemoteServer endpoint, string token, IPAddress peer)
    {
        if (active is not null)
            ReleaseDragLocked();
        active = new ActiveSession(endpoint, token, peer);
    }

    internal void DisconnectLocked(RemoteServer endpoint)
    {
        if (!IsConnectedLocked(endpoint))
            return;
        active = null;
        ReleaseDragLocked();
    }

    internal bool AuthorizeLocked(RemoteServer endpoint, string? token, IPAddress peer)
    {
        if (active is not { } current || current.Endpoint != endpoint || token is null ||
            !current.Peer.Equals(peer) || !FixedEquals(token, current.Token))
            return false;
        current.LastActivity = Stopwatch.GetTimestamp();
        return true;
    }

    internal void MaintainLocked()
    {
        if (active is { } current && Stopwatch.GetElapsedTime(current.LastActivity) >= SessionIdle)
            DisconnectLocked(current.Endpoint);
        if (releasePending || (dragActivity is long started && Stopwatch.GetElapsedTime(started) >= DragTimeout))
            ReleaseDragLocked();
    }

    internal int ApplyLocked(ControlInput? command)
    {
        if (releasePending)
            return StatusCodes.Status503ServiceUnavailable;
        try
        {
            if (command is null || !desktop.Apply(command))
                return StatusCodes.Status400BadRequest;
            if (command.Action == "down" || (command.Action == "move" && dragActivity is not null))
                dragActivity = Stopwatch.GetTimestamp();
            if (command.Action == "up")
                dragActivity = null;
            return StatusCodes.Status204NoContent;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ReleaseDragLocked();
            return StatusCodes.Status503ServiceUnavailable;
        }
    }

    internal byte[]? Capture()
    {
        lock (frames)
        {
            if (lastCapture != 0 && Stopwatch.GetElapsedTime(lastCapture) < FrameInterval)
                return cachedFrame;
            try
            {
                cachedFrame = desktop.CaptureFrame();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or
                                          System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                cachedFrame = null;
            }
            lastCapture = Stopwatch.GetTimestamp();
            return cachedFrame;
        }
    }

    internal static bool FixedEquals(string first, string second) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));

    private void ReleaseDragLocked()
    {
        releasePending = !desktop.ReleaseButtons();
        dragActivity = null;
    }

    private sealed record ActiveSession(RemoteServer Endpoint, string Token, IPAddress Peer)
    {
        internal long LastActivity { get; set; } = Stopwatch.GetTimestamp();
    }
}
