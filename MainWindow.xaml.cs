using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PinnedApp> _pinnedApps = [];
    private readonly ObservableCollection<TrackedWindow> _runningWindows = [];
    private readonly PinnedAppStore _pinnedAppStore = new();
    private NativeWindowTracker? _windowTracker;

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
    }

    private void PositionDock()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(900, Math.Max(360, workArea.Width - 24));
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = Math.Max(workArea.Top, workArea.Bottom - Height - 12);
    }

    private void LoadPinnedApps()
    {
        var result = _pinnedAppStore.Load();
        foreach (var app in result.Apps)
        {
            _pinnedApps.Add(app);
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

        EmptyState.Visibility = _runningWindows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Dock'a uygulama sabitle",
            Filter = "Windows uygulamaları (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (_pinnedApps.Any(app => string.Equals(app.ExecutablePath, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus("Bu uygulama dock'ta zaten sabitli.");
            return;
        }

        var app = new PinnedApp(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
        var error = _pinnedAppStore.Save(_pinnedApps.Append(app));
        if (error is not null)
        {
            ShowStatus(error);
            return;
        }

        _pinnedApps.Add(app);
        ClearStatus();
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
        Height = 132;
        PositionDock();
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus()
    {
        StatusText.Text = string.Empty;
        StatusText.Visibility = Visibility.Collapsed;
        Height = 112;
        PositionDock();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_windowTracker is not null)
        {
            _windowTracker.WindowsChanged -= WindowTracker_WindowsChanged;
            _windowTracker.Dispose();
            _windowTracker = null;
        }
    }
}
