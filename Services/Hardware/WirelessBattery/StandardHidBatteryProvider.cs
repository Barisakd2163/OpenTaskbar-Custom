using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room standard USB/2.4G HID Battery Strength &amp; Power Device reader.
/// Reads Generic Device Controls (UsagePage 0x06, Usage 0x20 Battery Strength), Power Device (0x84),
/// and Battery System (0x85) capacities ONLY when the device's HID report descriptor explicitly
/// declares those read-only value capabilities. Never writes any output or feature reports.
/// </summary>
internal sealed class StandardHidBatteryProvider : IWirelessBatteryProvider
{
    public const ushort UsagePageGenericDeviceControls = 0x0006;
    public const ushort UsageBatteryStrength = 0x0020;

    public const ushort UsagePagePowerDevice = 0x0084;
    public const ushort UsagePageBatterySystem = 0x0085;

    public const ushort UsageRelativeStateOfCharge = 0x0064;
    public const ushort UsageAbsoluteStateOfCharge = 0x0065;
    public const ushort UsageRemainingCapacity = 0x0066;
    public const ushort UsageCharging = 0x0044;
    public const ushort UsageDischarging = 0x0045;
    public const ushort UsageAcPresent = 0x00D0;

    public WirelessBatterySource Source => WirelessBatterySource.StandardHid;

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

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor)
    {
        if (WirelessBatteryAggregator.IsIgnoredWiredDevice(descriptor.VendorId, descriptor.ProductId, descriptor.ProductName))
            return false;

        // Skip interfaces owned by dedicated vendor protocols so we never double-open their endpoints.
        if (descriptor.VendorId is SteelSeriesBatteryProvider.SteelSeriesVendorId
            or LogitechBatteryProvider.LogitechVendorId
            or RazerBatteryProvider.RazerVendorId
            or CorsairBatteryProvider.CorsairVendorId
            or AsusRogBatteryProvider.AsusVendorId
            || CompxAtkVxeBatteryProvider.IsSupportedVendor(descriptor.VendorId)
            || HyperXBatteryProvider.IsSupportedProduct(descriptor.VendorId, descriptor.ProductId)
            || GamepadHidBatteryProvider.IsSupportedGamepad(descriptor.VendorId, descriptor.ProductId)
            || WirelessKeyboard24gProvider.IsSupportedKeyboard(descriptor.VendorId, descriptor.ProductId))
        {
            return false;
        }

        if (descriptor.BatteryValueCaps is not { Count: > 0 })
            return false;

        return descriptor.BatteryValueCaps.Any(IsCapacityValueCap);
    }

    internal static bool IsCapacityValueCap(HidBatteryValueCap cap) =>
        (cap.UsagePage == UsagePageGenericDeviceControls && cap.Usage == UsageBatteryStrength)
        || (cap.UsagePage == UsagePagePowerDevice && cap.Usage == UsageAbsoluteStateOfCharge)
        || (cap.UsagePage == UsagePageBatterySystem
            && cap.Usage is UsageRelativeStateOfCharge or UsageAbsoluteStateOfCharge or UsageRemainingCapacity);

    internal static bool IsChargingValueCap(HidBatteryValueCap cap) =>
        (cap.UsagePage == UsagePageBatterySystem && cap.Usage == UsageCharging)
        || (cap.UsagePage == UsagePagePowerDevice && cap.Usage == UsageAcPresent);

    internal static IReadOnlyList<WirelessDeviceReading> ProbeHidDevices(
        IReadOnlyList<HidInterfaceDescriptor> interfaces,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WirelessDeviceReading>();
        var now = DateTimeOffset.UtcNow;
        var seenProducts = new HashSet<(ushort Vid, ushort Pid)>();

        foreach (var descriptor in interfaces)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!IsMatchingInterface(descriptor) || descriptor.BatteryValueCaps is null) continue;
            if (seenProducts.Contains((descriptor.VendorId, descriptor.ProductId))) continue;

            var capacityCap = descriptor.BatteryValueCaps.FirstOrDefault(IsCapacityValueCap);
            if (capacityCap is null) continue;

            if (!Win32HidTransport.TryOpenSession(descriptor.DevicePath, featureOnly: true, out var session, out _)
                || session is null)
            {
                continue;
            }

            using (session)
            {
                var reportLength = capacityCap.IsFeatureReport
                    ? Math.Max((int)descriptor.FeatureReportLength, 2)
                    : Math.Max((int)descriptor.InputReportLength, 2);

                if (reportLength is < 2 or > 512) continue;

                var buffer = new byte[reportLength];
                buffer[0] = capacityCap.ReportId;

                var readOk = capacityCap.IsFeatureReport
                    ? session.TryGetFeature(buffer, out _)
                    : session.TryGetInputReport(buffer, out _);

                if (!readOk) continue;

                int batteryPercent;
                if (session.TryExtractUsageValue(
                        capacityCap.IsFeatureReport,
                        capacityCap.UsagePage,
                        capacityCap.LinkCollection,
                        capacityCap.Usage,
                        buffer,
                        out var rawValue))
                {
                    batteryPercent = NormalizeLogicalValueToPercent((int)rawValue, capacityCap.LogicalMin, capacityCap.LogicalMax);
                }
                else if (!TryParseDirectByteReport(buffer, capacityCap.ReportId, capacityCap.LogicalMin, capacityCap.LogicalMax, out batteryPercent, out _))
                {
                    continue;
                }

                if (batteryPercent is <= 0 or > 100) continue;

                var isCharging = false;
                var chargingCap = descriptor.BatteryValueCaps.FirstOrDefault(c =>
                    IsChargingValueCap(c)
                    && c.IsFeatureReport == capacityCap.IsFeatureReport
                    && c.ReportId == capacityCap.ReportId);

                if (chargingCap is not null
                    && session.TryExtractUsageValue(
                        chargingCap.IsFeatureReport,
                        chargingCap.UsagePage,
                        chargingCap.LinkCollection,
                        chargingCap.Usage,
                        buffer,
                        out var chgVal))
                {
                    isCharging = chgVal != 0;
                }

                var name = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName.Trim()
                    : $"HID Wireless ({descriptor.VendorId:X4}:{descriptor.ProductId:X4})";
                var kind = ClassifyStandardHidKind(descriptor.UsagePage, descriptor.Usage, name);

                seenProducts.Add((descriptor.VendorId, descriptor.ProductId));
                results.Add(new WirelessDeviceReading(
                    Id: $"hid:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}",
                    Name: name,
                    Kind: kind,
                    BatteryLevel: batteryPercent,
                    IsConnected: true,
                    IsCharging: isCharging,
                    IsStale: false,
                    Source: WirelessBatterySource.StandardHid,
                    SampledAt: now));
            }
        }

        return results;
    }

    internal static int NormalizeLogicalValueToPercent(int rawValue, int logicalMin, int logicalMax)
    {
        if (logicalMax > logicalMin && logicalMax <= 10000)
        {
            if (rawValue < logicalMin || rawValue > logicalMax)
                return -1;
            var span = logicalMax - logicalMin;
            return Math.Clamp((int)Math.Round((rawValue - logicalMin) * 100.0 / span), 0, 100);
        }

        return rawValue is >= 0 and <= 100 ? rawValue : -1;
    }

    internal static bool TryParseDirectByteReport(
        ReadOnlySpan<byte> report,
        byte expectedReportId,
        int logicalMin,
        int logicalMax,
        out int batteryPercent,
        out bool isCharging)
    {
        batteryPercent = -1;
        isCharging = false;
        if (report.Length < 2) return false;
        if (expectedReportId != 0 && report[0] != expectedReportId) return false;

        var rawLevel = (int)report[1];
        batteryPercent = NormalizeLogicalValueToPercent(rawLevel, logicalMin, logicalMax);
        if (batteryPercent is < 0 or > 100)
            return false;

        if (report.Length >= 3 && report[2] <= 1)
            isCharging = report[2] == 1;

        if (batteryPercent == 0 && !isCharging)
            return false;

        return true;
    }

    internal static BluetoothDeviceKind ClassifyStandardHidKind(ushort usagePage, ushort usage, string name)
    {
        var byName = BluetoothBatteryWidget.ClassifyNode(name, string.Empty);
        if (byName != BluetoothDeviceKind.Generic) return byName;

        return (usagePage, usage) switch
        {
            (0x0001, 0x0002) => BluetoothDeviceKind.Mouse,
            (0x0001, 0x0006) or (0x0001, 0x0007) => BluetoothDeviceKind.Keyboard,
            (0x0001, 0x0004) or (0x0001, 0x0005) => BluetoothDeviceKind.Gamepad,
            (0x000C, _) or (0x000B, _) => BluetoothDeviceKind.Headphones,
            _ => BluetoothDeviceKind.Generic
        };
    }
}
