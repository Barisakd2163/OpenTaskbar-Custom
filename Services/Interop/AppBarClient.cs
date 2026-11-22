using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Single choke point for every <c>SHAppBarMessage</c> call made by the replacement session.
/// <para>
/// <c>SHAppBarMessage</c> has no timeout parameter and blocks while Explorer processes the request. Before each
/// call we probe Explorer's validated <c>Shell_TrayWnd</c> with <see cref="SendMessageTimeout"/>, so a hung or
/// restarting Explorer is detected instead of freezing the UI dispatcher for the shell's full ~4 s timeout.
/// Sent-message reentrancy (ABN_POSCHANGED arrives while a call is still on the stack) is broken with
/// <see cref="NotificationsSuspended"/>: handlers defer to a bounded reconcile instead of nesting.
/// </para>
/// Position changes always run QUERYPOS -> (caller pins the edge) -> SETPOS, and the shell-resolved rectangle is
/// returned so the caller can move its window only after the reservation has been committed.
/// </summary>
internal static class AppBarClient
{
    internal const uint AbmNew = 0x00000000;
    internal const uint AbmRemove = 0x00000001;
    internal const uint AbmQueryPos = 0x00000002;
    internal const uint AbmSetPos = 0x00000003;
    internal const uint AbmGetState = 0x00000004;
    internal const uint AbmGetTaskbarPos = 0x00000005;
    internal const uint AbmSetState = 0x0000000A;
    internal const uint AbeBottom = 3;
    internal const uint AbsAutoHide = 0x00000001;
    internal const uint AbsAlwaysOnTop = 0x00000002;

    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ExplorerProbeTimeoutMs = 500;

    private static int _notificationGate;

    /// <summary>True while an AppBar call is in flight; shell callbacks must be deferred, not handled inline.</summary>
    internal static bool NotificationsSuspended => Volatile.Read(ref _notificationGate) != 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public TaskbarReplacementSession.Rect rc;
        public nint lParam;
    }

    /// <summary>Pins a QUERYPOS result to the requested edge before SETPOS commits it.</summary>
    internal delegate void PositionAdjustment(ref TaskbarReplacementSession.Rect rect);

    internal static AppBarData CreateData(nint windowHandle) => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = windowHandle
    };

    internal static uint GetState(string reason = "GetState")
    {
        var data = CreateData(0);
        return unchecked((uint)Send(AbmGetState, ref data, reason));
    }

    internal static nuint SetState(ref AppBarData data, string reason = "SetState") =>
        Send(AbmSetState, ref data, reason);

    /// <summary>
    /// Runs the official two-step reservation protocol and returns the rectangle the shell actually assigned.
    /// The caller moves its window only after this method returns, so a synchronous ABN_POSCHANGED can never
    /// interleave with a half-committed reservation.
    /// </summary>
    internal static bool SetPosition(nint windowHandle, uint edge, TaskbarReplacementSession.Rect requested,
        PositionAdjustment? adjustAfterQuery, string reason, out TaskbarReplacementSession.Rect resolved)
    {
        var data = CreateData(windowHandle);
        data.uEdge = edge;
        data.rc = requested;
        var query = Send(AbmQueryPos, ref data, reason + "/QUERYPOS");
        if (query == 0)
        {
            ShellTelemetry.Event("AppBarQueryPosFailed", ("reason", reason), ("edge", edge));
        }

        adjustAfterQuery?.Invoke(ref data.rc);
        var committed = Send(AbmSetPos, ref data, reason + "/SETPOS");
        resolved = data.rc;
        return committed != 0;
    }

    internal static nuint Register(nint windowHandle, uint callbackMessage, string reason = "Register")
    {
        var data = CreateData(windowHandle);
        data.uCallbackMessage = callbackMessage;
        return Send(AbmNew, ref data, reason);
    }

    internal static nuint Unregister(nint windowHandle, string reason = "Unregister")
    {
        var data = CreateData(windowHandle);
        return Send(AbmRemove, ref data, reason);
    }

    /// <summary>Sends an AppBar message with a hung-Explorer probe, bounded telemetry and reentrancy suppression.</summary>
    internal static nuint Send(uint message, ref AppBarData data, string reason)
    {
        if (!NativeTaskbarWindow.TryAcquire(out var taskbar))
        {
            ShellTelemetry.Event("AppBarSkipped", ("reason", reason), ("message", message), ("cause", "no-taskbar"));
            return 0;
        }

        var probe = SendMessageTimeout(taskbar.Handle, WmNull, 0, 0, SmtoAbortIfHung, ExplorerProbeTimeoutMs, out _);
        if (probe == 0)
        {
            ShellTelemetry.Event("AppBarSkipped", ("reason", reason), ("message", message),
                ("cause", "explorer-hung"), ("pid", taskbar.ProcessId));
            return 0;
        }

        var sequence = ShellTelemetry.NextSequence();
        nuint result;
        using (SuspendNotifications())
        {
            result = SHAppBarMessage(message, ref data);
        }

        ShellTelemetry.Event("AppBarMessage", ("seq", sequence), ("reason", reason), ("message", message),
            ("result", (long)result), ("pid", taskbar.ProcessId));
        return result;
    }

    /// <summary>
    /// Suppresses inline ABN_* handling for the lifetime of an AppBar call. Windows dispatches sent messages
    /// while a thread waits for a SendMessage reply, so a callback can reenter our window procedure before
    /// SHAppBarMessage returns; deferring it here breaks the POSCHANGED/STATECHANGE cycle.
    /// </summary>
    private static NotificationScope SuspendNotifications() => new();

    private readonly struct NotificationScope : IDisposable
    {
        public NotificationScope() => Interlocked.Increment(ref _notificationGate);

        public void Dispose() => Interlocked.Decrement(ref _notificationGate);
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(nint windowHandle, uint message, nint wParam, nint lParam,
        uint flags, uint timeoutMs, out nint result);
}
