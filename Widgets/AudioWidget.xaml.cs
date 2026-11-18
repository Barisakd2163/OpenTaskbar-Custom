using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

public partial class AudioWidget : DockWidget
{
    private readonly DockFlyout _flyout = new();
    private AudioEndpointMonitor.SharedHandle? _monitor;
    private bool _updating;
    private bool _updatingMic;
    private bool? _lastHasDevice;
    private int _lastLevel = -1;
    private bool _lastMuted;
    private string? _lastDeviceName;
    private string? _lastIconKey;
    private AppLanguage _lastLanguage;

    private Border? _flyoutContainer;
    private Slider? _flyoutSlider;
    private TextBlock? _flyoutLevelText;
    private ContentControl? _flyoutIcon;
    private Slider? _flyoutMicSlider;
    private TextBlock? _flyoutMicLevelText;
    private ContentControl? _flyoutMicIcon;

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
            Grid.SetRow(MuteButton, 0);
            Grid.SetRowSpan(MuteButton, 1);
            Grid.SetColumn(MuteButton, 0);
            Grid.SetColumnSpan(MuteButton, 3);
            MuteButton.Width = 24;
            MuteButton.Height = 20;
            MuteButton.HorizontalAlignment = HorizontalAlignment.Center;

            Grid.SetRow(LevelText, 1);
            Grid.SetColumn(LevelText, 0);
            Grid.SetColumnSpan(LevelText, 3);
            LevelText.HorizontalAlignment = HorizontalAlignment.Center;
            LevelText.FontSize = 10.5;
        }
        else
        {
            Grid.SetRow(MuteButton, 0);
            Grid.SetRowSpan(MuteButton, 2);
            Grid.SetColumn(MuteButton, 0);
            Grid.SetColumnSpan(MuteButton, 1);
            MuteButton.Width = 28;
            MuteButton.Height = 28;
            MuteButton.HorizontalAlignment = HorizontalAlignment.Left;

            Grid.SetRow(LevelText, 0);
            Grid.SetColumn(LevelText, 2);
            Grid.SetColumnSpan(LevelText, 1);
            LevelText.HorizontalAlignment = HorizontalAlignment.Right;
            LevelText.ClearValue(TextBlock.FontSizeProperty);
        }
    }

    public override UIElement? BuildHoverFlyoutContent() => BuildFlyoutContent();

    public UIElement BuildFlyoutContent()
    {
        var container = new Border { Width = 310 };
        _flyoutContainer = container;
        container.Unloaded += (_, _) =>
        {
            if (!ReferenceEquals(_flyoutContainer, container)) return;
            _flyoutContainer = null;
            _flyoutSlider = null;
            _flyoutLevelText = null;
            _flyoutIcon = null;
            _flyoutMicSlider = null;
            _flyoutMicLevelText = null;
            _flyoutMicIcon = null;
        };

        PopulateFlyout(container);
        return container;
    }

    private void PopulateFlyout(Border container)
    {
        var root = new StackPanel { Margin = new Thickness(0) };

        // 1. Header (Volume Center)
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 8) };
        var headerIcon = new ContentControl
        {
            Width = 16,
            Height = 16,
            Content = FindResource("IconSliders"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerIcon.SetResourceReference(StyleProperty, "WidgetIcon");
        var headerTitle = new TextBlock
        {
            Text = UiText.Get("Volume"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        header.Children.Add(headerIcon);
        header.Children.Add(headerTitle);
        root.Children.Add(header);

        // 2. Playback / Output Device Section
        var outHeader = new TextBlock
        {
            Text = UiText.Get("AudioOutputDevices"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 4)
        };
        outHeader.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(outHeader);

        var outDevices = _monitor?.GetPlaybackDevices() ?? [];
        if (outDevices.Count > 1)
        {
            var devPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var dev in outDevices)
            {
                var isSelected = dev.IsDefault;
                var isHeadphone = dev.Name.Contains("headphone", StringComparison.OrdinalIgnoreCase)
                    || dev.Name.Contains("kulaklık", StringComparison.OrdinalIgnoreCase)
                    || dev.Name.Contains("headset", StringComparison.OrdinalIgnoreCase)
                    || dev.Name.Contains("buds", StringComparison.OrdinalIgnoreCase)
                    || dev.Name.Contains("airpod", StringComparison.OrdinalIgnoreCase);

                var devIcon = new ContentControl
                {
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = FindResource(isHeadphone ? "IconHeadphones" : "IconVolume")
                };
                devIcon.SetResourceReference(StyleProperty, "WidgetIcon");

                var devText = new TextBlock
                {
                    Text = dev.Name,
                    MaxWidth = 180,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11.5
                };

                var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
                chipContent.Children.Add(devIcon);
                chipContent.Children.Add(devText);

                var pill = new ToggleButton
                {
                    Content = chipContent,
                    IsChecked = isSelected,
                    Height = 28,
                    Margin = new Thickness(0, 2, 4, 2),
                    Padding = new Thickness(8, 0, 8, 0),
                    ToolTip = dev.Name
                };
                pill.SetResourceReference(StyleProperty, "WidgetPill");
                var capturedId = dev.Id;
                pill.Click += (_, _) =>
                {
                    pill.IsChecked = true;
                    if (_monitor is null) return;
                    _monitor.SelectDefaultDevice(capturedId);
                    _monitor.BindDefaultDevice();
                    Render();
                    if (_flyoutContainer is not null) PopulateFlyout(_flyoutContainer);
                };
                devPanel.Children.Add(pill);
            }
            root.Children.Add(devPanel);
        }
        else if (outDevices.Count == 1)
        {
            var devText = new TextBlock
            {
                Text = outDevices[0].Name,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 4)
            };
            devText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            root.Children.Add(devText);
        }

        var hasOutDevice = _monitor?.HasDevice == true;
        var outLevel = hasOutDevice ? (int)Math.Round(_monitor!.Volume * 100) : 0;
        var outMuted = _monitor?.Muted == true;

        var outRow = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var outIconKey = outMuted || outLevel == 0 ? "IconVolumeMute" : outLevel < 50 ? "IconVolumeLow" : "IconVolume";
        var outIcon = new ContentControl { Width = 16, Height = 16, Content = FindResource(outIconKey) };
        outIcon.SetResourceReference(StyleProperty, "WidgetIcon");

        var outMuteBtn = new Button
        {
            Width = 28,
            Height = 28,
            Content = outIcon,
            IsEnabled = hasOutDevice,
            ToolTip = UiText.Get("MuteToggle")
        };
        outMuteBtn.SetResourceReference(StyleProperty, "WidgetIconButton");
        outMuteBtn.Click += MuteButton_Click;

        var outSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = outLevel,
            IsEnabled = hasOutDevice,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        outSlider.SetResourceReference(StyleProperty, "WidgetSlider");
        outSlider.ValueChanged += (_, args) =>
        {
            if (_updating || _monitor is null) return;
            if (_monitor.SetVolume((float)(args.NewValue / 100.0))) Render();
            else Report(UiText.Get("ErrorVolumeChange"));
        };

        var outLevelText = new TextBlock
        {
            Text = hasOutDevice ? (outMuted ? "—" : UiText.Format("Percent", outLevel)) : string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 36,
            TextAlignment = TextAlignment.Right
        };
        outLevelText.SetResourceReference(StyleProperty, "WidgetCaption");

        Grid.SetColumn(outMuteBtn, 0);
        Grid.SetColumn(outSlider, 1);
        Grid.SetColumn(outLevelText, 2);
        outRow.Children.Add(outMuteBtn);
        outRow.Children.Add(outSlider);
        outRow.Children.Add(outLevelText);
        root.Children.Add(outRow);

        _flyoutIcon = outIcon;
        _flyoutSlider = outSlider;
        _flyoutLevelText = outLevelText;

        // 3. Microphone / Recording Device Section
        var micDivider = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 8) };
        micDivider.SetResourceReference(Border.BackgroundProperty, "StrokeSubtle");
        root.Children.Add(micDivider);

        var micHeader = new TextBlock
        {
            Text = UiText.Get("AudioInputDevices"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 4)
        };
        micHeader.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(micHeader);

        var inDevices = _monitor?.GetRecordingDevices() ?? [];
        if (inDevices.Count > 1)
        {
            var inDevPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var dev in inDevices)
            {
                var isSelected = dev.IsDefault;
                var devIcon = new ContentControl
                {
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = FindResource("IconMic")
                };
                devIcon.SetResourceReference(StyleProperty, "WidgetIcon");

                var devText = new TextBlock
                {
                    Text = dev.Name,
                    MaxWidth = 180,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11.5
                };

                var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
                chipContent.Children.Add(devIcon);
                chipContent.Children.Add(devText);

                var pill = new ToggleButton
                {
                    Content = chipContent,
                    IsChecked = isSelected,
                    Height = 28,
                    Margin = new Thickness(0, 2, 4, 2),
                    Padding = new Thickness(8, 0, 8, 0),
                    ToolTip = dev.Name
                };
                pill.SetResourceReference(StyleProperty, "WidgetPill");
                var capturedId = dev.Id;
                pill.Click += (_, _) =>
                {
                    pill.IsChecked = true;
                    if (_monitor is null) return;
                    _monitor.SelectDefaultDevice(capturedId);
                    _monitor.BindDefaultMicDevice();
                    RenderMic();
                    if (_flyoutContainer is not null) PopulateFlyout(_flyoutContainer);
                };
                inDevPanel.Children.Add(pill);
            }
            root.Children.Add(inDevPanel);
        }
        else if (inDevices.Count == 1)
        {
            var devText = new TextBlock
            {
                Text = inDevices[0].Name,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 4)
            };
            devText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            root.Children.Add(devText);
        }

        var hasMic = _monitor?.HasMicDevice == true;
        var micLevel = hasMic ? (int)Math.Round(_monitor!.MicVolume * 100) : 0;
        var micMuted = _monitor?.MicMuted == true;

        if (!hasMic)
        {
            var noMic = new TextBlock
            {
                Text = UiText.Get("NoMicDevice"),
                Margin = new Thickness(4, 2, 4, 4),
                FontSize = 11.5
            };
            noMic.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            root.Children.Add(noMic);
        }
        else
        {
            var micRow = new Grid { Margin = new Thickness(0, 2, 0, 6) };
            micRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            micRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            micRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var micIcon = new ContentControl
            {
                Width = 16,
                Height = 16,
                Content = FindResource(micMuted ? "IconMicMute" : "IconMic")
            };
            micIcon.SetResourceReference(StyleProperty, "WidgetIcon");

            var micMuteBtn = new Button
            {
                Width = 28,
                Height = 28,
                Content = micIcon,
                ToolTip = UiText.Get("MicMuteToggle")
            };
            micMuteBtn.SetResourceReference(StyleProperty, "WidgetIconButton");
            micMuteBtn.Click += (_, _) =>
            {
                if (_monitor is null) return;
                if (_monitor.ToggleMicMute()) RenderMic();
            };

            var micSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = micLevel,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            micSlider.SetResourceReference(StyleProperty, "WidgetSlider");
            micSlider.ValueChanged += (_, args) =>
            {
                if (_updatingMic || _monitor is null) return;
                if (_monitor.SetMicVolume((float)(args.NewValue / 100.0))) RenderMic();
            };

            var micLevelText = new TextBlock
            {
                Text = micMuted ? "—" : UiText.Format("Percent", micLevel),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 36,
                TextAlignment = TextAlignment.Right
            };
            micLevelText.SetResourceReference(StyleProperty, "WidgetCaption");

            Grid.SetColumn(micMuteBtn, 0);
            Grid.SetColumn(micSlider, 1);
            Grid.SetColumn(micLevelText, 2);
            micRow.Children.Add(micMuteBtn);
            micRow.Children.Add(micSlider);
            micRow.Children.Add(micLevelText);
            root.Children.Add(micRow);

            _flyoutMicIcon = micIcon;
            _flyoutMicSlider = micSlider;
            _flyoutMicLevelText = micLevelText;
        }

        // 4. Per-App Volume Mixer Section
        var appDivider = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 8) };
        appDivider.SetResourceReference(Border.BackgroundProperty, "StrokeSubtle");
        root.Children.Add(appDivider);

        var appHeader = new TextBlock
        {
            Text = UiText.Get("AppVolumeMixer"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 4)
        };
        appHeader.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(appHeader);

        var sessions = _monitor?.GetAppSessions() ?? [];
        if (sessions.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = UiText.Get("NoActiveAudioApps"),
                FontSize = 11.5,
                Margin = new Thickness(4, 2, 4, 4)
            };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            root.Children.Add(emptyText);
        }
        else
        {
            var appList = new StackPanel();
            foreach (var session in sessions)
            {
                var appRow = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24, GridUnitType.Pixel) });
                appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76, GridUnitType.Pixel) });
                appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28, GridUnitType.Pixel) });
                appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34, GridUnitType.Pixel) });

                UIElement appIconEl;
                if (session.Icon is not null)
                {
                    appIconEl = new Image
                    {
                        Source = session.Icon,
                        Width = 18,
                        Height = 18,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                }
                else
                {
                    var ic = new ContentControl
                    {
                        Width = 16,
                        Height = 16,
                        Content = FindResource("IconSliders"),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    ic.SetResourceReference(StyleProperty, "WidgetIcon");
                    appIconEl = ic;
                }

                var nameBlock = new TextBlock
                {
                    Text = session.Name,
                    FontSize = 11.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = session.Name,
                    Margin = new Thickness(4, 0, 4, 0)
                };
                nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

                var currentMuted = session.IsMuted;
                var appVol = (int)Math.Round(session.Volume * 100);

                var appIcon = new ContentControl
                {
                    Width = 14,
                    Height = 14,
                    Content = FindResource(currentMuted || appVol == 0 ? "IconVolumeMute" : "IconVolume")
                };
                appIcon.SetResourceReference(StyleProperty, "WidgetIcon");

                var appMuteBtn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Content = appIcon,
                    ToolTip = UiText.Get("MuteToggle")
                };
                appMuteBtn.SetResourceReference(StyleProperty, "WidgetIconButton");

                var appPercent = new TextBlock
                {
                    Text = currentMuted ? "—" : UiText.Format("Percent", appVol),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right
                };
                appPercent.SetResourceReference(StyleProperty, "WidgetCaption");

                var appSlider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = appVol,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                };
                appSlider.SetResourceReference(StyleProperty, "WidgetSlider");

                var localSession = session;
                var appUpdating = false;

                appSlider.ValueChanged += (_, args) =>
                {
                    if (appUpdating) return;
                    var newLevel = (int)Math.Round(args.NewValue);
                    localSession.SetVolume(newLevel / 100f);
                    if (currentMuted && newLevel > 0)
                    {
                        currentMuted = false;
                        appIcon.Content = FindResource("IconVolume");
                    }
                    appPercent.Text = currentMuted ? "—" : UiText.Format("Percent", newLevel);
                };

                appMuteBtn.Click += (_, _) =>
                {
                    currentMuted = !currentMuted;
                    localSession.SetMute(currentMuted);
                    appIcon.Content = FindResource(currentMuted || (int)appSlider.Value == 0 ? "IconVolumeMute" : "IconVolume");
                    appPercent.Text = currentMuted ? "—" : UiText.Format("Percent", (int)Math.Round(appSlider.Value));
                };

                Grid.SetColumn(appIconEl, 0);
                Grid.SetColumn(nameBlock, 1);
                Grid.SetColumn(appMuteBtn, 2);
                Grid.SetColumn(appSlider, 3);
                Grid.SetColumn(appPercent, 4);

                appRow.Children.Add(appIconEl);
                appRow.Children.Add(nameBlock);
                appRow.Children.Add(appMuteBtn);
                appRow.Children.Add(appSlider);
                appRow.Children.Add(appPercent);
                appList.Children.Add(appRow);
            }

            var scrollViewer = new ScrollViewer
            {
                Content = appList,
                MaxHeight = 180,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
            root.Children.Add(scrollViewer);
        }

        container.Child = root;
    }

    protected override void OnActivated()
    {
        UiText.Current.PropertyChanged += UiText_PropertyChanged;
        try
        {
            _monitor = AudioEndpointMonitor.AcquireShared();
            _monitor.VolumeChanged += Monitor_VolumeChanged;
            _monitor.DefaultDeviceChanged += Monitor_DefaultDeviceChanged;
            _monitor.MicVolumeChanged += Monitor_MicVolumeChanged;
            _monitor.DefaultMicChanged += Monitor_DefaultMicChanged;
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
        _flyout.Close();
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;
        if (_monitor is null) return;
        _monitor.VolumeChanged -= Monitor_VolumeChanged;
        _monitor.DefaultDeviceChanged -= Monitor_DefaultDeviceChanged;
        _monitor.MicVolumeChanged -= Monitor_MicVolumeChanged;
        _monitor.DefaultMicChanged -= Monitor_DefaultMicChanged;
        _monitor.Dispose();
        _monitor = null;
    }

    private void UiText_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Render();
        if (_flyoutContainer is not null) PopulateFlyout(_flyoutContainer);
    }

    private void InvalidateCache()
    {
        _lastHasDevice = null;
        _lastLevel = -1;
        _lastIconKey = null;
        _lastDeviceName = null;
    }

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
        if (_flyoutContainer is not null) PopulateFlyout(_flyoutContainer);
    });

    private void Monitor_MicVolumeChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (IsActive) RenderMic();
    });

    private void Monitor_DefaultMicChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!IsActive || _monitor is null) return;
        _monitor.BindDefaultMicDevice();
        AppLog.Info($"Default mic device: {_monitor.MicDeviceName}");
        RenderMic();
        if (_flyoutContainer is not null) PopulateFlyout(_flyoutContainer);
    });

    private void RenderMic()
    {
        if (_monitor is null || _flyoutMicSlider is null || _flyoutMicLevelText is null || _flyoutMicIcon is null) return;
        var hasMic = _monitor.HasMicDevice;
        var level = hasMic ? (int)Math.Round(_monitor.MicVolume * 100) : 0;
        var muted = _monitor.MicMuted;
        _updatingMic = true;
        _flyoutMicSlider.Value = level;
        _flyoutMicLevelText.Text = muted ? "—" : UiText.Format("Percent", level);
        _flyoutMicIcon.Content = FindResource(muted ? "IconMicMute" : "IconMic");
        _updatingMic = false;
    }

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
            if (_flyoutSlider is not null) _flyoutSlider.IsEnabled = false;
            if (_flyoutLevelText is not null) _flyoutLevelText.Text = string.Empty;
            if (_flyoutIcon is not null) _flyoutIcon.Content = FindResource("IconVolumeMute");
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
        if (_flyoutSlider is not null) _flyoutSlider.IsEnabled = true;
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

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && (IsDescendantOf(dep, VolumeSlider) || IsDescendantOf(dep, MuteButton)))
        {
            return;
        }

        _flyout.Toggle(Card, BuildFlyoutContent, animate: true, focusable: false);
        e.Handled = true;
    }

    private void Card_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_monitor?.HasDevice != true) return;
        if (_monitor.SetVolume(Math.Clamp(_monitor.Volume + Math.Sign(e.Delta) * 0.02f, 0f, 1f))) Render();
        else Report(UiText.Get("ErrorVolumeChange"));
        e.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject target)
    {
        var current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, target)) return true;
            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
