using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;

namespace OpenTaskbar.Services;

/// <summary>
/// Hosts the notification area so background apps' tray icons, and their own menus, are reachable while
/// Explorer's taskbar is hidden. Built on ManagedShell (Apache-2.0, the library RetroBar uses): it owns an invisible
/// topmost <c>Shell_TrayWnd</c>, so <c>Shell_NotifyIcon</c> calls reach it, and broadcasts <c>TaskbarCreated</c> so
/// running apps re-register their icons. Messages it does not handle are forwarded to Explorer's taskbar. On
/// shutdown it broadcasts <c>TaskbarCreated</c> again so the icons return to Explorer.
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private const uint SMTOAbortIfHung = 0x0002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint SecurityMandatoryMediumRid = 0x2000;
    private const int ErrorAccessDenied = 5;

    private static readonly Lazy<uint> CurrentIntegrityRid = new(ResolveCurrentIntegrityRid);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam,
        uint flags, uint timeoutMs, out nint result);

    /// <summary>Shell-owned system icons (volume, network, power, ...); the dock has its own widgets for these.</summary>
    private static readonly HashSet<Guid> SystemIconGuids =
    [
        new(NotificationArea.VOLUME_GUID), new(NotificationArea.NETWORK_GUID), new(NotificationArea.POWER_GUID),
        new(NotificationArea.HEALTH_GUID), new(NotificationArea.MEETNOW_GUID), new(NotificationArea.LOCATION_GUID),
        new(NotificationArea.MICROPHONE_GUID)
    ];

    private static readonly AppBarMessageDelegate SharedAppBarForwarder = ForwardSharedAppBarMessage;

    private TrayService? _trayService;
    private NotificationArea? _area;

    public static TrayHost? Current { get; private set; }

    /// <summary>
    /// True while this process broadcasts <c>TaskbarCreated</c> itself (on start and shutdown). Kept for
    /// diagnostics; the dock now distinguishes its own broadcast by the correlation sequence in lParam.
    /// </summary>
    public static bool IsBroadcasting { get; private set; }

    /// <summary>wParam marker: broadcast originated from this process (must not trigger Explorer-restart handling).</summary>
    public static readonly nuint SelfBroadcastWParam = 1;
    /// <summary>wParam marker: broadcast came from the watchdog/--restore path (must be handled).</summary>
    public static readonly nuint RestoreBroadcastWParam = 2;

    private static long _lastSelfBroadcastSequence;

    /// <summary>True when the message carries the sequence of our most recent self-broadcast.</summary>
    public static bool IsOwnBroadcast(IntPtr lParam)
    {
        var sequence = lParam.ToInt64();
        return sequence != 0 && sequence == Interlocked.Read(ref _lastSelfBroadcastSequence);
    }

    public bool IsRunning => _area is { IsFailed: false };

    public ObservableCollection<NotifyIcon> Icons => _area?.TrayIcons ?? [];

    /// <summary>Starts hosting on the UI thread; a failure leaves the registry-based tray list in place.</summary>
    public static TrayHost? Start()
    {
        if (Current is not null) return Current;
        var host = new TrayHost();
        try
        {
            IsBroadcasting = true;
            host._trayService = new TrayService();
            host._area = new NotificationArea(host._trayService, new ExplorerTrayService());
            // ManagedShell's invisible topmost Shell_TrayWnd intercepts SHAppBarMessage calls after startup.
            // Re-allocate shared memory into Explorer's PID for ABM_QUERYPOS/ABM_SETPOS/ABM_GETTASKBARPOS so
            // Explorer can lock the buffer and recalculate monitor work areas live (#86, #92).
            host._area.SetAppBarMessageCallback(SharedAppBarForwarder);
            host._area.Initialize();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException or TypeInitializationException)
        {
            AppLog.Warning($"Tray host failed to start: {ex.Message}");
        }
        finally
        {
            IsBroadcasting = false;
        }

        if (!host.IsRunning)
        {
            AppLog.Warning("Tray host unavailable; using the registry tray list.");
            host.Dispose();
            return null;
        }

        AppLog.Info("Tray host started.");
        return Current = host;
    }

    /// <summary>Visible, app-owned icons in their registration order.</summary>
    public IReadOnlyList<NotifyIcon> AppIcons() =>
        Icons.Where(icon => !icon.IsHidden && !SystemIconGuids.Contains(icon.GUID) && icon.HWnd != 0).ToList();

    /// <summary>True when a process at <paramref name="currentRid"/> can send window messages to <paramref name="targetRid"/> under UIPI.</summary>
    internal static bool IsIntegrityReachable(uint currentRid, uint targetRid) => targetRid <= currentRid;

    /// <summary>
    /// Returns true when the target tray owner window is alive and not isolated above our integrity level
    /// (UIPI). Elevated/PPL processes such as Process Lasso or security suites drop un-elevated tray messages.
    /// </summary>
    internal static bool CanForwardInputToWindow(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return false;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (processHandle == 0)
            return Marshal.GetLastWin32Error() != ErrorAccessDenied;

        try
        {
            if (!TryGetProcessIntegrityRid(processHandle, out var targetRid, out var win32Error))
                return win32Error != ErrorAccessDenied;

            return IsIntegrityReachable(CurrentIntegrityRid.Value, targetRid);
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static uint ResolveCurrentIntegrityRid()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return TryGetProcessIntegrityRid(process.Handle, out var rid, out _)
            ? rid
            : SecurityMandatoryMediumRid;
    }

    private static bool TryGetProcessIntegrityRid(nint processHandle, out uint rid, out int win32Error)
    {
        rid = SecurityMandatoryMediumRid;
        win32Error = 0;
        if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            _ = GetTokenInformation(tokenHandle, TokenIntegrityLevel, nint.Zero, 0, out var requiredBytes);
            if (requiredBytes == 0 || requiredBytes > 4096)
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            var buffer = Marshal.AllocHGlobal((int)requiredBytes);
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenIntegrityLevel, buffer, requiredBytes, out _))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                var sidPtr = Marshal.ReadIntPtr(buffer);
                if (sidPtr == nint.Zero) return false;
                var subAuthCountPtr = GetSidSubAuthorityCount(sidPtr);
                if (subAuthCountPtr == nint.Zero) return false;
                var subAuthCount = Marshal.ReadByte(subAuthCountPtr);
                if (subAuthCount == 0) return false;
                var ridPtr = GetSidSubAuthority(sidPtr, (uint)(subAuthCount - 1));
                if (ridPtr == nint.Zero) return false;
                rid = unchecked((uint)Marshal.ReadInt32(ridPtr));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    /// <summary>
    /// Forwards a click to the tray icon's owner when UIPI permits it; returns false when the owner window is
    /// elevated/inaccessible so callers can show a fallback menu instead of silently failing.
    /// </summary>
    public static bool TryClick(NotifyIcon icon, MouseButton button)
    {
        if (!CanForwardInputToWindow(icon.HWnd)) return false;

        _ = GetWindowThreadProcessId(icon.HWnd, out var pid);
        if (pid != 0) _ = AllowSetForegroundWindow(pid);

        GetCursorPos(out var cursor);
        var position = (uint)((cursor.Y << 16) | (cursor.X & 0xFFFF));
        var doubleClickTime = (int)GetDoubleClickTime();
        icon.IconMouseDown(button, position, doubleClickTime);
        icon.IconMouseUp(button, position, doubleClickTime);
        return true;
    }

    /// <summary>Sends the click the native taskbar would send, so the app shows its own window or menu.</summary>
    public static void Click(NotifyIcon icon, MouseButton button) => _ = TryClick(icon, button);

    public void Dispose()
    {
        try
        {
            IsBroadcasting = true;
            _area?.Dispose();
            BroadcastTaskbarCreated();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException)
        {
            AppLog.Warning($"Tray host shutdown: {ex.Message}");
        }
        finally
        {
            IsBroadcasting = false;
        }

        _area = null;
        _trayService = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    internal static bool UsesSharedMemory(NativeMethods.ABMsg message) =>
        message is NativeMethods.ABMsg.ABM_QUERYPOS
            or NativeMethods.ABMsg.ABM_SETPOS
            or NativeMethods.ABMsg.ABM_GETTASKBARPOS
            or NativeMethods.ABMsg.ABM_GETAUTOHIDEBAREX;

    /// <summary>
    /// Forwards shared-memory SHAppBarMessage calls (ABM_QUERYPOS, ABM_SETPOS, ABM_GETTASKBARPOS,
    /// ABM_GETAUTOHIDEBAREX) to Explorer's real Shell_TrayWnd by re-allocating APPBARDATAV2 in Explorer's PID
    /// and copying Explorer's updated rectangle back to the caller's shared buffer (#86, #92).
    /// </summary>
    private static nint ForwardSharedAppBarMessage(NativeMethods.APPBARMSGDATAV3 amd, ref bool handled)
    {
        var message = (NativeMethods.ABMsg)amd.dwMessage;
        if (!UsesSharedMemory(message) || amd.hSharedMemory == 0) return nint.Zero;

        var explorerTray = NativeTaskbarWindow.Find();
        if (explorerTray == 0) return nint.Zero;
        _ = GetWindowThreadProcessId(explorerTray, out var explorerPid);
        if (explorerPid == 0) return nint.Zero;

        var sourceHandle = unchecked((nint)amd.hSharedMemory);
        var sourcePid = unchecked((uint)amd.dwSourceProcessId);
        var lockedOld = NativeMethods.SHLockShared(sourceHandle, sourcePid);
        if (lockedOld == nint.Zero)
        {
            AppLog.Warning($"TrayHost: SHLockShared failed for AppBar {message} (srcPid={sourcePid}).");
            return nint.Zero;
        }

        nint sharedNew = nint.Zero;
        nint hAmd = nint.Zero;
        nint hCopyData = nint.Zero;
        try
        {
            var abdSize = Marshal.SizeOf<NativeMethods.APPBARDATAV2>();
            sharedNew = NativeMethods.SHAllocShared(nint.Zero, (uint)abdSize, explorerPid);
            if (sharedNew == nint.Zero) return nint.Zero;

            var lockedNew = NativeMethods.SHLockShared(sharedNew, explorerPid);
            if (lockedNew == nint.Zero) return nint.Zero;
            try
            {
                var abdOld = Marshal.PtrToStructure<NativeMethods.APPBARDATAV2>(lockedOld);
                Marshal.StructureToPtr(abdOld, lockedNew, fDeleteOld: false);
            }
            finally
            {
                NativeMethods.SHUnlockShared(lockedNew);
            }

            amd.hSharedMemory = sharedNew;
            amd.dwSourceProcessId = unchecked((int)explorerPid);

            var amdSize = Marshal.SizeOf<NativeMethods.APPBARMSGDATAV3>();
            hAmd = Marshal.AllocHGlobal(amdSize);
            Marshal.StructureToPtr(amd, hAmd, fDeleteOld: false);

            var copyData = new NativeMethods.COPYDATASTRUCT
            {
                dwData = nint.Zero,
                cbData = amdSize,
                lpData = hAmd
            };
            hCopyData = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.COPYDATASTRUCT>());
            Marshal.StructureToPtr(copyData, hCopyData, fDeleteOld: false);

            // A hung Explorer must never freeze the dock's UI dispatcher indefinitely.
            var result = SendMessageTimeout(
                explorerTray,
                (uint)NativeMethods.WM.COPYDATA,
                unchecked((nint)(int)amd.abd.hWnd),
                hCopyData,
                SMTOAbortIfHung,
                1000,
                out _);

            var lockedResult = NativeMethods.SHLockShared(sharedNew, explorerPid);
            if (lockedResult != nint.Zero)
            {
                try
                {
                    var abdUpdated = Marshal.PtrToStructure<NativeMethods.APPBARDATAV2>(lockedResult);
                    Marshal.StructureToPtr(abdUpdated, lockedOld, fDeleteOld: false);
                }
                finally
                {
                    NativeMethods.SHUnlockShared(lockedResult);
                }
            }

            handled = true;
            return result;
        }
        finally
        {
            if (hCopyData != nint.Zero) Marshal.FreeHGlobal(hCopyData);
            if (hAmd != nint.Zero) Marshal.FreeHGlobal(hAmd);
            if (sharedNew != nint.Zero) NativeMethods.SHFreeShared(sharedNew, explorerPid);
            NativeMethods.SHUnlockShared(lockedOld);
        }
    }

    /// <summary>
    /// Asks every running app to register its tray icons again with whichever <c>Shell_TrayWnd</c> is current.
    /// Used after an unclean exit, when icons were left registered with this process's destroyed tray window.
    /// </summary>
    public static void BroadcastTaskbarCreated(string source = "self")
    {
        var message = RegisterWindowMessage("TaskbarCreated");
        if (message == 0)
        {
            ShellTelemetry.Result("TaskbarBroadcast", source, success: false, verified: false, ("reason", "RegisterWindowMessage failed"));
            return;
        }

        var sequence = ShellTelemetry.NextSequence();
        var isSelf = string.Equals(source, "self", StringComparison.OrdinalIgnoreCase);
        if (isSelf) Interlocked.Exchange(ref _lastSelfBroadcastSequence, sequence);
        var wParam = isSelf ? SelfBroadcastWParam : RestoreBroadcastWParam;
        ShellTelemetry.Event("TaskbarBroadcast", ("source", source), ("seq", sequence));
        // lParam carries the correlation sequence; other apps ignore it. SendNotifyMessage is asymmetric
        // (fire-and-forget) because Explorer must not be able to block our shutdown path.
        SendNotifyMessage(new nint(0xFFFF), message, wParam, new nint(sequence));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint subAuthority);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessage(nint windowHandle, uint message, nuint wParam, nint lParam);
}
