using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

public partial class AudioWidget : DockWidget
{
    private AudioEndpointMonitor? _monitor;
    private bool _updating;
    private bool? _lastHasDevice;
    private int _lastLevel = -1;
    private bool _lastMuted;
    private string? _lastDeviceName;
    private string? _lastIconKey;
    private AppLanguage _lastLanguage;
    private System.Windows.Controls.Slider? _flyoutSlider;
    private System.Windows.Controls.TextBlock? _flyoutLevelText;
    private System.Windows.Controls.ContentControl? _flyoutIcon;

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 40;
    public override double FullWidthEstimate => 190;
    public override double CompactWidthEstimate => 54;

    public AudioWidget()
    {
        InitializeComponent();
    }

    protected override void OnCompactChanged(bool compact) => ApplyDensityLayout();
    protected override void OnHoverExpandedChanged(bool expanded) => ApplyDensityLayout();

    private void ApplyDensityLayout()
    {
        var compact = IsCompact && !IsHoverExpanded;
        Card.Width = compact ? 48 : 184;
        Card.Padding = compact ? new Thickness(4, 2, 4, 2) : new Thickness(6, 0, 12, 0);
        DeviceText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        if (compact)
        {
            System.Windows.Controls.Grid.SetRow(MuteButton, 0);
            System.Windows.Controls.Grid.SetRowSpan(MuteButton, 1);
            System.Windows.Controls.Grid.SetColumn(MuteButton, 0);
            System.Windows.Controls.Grid.SetColumnSpan(MuteButton, 3);
            MuteButton.Width = 24;
            MuteButton.Height = 20;
            MuteButton.HorizontalAlignment = HorizontalAlignment.Center;

            System.Windows.Controls.Grid.SetRow(LevelText, 1);
            System.Windows.Controls.Grid.SetColumn(LevelText, 0);
            System.Windows.Controls.Grid.SetColumnSpan(LevelText, 3);
            LevelText.HorizontalAlignment = HorizontalAlignment.Center;
            LevelText.FontSize = 10.5;
        }
        else
        {
            System.Windows.Controls.Grid.SetRow(MuteButton, 0);
            System.Windows.Controls.Grid.SetRowSpan(MuteButton, 2);
            System.Windows.Controls.Grid.SetColumn(MuteButton, 0);
            System.Windows.Controls.Grid.SetColumnSpan(MuteButton, 1);
            MuteButton.Width = 28;
            MuteButton.Height = 28;
            MuteButton.HorizontalAlignment = HorizontalAlignment.Left;

            System.Windows.Controls.Grid.SetRow(LevelText, 0);
            System.Windows.Controls.Grid.SetColumn(LevelText, 2);
            System.Windows.Controls.Grid.SetColumnSpan(LevelText, 1);
            LevelText.HorizontalAlignment = HorizontalAlignment.Right;
            LevelText.ClearValue(System.Windows.Controls.TextBlock.FontSizeProperty);
        }
    }

    public override UIElement? BuildHoverFlyoutContent()
    {
        var hasDevice = _monitor?.HasDevice == true;
        var level = hasDevice ? (int)Math.Round(_monitor!.Volume * 100) : 0;
        var muted = _monitor?.Muted == true;
        var deviceName = _monitor?.DeviceName ?? UiText.Get("NoAudioDevice");

        var root = new System.Windows.Controls.StackPanel { Width = 228 };
        root.Children.Add(DockFlyout.Heading(deviceName));

        var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 4, 0, 2) };
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

        var icon = new System.Windows.Controls.ContentControl
        {
            Width = 16,
            Height = 16,
            Content = FindResource(muted || level == 0 ? "IconVolumeMute" : level < 50 ? "IconVolumeLow" : "IconVolume")
        };
        icon.SetResourceReference(StyleProperty, "WidgetIcon");
        var muteBtn = new System.Windows.Controls.Button
        {
            Width = 28,
            Height = 28,
            Content = icon,
            IsEnabled = hasDevice
        };
        muteBtn.SetResourceReference(StyleProperty, "WidgetIconButton");
        muteBtn.Click += MuteButton_Click;

        var slider = new System.Windows.Controls.Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = level,
            IsEnabled = hasDevice,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        slider.SetResourceReference(StyleProperty, "WidgetSlider");
        slider.ValueChanged += (_, args) =>
        {
            if (_updating || _monitor is null) return;
            if (_monitor.SetVolume((float)(args.NewValue / 100.0))) Render();
            else Report(UiText.Get("ErrorVolumeChange"));
        };

        var levelText = new System.Windows.Controls.TextBlock
        {
            Text = hasDevice ? (muted ? "—" : UiText.Format("Percent", level)) : string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 34,
            TextAlignment = TextAlignment.Right
        };
        levelText.SetResourceReference(StyleProperty, "WidgetCaption");

        System.Windows.Controls.Grid.SetColumn(muteBtn, 0);
        System.Windows.Controls.Grid.SetColumn(slider, 1);
        System.Windows.Controls.Grid.SetColumn(levelText, 2);
        row.Children.Add(muteBtn);
        row.Children.Add(slider);
        row.Children.Add(levelText);
        root.Children.Add(row);

        _flyoutIcon = icon;
        _flyoutSlider = slider;
        _flyoutLevelText = levelText;
        root.Unloaded += (_, _) =>
        {
            _flyoutIcon = null;
            _flyoutSlider = null;
            _flyoutLevelText = null;
        };
        root.MouseWheel += Card_MouseWheel;
        return root;
    }

    protected override void OnActivated()
    {
        UiText.Current.PropertyChanged += UiText_PropertyChanged;
        try
        {
            _monitor = new AudioEndpointMonitor();
            _monitor.VolumeChanged += Monitor_VolumeChanged;
            _monitor.DefaultDeviceChanged += Monitor_DefaultDeviceChanged;
        }
        catch (COMException ex)
        {
            AppLog.Warning($"Core Audio unavailable: {ex.Message}");
            _monitor = null;
        }

        InvalidateCache();
        Render();
    }

    protected override void OnDeactivated()
    {
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;
        if (_monitor is null) return;
        _monitor.VolumeChanged -= Monitor_VolumeChanged;
        _monitor.DefaultDeviceChanged -= Monitor_DefaultDeviceChanged;
        _monitor.Dispose();
        _monitor = null;
    }

    private void UiText_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void InvalidateCache()
    {
        _lastHasDevice = null;
        _lastLevel = -1;
        _lastIconKey = null;
        _lastDeviceName = null;
    }

    // Both events arrive on Core Audio worker threads.
    private void Monitor_VolumeChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (IsActive) Render();
    });

    private void Monitor_DefaultDeviceChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!IsActive || _monitor is null) return;
        _monitor.BindDefaultDevice();
        AppLog.Info($"Default audio device: {_monitor.DeviceName}");
        Render();
    });

    private void Render()
    {
        var hasDevice = _monitor?.HasDevice == true;
        var lang = UiText.Current.Language;
        if (!hasDevice)
        {
            if (_lastHasDevice == false && _lastLanguage == lang) return;
            _lastHasDevice = false;
            _lastLanguage = lang;
            _lastLevel = -1;
            MuteButton.IsEnabled = false;
            VolumeSlider.IsEnabled = false;
            DeviceText.Text = UiText.Get("NoAudioDevice");
            Card.ToolTip = DeviceText.Text;
            LevelText.Text = string.Empty;
            SetVolumeIcon("IconVolumeMute");
            return;
        }

        var level = (int)Math.Round(_monitor!.Volume * 100);
        var muted = _monitor.Muted;
        var deviceName = _monitor.DeviceName ?? UiText.Get("Volume");
        if (_lastHasDevice == true && _lastLevel == level && _lastMuted == muted
            && _lastDeviceName == deviceName && _lastLanguage == lang)
        {
            return;
        }

        _lastHasDevice = true;
        _lastLevel = level;
        _lastMuted = muted;
        _lastDeviceName = deviceName;
        _lastLanguage = lang;

        MuteButton.IsEnabled = true;
        VolumeSlider.IsEnabled = true;
        DeviceText.Text = deviceName;
        Card.ToolTip = deviceName;
        LevelText.Text = muted ? "—" : UiText.Format("Percent", level);
        var iconKey = muted || level == 0 ? "IconVolumeMute" : level < 50 ? "IconVolumeLow" : "IconVolume";
        SetVolumeIcon(iconKey);
        _updating = true;
        VolumeSlider.Value = level;
        if (_flyoutSlider is not null) _flyoutSlider.Value = level;
        if (_flyoutLevelText is not null) _flyoutLevelText.Text = LevelText.Text;
        if (_flyoutIcon is not null) _flyoutIcon.Content = FindResource(iconKey);
        _updating = false;
    }

    private void SetVolumeIcon(string key)
    {
        if (_lastIconKey == key) return;
        _lastIconKey = key;
        VolumeIcon.Content = FindResource(key);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _monitor is null) return;
        if (_monitor.SetVolume((float)(e.NewValue / 100.0))) Render();
        else Report(UiText.Get("ErrorVolumeChange"));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor is null) return;
        if (_monitor.ToggleMute()) Render();
        else Report(UiText.Get("ErrorVolumeChange"));
    }

    private void Card_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_monitor?.HasDevice != true) return;
        if (_monitor.SetVolume(Math.Clamp(_monitor.Volume + Math.Sign(e.Delta) * 0.02f, 0f, 1f))) Render();
        else Report(UiText.Get("ErrorVolumeChange"));
        e.Handled = true;
    }
}
