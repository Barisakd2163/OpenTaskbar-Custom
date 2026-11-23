using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room ASUS ROG Omni / SpeedNova 2.4 GHz wireless battery provider (VID 0x0B05).
/// Queries vendor HID collections (UsagePage 0xFF00 / 0xFF01, 64/65-byte reports, Command [0x12, 0x07])
/// for ROG Harpe Ace, Keris, Gladius, Chakram, Azoth, Falchion, Strix Scope, and Delta S Wireless.
/// </summary>
internal sealed class AsusRogBatteryProvider : IWirelessBatteryProvider
{
    public const ushort AsusVendorId = 0x0B05;
    public const ushort VendorUsagePage0 = 0xFF00;
    public const ushort VendorUsagePage1 = 0xFF01;

    public const byte CommandCategoryPower = 0x12;
    public const byte CommandGetBattery = 0x07;

    public WirelessBatterySource Source => WirelessBatterySource.AsusRogHid;

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

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        descriptor.VendorId == AsusVendorId
        && descriptor.UsagePage is VendorUsagePage0 or VendorUsagePage1
        && descriptor.InputReportLength is 64 or 65
        && descriptor.OutputReportLength is 64 or 65;

    internal static IReadOnlyList<WirelessDeviceReading> ProbeHidDevices(
        IReadOnlyList<HidInterfaceDescriptor> interfaces,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WirelessDeviceReading>();
        var now = DateTimeOffset.UtcNow;
        var seenProducts = new HashSet<ushort>();

        foreach (var descriptor in interfaces)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!IsMatchingInterface(descriptor)) continue;
            if (seenProducts.Contains(descriptor.ProductId)) continue;

            if (!Win32HidTransport.TryOpenSession(descriptor.DevicePath, featureOnly: false, out var session, out _)
                || session is null)
            {
                continue;
            }

            using (session)
            {
                var outLen = Math.Max((int)descriptor.OutputReportLength, 64);
                var inLen = Math.Max((int)descriptor.InputReportLength, 64);
                var request = BuildBatteryRequest(outLen);
                var response = new byte[inLen];

                if (!session.TryTransactInterrupt(
                        request,
                        response,
                        timeoutMs: 220,
                        validator: IsValidBatteryReply,
                        out var bytesRead,
                        out _))
                {
                    continue;
                }

                var name = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName.Trim()
                    : "ASUS ROG Wireless";
                var id = $"asus:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (TryParseBatteryReply(response.AsSpan(0, bytesRead), id, name, now, out var reading)
                    && reading is not null)
                {
                    seenProducts.Add(descriptor.ProductId);
                    results.Add(reading);
                }
            }
        }

        return results;
    }

    internal static byte[] BuildBatteryRequest(int reportLength = 64)
    {
        var len = Math.Max(reportLength, 64);
        var packet = new byte[len];
        var offset = len == 65 ? 1 : 0;
        packet[offset] = CommandCategoryPower;
        packet[offset + 1] = CommandGetBattery;
        return packet;
    }

    private static ReadOnlySpan<byte> NormalizeFraming(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 7 && raw[0] == 0x00 && raw[1] == CommandCategoryPower && raw[2] == CommandGetBattery)
            return raw[1..];
        return raw;
    }

    internal static bool IsValidBatteryReply(ReadOnlySpan<byte> raw)
    {
        var reply = NormalizeFraming(raw);
        return reply.Length >= 6
               && reply[0] == CommandCategoryPower
               && reply[1] == CommandGetBattery;
    }

    internal static bool TryParseBatteryReply(
        ReadOnlySpan<byte> rawReply,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        var reply = NormalizeFraming(rawReply);
        if (!IsValidBatteryReply(reply)) return false;

        var batteryPercent = (int)reply[4];
        var chargingState = reply[5];

        if (batteryPercent is < 0 or > 100 || chargingState > 3)
            return false;

        var isCharging = chargingState is 1 or 2;
        if (batteryPercent == 0 && !isCharging)
            return false;

        var cleanName = string.IsNullOrWhiteSpace(name) ? "ASUS ROG Wireless" : name.Trim();
        reading = new WirelessDeviceReading(
            Id: id,
            Name: cleanName,
            Kind: ClassifyRogKind(cleanName),
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.AsusRogHid,
            SampledAt: sampledAt);
        return true;
    }

    internal static BluetoothDeviceKind ClassifyRogKind(string name)
    {
        var classified = BluetoothBatteryWidget.ClassifyNode(name, string.Empty);
        if (classified != BluetoothDeviceKind.Generic) return classified;
        if (name.Contains("Azoth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Falchion", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Scope", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Claymore", StringComparison.OrdinalIgnoreCase))
        {
            return BluetoothDeviceKind.Keyboard;
        }
        if (name.Contains("Delta", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Cetra", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pelton", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pelta", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Strix Go", StringComparison.OrdinalIgnoreCase))
        {
            return BluetoothDeviceKind.Headphones;
        }
        if (name.Contains("Raikiri", StringComparison.OrdinalIgnoreCase))
            return BluetoothDeviceKind.Gamepad;
        return BluetoothDeviceKind.Mouse;
    }
}
