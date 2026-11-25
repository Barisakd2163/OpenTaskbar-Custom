using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenTaskbar.Services;

internal static class WindowActivationChecks
{
    public static int Run()
    {
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        nint target = 0;
        Exception? targetError = null;
        var targetThread = new Thread(() =>
        {
            try
            {
                // A hidden native window cannot steal focus or disturb the user's desktop.
                target = CreateWindowEx(0, "STATIC", "OpenTaskbar activation regression", 0,
                    -20000, -20000, 100, 100, 0, 0, 0, 0);
                if (target == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                ready.Set();
                // Deliberately do not pump messages: synchronous activation fallbacks would hang.
                release.Wait();
            }
            catch (Exception error)
            {
                targetError = error;
            }
            finally
            {
                ready.Set();
                if (target != 0) DestroyWindow(target);
            }
        }) { IsBackground = true };
        targetThread.Start();
        Thread? activationThread = null;
        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Activation target did not initialize.");
            if (targetError is not null) throw new InvalidOperationException("Activation target failed.", targetError);
            var activated = false;
            Exception? activationError = null;
            activationThread = new Thread(() =>
            {
                try { activated = NativeWindowTracker.Activate(target); }
                catch (Exception error) { activationError = error; }
            }) { IsBackground = true };
            activationThread.Start();
            if (!activationThread.Join(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("Activating an unresponsive window must not wait on its input queue.");
            if (activationError is not null) throw new InvalidOperationException("Window activation failed.", activationError);
            if (activated) throw new InvalidOperationException("A hidden window must not be reported as activated.");
            return 1;
        }
        finally
        {
            release.Set();
            targetThread.Join(TimeSpan.FromSeconds(5));
            activationThread?.Join(TimeSpan.FromSeconds(5));
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
