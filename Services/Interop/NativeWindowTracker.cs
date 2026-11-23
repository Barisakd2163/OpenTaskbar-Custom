using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class NativeWindowTracker : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectNameChange = 0x800C;
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;
    private const uint EventSystemAlert = 0x0002;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint WinEventFlags = WineventOutOfContext | WineventSkipOwnProcess;
    private const int ObjectWindow = 0;
    private const int ChildIdSelf = 0;
    private const int WindowOwner = 4;
    private const int GwlExStyle = -20;
    private const uint GaRootOwner = 3;
    private const uint WmClose = 0x0010;
    private const int ShowRestore = 9;
    private const int ShowMinimize = 6;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int DwmwaCloaked = 14;
    private static readonly string WindowsSystemAppsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps") + Path.DirectorySeparatorChar;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _locationTimer;
    private readonly WinEventDelegate _eventCallback;
    // HWND + PID: a recycled handle must not inherit a previous window's notification badge.
    private readonly Dictionary<nint, uint> _notificationWindows = [];

    private static uint PidOf(nint windowHandle)
    {
        GetWindowThreadProcessId(windowHandle, out var pid);
        return pid;
    }
    // Only suppresses redundant name-change events from helper windows. Eligibility is always re-evaluated
    // during enumeration: a cached "ignore" entry would otherwise survive style changes and HWND reuse.
    private readonly HashSet<nint> _quietWindows = [];
    private readonly HashSet<nint> _snapshotHandles = [];
    private readonly Dictionary<nint, TrackedWindow> _previousByHandle = [];
    private readonly Dictionary<nint, uint> _previousPidByHandle = [];
    private nint _windowHook;
    private nint _locationHook;
    private nint _nameChangeHook;
    private nint _cloakHook;
    private nint _systemEventHook;
    private nint _excludedWindow;
    private nint _lastForegroundWindow;
    private nint _pendingLocationForeground;
    private bool _foregroundPublished;
    private int _refreshQueued;
    private int _urgentRefresh;
    private bool _disposed;
    private bool _scanInFlight;
    private bool _rescanPending;

    /// <summary>Per-scan state: the background worker never touches the tracker's shared collections.</summary>
    private sealed class ScanContext
    {
        public nint ExcludedWindow;
        public Dictionary<nint, TrackedWindow> Previous = [];
        public Dictionary<nint, uint> PreviousPids = [];
        public Dictionary<nint, uint> Notifications = [];
        public HashSet<nint> QuietFound = [];
        public List<TrackedWindow> Pending = [];
        public readonly char[] TitleBuffer = new char[1024];
        public readonly char[] ClassBuffer = new char[256];
    }

    public NativeWindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // Enumeration does ~10 interop calls per window and can take 50-128 ms. Running it at Input priority made
        // clicks wait behind it; Background lets real input and rendering preempt the scan (#128 finding 6).
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _locationTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _locationTimer.Tick += LocationTimer_Tick;
        _eventCallback = OnWinEvent;
    }

    public event EventHandler<IReadOnlyList<TrackedWindow>>? WindowsChanged;

    public event Action<nint>? ForegroundWindowChanged;

    public IReadOnlyList<TrackedWindow> Snapshot { get; private set; } = [];

    public bool NotificationsAvailable => _systemEventHook != 0;

    public void Start(nint excludedWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowHook != 0) return;
        _excludedWindow = excludedWindow;
        _windowHook = SetWinEventHook(EventObjectCreate, EventObjectHide, 0, _eventCallback, 0, 0, WinEventFlags);
        if (_windowHook == 0)
        {
            throw new InvalidOperationException("ErrorWindowEvents");
        }

        _nameChangeHook = SetWinEventHook(EventObjectNameChange, EventObjectNameChange, 0, _eventCallback, 0, 0, WinEventFlags);
        if (_nameChangeHook == 0)
        {
            UnhookWinEvent(_windowHook);
            _windowHook = 0;
            throw new InvalidOperationException("ErrorWindowTitleHook");
        }

        // Cloak changes are the only notification for a suspended/background UWP frame becoming (in)visible;
        // without them the snapshot can keep a phantom app button until an unrelated window event arrives.
        _cloakHook = SetWinEventHook(EventObjectCloaked, EventObjectUncloaked, 0, _eventCallback, 0, 0, WinEventFlags);
        if (_cloakHook == 0) AppLog.Warning("Cloak-change hook unavailable; UWP visibility changes may be delayed.");
        _locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, 0, _eventCallback, 0, 0, WinEventFlags);
        if (_locationHook == 0) AppLog.Warning("Location-change hook unavailable.");
        _systemEventHook = SetWinEventHook(EventSystemAlert, EventSystemForeground, 0, _eventCallback, 0, 0, WinEventFlags);
        if (_systemEventHook == 0) AppLog.Warning("Foreground-event hook unavailable; using polling fallback.");
        RefreshWindows();
        PublishForeground(NormalizedForeground());
    }

    private nint NormalizedForeground()
    {
        var foreground = GetForegroundWindow();
        return foreground != 0 ? NormalizeRootWindow(foreground) : 0;
    }

    /// <summary>
    /// Publishes a foreground change, deduping repeat refreshes for the same handle while allowing location
    /// updates (<paramref name="force"/> = true) to re-trigger overlap checks for smart auto-hide (#85).
    /// </summary>
    internal void PublishForeground(nint target, bool force = false)
    {
        if (!force && _foregroundPublished && _lastForegroundWindow == target) return;
        _foregroundPublished = true;
        _lastForegroundWindow = target;
        ForegroundWindowChanged?.Invoke(target);
    }

    // This callback is invoked from native code; an exception escaping it is undefined behaviour and can take
    // down the process, so isolate all work (including subscriber callbacks) behind a logged guard.
    private void OnWinEvent(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime)
    {
        try
        {
            HandleWinEvent(eventType, windowHandle, objectId, childId);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Window event {eventType:X} failed: {ex.Message}");
        }
    }

    private void HandleWinEvent(uint eventType, nint windowHandle, int objectId, int childId)
    {
        if (_disposed) return;

        if (eventType == EventObjectLocationChange)
        {
            if (objectId == ObjectWindow && childId == ChildIdSelf && windowHandle != 0)
            {
                var foreground = GetForegroundWindow();
                if (foreground != 0)
                {
                    var foregroundRoot = NormalizeRootWindow(foreground);
                    if (windowHandle == foreground || windowHandle == foregroundRoot)
                    {
                        _pendingLocationForeground = foregroundRoot;
                        if (!_locationTimer.IsEnabled) _locationTimer.Start();
                    }
                }
            }
            return;
        }

        if (eventType == EventSystemAlert && windowHandle != 0)
        {
            var target = NormalizeRootWindow(windowHandle);
            if (target != 0 && target != _excludedWindow && IsWindowVisible(target) && GetWindow(target, WindowOwner) == 0)
            {
                _notificationWindows[target] = PidOf(target);
                QueueRefresh();
            }
            return;
        }

        if (eventType == EventSystemForeground)
        {
            var target = windowHandle != 0 ? NormalizeRootWindow(windowHandle) : 0;
            _notificationWindows.Remove(windowHandle);
            _notificationWindows.Remove(target);
            _locationTimer.Stop();
            QueueRefresh(urgent: true);
            PublishForeground(target);
            return;
        }

        if (objectId != ObjectWindow || childId != ChildIdSelf
            || eventType is not (EventObjectCreate or EventObjectDestroy or EventObjectShow or EventObjectHide
                or EventObjectNameChange or EventObjectCloaked or EventObjectUncloaked))
        {
            return;
        }

        if (eventType is EventObjectCreate or EventObjectDestroy)
        {
            _quietWindows.Remove(windowHandle);
        }
        else if (_quietWindows.Contains(windowHandle) && eventType == EventObjectNameChange)
        {
            return;
        }

        QueueRefresh(urgent: eventType is EventObjectShow or EventObjectDestroy or EventObjectCloaked or EventObjectUncloaked
            || (eventType == EventObjectNameChange && !_snapshotHandles.Contains(windowHandle)));
    }

    private void LocationTimer_Tick(object? sender, EventArgs e)
    {
        _locationTimer.Stop();
        if (_disposed) return;
        var foreground = GetForegroundWindow();
        var target = foreground != 0 ? NormalizeRootWindow(foreground) : _pendingLocationForeground;
        _pendingLocationForeground = 0;
        if (target != 0 && IsWindow(target)) PublishForeground(target, force: true);
    }

    private void QueueRefresh(bool urgent = false)
    {
        if (urgent) Interlocked.Exchange(ref _urgentRefresh, 1);
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed)
            {
                if (Interlocked.Exchange(ref _urgentRefresh, 0) != 0)
                {
                    FlushRefresh();
                }
                else if (!_refreshTimer.IsEnabled)
                {
                    // Bound the wait from the first event; later events must not postpone discovery.
                    _refreshTimer.Start();
                }
            }
        }, DispatcherPriority.Background);
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        FlushRefresh();
    }

    /// <summary>
    /// Drops cached foreground state after a secure-desktop transition. Without this, LogonUI's handle can survive
    /// as the "previous foreground window" and be reactivated on the first click after unlocking.
    /// </summary>
    public void ResyncForeground()
    {
        _refreshTimer.Stop();
        _locationTimer.Stop();
        _pendingLocationForeground = 0;
        RefreshWindows();
        PublishForeground(NormalizedForeground());
    }

    private void FlushRefresh()
    {
        _refreshTimer.Stop();
        RefreshWindows();
        PublishForeground(NormalizedForeground());
    }

    private void RefreshWindows()
    {
        if (_disposed) return;
        if (_scanInFlight)
        {
            _rescanPending = true;
            return;
        }

        // The 50-128 ms EnumWindows + per-window COM scan used to run on the UI dispatcher. Run it on the
        // dedicated background STA worker instead and publish the result back here (#128 finding 6).
        _scanInFlight = true;
        _rescanPending = false;
        var context = new ScanContext
        {
            ExcludedWindow = _excludedWindow,
            Previous = new Dictionary<nint, TrackedWindow>(_previousByHandle),
            PreviousPids = new Dictionary<nint, uint>(_previousPidByHandle),
            Notifications = new Dictionary<nint, uint>(_notificationWindows)
        };
        foreach (var item in Snapshot) context.Previous[item.Handle] = item;

        _ = StaTask.RunScan(() =>
        {
            EnumWindows((handle, _) => CollectWindow(handle, context), 0);
            return context;
        }).ContinueWith(task =>
        {
            _scanInFlight = false;
            if (task.IsFaulted)
            {
                AppLog.Warning($"Window scan failed: {task.Exception?.GetBaseException().Message}");
            }
            else if (!_disposed)
            {
                ApplyScan(task.Result);
            }

            if (!_disposed && _rescanPending)
            {
                _rescanPending = false;
                RefreshWindows();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Publishes a completed background scan on the UI thread.</summary>
    private void ApplyScan(ScanContext context)
    {
        foreach (var handle in context.QuietFound) _quietWindows.Add(handle);
        _quietWindows.RemoveWhere(windowHandle => !IsWindow(windowHandle));

        foreach (var stale in _notificationWindows.Keys
            .Where(handle => !IsWindow(handle)
                || (!_notificationWindows.TryGetValue(handle, out var pid) || PidOf(handle) != pid)).ToList())
        {
            _notificationWindows.Remove(stale);
        }

        if (Snapshot.SequenceEqual(context.Pending)) return;

        var next = context.Pending.ToArray();
        _previousByHandle.Clear();
        _previousPidByHandle.Clear();
        _snapshotHandles.Clear();
        for (var i = 0; i < next.Length; i++)
        {
            var item = next[i];
            _previousByHandle[item.Handle] = item;
            var pid = PidOf(item.Handle);
            if (pid != 0) _previousPidByHandle[item.Handle] = pid;
            _snapshotHandles.Add(item.Handle);
        }

        Snapshot = next;
        WindowsChanged?.Invoke(this, next);
    }

    // EnumWindows invokes this delegate from native code; never let an exception cross that boundary.
    private bool CollectWindow(nint windowHandle, ScanContext context)
    {
        try
        {
            return CollectWindowCore(windowHandle, context);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Window enumeration failed for {windowHandle}: {ex.Message}");
            return true;
        }
    }

    private bool CollectWindowCore(nint windowHandle, ScanContext context)
    {
        if (windowHandle == context.ExcludedWindow || !IsWindowVisible(windowHandle)) return true;

        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == Environment.ProcessId) return true;

        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        // Tray-only apps leave visible helper windows behind: non-activatable overlays, windows removed by
        // ITaskbarList::DeleteTab, tool windows and hidden immersive frames. Match the shell's rules so none
        // of them is promoted to a normal running-app button.
        if (!TaskbarWindowRules.HasTaskbarButton(extendedStyle, GetWindow(windowHandle, WindowOwner) != 0,
                GetProp(windowHandle, TaskbarWindowRules.DeletedFromTaskbarProperty) != 0))
        {
            return true;
        }

        // DWM_CLOAKED_SHELL is kept for windows on other virtual desktops; all other cloak reasons hide the window.
        if (DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0
            && TaskbarWindowRules.IsHiddenCloak(cloaked))
        {
            return true;
        }

        var classLength = GetClassName(windowHandle, context.ClassBuffer, context.ClassBuffer.Length);
        if (classLength > 0)
        {
            ReadOnlySpan<char> cls = context.ClassBuffer.AsSpan(0, classLength);
            if (TaskbarWindowRules.IsHiddenImmersiveFrame(cls, extendedStyle))
            {
                context.QuietFound.Add(windowHandle);
                return true;
            }
        }

        var titleLength = GetWindowTextLength(windowHandle);
        if (titleLength <= 0) return true;

        var readCapacity = Math.Min(titleLength + 1, context.TitleBuffer.Length);
        var actualLength = GetWindowText(windowHandle, context.TitleBuffer, readCapacity);
        if (actualLength <= 0) return true;

        ReadOnlySpan<char> trimmedSpan = context.TitleBuffer.AsSpan(0, actualLength).Trim();
        if (trimmedSpan.IsEmpty) return true;

        TrackedWindow? previous = null;
        if (context.Previous.TryGetValue(windowHandle, out var cached)
            && (!context.PreviousPids.TryGetValue(windowHandle, out var cachedPid) || cachedPid == processId))
        {
            previous = cached;
        }
        // A first lookup can fail for a just-starting process; retry it instead of hiding the app forever.
        var executablePath = previous?.ExecutablePath ?? GetExecutablePath(windowHandle);
        if (IsWindowsSystemApp(executablePath) || IsExplorerShellWindow(executablePath, windowHandle, context.ClassBuffer))
        {
            context.QuietFound.Add(windowHandle);
            return true;
        }

        var hasNotification = context.Notifications.TryGetValue(windowHandle, out var notificationPid)
            && PidOf(windowHandle) == notificationPid;
        if (previous is not null)
        {
            var title = trimmedSpan.SequenceEqual(previous.Title) ? previous.Title : trimmedSpan.ToString();
            context.Pending.Add(previous with { Title = title, ExecutablePath = executablePath, HasNotification = hasNotification });
        }
        else
        {
            // Read the Shell AppUserModelId once per window (cached on later refreshes) so windows from
            // different executables of the same app group together. This runs on the background STA worker
            // now; a COM hiccup must never take down the tracker — fall back to path-only grouping.
            string? appId = null;
            try { appId = NativeJumpListSession.ReadWindowAppId(windowHandle); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { }
            context.Pending.Add(new TrackedWindow(windowHandle, trimmedSpan.ToString(), executablePath, hasNotification, appId));
        }

        return true;
    }

    private static bool IsForegroundRoot(nint windowHandle)
    {
        var active = GetForegroundWindow();
        return active == windowHandle
            || (active != 0 && NormalizeRootWindow(active) == NormalizeRootWindow(windowHandle));
    }

    private static nint NormalizeRootWindow(nint windowHandle)
    {
        if (windowHandle == 0) return 0;
        var root = GetAncestor(windowHandle, GaRootOwner);
        return root != 0 ? root : windowHandle;
    }

    public static bool Activate(nint windowHandle)
    {
        if (!IsWindow(windowHandle))
        {
            return false;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindowAsync(windowHandle, ShowRestore);
        }

        // Grant foreground privilege only to the app being activated, never ASFW_ANY (-1)
        // which would let every process steal focus during this window's input window.
        GetWindowThreadProcessId(windowHandle, out var targetPid);
        if (targetPid != 0 && targetPid <= int.MaxValue) AllowSetForegroundWindow((int)targetPid);
        // Fast path: a dock click is the last input event, so this process may set the foreground window directly.
        // Attaching input queues and raising the window first waits on both apps' UI threads and cost 50-250 ms per
        // click; keep that only as the fallback for when Windows refuses the plain request.
        if (SetForegroundWindow(windowHandle) && IsForegroundRoot(windowHandle))
        {
            return true;
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        var targetThread = GetWindowThreadProcessId(windowHandle, out _);
        var currentThread = GetCurrentThreadId();
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread
            && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(windowHandle);
            if (SetForegroundWindow(windowHandle))
            {
                return true;
            }
        }
        finally
        {
            if (attachedTarget)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
            if (attachedForeground)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        SwitchToThisWindow(windowHandle, true);
        var active = GetForegroundWindow();
        return active == windowHandle
            || (active != 0 && NormalizeRootWindow(active) == NormalizeRootWindow(windowHandle));
    }

    public static nint GetForegroundApplicationWindow() =>
        TryGetForegroundApplicationWindow(out var window) ? window : 0;

    /// <summary>
    /// Reads the OS foreground window once and resolves its root owner. Returns <c>false</c> when the
    /// foreground window belongs to this process (e.g. a dock menu or keyboard focus), distinguishing
    /// own-process focus from a genuinely empty/desktop foreground (<c>true</c> with <c>0</c>).
    /// </summary>
    internal static bool TryGetForegroundApplicationWindow(out nint window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            window = 0;
            return true;
        }
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            window = 0;
            return false;
        }
        var root = GetAncestor(foreground, GaRootOwner);
        window = root != 0 ? root : foreground;
        return true;
    }

    public static bool ToggleActivation(nint windowHandle, string executablePath, nint? foregroundAtPress = null)
    {
        if (!IsWindow(windowHandle))
        {
            return false;
        }

        var foregroundWindow = foregroundAtPress ?? GetForegroundApplicationWindow();
        if (foregroundWindow != 0)
        {
            var foregroundRoot = NormalizeRootWindow(foregroundWindow);
            if (IsWindow(foregroundRoot) && !IsIconic(foregroundRoot)
                && (foregroundRoot == windowHandle
                    || (!string.IsNullOrEmpty(executablePath)
                        && string.Equals(GetExecutablePath(foregroundRoot), executablePath, StringComparison.OrdinalIgnoreCase))))
            {
                return ShowWindowAsync(foregroundRoot, ShowMinimize);
            }
        }

        return Activate(windowHandle);
    }

    public static bool RequestClose(nint windowHandle)
    {
        return IsWindow(windowHandle) && PostMessage(windowHandle, WmClose, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _locationTimer.Stop();
        _locationTimer.Tick -= LocationTimer_Tick;
        if (_windowHook != 0)
        {
            UnhookWinEvent(_windowHook);
            _windowHook = 0;
        }

        if (_locationHook != 0)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = 0;
        }

        if (_nameChangeHook != 0)
        {
            UnhookWinEvent(_nameChangeHook);
            _nameChangeHook = 0;
        }

        if (_cloakHook != 0)
        {
            UnhookWinEvent(_cloakHook);
            _cloakHook = 0;
        }

        if (_systemEventHook != 0)
        {
            UnhookWinEvent(_systemEventHook);
            _systemEventHook = 0;
        }

        _notificationWindows.Clear();
        _quietWindows.Clear();
        _snapshotHandles.Clear();
        _previousByHandle.Clear();
        _excludedWindow = 0;
        _lastForegroundWindow = 0;
        _pendingLocationForeground = 0;
        _foregroundPublished = false;
        Snapshot = [];
    }

    private delegate void WinEventDelegate(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime);
    private delegate bool EnumWindowsDelegate(nint windowHandle, nint state);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint eventHookModule, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, nint state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, int command);

    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint windowHandle, string propertyName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, char[] text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    internal static string? GetExecutablePath(nint windowHandle)
    {
        var threadId = GetWindowThreadProcessId(windowHandle, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            return null;
        }

        var buffer = ArrayPool<char>.Shared.Rent(1024);
        try
        {
            var length = (uint)buffer.Length;
            if (QueryFullProcessImageName(processHandle, 0, buffer, ref length))
            {
                return new string(buffer, 0, (int)length);
            }

            if (Marshal.GetLastWin32Error() == ErrorInsufficientBuffer)
            {
                ArrayPool<char>.Shared.Return(buffer);
                buffer = ArrayPool<char>.Shared.Rent(32768);
                length = (uint)buffer.Length;
                if (QueryFullProcessImageName(processHandle, 0, buffer, ref length))
                {
                    return new string(buffer, 0, (int)length);
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            CloseHandle(processHandle);
        }
    }

    // Packaged processes such as WhatsApp.Root.exe report their AppUserModelID even when their windows do not.
    internal static string? GetPackagedAppId(nint windowHandle)
    {
        var threadId = GetWindowThreadProcessId(windowHandle, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            return null;
        }

        try
        {
            var length = 0u;
            if (GetApplicationUserModelId(processHandle, ref length, null) != ErrorInsufficientBuffer || length == 0)
            {
                return null;
            }

            var buffer = ArrayPool<char>.Shared.Rent((int)length);
            try
            {
                if (GetApplicationUserModelId(processHandle, ref length, buffer) != 0 || length == 0) return null;
                var charCount = (int)length;
                if (charCount > 0 && buffer[charCount - 1] == '\0') charCount--;
                return charCount > 0 ? new string(buffer, 0, charCount) : null;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private bool IsExplorerShellWindow(string? executablePath, nint windowHandle, char[] classBuffer)
    {
        if (!string.Equals(Path.GetFileName(executablePath), "explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var length = GetClassName(windowHandle, classBuffer, classBuffer.Length);
        if (length <= 0) return true;
        ReadOnlySpan<char> cls = classBuffer.AsSpan(0, length);
        return !cls.SequenceEqual("CabinetWClass") && !cls.SequenceEqual("ExploreWClass");
    }

    private static bool IsWindowsSystemApp(string? executablePath)
    {
        return executablePath is not null
            && executablePath.StartsWith(WindowsSystemAppsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(nint processHandle, ref uint length, char[]? applicationUserModelId);

    // GetWindowLongPtrW does not exist on 32-bit Windows; there it is a macro for GetWindowLongW.
    private static nint GetWindowLongPtr(nint windowHandle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(windowHandle, index) : GetWindowLong32(windowHandle, index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, char[] className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint threadIdAttach, uint threadIdAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint processHandle, uint flags, char[] executablePath, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint windowHandle, int command);
}
