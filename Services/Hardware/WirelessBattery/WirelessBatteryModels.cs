using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

public enum WirelessBatterySource
{
    Bluetooth,
    XInput,
    SteelSeriesHid,
    SteelSeriesGg,
    LogitechHidPlusPlus,
    LogitechGHub,
    CompxAtkVxe,
    RazerHid
}

public sealed record WirelessDeviceReading(
    string Id,
    string Name,
    BluetoothDeviceKind Kind,
    int BatteryLevel,
    bool IsConnected,
    bool IsCharging = false,
    bool IsStale = false,
    WirelessBatterySource Source = WirelessBatterySource.Bluetooth,
    DateTimeOffset SampledAt = default,
    string? LevelOverrideText = null,
    int? SecondaryBatteryLevel = null,
    int? VoltageMv = null)
{
    public string SourceLabel => GetSourceLabel(Source);

    public static string GetSourceLabel(WirelessBatterySource source) => source switch
    {
        WirelessBatterySource.SteelSeriesHid or WirelessBatterySource.SteelSeriesGg => "SteelSeries",
        WirelessBatterySource.CompxAtkVxe => "ATK/VXE",
        WirelessBatterySource.LogitechHidPlusPlus or WirelessBatterySource.LogitechGHub => "HID++",
        WirelessBatterySource.RazerHid => "Razer",
        WirelessBatterySource.XInput => "XInput",
        _ => "Bluetooth"
    };

    public BluetoothDeviceBatteryInfo ToBatteryInfo() => new(
        Name: Name,
        Id: Id,
        IsConnected: IsConnected,
        BatteryLevel: BatteryLevel,
        Kind: Kind,
        IsCharging: IsCharging,
        IsStale: IsStale,
        SourceLabel: SourceLabel,
        SampledAt: SampledAt == default ? null : SampledAt,
        LevelOverrideText: LevelOverrideText,
        SecondaryBatteryLevel: SecondaryBatteryLevel);
}

internal interface IWirelessBatteryProvider
{
    WirelessBatterySource Source { get; }
    Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(CancellationToken cancellationToken);
}
