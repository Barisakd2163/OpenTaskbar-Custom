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
using OpenTaskbar.Widgets;

namespace OpenTaskbar;

public sealed class WidgetOrderItem(string id, string label)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
}

public partial class SettingsWindow : Window
{
    private readonly ApplicationPreferences _preferences;
    private readonly StartupRegistration _startup = new();
    private readonly ObservableCollection<DockWidgetOption> _widgetOptions = [];
    private readonly ObservableCollection<DockWidgetOption> _installedWidgetOptions = [];
    private readonly ObservableCollection<WidgetOrderItem> _widgetOrderItems = [];
    private readonly DockWidgetService _widgetPreviewService = new(TimeProvider.System);
    private readonly DispatcherTimer _widgetPreviewTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _startupEnabled;
    private bool _startupAvailable;
    private bool _widgetOrderChanged;
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
        foreach (var option in _widgetPreviewService.CreateOptions(_applied.InstalledWidgets, _applied.Widgets))
        {
            option.PropertyChanged += WidgetOption_PropertyChanged;
            _widgetOptions.Add(option);
        }
        WidgetOptionsList.ItemsSource = _widgetOptions;
        InstalledWidgetsList.ItemsSource = _installedWidgetOptions;
        WidgetOrderList.ItemsSource = _widgetOrderItems;
        RefreshInstalledWidgetsList();
        LoadWidgetOrder(_applied.WidgetOrder);
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
            (Application.Current.MainWindow as MainWindow)?.SetSizePreview(null, null);
        };
        DarkTheme.IsChecked = _applied.Theme == AppTheme.Dark;
        LightTheme.IsChecked = _applied.Theme == AppTheme.Light;
        TurkishLanguage.IsChecked = _applied.Language == AppLanguage.Turkish;
        EnglishLanguage.IsChecked = _applied.Language == AppLanguage.English;
        SelectAlignment(_applied);
        AnimationsToggle.IsChecked = _applied.AnimationsEnabled;
        PreviewsToggle.IsChecked = _applied.PreviewsEnabled;
        BadgesToggle.IsChecked = _applied.NotificationBadgesEnabled;
        AutoHideDisabled.IsChecked = _applied.AutoHide == AutoHideMode.Disabled;
        AutoHideSmart.IsChecked = _applied.AutoHide == AutoHideMode.Smart;
        AutoHideAlways.IsChecked = _applied.AutoHide == AutoHideMode.Always;
        OpacitySlider.Value = _applied.SurfaceOpacity * 100;
        UpdateOpacityLabel();
        MagnificationToggle.IsChecked = _applied.MagnificationEnabled;
        CompactSize.IsChecked = _applied.Size == DockSize.Compact;
        MediumSize.IsChecked = _applied.Size == DockSize.Medium;
        LargeSize.IsChecked = _applied.Size == DockSize.Large;
        DockScaleSlider.Value = Math.Round(_applied.DockScale * 100);
        UpdateDockScaleLabel();
        FocusMinutesSlider.Value = _applied.FocusMinutes;
        BreakMinutesSlider.Value = _applied.FocusBreakMinutes;
        FocusSessionsSlider.Value = _applied.FocusSessions;
        LoadFeatureSelection(_applied);
        UpdateWidgetOptionState();
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
        UpdateDockScaleLabel();
        UpdateWidgetOptionState();
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

    private DockSettings ReadSelection()
    {
        var enabledWidgets = _widgetOptions.Where(option => option.IsInstalled && option.IsEnabled).Select(option => option.Id).ToArray();
        var installedWidgets = _widgetOptions.Where(option => option.IsInstalled).Select(option => option.Id).ToArray();
        bool IsEnabled(string id) => enabledWidgets.Contains(id, StringComparer.OrdinalIgnoreCase);
        return _preferences.Current with
        {
            Theme = LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.Dark,
            Language = EnglishLanguage.IsChecked == true ? AppLanguage.English : AppLanguage.Turkish,
            // Full width keeps the last floating alignment so switching back restores it.
            Position = RightPosition.IsChecked == true ? DockPosition.Right : LeftPosition.IsChecked == true ? DockPosition.Left
                : CenterPosition.IsChecked == true ? DockPosition.Center : _preferences.Current.Position,
            AnimationsEnabled = AnimationsToggle.IsChecked == true,
            PreviewsEnabled = PreviewsToggle.IsChecked == true,
            NotificationBadgesEnabled = BadgesToggle.IsChecked == true,
            AutoHide = AutoHideAlways.IsChecked == true ? AutoHideMode.Always : AutoHideSmart.IsChecked == true ? AutoHideMode.Smart : AutoHideMode.Disabled,
            SurfaceOpacity = Math.Round(OpacitySlider.Value) / 100,
            Widgets = enabledWidgets,
            InstalledWidgets = installedWidgets,
            MagnificationEnabled = MagnificationToggle.IsChecked == true,
            Layout = FullWidthLayout.IsChecked == true ? DockLayout.FullWidth : DockLayout.Floating,
            Size = LargeSize.IsChecked == true ? DockSize.Large : MediumSize.IsChecked == true ? DockSize.Medium : DockSize.Compact,
            DockScale = Math.Round(DockScaleSlider.Value) / 100,
            ShowClock = IsEnabled("Clock"),
            ShowMedia = IsEnabled("Media"),
            ShowSystemStats = IsEnabled("SystemStats"),
            ShowAudio = IsEnabled("Audio"),
            ShowQuickSettings = IsEnabled("QuickSettings"),
            ShowFocus = IsEnabled("Focus"),
            FocusMinutes = (int)Math.Round(FocusMinutesSlider.Value),
            FocusBreakMinutes = (int)Math.Round(BreakMinutesSlider.Value),
            FocusSessions = (int)Math.Round(FocusSessionsSlider.Value),
            ShowRecycleBin = RecycleBinToggle.IsChecked == true,
            ShowOnAllMonitors = AllMonitorsToggle.IsChecked == true,
            CurrentDesktopOnly = CurrentDesktopToggle.IsChecked == true,
            CheckUpdates = UpdatesToggle.IsChecked == true,
            Edge = LeftEdge.IsChecked == true ? DockEdge.Left : RightEdge.IsChecked == true ? DockEdge.Right : DockEdge.Bottom,
            ShowVirtualDesktops = IsEnabled("Desktops"),
            ShowTray = IsEnabled("Tray"),
            ShowWeather = IsEnabled("Weather"),
            WeatherCity = WeatherCityBox.Text.Trim(),
            LauncherHotkey = LauncherAltSpace.IsChecked == true ? HotkeyChoice.AltSpace
                : LauncherCtrlSpace.IsChecked == true ? HotkeyChoice.CtrlSpace
                : LauncherCtrlAltSpace.IsChecked == true ? HotkeyChoice.CtrlAltSpace : HotkeyChoice.Disabled,
            DockHotkey = DockCtrlAltD.IsChecked == true ? HotkeyChoice.CtrlAltD : DockWinAltD.IsChecked == true ? HotkeyChoice.WinAltD : HotkeyChoice.Disabled,
            StartAction = StartOpensLauncher.IsChecked == true ? StartButtonAction.Launcher : StartButtonAction.WindowsStart,
            StartPlacement = StartLast.IsChecked == true ? StartButtonPlacement.End : StartHidden.IsChecked == true ? StartButtonPlacement.Hidden : StartButtonPlacement.Start,
            WheelVolume = WheelVolumeToggle.IsChecked == true,
            WidgetOrder = _widgetOrderChanged ? _widgetOrderItems.Select(w => w.Id).ToArray() : _applied.WidgetOrder
        };
    }

    private void RefreshInstalledWidgetsList()
    {
        _installedWidgetOptions.Clear();
        foreach (var option in _widgetOptions.Where(option => option.IsInstalled))
        {
            _installedWidgetOptions.Add(option);
        }
    }

    private void WidgetOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_initializing) return;
        if (e.PropertyName is nameof(DockWidgetOption.IsInstalled) or nameof(DockWidgetOption.IsEnabled))
        {
            RefreshInstalledWidgetsList();
            LoadWidgetOrder(_widgetOrderItems.Select(w => w.Id).ToArray());
            PreferenceEdited(this, new RoutedEventArgs());
        }
    }

    private void LoadWidgetOrder(IReadOnlyList<string>? savedOrder)
    {
        _widgetOrderItems.Clear();
        var order = (savedOrder ?? []).ToList();
        foreach (var def in WidgetCoordinator.DefaultWidgetOrder)
        {
            if (!order.Contains(def, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(def);
            }
        }

        foreach (var id in order)
        {
            var labelKey = WidgetCoordinator.GetLabelKey(id);
            if (labelKey is not null)
            {
                _widgetOrderItems.Add(new WidgetOrderItem(id, UiText.Get(labelKey)));
            }
        }
    }

    private void WidgetMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            var list = _widgetOrderItems.ToList();
            var index = list.FindIndex(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index > 0)
            {
                _widgetOrderChanged = true;
                _widgetOrderItems.Move(index, index - 1);
                PreferenceEdited(sender, e);
            }
        }
    }

    private void WidgetMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            var list = _widgetOrderItems.ToList();
            var index = list.FindIndex(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < _widgetOrderItems.Count - 1)
            {
                _widgetOrderChanged = true;
                _widgetOrderItems.Move(index, index + 1);
                PreferenceEdited(sender, e);
            }
        }
    }

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
        _initializing = true;
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
        _initializing = false;
        RefreshInstalledWidgetsList();
        LoadWidgetOrder(_widgetOrderItems.Select(w => w.Id).ToArray());
        UpdateWidgetOptionState();
        SaveSettings();
    }

    private void SelectAlignment(DockSettings settings)
    {
        var fullWidth = settings.Layout == DockLayout.FullWidth;
        FullWidthLayout.IsChecked = fullWidth;
        LeftPosition.IsChecked = !fullWidth && settings.Position == DockPosition.Left;
        CenterPosition.IsChecked = !fullWidth && settings.Position == DockPosition.Center;
        RightPosition.IsChecked = !fullWidth && settings.Position == DockPosition.Right;
    }

    private void LoadFeatureSelection(DockSettings settings)
    {
        RecycleBinToggle.IsChecked = settings.ShowRecycleBin;
        AllMonitorsToggle.IsChecked = settings.ShowOnAllMonitors;
        CurrentDesktopToggle.IsChecked = settings.CurrentDesktopOnly;
        UpdatesToggle.IsChecked = settings.CheckUpdates;
        BottomEdge.IsChecked = settings.Edge == DockEdge.Bottom;
        LeftEdge.IsChecked = settings.Edge == DockEdge.Left;
        RightEdge.IsChecked = settings.Edge == DockEdge.Right;
        WeatherCityBox.Text = settings.WeatherCity;
        LauncherAltSpace.IsChecked = settings.LauncherHotkey == HotkeyChoice.AltSpace;
        LauncherCtrlSpace.IsChecked = settings.LauncherHotkey == HotkeyChoice.CtrlSpace;
        LauncherCtrlAltSpace.IsChecked = settings.LauncherHotkey == HotkeyChoice.CtrlAltSpace;
        LauncherOff.IsChecked = settings.LauncherHotkey is not (HotkeyChoice.AltSpace or HotkeyChoice.CtrlSpace or HotkeyChoice.CtrlAltSpace);
        DockCtrlAltD.IsChecked = settings.DockHotkey == HotkeyChoice.CtrlAltD;
        DockWinAltD.IsChecked = settings.DockHotkey == HotkeyChoice.WinAltD;
        DockOff.IsChecked = settings.DockHotkey is not (HotkeyChoice.CtrlAltD or HotkeyChoice.WinAltD);
        StartOpensWindows.IsChecked = settings.StartAction == StartButtonAction.WindowsStart;
        StartOpensLauncher.IsChecked = settings.StartAction == StartButtonAction.Launcher;
        StartFirst.IsChecked = settings.StartPlacement == StartButtonPlacement.Start;
        StartLast.IsChecked = settings.StartPlacement == StartButtonPlacement.End;
        StartHidden.IsChecked = settings.StartPlacement == StartButtonPlacement.Hidden;
        WheelVolumeToggle.IsChecked = settings.WheelVolume;
    }

    private void WeatherCityBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => PreferenceEdited(sender, e);

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "OpenTaskbar-settings.json",
            Filter = "OpenTaskbar (*.json)|*.json",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var error = SettingsBackup.Export(dialog.FileName, _preferences.Current);
        ShowResult(error ?? UiText.Get("ExportDone"), error is not null);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "OpenTaskbar (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var (settings, pins, error) = SettingsBackup.Read(dialog.FileName);
        if (settings is null)
        {
            ShowResult(error ?? UiText.Get("ErrorSettingsFileInvalid"), error: true);
            return;
        }

        var added = (Application.Current.MainWindow as MainWindow)?.ImportPins(pins) ?? 0;
        if (_preferences.Save(settings with { PinOrder = settings.PinOrder ?? _preferences.Current.PinOrder }) is { } saveError)
        {
            ShowResult(saveError, error: true);
            return;
        }

        _applied = _preferences.Current;
        _initializing = true;
        LoadAllSelections(_applied);
        _initializing = false;
        UpdateWidgetOptionState();
        UpdateApplyState();
        ShowResult(UiText.Format("ImportDone", added), error: false);
    }

    private void LoadAllSelections(DockSettings settings)
    {
        DarkTheme.IsChecked = settings.Theme == AppTheme.Dark;
        LightTheme.IsChecked = settings.Theme == AppTheme.Light;
        TurkishLanguage.IsChecked = settings.Language == AppLanguage.Turkish;
        EnglishLanguage.IsChecked = settings.Language == AppLanguage.English;
        SelectAlignment(settings);
        AnimationsToggle.IsChecked = settings.AnimationsEnabled;
        PreviewsToggle.IsChecked = settings.PreviewsEnabled;
        BadgesToggle.IsChecked = settings.NotificationBadgesEnabled;
        AutoHideDisabled.IsChecked = settings.AutoHide == AutoHideMode.Disabled;
        AutoHideSmart.IsChecked = settings.AutoHide == AutoHideMode.Smart;
        AutoHideAlways.IsChecked = settings.AutoHide == AutoHideMode.Always;
        OpacitySlider.Value = settings.SurfaceOpacity * 100;
        UpdateOpacityLabel();
        MagnificationToggle.IsChecked = settings.MagnificationEnabled;
        CompactSize.IsChecked = settings.Size == DockSize.Compact;
        MediumSize.IsChecked = settings.Size == DockSize.Medium;
        LargeSize.IsChecked = settings.Size == DockSize.Large;
        DockScaleSlider.Value = Math.Round(settings.DockScale * 100);
        UpdateDockScaleLabel();
        foreach (var option in _widgetOptions)
        {
            option.IsInstalled = settings.InstalledWidgets?.Contains(option.Id, StringComparer.Ordinal) == true;
            option.IsEnabled = settings.Widgets?.Contains(option.Id, StringComparer.Ordinal) == true;
        }
        RefreshInstalledWidgetsList();
        FocusMinutesSlider.Value = settings.FocusMinutes;
        BreakMinutesSlider.Value = _applied.FocusBreakMinutes;
        FocusSessionsSlider.Value = settings.FocusSessions;
        LoadFeatureSelection(settings);
        LoadWidgetOrder(settings.WidgetOrder);
    }

    private void UpdateWidgetOptionState()
    {
        bool IsInstalled(string id) => _widgetOptions.Any(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase) && o.IsInstalled);
        InstalledWidgetsSection.Visibility = _installedWidgetOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WidgetOrderSection.Visibility = _widgetOrderItems.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        FocusOptions.Visibility = IsInstalled("Focus") ? Visibility.Visible : Visibility.Collapsed;
        WeatherOptions.Visibility = IsInstalled("Weather") ? Visibility.Visible : Visibility.Collapsed;
        FocusMinutesValue.Text = UiText.Format("MinutesFormat", (int)Math.Round(FocusMinutesSlider.Value));
        BreakMinutesValue.Text = UiText.Format("MinutesFormat", (int)Math.Round(BreakMinutesSlider.Value));
        FocusSessionsValue.Text = ((int)Math.Round(FocusSessionsSlider.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void FocusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        PreferenceEdited(sender, e);
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
        _widgetOrderChanged = false;
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
        // Pin order, stacks and profiles change from the dock behind this window and are not part of its edit buffer.
        return selected != (_applied with
            {
                PinOrder = selected.PinOrder,
                Widgets = selected.Widgets,
                InstalledWidgets = selected.InstalledWidgets,
                WidgetOrder = selected.WidgetOrder,
                StackFolders = selected.StackFolders,
                Profiles = selected.Profiles,
                ActiveProfile = selected.ActiveProfile
            })
            || !(selected.Widgets ?? []).SequenceEqual(_applied.Widgets ?? [], StringComparer.Ordinal)
            || !(selected.InstalledWidgets ?? []).SequenceEqual(_applied.InstalledWidgets ?? [], StringComparer.Ordinal)
            || (_widgetOrderChanged && !(selected.WidgetOrder ?? []).SequenceEqual(_applied.WidgetOrder ?? WidgetCoordinator.DefaultWidgetOrder, StringComparer.OrdinalIgnoreCase))
            || (_startupAvailable && StartupToggle.IsChecked != _startupEnabled);
    }

    private void PreferenceEdited(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SaveStatus.Visibility = Visibility.Collapsed;
        var selectedSize = LargeSize.IsChecked == true ? DockSize.Large : MediumSize.IsChecked == true ? DockSize.Medium : DockSize.Compact;
        (Application.Current.MainWindow as MainWindow)?.SetSizePreview(selectedSize, Math.Round(DockScaleSlider.Value) / 100);
        UpdateWidgetOptionState();
        UpdateApplyState();
    }

    private void UpdateApplyState() => ApplyButton.IsEnabled = HasEdits() || _preferences.LoadError is not null;

    private void UpdateOpacityLabel() => OpacityValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(OpacitySlider.Value));

    private void UpdateDockScaleLabel() => DockScaleValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(DockScaleSlider.Value));

    private void DockScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateDockScaleLabel();
        PreferenceEdited(sender, e);
    }

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
