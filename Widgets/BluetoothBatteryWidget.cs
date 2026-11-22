using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using Windows.Devices.Enumeration;

namespace OpenTaskbar.Widgets;

public enum BluetoothDeviceKind
{
    Generic,
    Headphones,
    Mouse,
    Keyboard,
    Speaker,
    Watch,
    Phone,
    Gamepad
}

public sealed record BluetoothDeviceBatteryInfo(
    string Name,
    string Id,
    bool IsConnected,
    int BatteryLevel,
    BluetoothDeviceKind Kind = BluetoothDeviceKind.Generic,
    bool IsCharging = false,
    bool IsStale = false,
    string? SourceLabel = null,
    DateTimeOffset? SampledAt = null,
    string? LevelOverrideText = null,
    int? SecondaryBatteryLevel = null);

/// <summary>
/// A single dock card that lists connected wireless and Bluetooth devices side by side with a device-type icon,
/// battery percentage or coarse level, charging bolt indicator, and multi-protocol hover flyout.
/// </summary>
public sealed class BluetoothBatteryWidget : DockWidget
{
    public static string Id => "BluetoothBattery";

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 32;
    public override double CompactWidthEstimate => _compactWidth;
    public override double FullWidthEstimate => _fullWidth;

    // Windows publishes the same 0..100 battery byte under both names. This is also the value
    // shown in Settings > Bluetooth, and it covers classic headsets/mice with no GATT service.
    // Bluetooth AEP enumeration is intentionally NOT used: on Windows it starts a fresh discovery
    // that takes ~30 s per protocol. PnP device nodes carry the same battery data in ~150 ms.
    private const string BatteryLevelProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string BatteryLifeProperty = "System.Devices.BatteryLife";
    private const string ContainerProperty = "System.Devices.ContainerId";
    private const string AepIdProperty = "System.Devices.Aep.AepId";
    private const string PresentProperty = "System.Devices.Present";
    private const string DevPkeyIsPresentProperty = "{540b947e-8b40-45bc-a8a2-6a0b894cbda2} 5";
    private const string LastArrivalDateProperty = "{83da6326-97a6-4088-9453-a1923f573b29} 102";
    private const string ConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string CategoryProperty = "System.Devices.Aep.Category";
    private const string AppearanceProperty = "System.Devices.Aep.Bluetooth.Le.Appearance";

    // PnP node enumeration is fast (~150 ms). The AEP id on each node lets us look up the exact
    // endpoint directly (~60 ms for all devices, in parallel) without the 30 s discovery scan
    // that a full AssociationEndpoint enumeration would trigger.
    private static readonly string[] NodeProperties =
    [
        ContainerProperty,
        AepIdProperty,
        BatteryLevelProperty,
        BatteryLifeProperty,
        PresentProperty,
        DevPkeyIsPresentProperty,
        LastArrivalDateProperty
    ];

    private static readonly string[] AepProperties =
    [
        ConnectedProperty,
        CategoryProperty,
        AppearanceProperty
    ];

    private readonly StackPanel _deviceTiles = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly TextBlock _emptyText = new()
    {
        Text = "—",
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button _cardButton;
    private IReadOnlyList<BluetoothDeviceBatteryInfo> _devices = [];
    private int _refreshing;
    private long _lastRefreshTickMs;
    private double _fullWidth = 78;
    private double _compactWidth = 46;
    private const long RefreshIntervalMs = 15000;

    internal Func<Task<IReadOnlyList<BluetoothDeviceBatteryInfo>>> BatteryReader { get; set; } = WirelessBatteryAggregator.ReadAllAsync;
    internal Action OpenSettingsAction { get; set; } = DefaultOpenBluetoothSettings;

    public BluetoothBatteryWidget()
    {
        _emptyText.SetResourceReference(StyleProperty, "WidgetText");
        _emptyText.SetResourceReference(ForegroundProperty, "TextSecondary");

        _cardButton = new Button { Padding = new Thickness(6, 0, 6, 0) };
        _cardButton.SetResourceReference(StyleProperty, "WidgetCardButton");
        _cardButton.Click += (_, _) => OpenSettingsAction();
        Content = _cardButton;
        UpdateDisplay();
    }

    protected override void OnActivated()
    {
        _lastRefreshTickMs = 0;
        RefreshBatteries();
    }

    protected override void OnTick(DateTime now)
    {
        if (Environment.TickCount64 - _lastRefreshTickMs >= RefreshIntervalMs) RefreshBatteries();
    }

    protected override void OnLanguageChanged() => UpdateDisplay();

    protected override void OnCompactChanged(bool compact) => ApplyDensityLayout();
    protected override void OnHoverExpandedChanged(bool expanded) => ApplyDensityLayout();

    private void ApplyDensityLayout()
    {
        var compact = IsCompact && !IsHoverExpanded;
        // Compact keeps only the type icons; percentages (and optional charging bolt) come back in expanded or hover-expanded mode.
        foreach (var child in _deviceTiles.Children)
        {
            if (child is Border { Child: StackPanel stack } && stack.Children.Count > 1)
            {
                for (var i = 1; i < stack.Children.Count; i++)
                    stack.Children[i].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    internal async Task RefreshBatteriesAsync()
    {
        try
        {
            var list = await BatteryReader().ConfigureAwait(false);
            if (!IsActive) return;
            if (Dispatcher.CheckAccess())
            {
                if (!IsActive) return;
                _devices = list;
                UpdateDisplay();
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsActive) return;
                    _devices = list;
                    UpdateDisplay();
                });
            }
        }
        catch
        {
        }
    }

    internal void RefreshBatteries()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _lastRefreshTickMs = Environment.TickCount64;
        _ = Task.Run(async () =>
        {
            try { await RefreshBatteriesAsync().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    internal void UpdateDisplay()
    {
        var connected = _devices.Where(device => device.IsConnected).ToList();
        if (connected.Count == 0)
        {
            _cardButton.Content = _emptyText;
            _emptyText.Text = _devices.Count > 0 ? "—" : UiText.Get("BluetoothNoDevicesAbbr");
            _cardButton.ToolTip = UiText.Get("WidgetBluetoothBatteryDescription");
            AutomationProperties.SetName(_cardButton, UiText.Get("WidgetBluetoothBattery"));
            _fullWidth = 78;
            _compactWidth = 46;
        }
        else
        {
            _deviceTiles.Children.Clear();
            for (var i = 0; i < connected.Count; i++)
            {
                _deviceTiles.Children.Add(CreateDeviceTile(connected[i], isLast: i == connected.Count - 1));
            }
            _cardButton.Content = _deviceTiles;
            _cardButton.ToolTip = string.Join("\n", connected.Select(FormatDeviceTooltip));
            AutomationProperties.SetName(_cardButton,
                $"{UiText.Get("WidgetBluetoothBattery")}: {string.Join(", ", connected.Select(device => device.Name))}");
            _fullWidth = Math.Max(78, connected.Count * 62 + 12);
            _compactWidth = Math.Max(46, connected.Count * 24 + 8);
        }
        RaiseContentWidthChanged();
    }

    internal static bool IsLowBatteryWarning(int batteryLevel, bool isCharging) =>
        batteryLevel >= 0 && (batteryLevel <= 15 || (!isCharging && batteryLevel <= 20));

    internal static string FormatLevelText(BluetoothDeviceBatteryInfo device)
    {
        if (!string.IsNullOrWhiteSpace(device.LevelOverrideText))
            return device.LevelOverrideText!;
        return device.BatteryLevel >= 0
            ? $"{device.BatteryLevel.ToString(CultureInfo.InvariantCulture)}%"
            : "—";
    }

    private static string FormatDeviceTooltip(BluetoothDeviceBatteryInfo device)
    {
        var levelText = FormatLevelText(device);
        var summary = $"{device.Name}: {levelText}";
        if (device.IsCharging)
            summary += $" ({UiText.Get("WirelessBatteryCharging")})";
        if (device.SecondaryBatteryLevel is >= 0 and <= 100)
            summary += $" · {UiText.Format("WirelessBatterySpareDock", device.SecondaryBatteryLevel.Value)}";
        return summary;
    }

    private FrameworkElement CreateDeviceTile(BluetoothDeviceBatteryInfo device, bool isLast)
    {
        var icon = CreateDeviceIcon(device.Kind);
        var compact = IsCompact && !IsHoverExpanded;
        var levelText = FormatLevelText(device);
        var isLow = IsLowBatteryWarning(device.BatteryLevel, device.IsCharging);

        var percent = new TextBlock
        {
            Text = levelText,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
            Visibility = compact ? Visibility.Collapsed : Visibility.Visible
        };
        percent.SetResourceReference(StyleProperty, "WidgetText");
        percent.SetResourceReference(ForegroundProperty, isLow ? "WidgetWarning" : "TextPrimary");

        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(percent); // Index 1 is strictly preserved as the percentage/level TextBlock.

        if (device.IsCharging)
        {
            var bolt = new ContentControl
            {
                Content = Application.Current?.TryFindResource("IconZap"),
                Width = 10,
                Height = 10,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = compact ? Visibility.Collapsed : Visibility.Visible
            };
            bolt.SetResourceReference(StyleProperty, "WidgetGlyph");
            bolt.SetResourceReference(ForegroundProperty, "WidgetAccent");
            stack.Children.Add(bolt);
        }

        var border = new Border
        {
            Child = stack,
            Height = 20,
            Opacity = device.IsStale ? 0.55 : 1.0,
            Padding = new Thickness(0, 0, isLast ? 0 : 9, 0),
            Margin = new Thickness(0, 0, isLast ? 0 : 9, 0),
            BorderThickness = new Thickness(0, 0, isLast ? 0 : 1, 0),
            ToolTip = FormatDeviceTooltip(device)
        };
        if (!isLast) border.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");
        return border;
    }

    private static ContentControl CreateDeviceIcon(BluetoothDeviceKind kind)
    {
        var icon = new ContentControl
        {
            Content = Application.Current?.TryFindResource(IconKey(kind))
                      ?? Application.Current?.TryFindResource("IconBluetooth"),
            Width = 15,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        icon.SetResourceReference(StyleProperty, "WidgetIcon");
        icon.SetResourceReference(ForegroundProperty, "TextSecondary");
        return icon;
    }

    private static string IconKey(BluetoothDeviceKind kind) => kind switch
    {
        BluetoothDeviceKind.Headphones => "IconHeadphones",
        BluetoothDeviceKind.Mouse => "IconMouse",
        BluetoothDeviceKind.Keyboard => "IconKeyboard",
        BluetoothDeviceKind.Speaker => "IconVolume",
        BluetoothDeviceKind.Watch => "IconWatch",
        BluetoothDeviceKind.Phone => "IconPhone",
        BluetoothDeviceKind.Gamepad => "IconGamepad",
        _ => "IconBluetooth"
    };

    public override UIElement? BuildHoverFlyoutContent()
    {
        var panel = new StackPanel { Width = 264 };

        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var heading = DockFlyout.Heading(UiText.Get("WidgetBluetoothBattery"));
        heading.Margin = new Thickness(4, 0, 4, 0);
        heading.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(heading, 0);
        headerGrid.Children.Add(heading);

        var refreshLabel = UiText.Get("WirelessBatteryRefresh");
        var refreshBtn = DockFlyout.FlatButton(
            new TextBlock
            {
                Text = refreshLabel,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold
            },
            (_, _) => RefreshBatteries(),
            refreshLabel);
        refreshBtn.Padding = new Thickness(7, 3, 7, 3);
        refreshBtn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(refreshBtn, 1);
        headerGrid.Children.Add(refreshBtn);

        panel.Children.Add(headerGrid);

        if (_devices.Count == 0)
        {
            panel.Children.Add(DockFlyout.Caption(UiText.Get("BluetoothNoDevices")));
            panel.Children.Add(BuildSettingsFooterButton());
            return panel;
        }

        var listPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var device in _devices)
        {
            var rowStack = new StackPanel
            {
                Margin = new Thickness(4, 4, 4, 6),
                Opacity = device.IsStale ? 0.65 : 1.0
            };

            var itemGrid = new Grid();
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = CreateDeviceIcon(device.Kind);
            Grid.SetColumn(icon, 0);
            itemGrid.Children.Add(icon);

            var nameStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(7, 0, 6, 0) };

            var titleWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleWrap.Children.Add(new TextBlock
            {
                Text = device.Name,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 138,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrWhiteSpace(device.SourceLabel))
            {
                var sourceBadge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = (Brush)FindResource("SurfaceSelected")
                };
                sourceBadge.Child = new TextBlock
                {
                    Text = device.SourceLabel,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextSecondary")
                };
                titleWrap.Children.Add(sourceBadge);
            }
            nameStack.Children.Add(titleWrap);

            var subtitleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            subtitleRow.Children.Add(new TextBlock
            {
                Text = BuildDeviceSubtitle(device),
                FontSize = 10,
                Foreground = device.IsCharging || (device.IsConnected && !device.IsStale)
                    ? (Brush)FindResource("WidgetAccent")
                    : (Brush)FindResource("TextSecondary")
            });

            if (device.SecondaryBatteryLevel is >= 0 and <= 100)
            {
                var spareBadge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = (Brush)FindResource("SurfaceSelected")
                };
                spareBadge.Child = new TextBlock
                {
                    Text = UiText.Format("WirelessBatterySpareDock", device.SecondaryBatteryLevel.Value),
                    FontSize = 9.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = (Brush)FindResource("TextPrimary")
                };
                subtitleRow.Children.Add(spareBadge);
            }

            nameStack.Children.Add(subtitleRow);
            Grid.SetColumn(nameStack, 1);
            itemGrid.Children.Add(nameStack);

            var valueRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            if (device.IsCharging)
            {
                var bolt = new ContentControl
                {
                    Content = Application.Current?.TryFindResource("IconZap"),
                    Width = 10,
                    Height = 10,
                    Margin = new Thickness(0, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                bolt.SetResourceReference(StyleProperty, "WidgetGlyph");
                bolt.SetResourceReference(ForegroundProperty, "WidgetAccent");
                valueRow.Children.Add(bolt);
            }

            var isLow = IsLowBatteryWarning(device.BatteryLevel, device.IsCharging);
            var batteryText = new TextBlock
            {
                Text = FormatLevelText(device),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
            };
            batteryText.SetResourceReference(StyleProperty, "WidgetText");
            batteryText.SetResourceReference(ForegroundProperty, isLow ? "WidgetWarning" : "TextPrimary");
            valueRow.Children.Add(batteryText);

            Grid.SetColumn(valueRow, 2);
            itemGrid.Children.Add(valueRow);
            rowStack.Children.Add(itemGrid);

            if (device.BatteryLevel >= 0)
            {
                const double barTrackWidth = 248.0;
                var clampedLevel = Math.Clamp(device.BatteryLevel, 0, 100);
                var barFill = new Border
                {
                    Height = 3.5,
                    CornerRadius = new CornerRadius(1.75),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(4.0, barTrackWidth * clampedLevel / 100.0)
                };
                barFill.SetResourceReference(
                    Border.BackgroundProperty,
                    isLow ? "WidgetWarning" : "WidgetAccent");

                var barTrack = new Border
                {
                    Height = 3.5,
                    CornerRadius = new CornerRadius(1.75),
                    Margin = new Thickness(0, 5, 0, 0),
                    Child = barFill
                };
                barTrack.SetResourceReference(Border.BackgroundProperty, "WidgetTrack");
                rowStack.Children.Add(barTrack);
            }

            listPanel.Children.Add(rowStack);
        }

        panel.Children.Add(listPanel);
        panel.Children.Add(BuildSettingsFooterButton());
        return panel;
    }

    private Button BuildSettingsFooterButton()
    {
        var settingsLabel = UiText.Format("RadioSettings", UiText.Get("Bluetooth"));
        var button = DockFlyout.FlatButton(
            new TextBlock
            {
                Text = settingsLabel,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("TextSecondary")
            },
            (_, _) => OpenSettingsAction(),
            settingsLabel);
        button.Margin = new Thickness(0, 2, 0, 0);
        return button;
    }

    private static string BuildDeviceSubtitle(BluetoothDeviceBatteryInfo device)
    {
        if (device.IsCharging)
            return UiText.Get("WirelessBatteryCharging");

        if (device.IsStale)
        {
            if (device.SampledAt is { } sampledAt)
            {
                var elapsed = DateTimeOffset.UtcNow - sampledAt;
                var minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes));
                var timeLabel = UiText.Format("MinutesShort", minutes);
                return UiText.Format("WirelessBatteryLastReading", timeLabel);
            }
            return UiText.Format("WirelessBatteryLastReading", "—");
        }

        return device.IsConnected ? UiText.Get("BluetoothConnected") : UiText.Get("BluetoothDisconnected");
    }

    private sealed class BatteryCandidate
    {
        public required string Name { get; set; }
        public required string Id { get; set; }
        public required Guid Container { get; init; }
        public required int Battery { get; set; }
        public bool IsPresent { get; set; }
        public DateTimeOffset LastArrival { get; set; }
        public string? AepId { get; set; }
        public BluetoothDeviceKind Kind { get; set; } = BluetoothDeviceKind.Generic;
        public bool Connected { get; set; }
    }

    /// <summary>
    /// Decides whether a newly enumerated PnP node in the same device container should replace
    /// the previously recorded battery reading. Active (Present=true) nodes always supersede
    /// disconnected ghost nodes carrying stale battery values; ties prefer the latest arrival.
    /// </summary>
    internal static bool ShouldPreferNode(
        bool existingPresent, DateTimeOffset existingArrival,
        bool candidatePresent, DateTimeOffset candidateArrival)
    {
        if (candidatePresent != existingPresent) return candidatePresent;
        return candidateArrival > existingArrival;
    }

    internal static async Task<IReadOnlyList<WirelessDeviceReading>> ReadLiveBluetoothReadingsAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return [];
        var list = await ReadLiveBluetoothBatteriesAsync().ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return list.Select(info => new WirelessDeviceReading(
            Id: info.Id,
            Name: info.Name,
            Kind: info.Kind,
            BatteryLevel: info.BatteryLevel,
            IsConnected: info.IsConnected,
            IsCharging: info.IsCharging,
            IsStale: info.IsStale,
            Source: WirelessBatterySource.Bluetooth,
            SampledAt: info.SampledAt ?? now)).ToList();
    }

    /// <summary>
    /// Reads battery straight from PnP device nodes (~150 ms). Enumerating Bluetooth association endpoints
    /// instead takes ~30 s per protocol on Windows because it starts a fresh discovery every time.
    /// </summary>
    internal static async Task<IReadOnlyList<BluetoothDeviceBatteryInfo>> ReadLiveBluetoothBatteriesAsync()
    {
        DeviceInformationCollection nodes;
        try
        {
            nodes = await DeviceInformation
                .FindAllAsync(string.Empty, NodeProperties, DeviceInformationKind.Device)
                .AsTask().ConfigureAwait(false);
        }
        catch
        {
            return [];
        }

        var byContainer = new Dictionary<Guid, BatteryCandidate>();
        foreach (var node in nodes)
        {
            var battery = ReadPercentage(node.Properties);
            if (battery < 0) continue;
            var container = ReadContainer(node.Properties);
            if (container == Guid.Empty) continue;
            var aepId = node.Properties.TryGetValue(AepIdProperty, out var aepValue) ? aepValue?.ToString() : null;
            var isPresent = ReadIsPresent(node.Properties);
            var lastArrival = ReadLastArrival(node.Properties);

            if (byContainer.TryGetValue(container, out var existing))
            {
                // A headset can leave behind disconnected ghost PnP nodes (Present=false) with stale
                // battery levels alongside the live active node (Present=true) in the same container.
                if (ShouldPreferNode(existing.IsPresent, existing.LastArrival, isPresent, lastArrival))
                {
                    existing.Name = DisplayName(node.Name);
                    existing.Id = node.Id;
                    existing.Battery = battery;
                    existing.IsPresent = isPresent;
                    existing.LastArrival = lastArrival;
                    if (!string.IsNullOrEmpty(aepId)) existing.AepId = aepId;
                }
                else if (existing.AepId is null && !string.IsNullOrEmpty(aepId))
                {
                    existing.AepId = aepId;
                }
                continue;
            }

            byContainer[container] = new BatteryCandidate
            {
                Name = DisplayName(node.Name),
                Id = node.Id,
                Container = container,
                Battery = battery,
                IsPresent = isPresent,
                LastArrival = lastArrival,
                AepId = aepId
            };
        }

        // Direct per-id AEP lookups are fast (~60 ms in parallel) and give the authoritative
        // connection state, category and appearance without the 30 s discovery scan.
        var candidates = byContainer.Values.Where(candidate => !string.IsNullOrEmpty(candidate.AepId)).ToList();
        if (candidates.Count > 0)
        {
            var lookups = await Task.WhenAll(candidates.Select(async candidate =>
            {
                try
                {
                    return await DeviceInformation
                        .CreateFromIdAsync(candidate.AepId!, AepProperties, DeviceInformationKind.AssociationEndpoint)
                        .AsTask().ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            })).ConfigureAwait(false);

            for (var i = 0; i < candidates.Count; i++)
            {
                if (lookups[i] is not { } info) continue;
                var candidate = candidates[i];
                candidate.Connected = info.Properties.TryGetValue(ConnectedProperty, out var connected) && connected is true;
                var classified = Classify(ReadCategory(info.Properties), ReadAppearance(info.Properties), candidate.Name);
                if (classified != BluetoothDeviceKind.Generic) candidate.Kind = classified;
                if (!string.IsNullOrWhiteSpace(info.Name)) candidate.Name = info.Name;
                candidate.Id = info.Id;
            }
        }

        var now = DateTimeOffset.UtcNow;
        return candidates
            .Where(candidate => candidate.Connected)
            .Select(candidate => new BluetoothDeviceBatteryInfo(
                Name: candidate.Name,
                Id: candidate.Id,
                IsConnected: true,
                BatteryLevel: candidate.Battery,
                Kind: candidate.Kind,
                SourceLabel: "Bluetooth",
                SampledAt: now))
            .ToList();
    }

    /// <summary>Removes the protocol suffix Windows appends, e.g. "Headset Hands-Free AG" -> "Headset".</summary>
    private static string DisplayName(string name)
    {
        var text = (name ?? string.Empty).Trim();
        foreach (var suffix in new[] { " Hands-Free AG", " Headset", " Audio Gateway", " Stereo", " A2DP" })
        {
            var index = text.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index > 0) return text[..index].Trim();
        }
        return text;
    }

    /// <summary>Generic type detection from the node name and Bluetooth service UUID — no model names.</summary>
    internal static BluetoothDeviceKind ClassifyNode(string name, string instanceId)
    {
        var text = name ?? string.Empty;
        bool Has(params string[] words) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("hands-free", "headset", "headphone", "earbud", "stereo", "audio gateway")) return BluetoothDeviceKind.Headphones;
        if (Has("mouse", "trackball")) return BluetoothDeviceKind.Mouse;
        if (Has("keyboard", "keypad")) return BluetoothDeviceKind.Keyboard;
        if (Has("gamepad", "controller", "joystick")) return BluetoothDeviceKind.Gamepad;
        if (Has("speaker")) return BluetoothDeviceKind.Speaker;
        if (Has("watch")) return BluetoothDeviceKind.Watch;
        if (Has("phone")) return BluetoothDeviceKind.Phone;

        var id = instanceId ?? string.Empty;
        bool HasUuid(params string[] uuids) => uuids.Any(uuid => id.Contains(uuid, StringComparison.OrdinalIgnoreCase));
        // HFP, A2DP and Advanced Audio Distribution are all headset/audio profiles.
        if (HasUuid("0000111e", "0000110b", "0000110d")) return BluetoothDeviceKind.Headphones;
        return BluetoothDeviceKind.Generic;
    }

    /// <summary>Fully generic classification: Windows category first, then SIG appearance, then name words.</summary>
    private static BluetoothDeviceKind Classify(string? category, ushort? appearance, string name)
    {
        if (!string.IsNullOrEmpty(category))
        {
            var segments = category.Split('.', StringSplitOptions.RemoveEmptyEntries);
            bool HasCategory(params string[] tokens) => segments.Any(segment =>
                tokens.Any(token => segment.Contains(token, StringComparison.OrdinalIgnoreCase)));

            if (HasCategory("headset", "headphone", "earbud", "audio")) return BluetoothDeviceKind.Headphones;
            if (HasCategory("mouse", "trackball")) return BluetoothDeviceKind.Mouse;
            if (HasCategory("keyboard", "keypad")) return BluetoothDeviceKind.Keyboard;
            if (HasCategory("gamepad", "gaming", "joystick")) return BluetoothDeviceKind.Gamepad;
            if (HasCategory("speaker")) return BluetoothDeviceKind.Speaker;
            if (HasCategory("watch", "wearable")) return BluetoothDeviceKind.Watch;
            if (HasCategory("phone")) return BluetoothDeviceKind.Phone;
        }

        switch (appearance)
        {
            case 961: return BluetoothDeviceKind.Keyboard;
            case 962: return BluetoothDeviceKind.Mouse;
            case 963 or 964: return BluetoothDeviceKind.Gamepad;
            case 192 or 193: return BluetoothDeviceKind.Watch;
            case 64 or 65: return BluetoothDeviceKind.Phone;
        }

        var text = name ?? string.Empty;
        bool Has(params string[] words) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("headphone", "headset", "earbud", "kulaklık", "kulaklik")) return BluetoothDeviceKind.Headphones;
        if (Has("mouse", "fare")) return BluetoothDeviceKind.Mouse;
        if (Has("keyboard", "klavye")) return BluetoothDeviceKind.Keyboard;
        return BluetoothDeviceKind.Generic;
    }

    private static string? ReadCategory(IReadOnlyDictionary<string, object> properties)
    {
        if (!properties.TryGetValue(CategoryProperty, out var value) || value is null) return null;
        return value switch
        {
            string text => text,
            string[] array => string.Join('.', array),
            _ => value.ToString()
        };
    }

    private static ushort? ReadAppearance(IReadOnlyDictionary<string, object> properties)
    {
        if (!properties.TryGetValue(AppearanceProperty, out var value)) return null;
        if (value is ushort shortValue) return shortValue;
        if (value is int intValue) return (ushort)intValue;
        return null;
    }

    private static Guid ReadContainer(IReadOnlyDictionary<string, object> properties)
    {
        if (!properties.TryGetValue(ContainerProperty, out var value) || value is null) return Guid.Empty;
        if (value is Guid guid) return guid;
        return Guid.TryParse(value.ToString(), out var parsed) ? parsed : Guid.Empty;
    }

    private static bool ReadIsPresent(IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue(PresentProperty, out var present) && present is bool isPresent) return isPresent;
        if (properties.TryGetValue(DevPkeyIsPresentProperty, out var pkeyPresent) && pkeyPresent is bool isPkeyPresent) return isPkeyPresent;
        return false;
    }

    private static DateTimeOffset ReadLastArrival(IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue(LastArrivalDateProperty, out var value))
        {
            if (value is DateTimeOffset dto) return dto;
            if (value is DateTime dt) return new DateTimeOffset(dt);
        }
        return DateTimeOffset.MinValue;
    }

    private static int ReadPercentage(IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue(BatteryLevelProperty, out var raw))
        {
            var level = AsPercentage(raw);
            if (level >= 0) return level;
        }
        if (properties.TryGetValue(BatteryLifeProperty, out var life))
        {
            var level = AsPercentage(life);
            if (level >= 0) return level;
        }
        return -1;
    }

    private static int AsPercentage(object? value)
    {
        if (value is null) return -1;
        try
        {
            var level = Convert.ToInt32(value);
            return level is >= 0 and <= 100 ? level : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void DefaultOpenBluetoothSettings()
    {
        DockFlyout.CloseAll();
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
