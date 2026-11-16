using System.Windows;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

public partial class SystemStatsWidget : DockWidget
{
    private const int LowBatteryPercent = 15;
    private readonly SystemStats _stats = new();
    private int _tick;

    public SystemStatsWidget()
    {
        InitializeComponent();
    }

    protected override void OnActivated()
    {
        _tick = 0;
        _stats.SampleCpu();
    }

    protected override void OnTick(DateTime now)
    {
        // CPU every 2 s smooths spikes; memory and battery change slowly.
        if (_tick++ % 2 != 0) return;

        if (_stats.SampleCpu() is { } cpu) SetMeter(CpuRing, CpuButton, cpu, "Cpu");
        if (SystemStats.SampleMemory() is { } memory) SetMeter(MemoryRing, MemoryButton, memory, "Memory");
        UpdateBattery(SystemStats.SampleBattery());
    }

    private static void SetMeter(RingMeter ring, FrameworkElement owner, double value, string labelKey)
    {
        var percent = (int)Math.Round(value * 100);
        ring.Value = value;
        ring.Text = percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ring.SetResourceReference(RingMeter.ValueBrushProperty, value >= 0.9 ? "WidgetFocusAccent" : value >= 0.75 ? "WidgetWarning" : "WidgetAccent");
        owner.ToolTip = $"{UiText.Get(labelKey)}: {UiText.Format("Percent", percent)}";
    }

    private void UpdateBattery(BatteryStatus battery)
    {
        BatteryButton.Visibility = battery.Present ? Visibility.Visible : Visibility.Collapsed;
        if (!battery.Present) return;
        BatteryText.Text = battery.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ChargingIcon.Visibility = battery.Charging ? Visibility.Visible : Visibility.Collapsed;
        BatteryButton.SetResourceReference(BackgroundProperty,
            battery.Percent <= LowBatteryPercent && !battery.PluggedIn ? "WidgetFocusAccent" : "WidgetAccentFill");
        BatteryButton.ToolTip = $"{UiText.Get(battery.Charging ? "BatteryCharging" : "Battery")}: {UiText.Format("Percent", battery.Percent)}";
    }

    private void BatteryButton_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenUri("ms-settings:batterysaver"));

    private void TaskManager_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenUri("taskmgr.exe"));
}
