using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

public partial class SettingsWindow : Window
{
    private readonly ApplicationPreferences _preferences;
    private readonly StartupRegistration _startup = new();
    private readonly ObservableCollection<DockWidgetOption> _widgetOptions = [];
    private readonly DockWidgetService _widgetPreviewService = new(TimeProvider.System);
    private readonly DispatcherTimer _widgetPreviewTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _startupEnabled;
    private bool _startupAvailable;
    private DockSettings _applied;
    private bool _closingApplication;
    private bool _initializing = true;
    private bool _sourceReady;
    private HwndSource? _source;

    public SettingsWindow(ApplicationPreferences preferences)
    {
        _preferences = preferences;
        _applied = preferences.Current;
        InitializeComponent();
        foreach (var option in _widgetPreviewService.CreateOptions(_applied.InstalledWidgets, _applied.Widgets)) _widgetOptions.Add(option);
        WidgetOptionsList.ItemsSource = _widgetOptions;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_widgetOptions).Filter = item =>
            item is DockWidgetOption option && option.MatchesSearch(WidgetSearch.Text.Trim(),
                CultureInfo.GetCultureInfo(_preferences.Current.Language == AppLanguage.Turkish ? "tr-TR" : "en-US"));
        _preferences.Changed += Preferences_Changed;
        _widgetPreviewTimer.Tick += WidgetPreviewTimer_Tick;
        IsVisibleChanged += Settings_IsVisibleChanged;
        Closed += (_, _) =>
        {
            _widgetPreviewTimer.Stop();
            _widgetPreviewTimer.Tick -= WidgetPreviewTimer_Tick;
            IsVisibleChanged -= Settings_IsVisibleChanged;
            _preferences.Changed -= Preferences_Changed;
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
            ThemeManager.SetSurfaceOpacity(_preferences.Current.SurfaceOpacity);
        };
        DarkTheme.IsChecked = _applied.Theme == AppTheme.Dark;
        LightTheme.IsChecked = _applied.Theme == AppTheme.Light;
        TurkishLanguage.IsChecked = _applied.Language == AppLanguage.Turkish;
        EnglishLanguage.IsChecked = _applied.Language == AppLanguage.English;
        LeftPosition.IsChecked = _applied.Position == DockPosition.Left;
        CenterPosition.IsChecked = _applied.Position == DockPosition.Center;
        RightPosition.IsChecked = _applied.Position == DockPosition.Right;
        AnimationsToggle.IsChecked = _applied.AnimationsEnabled;
        PreviewsToggle.IsChecked = _applied.PreviewsEnabled;
        BadgesToggle.IsChecked = _applied.NotificationBadgesEnabled;
        AutoHideDisabled.IsChecked = _applied.AutoHide == AutoHideMode.Disabled;
        AutoHideSmart.IsChecked = _applied.AutoHide == AutoHideMode.Smart;
        AutoHideAlways.IsChecked = _applied.AutoHide == AutoHideMode.Always;
        OpacitySlider.Value = _applied.SurfaceOpacity * 100;
        UpdateOpacityLabel();
        var startup = _startup.Read();
        _startupEnabled = startup.Enabled;
        _startupAvailable = startup.Error is null;
        StartupToggle.IsEnabled = _startupAvailable;
        if (startup.Error is not null)
        {
            StartupToggle.ToolTip = startup.Error;
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(StartupToggle, true);
        }
        StartupToggle.IsChecked = _startupEnabled;
        var errors = new[] { preferences.LoadError, startup.Error }.OfType<string>();
        var message = string.Join(" ", errors.Select(UiText.LocalizeError));
        if (message.Length > 0) ShowResult(message, error: true);
        var workArea = SystemParameters.WorkArea;
        MinHeight = Math.Min(MinHeight, workArea.Height);
        MinWidth = Math.Min(MinWidth, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(PreferenceEdited));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(PreferenceEdited));
        _initializing = false;
        UpdateApplyState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
        _sourceReady = true;
        UpdateBackdrop();
    }

    private nint WindowProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024 || lParam == 0) return 0; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            Trace.TraceWarning("Settings monitor work area unavailable: {0}", Marshal.GetLastWin32Error());
            return 0;
        }
        var sizing = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        sizing.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        sizing.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var dpi = GetDpiForWindow(window);
        var scale = dpi > 0 ? dpi / 96d : 1;
        sizing.MinTrackSize = new NativePoint((int)Math.Ceiling(MinWidth * scale), (int)Math.Ceiling(MinHeight * scale));
        Marshal.StructureToPtr(sizing, lParam, false);
        handled = true;
        return 0;
    }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        if (_applied.Language != _preferences.Current.Language)
        {
            var localizedOptions = _widgetPreviewService.CreateOptions([], []).ToDictionary(option => option.Id, StringComparer.Ordinal);
            foreach (var option in _widgetOptions)
            {
                var localized = localizedOptions[option.Id];
                option.UpdateText(localized.Label, localized.Description);
            }
            RefreshWidgetSearch();
            RefreshWidgetPreviews();
        }
        ThemeManager.SetSurfaceOpacity(Math.Round(OpacitySlider.Value) / 100);
        UpdateOpacityLabel();
        UpdateBackdrop();
    }

    private void UpdateBackdrop()
    {
        if (!_sourceReady) return;
        var glass = SettingsBackdrop.TryEnable(this, _preferences.Current.Theme);
        SettingsFrame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, glass ? "SettingsGlassMaterial" : "SettingsBackground");
        if (glass)
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(-1);
            SettingsFrame.ClearValue(System.Windows.Controls.Border.CornerRadiusProperty);
        }
        else
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(0);
            SettingsFrame.CornerRadius = new CornerRadius(0);
        }
    }

    private DockSettings ReadSelection() => _preferences.Current with
    {
        Theme = LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.Dark,
        Language = EnglishLanguage.IsChecked == true ? AppLanguage.English : AppLanguage.Turkish,
        Position = RightPosition.IsChecked == true ? DockPosition.Right : CenterPosition.IsChecked == true ? DockPosition.Center : DockPosition.Left,
        AnimationsEnabled = AnimationsToggle.IsChecked == true,
        PreviewsEnabled = PreviewsToggle.IsChecked == true,
        NotificationBadgesEnabled = BadgesToggle.IsChecked == true,
        AutoHide = AutoHideAlways.IsChecked == true ? AutoHideMode.Always : AutoHideSmart.IsChecked == true ? AutoHideMode.Smart : AutoHideMode.Disabled,
        SurfaceOpacity = Math.Round(OpacitySlider.Value) / 100,
        Widgets = _widgetOptions.Where(option => option.IsInstalled && option.IsEnabled).Select(option => option.Id).ToArray(),
        InstalledWidgets = _widgetOptions.Where(option => option.IsInstalled).Select(option => option.Id).ToArray()
    };

    private void WidgetSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_initializing) return;
        RefreshWidgetSearch();
    }

    private void RefreshWidgetSearch()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_widgetOptions);
        view.Refresh();
        WidgetNoResults.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshWidgetPreviews() => DockWidgetService.Refresh(
        _widgetOptions.Select(option => option.Preview), _preferences.Current.Language);

    private void WidgetPreviewTimer_Tick(object? sender, EventArgs e) => RefreshWidgetPreviews();

    private void Settings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateWidgetPreviewRefresh();

    private void SettingsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_initializing && ReferenceEquals(e.Source, SettingsTabs)) UpdateWidgetPreviewRefresh();
    }

    private void UpdateWidgetPreviewRefresh()
    {
        if (IsVisible && WidgetsTab.IsSelected)
        {
            RefreshWidgetPreviews();
            _widgetPreviewTimer.Start();
        }
        else _widgetPreviewTimer.Stop();
    }

    private void WidgetInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: DockWidgetOption option }) return;
        if (option.IsInstalled)
        {
            option.IsInstalled = false;
            option.IsEnabled = false;
        }
        else
        {
            option.IsInstalled = true;
            option.IsEnabled = true;
        }
        SaveSettings();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void SaveSettings()
    {
        var startupChanged = _startupAvailable && StartupToggle.IsChecked != _startupEnabled;
        if (startupChanged && _startup.SetEnabled(StartupToggle.IsChecked == true) is { } startupError)
        {
            ShowResult(startupError, error: true);
            return;
        }
        var settings = ReadSelection();
        if (_preferences.Save(settings) is { } error)
        {
            error = UiText.LocalizeError(error);
            if (startupChanged && _startup.SetEnabled(_startupEnabled) is not null)
                error += " " + UiText.Get("StartupRestoreError");
            ShowResult(error, error: true);
            return;
        }
        _applied = settings;
        _startupEnabled = StartupToggle.IsChecked == true;
        ShowResult(UiText.Get("SettingsSaved"), error: false);
        UpdateApplyState();
    }

    private void ShowResult(string message, bool error)
    {
        SaveStatus.Text = UiText.LocalizeError(message);
        SaveStatus.Visibility = Visibility.Visible;
        SaveStatus.SetResourceReference(ForegroundProperty, error ? "DangerText" : "TextSecondary");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingApplication || Application.Current.Dispatcher.HasShutdownStarted) return;
        if (!HasEdits()) return;
        e.Cancel = MessageBox.Show(this, UiText.Get("DiscardChanges"), UiText.Get("DiscardTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
    }

    private bool HasEdits()
    {
        var selected = ReadSelection();
        // Pin order may change behind this window and is not part of its edit buffer.
        return selected != (_applied with
            {
                PinOrder = selected.PinOrder,
                Widgets = selected.Widgets,
                InstalledWidgets = selected.InstalledWidgets
            })
            || !(selected.Widgets ?? []).SequenceEqual(_applied.Widgets ?? [], StringComparer.Ordinal)
            || !(selected.InstalledWidgets ?? []).SequenceEqual(_applied.InstalledWidgets ?? [], StringComparer.Ordinal)
            || (_startupAvailable && StartupToggle.IsChecked != _startupEnabled);
    }

    private void PreferenceEdited(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SaveStatus.Visibility = Visibility.Collapsed;
        UpdateApplyState();
    }

    private void UpdateApplyState() => ApplyButton.IsEnabled = HasEdits() || _preferences.LoadError is not null;

    private void UpdateOpacityLabel() => OpacityValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(OpacitySlider.Value));

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateOpacityLabel();
        ThemeManager.SetSurfaceOpacity(Math.Round(e.NewValue) / 100);
        PreferenceEdited(sender, e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void CloseForShutdown()
    {
        _closingApplication = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);

}
