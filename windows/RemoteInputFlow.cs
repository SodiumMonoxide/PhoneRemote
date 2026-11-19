namespace RemoteControl;

// Stopping the queue must not abandon an HTTP request that the host may still apply.
internal static class RemoteInputFlow
{
    internal static async Task PumpAsync(WindowsInputQueue queue, Func<ControlInput, CancellationToken, Task> send,
        CancellationToken stopQueue, CancellationToken connection)
    {
        while (!stopQueue.IsCancellationRequested)
        {
            ControlInput input;
            try { input = await queue.TakeAsync(stopQueue); }
            catch (OperationCanceledException) when (stopQueue.IsCancellationRequested) { return; }
            if (stopQueue.IsCancellationRequested) return;
            await send(input, connection);
        }
    }

    internal static async Task<bool> ReleaseAsync(Task<bool> previous, Task? worker, Func<Task> release)
    {
        bool acknowledged = await previous;
        try { if (worker is not null) await worker; }
        catch { acknowledged = false; }
        try { await release(); }
        catch { acknowledged = false; }
        return acknowledged;
    }

    internal static async Task SendTextAsync(IReadOnlyList<string> chunks, Func<ControlInput, CancellationToken, Task> send,
        CancellationToken stopAfterCurrent, CancellationToken connection)
    {
        foreach (string chunk in chunks)
        {
            stopAfterCurrent.ThrowIfCancellationRequested();
            await send(new ControlInput { Action = "text", Text = chunk }, connection);
        }
    }
}
