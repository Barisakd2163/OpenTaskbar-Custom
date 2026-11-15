using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shell;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

public partial class SettingsWindow : Window
{
    private readonly ApplicationPreferences _preferences;
    private readonly StartupRegistration _startup = new();
    private bool _startupEnabled;
    private bool _startupAvailable;
    private DockSettings _applied;
    private bool _closingApplication;
    private bool _initializing = true;
    private bool _sourceReady;

    public SettingsWindow(ApplicationPreferences preferences)
    {
        _preferences = preferences;
        _applied = preferences.Current;
        InitializeComponent();
        _preferences.Changed += Preferences_Changed;
        Closed += (_, _) =>
        {
            _preferences.Changed -= Preferences_Changed;
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
        _sourceReady = true;
        UpdateBackdrop();
    }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
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
        SurfaceOpacity = Math.Round(OpacitySlider.Value) / 100
    };

    private void Apply_Click(object sender, RoutedEventArgs e)
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
        return selected != (_applied with { PinOrder = selected.PinOrder })
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

}
