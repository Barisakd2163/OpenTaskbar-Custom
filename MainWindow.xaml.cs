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
    private readonly DockSettingsStore _dockSettingsStore = new();
    private DockPosition _dockPosition = DockPosition.Left;
    private NativeWindowTracker? _windowTracker;
    private TaskbarReplacementSession? _replacementSession;
    private HwndSource? _source;

    public MainWindow()
    {
        InitializeComponent();
        PinnedApps.ItemsSource = _pinnedApps;
        RunningWindows.ItemsSource = _runningWindows;
        LoadPinnedApps();
        var settingsResult = _dockSettingsStore.Load();
        _dockPosition = settingsResult.Settings.Position;
        if (settingsResult.Error is not null)
        {
            ShowStatus(settingsResult.Error);
        }

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
            if (!_windowTracker.NotificationsAvailable)
            {
                ShowStatus("Windows uygulama bildirimlerini dinleyemedi; bildirim rozeti kullanılamıyor.");
            }
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
        ResizeDockToContent();
        ApplyDockAlignment();
        Left = _dockPosition switch
        {
            DockPosition.Left => workArea.Left,
            DockPosition.Center => workArea.Left + (workArea.Width - Width) / 2,
            DockPosition.Right => workArea.Right - Width,
            _ => workArea.Left
        };
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

        ResizeDockToContent();
        ApplyDockAlignment();
        Height = 64;
        var session = new TaskbarReplacementSession(this, source.Handle, () => _dockPosition);
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

        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
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
        if (sender is Button { Tag: nint handle } && !NativeWindowTracker.Activate(handle))
        {
            ShowStatus("Pencere etkinleştirilemedi. Uygulama kapanmış veya Windows odağı engellemiş olabilir.");
            return;
        }

        ClearStatus();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void ClearStatus()
    {
        StatusText.Text = string.Empty;
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void DockContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        var settingsMenu = contextMenu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Header?.ToString() == "Ayarlar");
        var positionMenu = settingsMenu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Header?.ToString() == "Konum");
        if (positionMenu is not null)
        {
            foreach (var item in positionMenu.Items.OfType<MenuItem>())
            {
                item.IsChecked = string.Equals(item.Tag as string, _dockPosition.ToString(), StringComparison.Ordinal);
            }
        }

        var pinnedTarget = contextMenu.PlacementTarget is FrameworkElement
        {
            DataContext: PinnedApp,
            Tag: string executablePath
        };
        var removePinItem = contextMenu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.CommandParameter as string == "RemovePinnedApp");
        var divider = contextMenu.Items.OfType<Separator>()
            .FirstOrDefault(item => item.Tag as string == "PinnedAppDivider");
        if (removePinItem is not null)
        {
            removePinItem.Visibility = pinnedTarget ? Visibility.Visible : Visibility.Collapsed;
            removePinItem.Tag = pinnedTarget && contextMenu.PlacementTarget is FrameworkElement { Tag: string path }
                ? path
                : null;
        }

        if (divider is not null)
        {
            divider.Visibility = pinnedTarget ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DockPosition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string positionName }
            || !Enum.TryParse<DockPosition>(positionName, ignoreCase: false, out var position)
            || !Enum.IsDefined(position))
        {
            return;
        }

        var error = _dockSettingsStore.Save(new DockSettings(position));
        if (error is not null)
        {
            ShowStatus(error);
            return;
        }

        _dockPosition = position;
        if (_replacementSession?.IsActive == true)
        {
            ApplyDockAlignment();
        }
        else
        {
            PositionDock();
        }

        ClearStatus();
    }

    private void ApplyDockAlignment()
    {
        var alignment = _dockPosition switch
        {
            DockPosition.Left => HorizontalAlignment.Left,
            DockPosition.Center => HorizontalAlignment.Center,
            DockPosition.Right => HorizontalAlignment.Right,
            _ => throw new InvalidOperationException("Dock konumu desteklenmiyor.")
        };

        DockContents.HorizontalAlignment = alignment;
        DockScrollViewer.HorizontalContentAlignment = alignment;
        _replacementSession?.RefreshPosition();
    }

    private void ResizeDockToContent()
    {
        DockContents.Measure(new System.Windows.Size(double.PositiveInfinity, Height));
        var padding = DockSurface.Padding;
        var desiredWidth = Math.Ceiling(DockContents.DesiredSize.Width + padding.Left + padding.Right);
        Width = Math.Clamp(desiredWidth, 16, SystemParameters.PrimaryScreenWidth);
        DockSurface.Width = Width;
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
