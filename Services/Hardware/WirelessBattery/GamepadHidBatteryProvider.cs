using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room direct HID input report battery reader for Sony DualSense (0x054C:0x0CE6),
/// DualSense Edge (0x054C:0x0DF2), DualShock 4 v1/v2 (0x054C:0x05C4, 0x09CC) over USB &amp; Bluetooth,
/// and Nintendo Switch Pro Controller (0x057E:0x2009).
/// </summary>
internal sealed class GamepadHidBatteryProvider : IWirelessBatteryProvider
{
    public const ushort SonyVendorId = 0x054C;
    public const ushort DualShock4V1Pid = 0x05C4;
    public const ushort DualShock4V2Pid = 0x09CC;
    public const ushort DualSensePid = 0x0CE6;
    public const ushort DualSenseEdgePid = 0x0DF2;

    public const ushort NintendoVendorId = 0x057E;
    public const ushort SwitchProControllerPid = 0x2009;

    public const ushort UsagePageGenericDesktop = 0x0001;
    public const ushort UsageJoystick = 0x0004;
    public const ushort UsageGamepad = 0x0005;

    public const byte DualSenseUsbReportId = 0x01;
    public const byte DualSenseBtReportId = 0x31;
    public const byte DualShock4UsbReportId = 0x01;
    public const byte DualShock4BtReportId = 0x11;
    public const byte SwitchProFullReportId = 0x30;

    public WirelessBatterySource Source => WirelessBatterySource.GamepadHid;

    public Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(
        IReadOnlyList<HidInterfaceDescriptor> hidInterfaces,
        CancellationToken cancellationToken)
    {
        if (hidInterfaces.Count == 0 || !HasAnyMatchingInterface(hidInterfaces))
            return Task.FromResult<IReadOnlyList<WirelessDeviceReading>>([]);

        return Task.Run(() => ProbeHidDevices(hidInterfaces, cancellationToken), CancellationToken.None);
    }

    internal static bool HasAnyMatchingInterface(IReadOnlyList<HidInterfaceDescriptor> interfaces)
    {
        for (var i = 0; i < interfaces.Count; i++)
        {
            if (IsMatchingInterface(interfaces[i]))
                return true;
        }
        return false;
    }

    internal static bool IsSupportedGamepad(ushort vendorId, ushort productId) => (vendorId, productId) is
        (SonyVendorId, DualShock4V1Pid or DualShock4V2Pid or DualSensePid or DualSenseEdgePid)
        or (NintendoVendorId, SwitchProControllerPid);

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        IsSupportedGamepad(descriptor.VendorId, descriptor.ProductId)
        && descriptor.UsagePage == UsagePageGenericDesktop
        && descriptor.Usage is UsageGamepad or UsageJoystick
        && descriptor.InputReportLength >= 16;

    internal static IReadOnlyList<WirelessDeviceReading> ProbeHidDevices(
        IReadOnlyList<HidInterfaceDescriptor> interfaces,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WirelessDeviceReading>();
        var now = DateTimeOffset.UtcNow;
        var seenDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in interfaces)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!IsMatchingInterface(descriptor)) continue;

            var key = $"{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";
            if (seenDevices.Contains(key)) continue;

            if (!Win32HidTransport.TryOpenSession(descriptor.DevicePath, featureOnly: false, out var session, out _)
                || session is null)
            {
                continue;
            }

            using (session)
            {
                var inLen = Math.Max((int)descriptor.InputReportLength, 64);
                var buffer = new byte[inLen];
                if (!session.TryReadReport(buffer, timeoutMs: 120, out var bytesRead, out _) || bytesRead <= 0)
                    continue;

                var deviceName = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName.Trim()
                    : GetDefaultGamepadName(descriptor.VendorId, descriptor.ProductId);
                var deviceId = $"gamepad:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (TryParseGamepadReport(
                        descriptor.VendorId,
                        descriptor.ProductId,
                        buffer.AsSpan(0, bytesRead),
                        deviceId,
                        deviceName,
                        now,
                        out var reading)
                    && reading is not null)
                {
                    seenDevices.Add(key);
                    results.Add(reading);
                }
            }
        }

        return results;
    }

    internal static bool TryParseGamepadReport(
        ushort vendorId,
        ushort productId,
        ReadOnlySpan<byte> report,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        return (vendorId, productId) switch
        {
            (SonyVendorId, DualSensePid or DualSenseEdgePid) =>
                TryParseDualSenseReport(report, id, string.IsNullOrWhiteSpace(name) ? GetDefaultGamepadName(vendorId, productId) : name, sampledAt, out reading),
            (SonyVendorId, DualShock4V1Pid or DualShock4V2Pid) =>
                TryParseDualShock4Report(report, id, string.IsNullOrWhiteSpace(name) ? GetDefaultGamepadName(vendorId, productId) : name, sampledAt, out reading),
            (NintendoVendorId, SwitchProControllerPid) =>
                TryParseSwitchProReport(report, id, string.IsNullOrWhiteSpace(name) ? GetDefaultGamepadName(vendorId, productId) : name, sampledAt, out reading),
            _ => false
        };
    }

    internal static bool TryParseDualSenseReport(
        ReadOnlySpan<byte> report,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        if (report.Length < 54) return false;

        int batteryByteOffset;
        if (report[0] == DualSenseUsbReportId && report.Length >= 54)
        {
            batteryByteOffset = 53;
        }
        else if (report[0] == DualSenseBtReportId && report.Length >= 55)
        {
            batteryByteOffset = 54;
        }
        else
        {
            return false;
        }

        var rawBattery = report[batteryByteOffset];
        var rawStep = rawBattery & 0x0F;
        var powerState = (rawBattery >> 4) & 0x0F;

        // Power states: 0x00 = discharging, 0x01 = charging, 0x02 = complete/full; >= 0x0A = hardware error.
        if (powerState > 0x02) return false;
        if (powerState != 0x02 && rawStep > 10) return false;

        var isCharging = powerState == 0x01;
        var batteryPercent = powerState == 0x02
            ? 100
            : Math.Clamp(rawStep * 10, 0, 100);

        reading = new WirelessDeviceReading(
            Id: id,
            Name: name.Trim(),
            Kind: BluetoothDeviceKind.Gamepad,
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.GamepadHid,
            SampledAt: sampledAt);
        return true;
    }

    internal static bool TryParseDualShock4Report(
        ReadOnlySpan<byte> report,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        int batteryByteOffset;
        if (report.Length >= 31 && report[0] == DualShock4UsbReportId)
        {
            batteryByteOffset = 30;
        }
        else if (report.Length >= 33 && report[0] == DualShock4BtReportId)
        {
            batteryByteOffset = 32;
        }
        else
        {
            return false;
        }

        var rawBattery = report[batteryByteOffset];
        var isCableConnected = (rawBattery & 0x10) != 0;
        var rawStep = rawBattery & 0x0F;

        if (rawStep > 11) return false;
        if (!isCableConnected && rawStep > 10) return false;

        var batteryPercent = Math.Clamp(rawStep * 10, 0, 100);
        var isCharging = isCableConnected && rawStep < 11;

        reading = new WirelessDeviceReading(
            Id: id,
            Name: name.Trim(),
            Kind: BluetoothDeviceKind.Gamepad,
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.GamepadHid,
            SampledAt: sampledAt);
        return true;
    }

    internal static bool TryParseSwitchProReport(
        ReadOnlySpan<byte> report,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        if (report.Length < 13 || report[0] != SwitchProFullReportId)
            return false;

        var rawByte = report[2];
        if (rawByte == 0) return false;

        var isCharging = (rawByte & 0x10) != 0;
        var levelNibble = (rawByte >> 5) & 0x07;
        if (levelNibble > 4) return false;

        var batteryPercent = levelNibble switch
        {
            4 => 100,
            3 => 75,
            2 => 50,
            1 => 25,
            _ => 10
        };

        reading = new WirelessDeviceReading(
            Id: id,
            Name: name.Trim(),
            Kind: BluetoothDeviceKind.Gamepad,
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.GamepadHid,
            SampledAt: sampledAt);
        return true;
    }

    internal static string GetDefaultGamepadName(ushort vendorId, ushort productId) => (vendorId, productId) switch
    {
        (SonyVendorId, DualSensePid) => "DualSense Wireless Controller",
        (SonyVendorId, DualSenseEdgePid) => "DualSense Edge Wireless Controller",
        (SonyVendorId, DualShock4V1Pid or DualShock4V2Pid) => "DUALSHOCK 4 Wireless Controller",
        (NintendoVendorId, SwitchProControllerPid) => "Switch Pro Controller",
        _ => "Wireless Controller"
    };
}
