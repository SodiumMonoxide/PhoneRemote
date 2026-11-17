namespace RemoteControl;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var showRequest = new System.Threading.EventWaitHandle(false,
            System.Threading.EventResetMode.AutoReset, "Local\\PhoneRemote.ShowHost");
        using var instance = new System.Threading.Mutex(true, "Local\\PhoneRemote.Host", out bool first);
        if (!first)
        {
            showRequest.Set();
            return;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new HostForm();
        _ = form.Handle;
        var registration = System.Threading.ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) =>
        {
            try
            {
                if (!form.IsDisposed && form.IsHandleCreated)
                    form.BeginInvoke(new Action(form.RestoreWindow));
            }
            catch (InvalidOperationException)
            {
                // The process can receive a final request while its window exits.
            }
        }, null, System.Threading.Timeout.Infinite, false);
        try
        {
            Application.Run(form);
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
