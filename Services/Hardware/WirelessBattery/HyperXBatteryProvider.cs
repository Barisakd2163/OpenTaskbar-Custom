using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room HyperX wireless headset and mouse battery provider (VID 0x03F0 HP / 0x0951 Kingston).
/// Supports Cloud II Wireless, Cloud III Wireless, Cloud Alpha Wireless, Cloud Flight / Flight S,
/// Cloud Stinger 2 Wireless, Pulsefire Haste 2 Wireless, and Pulsefire Dart Wireless.
/// </summary>
internal sealed class HyperXBatteryProvider : IWirelessBatteryProvider
{
    public const ushort HpVendorId = 0x03F0;
    public const ushort KingstonVendorId = 0x0951;

    public const ushort UsagePageVendorFf00 = 0xFF00;
    public const ushort UsagePageVendorFf13 = 0xFF13;
    public const ushort UsagePageVendorFf90 = 0xFF90;
    public const ushort UsagePageVendorFfc0 = 0xFFC0;

    internal enum HyperXProtocolFamily
    {
        HpCloudHeadset64,    // [0x66, 0x89] -> level at byte 4, charging at byte 5
        KingstonCloud20,     // [0x06, 0x00, 0x02, ..., 0xBB] / [0x21, 0xFF, 0x05] -> level at byte 7 or 3
        PulsefireMouse64     // [0x04, 0x03] / [0x06, 0x03] -> level at byte 4, charging at byte 5
    }

    internal sealed record HyperXDeviceProfile(
        ushort VendorId,
        ushort ProductId,
        string DefaultName,
        BluetoothDeviceKind Kind,
        HyperXProtocolFamily Protocol);

    private static readonly HyperXDeviceProfile[] Profiles =
    [
        // Cloud III Wireless (HP)
        new(HpVendorId, 0x05B7, "HyperX Cloud III Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.HpCloudHeadset64),
        new(HpVendorId, 0x0C9D, "HyperX Cloud III Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.HpCloudHeadset64),

        // Cloud Alpha Wireless (HP / Kingston)
        new(HpVendorId, 0x098D, "HyperX Cloud Alpha Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.HpCloudHeadset64),
        new(KingstonVendorId, 0x1743, "HyperX Cloud Alpha Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.HpCloudHeadset64),

        // Cloud II Wireless (HP / Kingston)
        new(HpVendorId, 0x018B, "HyperX Cloud II Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.HpCloudHeadset64),
        new(HpVendorId, 0x0696, "HyperX Cloud II Core Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.HpCloudHeadset64),
        new(KingstonVendorId, 0x1718, "HyperX Cloud II Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.KingstonCloud20),

        // Cloud Flight / Flight S / Stinger 2 Wireless
        new(KingstonVendorId, 0x16C4, "HyperX Cloud Flight Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.KingstonCloud20),
        new(KingstonVendorId, 0x16EA, "HyperX Cloud Flight S", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.KingstonCloud20),
        new(HpVendorId, 0x0D93, "HyperX Cloud Stinger 2 Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.HpCloudHeadset64),
        new(KingstonVendorId, 0x1733, "HyperX Cloud Stinger Core Wireless", BluetoothDeviceKind.Headphones, HyperXProtocolFamily.KingstonCloud20),

        // Pulsefire Haste 2 Wireless & Pulsefire Dart Wireless
        new(HpVendorId, 0x02B8, "HyperX Pulsefire Haste 2 Wireless", BluetoothDeviceKind.Mouse, HyperXProtocolFamily.PulsefireMouse64),
        new(HpVendorId, 0x0F8E, "HyperX Pulsefire Haste 2 Wireless", BluetoothDeviceKind.Mouse, HyperXProtocolFamily.PulsefireMouse64),
        new(KingstonVendorId, 0x16E1, "HyperX Pulsefire Dart Wireless", BluetoothDeviceKind.Mouse, HyperXProtocolFamily.PulsefireMouse64)
    ];

    public WirelessBatterySource Source => WirelessBatterySource.HyperXHid;

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

    internal static bool IsSupportedProduct(ushort vendorId, ushort productId) =>
        TryGetProfile(vendorId, productId, out _);

    internal static bool TryGetProfile(ushort vendorId, ushort productId, out HyperXDeviceProfile? profile)
    {
        for (var i = 0; i < Profiles.Length; i++)
        {
            var candidate = Profiles[i];
            if (candidate.VendorId == vendorId && candidate.ProductId == productId)
            {
                profile = candidate;
                return true;
            }
        }
        profile = null;
        return false;
    }

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        TryGetProfile(descriptor.VendorId, descriptor.ProductId, out _)
        && descriptor.UsagePage is UsagePageVendorFf00 or UsagePageVendorFf13 or UsagePageVendorFf90 or UsagePageVendorFfc0
        && descriptor.InputReportLength is >= 20 and <= 65
        && descriptor.OutputReportLength is >= 20 and <= 65;

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
            if (!IsMatchingInterface(descriptor) || !TryGetProfile(descriptor.VendorId, descriptor.ProductId, out var profile) || profile is null)
                continue;
            if (seenProducts.Contains((descriptor.VendorId, descriptor.ProductId)))
                continue;

            if (!Win32HidTransport.TryOpenSession(descriptor.DevicePath, featureOnly: false, out var session, out _)
                || session is null)
            {
                continue;
            }

            using (session)
            {
                var outLen = Math.Max((int)descriptor.OutputReportLength, 20);
                var inLen = Math.Max((int)descriptor.InputReportLength, 20);
                var request = BuildBatteryRequest(profile.Protocol, outLen);
                var response = new byte[inLen];

                if (!session.TryTransactInterrupt(
                        request,
                        response,
                        timeoutMs: 240,
                        validator: span => IsValidReplyHeader(profile.Protocol, span),
                        out var bytesRead,
                        out _))
                {
                    continue;
                }

                var deviceName = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName.Trim()
                    : profile.DefaultName;
                var deviceId = $"hyperx:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (TryParseBatteryReply(profile.Protocol, response.AsSpan(0, bytesRead), deviceId, deviceName, profile.Kind, now, out var reading)
                    && reading is not null)
                {
                    seenProducts.Add((descriptor.VendorId, descriptor.ProductId));
                    results.Add(reading);
                }
            }
        }

        return results;
    }

    internal static byte[] BuildBatteryRequest(HyperXProtocolFamily protocol, int reportLength = 64)
    {
        var len = Math.Clamp(reportLength, 20, 65);
        var packet = new byte[len];
        switch (protocol)
        {
            case HyperXProtocolFamily.HpCloudHeadset64:
                packet[0] = 0x66;
                packet[1] = 0x89;
                break;
            case HyperXProtocolFamily.KingstonCloud20:
                packet[0] = 0x21;
                packet[1] = 0xFF;
                packet[2] = 0x05;
                break;
            case HyperXProtocolFamily.PulsefireMouse64:
                packet[0] = 0x04;
                packet[1] = 0x03;
                break;
        }
        return packet;
    }

    private static ReadOnlySpan<byte> NormalizeFraming(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 6 && raw[0] == 0x00 && raw[1] is 0x66 or 0x21 or 0x04)
            return raw[1..];
        return raw;
    }

    internal static bool IsValidReplyHeader(HyperXProtocolFamily protocol, ReadOnlySpan<byte> raw)
    {
        var payload = NormalizeFraming(raw);
        return protocol switch
        {
            HyperXProtocolFamily.HpCloudHeadset64 => payload.Length >= 6 && payload[0] == 0x66 && payload[1] == 0x89,
            HyperXProtocolFamily.KingstonCloud20 => payload.Length >= 8 && payload[0] == 0x21 && payload[1] == 0xFF && payload[2] == 0x05,
            HyperXProtocolFamily.PulsefireMouse64 => payload.Length >= 6 && payload[0] == 0x04 && payload[1] == 0x03,
            _ => false
        };
    }

    internal static bool TryParseBatteryReply(
        HyperXProtocolFamily protocol,
        ReadOnlySpan<byte> rawReply,
        string id,
        string name,
        BluetoothDeviceKind kind,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        var reply = NormalizeFraming(rawReply);
        if (!IsValidReplyHeader(protocol, reply)) return false;

        int batteryPercent;
        bool isCharging;

        switch (protocol)
        {
            case HyperXProtocolFamily.HpCloudHeadset64:
            case HyperXProtocolFamily.PulsefireMouse64:
                batteryPercent = reply[4];
                if (batteryPercent is < 0 or > 100) return false;
                if (reply[5] > 2) return false;
                isCharging = reply[5] == 1;
                if (batteryPercent == 0 && !isCharging) return false;
                break;

            case HyperXProtocolFamily.KingstonCloud20:
                batteryPercent = reply[7] is > 0 and <= 100 ? reply[7] : reply[3];
                if (batteryPercent is <= 0 or > 100) return false;
                isCharging = reply[4] == 1;
                break;

            default:
                return false;
        }

        reading = new WirelessDeviceReading(
            Id: id,
            Name: string.IsNullOrWhiteSpace(name) ? "HyperX Wireless" : name.Trim(),
            Kind: kind,
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.HyperXHid,
            SampledAt: sampledAt);
        return true;
    }
}
