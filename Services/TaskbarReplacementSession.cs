using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace OpenTaskbar.Services;

public sealed class TaskbarReplacementSession : IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmGetState = 0x00000004;
    private const uint AbmSetState = 0x0000000A;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbeBottom = 3;
    private const uint AbsAutoHide = 0x00000001;
    private const uint AbsAlwaysOnTop = 0x00000002;
    private const uint SupportedTaskbarState = AbsAutoHide | AbsAlwaysOnTop;
    private const uint AbnStateChange = 0;
    private const uint AbnPosChanged = 1;
    private const uint AbnFullScreenApp = 2;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint CallbackMessage = 0x8000 + 0x52;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly nint _handle;
    private bool _registered;
    private bool _active;
    private bool _stateChangeStarted;
    private uint _previousTaskbarState;
    private bool _disposed;
    private bool _fullScreenSuppressed;
    private bool _hideRetryPending;
    private double _shelfHeight = DockMetrics.ShelfHeight;
    private (int Left, int Bottom, int Explorer, int Extra)? _reservation;
    private double _nativeTaskbarHeight = DockMetrics.ShelfHeight;
    private const double DockHeadroomDips = DockMetrics.Headroom;
    private bool _autoHideEnabled;
    private Size _lastLayout;
    private int _stopInProgress;

    public TaskbarReplacementSession(Window window, nint handle)
    {
        _window = window;
        _handle = handle;
        AppDomain.CurrentDomain.ProcessExit += Process_Exit;
    }

    public bool IsActive => _active || _stateChangeStarted;

    // Explorer's hidden taskbar keeps reserving the work area; auto-hide releases it for the dock's hide modes.
    private uint ReplacementState => (_previousTaskbarState & AbsAlwaysOnTop) | (_autoHideEnabled ? AbsAutoHide : 0);

    private static TaskbarReplacementSession? _current;

    /// <summary>
    /// Restores the Windows taskbar from any thread. Used by crash handlers, where the UI thread may be unusable.
    /// </summary>
    public static string? EmergencyRestore() => Volatile.Read(ref _current)?.Stop();

    public static bool HasActiveSession => Volatile.Read(ref _current)?.IsActive == true;

    public event Action<string>? OperationFailed;
    public event Action<Size>? LayoutChanged;
    public event Action<bool>? FullScreenChanged;

    public void UpdateForeground(nint windowHandle)
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == Environment.ProcessId) return;
        SetFullScreenSuppression(IsFullScreen(windowHandle));
    }

    private static bool HasPendingRecovery(out string? error)
    {
        error = null;
        if (!File.Exists(JournalPath))
        {
            return false;
        }

        try
        {
            _ = ReadJournal();
            return true;
        }
        catch (InvalidDataException)
        {
            error = "Görev çubuğu kurtarma kaydı bozuk. Windows ayarlarını elle denetleyin.";
        }
        catch (IOException)
        {
            error = "Görev çubuğu kurtarma kaydı okunamadı.";
        }
        catch (UnauthorizedAccessException)
        {
            error = "Görev çubuğu kurtarma kaydına erişim izni yok.";
        }

        return false;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
        {
            return;
        }

        var pending = HasPendingRecovery(out var recoveryError);
        if (recoveryError is not null)
        {
            throw new InvalidOperationException(recoveryError);
        }

        _previousTaskbarState = pending ? ReadJournal() : GetTaskbarState();
        var nativeTaskbar = FindWindow("Shell_TrayWnd", null);
        if (nativeTaskbar != 0 && GetWindowRect(nativeTaskbar, out var taskbarRect))
        {
            var nativeDpi = GetDpiForWindow(nativeTaskbar);
            if (nativeDpi > 0 && taskbarRect.Bottom > taskbarRect.Top
                && taskbarRect.Right - taskbarRect.Left > taskbarRect.Bottom - taskbarRect.Top)
                _nativeTaskbarHeight = (taskbarRect.Bottom - taskbarRect.Top) * 96d / nativeDpi;
        }
        try
        {
            if (!pending)
            {
                WriteJournal(_previousTaskbarState);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Görev çubuğu ayarı değiştirilmedi; kurtarma kaydı yazılamadı.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Görev çubuğu ayarı değiştirilmedi; kurtarma kaydına yazma izni yok.", ex);
        }

        try
        {
            _stateChangeStarted = true;
            Volatile.Write(ref _current, this);
            var replacementState = _previousTaskbarState & AbsAlwaysOnTop;
            if (!SetTaskbarState(replacementState, _handle))
            {
                throw new InvalidOperationException("Windows görev çubuğunun kenar tetiklemesi kapatılamadı.");
            }

            if (!HideNativeTaskbar())
            {
                throw new InvalidOperationException("Windows görev çubuğu gizlenemedi.");
            }

            var data = CreateData();
            data.uCallbackMessage = CallbackMessage;
            if (SHAppBarMessage(AbmNew, ref data) == 0)
            {
                throw new InvalidOperationException("OpenTaskbar Windows'a AppBar olarak kaydedilemedi.");
            }

            _registered = true;
            _reservation = null;
            _active = true;
            PositionDockWindow();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void HandleTaskbarCreated()
    {
        if (!_active)
        {
            return;
        }

        _registered = false;
        _reservation = null;
        var nativeTaskbar = FindWindow("Shell_TrayWnd", null);
        if (nativeTaskbar != 0 && GetWindowRect(nativeTaskbar, out var taskbarRect))
        {
            var nativeDpi = GetDpiForWindow(nativeTaskbar);
            if (nativeDpi > 0 && taskbarRect.Bottom > taskbarRect.Top
                && taskbarRect.Right - taskbarRect.Left > taskbarRect.Bottom - taskbarRect.Top)
            {
                _nativeTaskbarHeight = (taskbarRect.Bottom - taskbarRect.Top) * 96d / nativeDpi;
            }
        }

        SetTaskbarState(ReplacementState, _handle);
        HideNativeTaskbar();

        var data = CreateData();
        data.uCallbackMessage = CallbackMessage;
        if (SHAppBarMessage(AbmNew, ref data) != 0)
        {
            _registered = true;
            _reservation = null;
        }

        PositionDockWindow();
    }

    public bool HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0)
        {
            return false;
        }

        if (message == CallbackMessage)
        {
            switch (unchecked((uint)wParam.ToInt64()))
            {
                case AbnPosChanged:
                    EnforceNativeTaskbarHidden();
                    PositionDockWindow();
                    return true;
                case AbnFullScreenApp:
                    if (lParam != 0)
                    {
                        UpdateForeground(GetForegroundWindow());
                    }
                    else
                    {
                        SetFullScreenSuppression(false);
                        PositionDockWindow();
                    }
                    return true;
                case AbnStateChange:
                    EnforceNativeTaskbarHidden();
                    return true;
            }
        }

        if (message is WmDisplayChange or WmDpiChanged)
        {
            PositionDockWindow();
        }

        return false;
    }

    public string? Stop()
    {
        if (Interlocked.CompareExchange(ref _stopInProgress, 1, 0) != 0)
        {
            return "Windows görev çubuğu geri yükleme işlemi devam ediyor.";
        }

        try
        {
            if (!_active && !_registered && !_stateChangeStarted) return null;

            // Stop enforcing hidden state before Shell calls can reenter our AppBar callback.
            _active = false;
            if (_registered)
            {
                var data = CreateData();
                SHAppBarMessage(AbmRemove, ref data);
                _registered = false;
                _reservation = null;
            }

            var stateRestored = SetTaskbarState(_previousTaskbarState, _handle);
            var taskbarShown = ShowNativeTaskbar();
            if (!stateRestored || !taskbarShown)
                return "Windows görev çubuğu geri yüklenemedi. Kapatmayı yeniden deneyin veya Windows görev çubuğu ayarlarını denetleyin.";

            _stateChangeStarted = false;
            return DeleteJournal();
        }
        finally
        {
            Volatile.Write(ref _stopInProgress, 0);
        }
    }

    public void RefreshPosition()
    {
        PositionDockWindow();
    }

    public void UpdateAutoHide(bool autoHide)
    {
        _autoHideEnabled = autoHide;
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        SetTaskbarState(ReplacementState, _handle);
        PositionDockWindow();
    }

    private void PositionDockWindow()
    {
        if ((!_active && !_registered) || Volatile.Read(ref _stopInProgress) != 0)
        {
            return;
        }

        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("Ekran boyutları alınamadı.");
        }

        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0) return;
        // Explorer's hidden taskbar keeps reserving its own height unless auto-hide is on. The dock reserves only
        // the part of its shelf that exceeds that strip, so maximized windows never sit underneath it.
        var explorerHeight = _autoHideEnabled ? 0 : (int)Math.Round(_nativeTaskbarHeight * dpi / 96d);
        var height = Math.Max(1, (int)Math.Round(ShelfHeight * dpi / 96d));
        ReserveExtraHeight(monitorInfo.rcMonitor, explorerHeight, _autoHideEnabled ? 0 : Math.Max(0, height - explorerHeight));
        var headroom = (int)Math.Round(DockHeadroomDips * dpi / 96d);
        var totalHeight = height + headroom;
        var layout = new Size((monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left) * 96d / dpi,
            height * 96d / dpi);
        var totalHeightDips = totalHeight * 96d / dpi;
        if (_lastLayout != layout)
        {
            _lastLayout = layout;
            _window.Width = layout.Width;
            _window.Height = totalHeightDips;
            LayoutChanged?.Invoke(layout);
        }
        var monitorLeft = monitorInfo.rcMonitor.Left;
        var monitorRight = monitorInfo.rcMonitor.Right;
        var width = monitorRight - monitorLeft;
        var left = monitorLeft;
        UpdateForeground(GetForegroundWindow());
        var top = monitorInfo.rcMonitor.Bottom - totalHeight;
        if (!SetWindowPos(_handle, _fullScreenSuppressed ? new nint(1) : HwndTopmost, left, top,
                width, totalHeight,
                SwpNoActivate | SwpNoOwnerZOrder))
        {
            throw new InvalidOperationException("OpenTaskbar ekranın alt kenarına yerleştirilemedi.");
        }
    }

    /// <summary>
    /// Reserves <paramref name="extraHeight"/> pixels directly above Explorer's strip through this AppBar.
    /// Zero releases the reservation. Unchanged requests are skipped so ABN_POSCHANGED cannot loop.
    /// </summary>
    private void ReserveExtraHeight(Rect monitor, int explorerHeight, int extraHeight)
    {
        if (!_registered) return;
        var request = (monitor.Left, monitor.Bottom, explorerHeight, extraHeight);
        if (_reservation == request) return;
        _reservation = request;
        var data = CreateData();
        data.uEdge = AbeBottom;
        var bottom = monitor.Bottom - explorerHeight;
        data.rc = new Rect(monitor.Left, bottom - extraHeight, monitor.Right, bottom);
        SHAppBarMessage(AbmQueryPos, ref data);
        data.rc.Top = data.rc.Bottom - extraHeight;
        SHAppBarMessage(AbmSetPos, ref data);
    }

    /// <summary>Shelf height in DIPs for the selected dock size; the window adds headroom above it.</summary>
    public double ShelfHeight
    {
        get => _shelfHeight;
        set
        {
            if (_shelfHeight == value) return;
            _shelfHeight = value;
            if (_active && Volatile.Read(ref _stopInProgress) == 0) PositionDockWindow();
        }
    }

    private bool IsFullScreen(nint windowHandle)
    {
        if (windowHandle == 0 || !IsWindowVisible(windowHandle) || IsIconic(windowHandle)
            || MonitorFromWindow(windowHandle, MonitorDefaultToNearest) != MonitorFromWindow(_handle, MonitorDefaultToNearest))
            return false;
        var className = new StringBuilder(128);
        GetClassName(windowHandle, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        // With auto-hide the work area is the whole monitor, so ordinary maximized windows cover it too.
        // Only captionless windows (games, video, F11 browsers) count as full screen.
        const long wsCaption = 0x00C00000;
        if (IsZoomed(windowHandle) && (GetWindowLongPtr(windowHandle, GwlStyle).ToInt64() & wsCaption) == wsCaption) return false;
        var monitor = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(windowHandle, MonitorDefaultToNearest), ref monitor)
            && GetWindowRect(windowHandle, out var rect)
            && rect.Left <= monitor.rcMonitor.Left && rect.Top <= monitor.rcMonitor.Top
            && rect.Right >= monitor.rcMonitor.Right && rect.Bottom >= monitor.rcMonitor.Bottom;
    }

    private void SetFullScreenSuppression(bool suppressed)
    {
        if (_fullScreenSuppressed == suppressed) return;
        _fullScreenSuppressed = suppressed;
        ShowWindow(_handle, suppressed ? SwHide : SwShowNoActivate);
        FullScreenChanged?.Invoke(suppressed);
    }

    private static uint GetTaskbarState()
    {
        var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        return unchecked((uint)SHAppBarMessage(AbmGetState, ref data));
    }

    private static bool HideNativeTaskbar()
    {
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == 0)
        {
            return false;
        }

        if (IsWindowVisible(taskbarHandle))
        {
            ShowWindow(taskbarHandle, SwHide);
        }

        // Secondary-monitor taskbars are best effort; the primary one decides success.
        foreach (var secondary in FindSecondaryTaskbars())
        {
            if (IsWindowVisible(secondary)) ShowWindow(secondary, SwHide);
        }

        return !IsWindowVisible(taskbarHandle);
    }

    private static List<nint> FindSecondaryTaskbars()
    {
        var handles = new List<nint>();
        var className = new StringBuilder(64);
        EnumWindows((handle, _) =>
        {
            className.Clear();
            if (GetClassName(handle, className, className.Capacity) > 0
                && className.ToString() == "Shell_SecondaryTrayWnd")
            {
                handles.Add(handle);
            }

            return true;
        }, 0);
        return handles;
    }

    private static bool ShowNativeTaskbar()
    {
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == 0)
        {
            return false;
        }

        ShowWindow(taskbarHandle, SwShowNoActivate);
        foreach (var secondary in FindSecondaryTaskbars())
        {
            ShowWindow(secondary, SwShowNoActivate);
        }

        return IsWindowVisible(taskbarHandle);
    }

    private void EnforceNativeTaskbarHidden()
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        if (!SetTaskbarState(ReplacementState, _handle))
        {
            OperationFailed?.Invoke("Windows görev çubuğunun otomatik açılması yeniden etkinleşti.");
            return;
        }

        if (!HideNativeTaskbar()) RetryHideNativeTaskbar();
    }

    // Explorer briefly re-shows an auto-hidden taskbar when the pointer touches the screen edge; retry before reporting.
    private void RetryHideNativeTaskbar()
    {
        if (_hideRetryPending) return;
        _hideRetryPending = true;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        var attempts = 0;
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (!_active || Volatile.Read(ref _stopInProgress) != 0 || HideNativeTaskbar() || attempts >= 3)
            {
                timer.Stop();
                _hideRetryPending = false;
                if (_active && attempts >= 3 && !HideNativeTaskbar())
                {
                    OperationFailed?.Invoke("Windows görev çubuğu yeniden gizlenemedi.");
                }
            }
        };
        timer.Start();
    }

    private static bool SetTaskbarState(uint state, nint windowHandle)
    {
        var data = new AppBarData
        {
            cbSize = (uint)Marshal.SizeOf<AppBarData>(),
            hWnd = windowHandle,
            lParam = unchecked((nint)state)
        };
        SHAppBarMessage(AbmSetState, ref data);
        return (GetTaskbarState() & SupportedTaskbarState) == (state & SupportedTaskbarState);
    }

    private AppBarData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = _handle
    };

    private static string JournalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar",
        "taskbar-state.txt");

    private static void WriteJournal(uint state)
    {
        var path = JournalPath;
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new IOException("Görev çubuğu kurtarma kaydı için yol oluşturulamadı.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, state.ToString(CultureInfo.InvariantCulture));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static uint ReadJournal()
    {
        var text = File.ReadAllText(JournalPath).Trim();
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var state)
            || (state & ~SupportedTaskbarState) != 0)
        {
            throw new InvalidDataException("Görev çubuğu kurtarma kaydı geçersiz.");
        }

        return state;
    }

    private static string? DeleteJournal()
    {
        try
        {
            if (File.Exists(JournalPath))
            {
                File.Delete(JournalPath);
            }

            return null;
        }
        catch (IOException)
        {
            return "Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydı silinemedi.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydını silme izni yok.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var error = Stop();
        if (error is not null) Trace.TraceError(error);
        if (IsActive) return;
        _disposed = true;
        Interlocked.CompareExchange(ref _current, null, this);
        AppDomain.CurrentDomain.ProcessExit -= Process_Exit;
    }

    private void Process_Exit(object? sender, EventArgs e)
    {
        var error = Stop();
        if (error is not null) Trace.TraceError(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    private const int GwlStyle = -16;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    private delegate bool EnumWindowsProc(nint windowHandle, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder name, int length);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);
}
