using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private readonly ObservableCollection<PinnedApp> _pinnedApps = [];
    private readonly ObservableCollection<TrackedWindow> _runningWindows = [];
    private readonly PinnedAppStore _pinnedAppStore = new();
    private NativeWindowTracker? _windowTracker;
    private TaskbarReplacementSession? _replacementSession;
    private HwndSource? _source;

    public MainWindow()
    {
        InitializeComponent();
        PinnedApps.ItemsSource = _pinnedApps;
        RunningWindows.ItemsSource = _runningWindows;
        LoadPinnedApps();
        PositionDock();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        if (source is null)
        {
            ShowStatus("Dock penceresi Windows'a bağlanamadı.");
            return;
        }

        _source = source;
        _source.AddHook(WindowProc);
        if (!ExcludeFromTaskSwitcher(source.Handle))
        {
            ShowStatus("OpenTaskbar Alt+Tab listesinden çıkarılamadı.");
        }

        _windowTracker = new NativeWindowTracker(Dispatcher);
        _windowTracker.WindowsChanged += WindowTracker_WindowsChanged;
        try
        {
            _windowTracker.Start(source.Handle);
        }
        catch (InvalidOperationException ex)
        {
            ShowStatus(ex.Message);
            _windowTracker.Dispose();
            _windowTracker = null;
        }

        StartReplacementMode();
    }

    private void PositionDock()
    {
        if (_replacementSession?.IsActive == true)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Width = SystemParameters.PrimaryScreenWidth;
        DockSurface.Width = Width;
        Left = workArea.Left;
        Top = Math.Max(workArea.Top, workArea.Bottom - Height);
    }

    private void StartReplacementMode()
    {
        var source = _source;
        if (source is null)
        {
            ShowStatus("Dock penceresi hazır olmadan görev çubuğu modu açılamaz.");
            return;
        }

        Width = SystemParameters.PrimaryScreenWidth;
        DockSurface.Width = Width;
        Height = 64;
        var session = new TaskbarReplacementSession(this, source.Handle);
        session.OperationFailed += ShowStatus;
        try
        {
            session.Start();
            _replacementSession = session;
        }
        catch (InvalidOperationException ex)
        {
            session.Dispose();
            Height = 64;
            PositionDock();
            ShowStatus(ex.Message);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_replacementSession?.HandleMessage(unchecked((uint)message), wParam, lParam) == true)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void LoadPinnedApps()
    {
        var result = _pinnedAppStore.Load();
        foreach (var app in result.Apps)
        {
            _pinnedApps.Add(app with { Icon = ApplicationIconProvider.GetForFile(app.ExecutablePath) });
        }

        if (result.Error is not null)
        {
            ShowStatus(result.Error);
        }
    }

    private void WindowTracker_WindowsChanged(object? sender, IReadOnlyList<TrackedWindow> windows)
    {
        _runningWindows.Clear();
        foreach (var window in windows)
        {
            _runningWindows.Add(window);
        }
    }

    private void PinnedApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string executablePath)
        {
            return;
        }

        if (!File.Exists(executablePath))
        {
            ShowStatus("Uygulama dosyası bulunamadı. Sabitlemeyi kaldırıp uygulamayı yeniden seçin.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
            ClearStatus();
        }
        catch (Win32Exception)
        {
            ShowStatus("Uygulama Windows tarafından başlatılamadı.");
        }
        catch (InvalidOperationException)
        {
            ShowStatus("Uygulama başlatma isteği tamamlanamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("Uygulamayı başlatmak için erişim izni yok.");
        }
        catch (IOException)
        {
            ShowStatus("Uygulama dosyasına erişilemedi.");
        }
    }

    private void RemovePinnedApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string executablePath })
        {
            return;
        }

        var app = _pinnedApps.FirstOrDefault(item => string.Equals(item.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            return;
        }

        var remainingApps = _pinnedApps.Where(item => !string.Equals(item.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)).ToList();
        var error = _pinnedAppStore.Save(remainingApps);
        if (error is not null)
        {
            ShowStatus(error);
            return;
        }

        _pinnedApps.Remove(app);
        ClearStatus();
    }

    private void RunningWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: nint handle })
        {
            NativeWindowTracker.Activate(handle);
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        _replacementSession?.RefreshPosition();
    }

    private void ClearStatus()
    {
        StatusText.Text = string.Empty;
        _replacementSession?.RefreshPosition();
    }

    private static bool ExcludeFromTaskSwitcher(nint windowHandle)
    {
        Marshal.SetLastPInvokeError(0);
        var currentStyle = GetWindowLongPtr(windowHandle, GwlExStyle);
        if (currentStyle == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            return false;
        }

        var style = new nint((currentStyle.ToInt64() | WsExToolWindow) & ~WsExAppWindow);
        if (style != currentStyle)
        {
            Marshal.SetLastPInvokeError(0);
            var previousStyle = SetWindowLongPtr(windowHandle, GwlExStyle, style);
            if (previousStyle == 0 && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }
        }

        return SetWindowPos(windowHandle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_replacementSession is null)
        {
            return;
        }

        var error = _replacementSession.Stop();
        if (_replacementSession.IsActive)
        {
            e.Cancel = true;
            ShowStatus(error ?? "Windows görev çubuğu geri yüklenemedi. Uygulama açık bırakıldı.");
            return;
        }

        _replacementSession.Dispose();
        _replacementSession = null;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _source?.RemoveHook(WindowProc);
        _source = null;

        if (_windowTracker is not null)
        {
            _windowTracker.WindowsChanged -= WindowTracker_WindowsChanged;
            _windowTracker.Dispose();
            _windowTracker = null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
