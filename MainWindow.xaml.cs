using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
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

    private readonly ObservableCollection<DockApp> _pinnedApps = [];
    private readonly ObservableCollection<DockApp> _runningApps = [];
    private readonly DockSettingsStore _dockSettingsStore = new();
    private readonly DispatcherTimer _previewCloseTimer;
    private readonly WindowThumbnailPreview _thumbnailPreview;
    private DockPosition _dockPosition = DockPosition.Left;
    private NativeWindowTracker? _windowTracker;
    private TaskbarReplacementSession? _replacementSession;
    private HwndSource? _source;

    public MainWindow()
    {
        InitializeComponent();
        PinnedApps.ItemsSource = _pinnedApps;
        RunningApps.ItemsSource = _runningApps;
        _thumbnailPreview = new WindowThumbnailPreview();
        _previewCloseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _previewCloseTimer.Tick += PreviewCloseTimer_Tick;
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
        _windowTracker.ForegroundWindowChanged += WindowTracker_ForegroundWindowChanged;
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
        Height = 48;
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
            Height = 48;
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
        var result = WindowsTaskbarPinImporter.Import();
        foreach (var app in result.Apps)
        {
            _pinnedApps.Add(app with
            {
                Icon = ApplicationIconProvider.GetForFile(app.ExecutablePath)
            });
        }

        if (result.Error is not null)
        {
            ShowStatus(result.Error);
        }
    }

    private void WindowTracker_WindowsChanged(object? sender, IReadOnlyList<TrackedWindow> windows)
    {
        UpdateNotificationBadges(_pinnedApps, windows);
        if (UpdateRunningApps(windows))
        {
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        }

        if (_thumbnailPreview.IsVisible)
        {
            var currentWindow = windows.FirstOrDefault(window => window.Handle == _thumbnailPreview.SourceHandle);
            if (currentWindow is null)
            {
                _thumbnailPreview.Hide();
            }
        }
    }

    private static void UpdateNotificationBadges(
        ObservableCollection<DockApp> apps,
        IReadOnlyList<TrackedWindow> windows)
    {
        for (var index = 0; index < apps.Count; index++)
        {
            var app = apps[index];
            var hasNotification = windows.Any(window => window.HasNotification
                && string.Equals(window.ExecutablePath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (app.HasNotification != hasNotification)
            {
                apps[index] = app with { HasNotification = hasNotification };
            }
        }
    }

    private bool UpdateRunningApps(IReadOnlyList<TrackedWindow> windows)
    {
        var pinnedPaths = _pinnedApps.Select(app => app.ExecutablePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentApps = _runningApps.ToDictionary(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        var desiredApps = windows
            .Where(window => window.ExecutablePath is not null
                && File.Exists(window.ExecutablePath)
                && !pinnedPaths.Contains(window.ExecutablePath))
            .GroupBy(window => window.ExecutablePath!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var executablePath = group.Key;
                var hasNotification = group.Any(window => window.HasNotification);
                if (currentApps.TryGetValue(executablePath, out var existingApp))
                {
                    return existingApp with { HasNotification = hasNotification };
                }

                return new DockApp(GetApplicationName(executablePath), executablePath)
                {
                    Icon = ApplicationIconProvider.GetForFile(executablePath),
                    HasNotification = hasNotification
                };
            })
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var layoutChanged = !_runningApps.Select(app => app.ExecutablePath)
            .SequenceEqual(desiredApps.Select(app => app.ExecutablePath), StringComparer.OrdinalIgnoreCase);

        for (var desiredIndex = 0; desiredIndex < desiredApps.Count; desiredIndex++)
        {
            var desiredApp = desiredApps[desiredIndex];
            if (desiredIndex < _runningApps.Count
                && string.Equals(_runningApps[desiredIndex].ExecutablePath, desiredApp.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                if (_runningApps[desiredIndex] != desiredApp)
                {
                    _runningApps[desiredIndex] = desiredApp;
                }

                continue;
            }

            var existingIndex = -1;
            for (var index = desiredIndex + 1; index < _runningApps.Count; index++)
            {
                if (string.Equals(_runningApps[index].ExecutablePath, desiredApp.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                _runningApps.Move(existingIndex, desiredIndex);
                if (_runningApps[desiredIndex] != desiredApp)
                {
                    _runningApps[desiredIndex] = desiredApp;
                }
            }
            else
            {
                _runningApps.Insert(desiredIndex, desiredApp);
            }
        }

        while (_runningApps.Count > desiredApps.Count)
        {
            _runningApps.RemoveAt(_runningApps.Count - 1);
        }

        return layoutChanged;
    }

    private void DockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DockApp app })
        {
            return;
        }

        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        OpenDockApp(app);
    }

    private void OpenDockApp(DockApp app)
    {
        var executablePath = app.ExecutablePath;
        var existingWindow = FindWindowForExecutable(executablePath);
        if (existingWindow is not null)
        {
            if (!NativeWindowTracker.Activate(existingWindow.Handle))
            {
                ShowStatus("Uygulama penceresi etkinleştirilemedi. Windows odağı engellemiş olabilir.");
                return;
            }

            ClearStatus();
            return;
        }

        if (!File.Exists(executablePath))
        {
            ShowStatus("Uygulama dosyası bulunamadı. Windows görev çubuğundaki kısayolu güncelleyin.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executablePath)
            {
                Arguments = app.LaunchArguments ?? string.Empty,
                UseShellExecute = true
            });
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

    private void DockApp_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewCloseTimer.Stop();
        if (sender is not Button { Tag: string executablePath })
        {
            return;
        }

        var window = FindWindowForExecutable(executablePath);
        if (window is null)
        {
            _thumbnailPreview.Hide();
            return;
        }

        _thumbnailPreview.ShowFor(window.Handle, (FrameworkElement)sender);
    }

    private void DockApp_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewCloseTimer.Stop();
        _previewCloseTimer.Start();
    }

    private void PreviewCloseTimer_Tick(object? sender, EventArgs e)
    {
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
    }

    private TrackedWindow? FindWindowForExecutable(string executablePath)
    {
        return _windowTracker?.Snapshot.FirstOrDefault(window =>
            string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetApplicationName(string executablePath)
    {
        var description = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
        return string.IsNullOrWhiteSpace(description)
            ? Path.GetFileNameWithoutExtension(executablePath)
            : description.Trim();
    }

    private void WindowTracker_ForegroundWindowChanged(nint windowHandle)
    {
        if (_replacementSession?.IsActive == true)
        {
            _replacementSession.RefreshPosition();
        }
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

        var settingsMenu = FindMenuItem(contextMenu, "DockSettings");
        var positionMenu = settingsMenu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Header?.ToString() == "Konum");
        if (positionMenu is not null)
        {
            foreach (var item in positionMenu.Items.OfType<MenuItem>())
            {
                item.IsChecked = string.Equals(item.Tag as string, _dockPosition.ToString(), StringComparison.Ordinal);
            }
        }

        var pinnedApp = (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as DockApp;
        var isPinnedTarget = pinnedApp is not null;
        var browserArguments = pinnedApp is null ? null : GetBrowserActions(pinnedApp.ExecutablePath);
        SetMenuItemVisibility(contextMenu, "ActivateDockApp", isPinnedTarget);
        SetMenuItemVisibility(contextMenu, "BrowserActions", browserArguments is not null);
        SetMenuItemVisibility(contextMenu, "CloseDockApp", isPinnedTarget);
        SetMenuItemVisibility(contextMenu, "TerminateDockApp", isPinnedTarget);
        SetSeparatorVisibility(contextMenu, "SettingsDivider", isPinnedTarget);

        if (pinnedApp is null)
        {
            return;
        }

        SetMenuCommandParameter(contextMenu, pinnedApp.ExecutablePath);
        if (FindMenuItem(contextMenu, "ActivateDockApp") is { } activateMenuItem)
        {
            activateMenuItem.CommandParameter = pinnedApp;
        }
        if (browserArguments is not null)
        {
            foreach (var item in FindMenuItem(contextMenu, "BrowserActions")?.Items.OfType<MenuItem>() ?? [])
            {
                item.CommandParameter = browserArguments;
            }
        }
    }

    private void DockAppContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: DockApp app })
        {
            OpenDockApp(app);
        }
    }

    private void BrowserAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: BrowserActions actions, Tag: string action })
        {
            return;
        }

        var arguments = action switch
        {
            "NewWindow" => actions.NewWindowArguments,
            "PrivateWindow" => actions.PrivateWindowArguments,
            _ => null
        };
        if (arguments is null)
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(actions.ExecutablePath) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            ClearStatus();
        }
        catch (Win32Exception)
        {
            ShowStatus("Tarayıcı penceresi Windows tarafından başlatılamadı.");
        }
        catch (InvalidOperationException)
        {
            ShowStatus("Tarayıcı penceresi başlatılamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("Tarayıcıyı başlatmak için erişim izni yok.");
        }
        catch (IOException)
        {
            ShowStatus("Tarayıcı dosyasına erişilemedi.");
        }
    }

    private void CloseDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string executablePath })
        {
            return;
        }

        var (processes, inaccessibleCount) = FindProcessesForExecutable(executablePath);
        var closedCount = 0;
        var failedCount = inaccessibleCount;
        foreach (var process in processes)
        {
            try
            {
                if (process.CloseMainWindow())
                {
                    closedCount++;
                }
                else
                {
                    failedCount++;
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while the menu command was running.
            }
            catch (Win32Exception)
            {
                failedCount++;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (closedCount > 0 && failedCount == 0)
        {
            ClearStatus();
        }
        else if (closedCount == 0)
        {
            ShowStatus("Uygulamaya normal kapanma isteği gönderilemedi. Zorla kapatmak için süreç sonlandırma seçeneğini kullanın.");
        }
        else
        {
            ShowStatus("Bazı uygulama süreçlerine normal kapanma isteği gönderilemedi.");
        }
    }

    private void TerminateDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string executablePath })
        {
            return;
        }

        var (processes, inaccessibleCount) = FindProcessesForExecutable(executablePath);
        if (processes.Count == 0)
        {
            ShowStatus(inaccessibleCount == 0
                ? "Uygulamanın çalışan süreci bulunamadı."
                : "Uygulama süreçlerine erişilemiyor; uygulama yöneticisi iznini denetleyin.");
            return;
        }

        var appName = _pinnedApps.FirstOrDefault(app =>
            string.Equals(app.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))?.Name
            ?? Path.GetFileNameWithoutExtension(executablePath);
        var confirmation = MessageBox.Show(
            $"{appName} uygulamasına ait {processes.Count} süreç zorla sonlandırılacak. Kaydedilmemiş çalışmalar kaybolabilir. Devam edilsin?",
            "OpenTaskbar · Süreci sonlandır",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            DisposeProcesses(processes);
            return;
        }

        var terminatedCount = 0;
        var failedCount = inaccessibleCount;
        foreach (var process in processes)
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    failedCount++;
                    continue;
                }

                process.Kill(entireProcessTree: true);
                terminatedCount++;
            }
            catch (InvalidOperationException)
            {
                // The process exited while the menu command was running.
            }
            catch (Win32Exception)
            {
                failedCount++;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (failedCount == 0 && terminatedCount > 0)
        {
            ClearStatus();
        }
        else if (terminatedCount == 0)
        {
            ShowStatus("Uygulama süreçleri sonlandırılamadı. Yetkiyi ve uygulamanın tekrar başlamadığını denetleyin.");
        }
        else
        {
            ShowStatus("Bazı uygulama süreçleri sonlandırılamadı.");
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

    private static MenuItem? FindMenuItem(ItemsControl owner, string tag)
    {
        foreach (var item in owner.Items.OfType<MenuItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                return item;
            }

            var nested = FindMenuItem(item, tag);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SetMenuItemVisibility(ItemsControl owner, string tag, bool visible)
    {
        var item = FindMenuItem(owner, tag);
        if (item is not null)
        {
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetSeparatorVisibility(ItemsControl owner, string tag, bool visible)
    {
        var separator = owner.Items.OfType<Separator>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
        if (separator is not null)
        {
            separator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetMenuCommandParameter(ItemsControl owner, string executablePath)
    {
        foreach (var item in owner.Items.OfType<MenuItem>())
        {
            item.CommandParameter = executablePath;
            SetMenuCommandParameter(item, executablePath);
        }
    }

    private static BrowserActions? GetBrowserActions(string executablePath)
    {
        var browserName = Path.GetFileName(executablePath).ToLowerInvariant();
        var newWindowArgument = browserName == "firefox.exe" ? "-new-window" : "--new-window";
        var privateArgument = browserName switch
        {
            "msedge.exe" => "--inprivate",
            "firefox.exe" => "-private-window",
            "opera.exe" => "--private",
            _ when browserName is "chrome.exe" or "brave.exe" or "vivaldi.exe" => "--incognito",
            _ => null
        };

        return privateArgument is null
            ? null
            : new BrowserActions(executablePath, [newWindowArgument], [privateArgument]);
    }

    private static (List<Process> Processes, int InaccessibleCount) FindProcessesForExecutable(string executablePath)
    {
        var matchingProcesses = new List<Process>();
        var inaccessibleCount = 0;
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    matchingProcesses.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (Win32Exception)
            {
                inaccessibleCount++;
                process.Dispose();
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
        }

        return (matchingProcesses, inaccessibleCount);
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private sealed record BrowserActions(
        string ExecutablePath,
        IReadOnlyList<string> NewWindowArguments,
        IReadOnlyList<string> PrivateWindowArguments);

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
            _windowTracker.ForegroundWindowChanged -= WindowTracker_ForegroundWindowChanged;
            _windowTracker.Dispose();
            _windowTracker = null;
        }

        _previewCloseTimer.Stop();
        _previewCloseTimer.Tick -= PreviewCloseTimer_Tick;
        _thumbnailPreview.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
