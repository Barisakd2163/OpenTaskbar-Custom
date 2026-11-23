using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using Windows.Devices.Radios;

namespace OpenTaskbar.Widgets;

public partial class QuickSettingsWidget : DockWidget
{
    private enum Setting { Wifi, Bluetooth, NightLight }
    private enum ChangeResult { Applied, Unavailable, Denied, Failed }

    private static readonly IReadOnlyDictionary<Setting, string> SettingNames = new Dictionary<Setting, string>
    {
        [Setting.Wifi] = "WiFi",
        [Setting.Bluetooth] = "Bluetooth",
        [Setting.NightLight] = "NightLight"
    };

    private const double ButtonSize = 27;
    private const double FullGap = 4;
    private const double CompactGap = 2;
    private const double FullPadding = 8;
    private const double CompactPadding = 4;

    // Primary and secondary docks share the same physical controls.
    private static readonly Dictionary<Setting, bool> PendingTargets = [];
    private static event Action? TransitionChanged;
    private static Task<RadioAccessStatus>? _radioAccess;

    private readonly Dictionary<Setting, ChangeResult> _errors = [];
    private IReadOnlyList<Radio> _radios = [];
    private CancellationTokenSource? _activation;
    private bool? _nightLightOn;
    private bool _loadingRadios;

    internal Func<bool?> ReadNightLight { get; init; } = NightLightControl.Read;
    internal Func<bool, bool> WriteNightLight { get; init; } = NightLightControl.Set;

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 20;
    public override double FullWidthEstimate => EstimateWidth(FullGap, FullPadding);
    public override double CompactWidthEstimate => EstimateWidth(CompactGap, CompactPadding);

    public QuickSettingsWidget()
    {
        InitializeComponent();
        AddShortcuts();
        ApplyDensityLayout();
        Render();
    }

    protected override void OnCompactChanged(bool compact) => ApplyDensityLayout();
    protected override void OnHoverExpandedChanged(bool expanded) => ApplyDensityLayout();
    protected override void OnSettingsChanged()
    {
        var orientation = Settings.Edge == DockEdge.Bottom ? Orientation.Horizontal : Orientation.Vertical;
        if (ControlsStrip.Orientation == orientation) return;
        ControlsStrip.Orientation = orientation;
        ApplyDensityLayout();
    }

    private double EstimateWidth(double gap, double padding)
    {
        var buttonCount = ControlsStrip.Children.Count;
        var contentWidth = Settings.Edge == DockEdge.Bottom
            ? ButtonSize * buttonCount + WifiPill.BorderThickness.Left * 2 * SettingNames.Count + gap * (buttonCount - 1)
            : ButtonSize + WifiPill.BorderThickness.Left * 2;
        return contentWidth + (Settings.Edge == DockEdge.Bottom ? 2 * padding : 0);
    }

    private void ApplyDensityLayout()
    {
        var compact = IsCompact && !IsHoverExpanded;
        var gap = compact ? CompactGap : FullGap;
        var padding = compact ? CompactPadding : FullPadding;
        var horizontal = ControlsStrip.Orientation == Orientation.Horizontal;
        Card.Padding = horizontal ? new Thickness(padding, 0, padding, 0) : new Thickness(0, padding, 0, padding);
        if (horizontal) Card.ClearValue(HeightProperty);
        else Card.Height = double.NaN;

        for (var i = 0; i < ControlsStrip.Children.Count; i++)
        {
            if (ControlsStrip.Children[i] is FrameworkElement item)
            {
                var trailingGap = i == ControlsStrip.Children.Count - 1 ? 0 : gap;
                item.Margin = horizontal ? new Thickness(0, 0, trailingGap, 0) : new Thickness(0, 0, 0, trailingGap);
                item.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            }
        }
        RaiseContentWidthChanged();
    }

    protected override async void OnActivated()
    {
        _activation = new CancellationTokenSource();
        var token = _activation.Token;
        TransitionChanged += RefreshState;
        _loadingRadios = true;
        RefreshState();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var radios = await Radio.GetRadiosAsync().AsTask(timeout.Token);
            if (token.IsCancellationRequested) return;

            _radios = radios.Where(radio => radio.Kind is RadioKind.WiFi or RadioKind.Bluetooth).ToArray();
            foreach (var radio in _radios)
                radio.StateChanged += Radio_StateChanged;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException
            or OperationCanceledException)
        {
            AppLog.Warning($"Quick settings radio discovery failed ({ex.GetType().Name}).");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _loadingRadios = false;
                Render();
            }
        }
    }

    protected override void OnDeactivated()
    {
        _activation?.Cancel();
        _activation?.Dispose();
        _activation = null;
        TransitionChanged -= RefreshState;
        foreach (var radio in _radios)
            radio.StateChanged -= Radio_StateChanged;
        _radios = [];
        _nightLightOn = null;
        _loadingRadios = false;
        _errors.Clear();
        Render();
    }

    protected override void OnTick(DateTime now)
    {
        if (now.Second % 5 == 0) RefreshState();
    }

    protected override void OnLanguageChanged() => Render();

    private void Radio_StateChanged(Radio sender, object args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsActive && _radios.Contains(sender)) Render();
        });

    private void RefreshState()
    {
        try
        {
            _nightLightOn = ReadNightLight();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            _nightLightOn = null;
        }
        Render();
    }

    private Radio[] RadiosFor(Setting setting) =>
        _radios.Where(radio => radio.Kind == (setting == Setting.Wifi ? RadioKind.WiFi : RadioKind.Bluetooth)).ToArray();

    private static bool? RadioIsOn(IReadOnlyList<Radio> radios)
    {
        if (radios.Any(radio => radio.State == RadioState.On)) return true;
        return radios.Any(radio => radio.State == RadioState.Off) ? false : null;
    }

    private void Render()
    {
        RenderSetting(Setting.Wifi, WifiToggle, WifiPill, RadioIsOn(RadiosFor(Setting.Wifi)));
        RenderSetting(Setting.Bluetooth, BluetoothToggle, BluetoothPill, RadioIsOn(RadiosFor(Setting.Bluetooth)));
        RenderSetting(Setting.NightLight, NightLightToggle, NightLightPill, _nightLightOn);
        ControlsStrip.IsEnabled = IsActive;
    }

    private void RenderSetting(Setting setting, ToggleButton button, Border pill, bool? on)
    {
        var pending = PendingTargets.TryGetValue(setting, out var target);
        var label = UiText.Get(SettingNames[setting]);
        var stateKey = pending ? (target ? "QuickTurningOn" : "QuickTurningOff")
            : _loadingRadios && setting != Setting.NightLight ? "QuickLoading"
            : on switch { true => "QuickOn", false => "QuickOff", null => "QuickUnavailable" };
        var state = UiText.Get(stateKey);
        if (!pending && _errors.TryGetValue(setting, out var error))
            state += " · " + UiText.Get(ErrorKey(error));

        var enabled = IsActive && !pending && on.HasValue;
        button.IsChecked = on == true;
        button.IsEnabled = enabled;
        button.ToolTip = $"{label} · {state}";
        ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetItemStatus(button, state);
        ApplyChipVisual(pill, button, on == true);
    }

    private static void ApplyChipVisual(Border pill, ToggleButton button, bool on)
    {
        if (on)
        {
            pill.SetResourceReference(Border.BackgroundProperty, "WidgetSelectedFill");
            pill.SetResourceReference(Border.BorderBrushProperty, "WidgetSelectedStroke");
            button.SetResourceReference(ForegroundProperty, "WidgetSelectedForeground");
        }
        else
        {
            pill.Background = Brushes.Transparent;
            pill.BorderBrush = Brushes.Transparent;
            button.SetResourceReference(ForegroundProperty, "TextSecondary");
        }
    }

    private void WifiToggle_Click(object sender, RoutedEventArgs e) => Toggle(Setting.Wifi);
    private void BluetoothToggle_Click(object sender, RoutedEventArgs e) => Toggle(Setting.Bluetooth);
    private void NightLightToggle_Click(object sender, RoutedEventArgs e) => Toggle(Setting.NightLight);

    private async void Toggle(Setting setting)
    {
        var activation = _activation;
        if (activation is null) return;
        if (PendingTargets.ContainsKey(setting))
        {
            Render();
            return;
        }

        if (setting == Setting.NightLight) RefreshState();
        var radios = setting == Setting.NightLight ? [] : RadiosFor(setting);
        var on = setting == Setting.NightLight ? _nightLightOn : RadioIsOn(radios);
        if (!on.HasValue)
        {
            Render();
            return;
        }

        var target = !on.Value;
        PendingTargets.Add(setting, target);
        var token = activation.Token;
        var result = ChangeResult.Failed;
        _errors.Remove(setting);
        try
        {
            TransitionChanged?.Invoke();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            result = setting == Setting.NightLight
                ? await SetNightLightAsync(target, timeout.Token)
                : await SetRadiosAsync(radios, target, timeout.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException
            or IOException or SecurityException or OperationCanceledException)
        {
            AppLog.Warning($"Quick settings {setting} change failed ({ex.GetType().Name}).");
            if (ex is UnauthorizedAccessException or SecurityException) result = ChangeResult.Denied;
        }
        finally
        {
            PendingTargets.Remove(setting);
            if (!token.IsCancellationRequested && result != ChangeResult.Applied)
                _errors[setting] = result;
            TransitionChanged?.Invoke();
        }

        if (!token.IsCancellationRequested && result != ChangeResult.Applied)
            Report(ErrorKey(result));
    }

    private static string ErrorKey(ChangeResult result) => result switch
    {
        ChangeResult.Denied => "QuickPermissionDenied",
        ChangeResult.Unavailable => "QuickUnavailable",
        _ => "QuickChangeFailed"
    };

    private static async Task<ChangeResult> SetRadiosAsync(Radio[] radios, bool on, CancellationToken token)
    {
        // Share permission discovery while Wi-Fi and Bluetooth requests overlap. A denial is not cached.
        var pendingAccess = _radioAccess ??= RequestRadioAccessAsync();
        RadioAccessStatus access;
        try
        {
            access = await pendingAccess;
        }
        finally
        {
            if (ReferenceEquals(_radioAccess, pendingAccess) && !pendingAccess.IsCompletedSuccessfully)
                _radioAccess = null;
        }

        if (access != RadioAccessStatus.Allowed)
        {
            if (ReferenceEquals(_radioAccess, pendingAccess)) _radioAccess = null;
            return ChangeResult.Denied;
        }
        token.ThrowIfCancellationRequested();

        var target = on ? RadioState.On : RadioState.Off;
        var controllable = radios.Where(radio => radio.State is RadioState.On or RadioState.Off).ToArray();
        if (controllable.Length == 0) return ChangeResult.Unavailable;

        foreach (var radio in controllable)
        {
            if (radio.State == target) continue;
            if (await radio.SetStateAsync(target).AsTask(token) != RadioAccessStatus.Allowed)
                return ChangeResult.Denied;
        }

        // Allowed is permission to change the radio. Drivers may apply it later or override it.
        bool Confirmed() => controllable.All(radio =>
        {
            var state = radio.State;
            return state == target || (!on && state == RadioState.Disabled);
        });
        while (!Confirmed())
            await Task.Delay(100, token);
        return ChangeResult.Applied;
    }

    private static async Task<RadioAccessStatus> RequestRadioAccessAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        return await Radio.RequestAccessAsync().AsTask(timeout.Token);
    }

    private async Task<ChangeResult> SetNightLightAsync(bool on, CancellationToken token)
    {
        var written = await Task.Run(() => WriteNightLight(on), token);
        if (!written) return ChangeResult.Unavailable;

        // Give the shell a chance to reconcile CloudStore before checking the persisted state.
        await Task.Delay(300, token);
        return ReadNightLight() == on ? ChangeResult.Applied : ChangeResult.Failed;
    }

    private void AddShortcuts()
    {
        var windows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

        AddShortcut("QuickAvailableNetworks", "IconSearch", ShellShortcuts.OpenAvailableNetworks);
        AddSettingsLink("QuickBluetoothSettings", "IconSettings", "ms-settings:bluetooth");
        AddSettingsLink("QuickNightLightSettings", "IconPalette", "ms-settings:nightlight");
        AddSettingsLink("QuickAirplaneMode", "IconAirplane", "ms-settings:network-airplanemode");
        AddSettingsLink("QuickCellular", "IconCellular", "ms-settings:network-cellular");
        AddSettingsLink("Hotspot", "IconHotspot", "ms-settings:network-mobilehotspot");
        AddSettingsLink("LauncherVpn", "IconShield", "ms-settings:network-vpn");
        AddSettingsLink("QuickBrightness", "IconSun", "ms-settings:display");
        AddSettingsLink("QuickRotationLock", "IconRotate", "ms-settings:screenrotation");
        AddSettingsLink("Volume", "IconVolume", "ms-settings:sound");
        AddSettingsLink("QuickNearbySharing", "IconLink", "ms-settings:crossdevice");
        AddSettingsLink("LauncherAccessibility", "IconAccessibility", "ms-settings:easeofaccess-display");
        AddSettingsLink("QuickEnergySaver", "IconBattery", windows11 ? "ms-settings:powersleep" : "ms-settings:batterysaver");
        AddSettingsLink("QuickNotificationMode", "IconBellOff", windows11 ? "ms-settings:notifications" : "ms-settings:quiethours");
        AddShortcut("Display", "IconDesktops", ShellShortcuts.OpenProjection);
        AddShortcut("QuickCast", "IconCast", _ => ShellShortcuts.OpenCast());

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            AddSettingsLink("QuickLiveCaptions", "IconCaptions", "ms-settings:easeofaccess-closedcaptioning");
            AddShortcut("QuickStudioEffects", "IconTarget", ShellShortcuts.OpenQuickSettings);
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            AddSettingsLink("QuickHearingDevices", "IconHeadphones", "ms-settings:easeofaccess-hearingaids");
        if (windows11)
            AddShortcut("NotificationCenter", "IconBell", ShellShortcuts.OpenNotificationCenter);
        AddShortcut("QuickOpenWindowsPanel", "IconSliders", ShellShortcuts.OpenQuickSettings);

        void AddSettingsLink(string labelKey, string iconKey, string uri) =>
            AddShortcut(labelKey, iconKey, _ => ShellShortcuts.OpenUri(uri));
    }

    private void AddShortcut(string labelKey, string iconKey, Func<FrameworkElement, string?> action)
    {
        var icon = new ContentControl();
        icon.SetResourceReference(StyleProperty, "WidgetIcon");
        icon.SetResourceReference(ContentControl.ContentProperty, iconKey);

        var button = new Button { Content = icon, Width = ButtonSize, Height = ButtonSize };
        button.SetResourceReference(StyleProperty, "WidgetIconButton");
        var label = new Binding($"[{labelKey}]") { Source = UiText.Current };
        button.SetBinding(ToolTipProperty, label);
        button.SetBinding(AutomationProperties.NameProperty, label);
        button.Click += (_, _) => Report(action(button));
        ControlsStrip.Children.Add(button);
    }
}
