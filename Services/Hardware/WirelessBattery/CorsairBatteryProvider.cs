using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room Corsair Slipstream &amp; Bragi wireless peripheral battery provider (VID 0x1B1C).
/// Supports Bragi 64-byte vendor HID collections (UsagePage 0xFF42, Property 0x0F battery permille/percent
/// and Property 0x10 charging status) as well as legacy Corsair wireless headset collections (UsagePage 0xFFC2).
/// </summary>
internal sealed class CorsairBatteryProvider : IWirelessBatteryProvider
{
    public const ushort CorsairVendorId = 0x1B1C;
    public const ushort BragiUsagePage = 0xFF42;
    public const ushort LegacyHeadsetUsagePage = 0xFFC2;

    public const byte BragiHostEndpoint = 0x08;
    public const byte BragiSubDeviceEndpoint = 0x09;
    public const byte BragiCommandGetProperty = 0x02;

    public const ushort BragiPropertyBatteryLevel = 0x000F;
    public const ushort BragiPropertyBatteryStatus = 0x0010;

    public const byte LegacyHeadsetCommand = 0xC9;
    public const byte LegacyHeadsetSubCommand = 0x64;

    public WirelessBatterySource Source => WirelessBatterySource.CorsairHid;

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
        descriptor.VendorId == CorsairVendorId
        && descriptor.UsagePage is BragiUsagePage or LegacyHeadsetUsagePage
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
                var name = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName.Trim()
                    : "Corsair Wireless";
                var kind = ClassifyCorsairKind(name);
                var id = $"corsair:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (descriptor.UsagePage == BragiUsagePage)
                {
                    foreach (var endpoint in new[] { BragiSubDeviceEndpoint, BragiHostEndpoint })
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        var levelReq = BuildBragiGetPropertyRequest(endpoint, BragiPropertyBatteryLevel, outLen);
                        var levelResp = new byte[inLen];
                        if (!session.TryTransactInterrupt(
                                levelReq,
                                levelResp,
                                timeoutMs: 220,
                                validator: IsValidBragiPropertyReply,
                                out var readLen,
                                out _)
                            || !TryParseBragiBatteryLevel(levelResp.AsSpan(0, readLen), out var batteryPercent))
                        {
                            continue;
                        }

                        var isCharging = false;
                        var statusReq = BuildBragiGetPropertyRequest(endpoint, BragiPropertyBatteryStatus, outLen);
                        var statusResp = new byte[inLen];
                        if (session.TryTransactInterrupt(
                                statusReq,
                                statusResp,
                                timeoutMs: 180,
                                validator: IsValidBragiPropertyReply,
                                out var statusLen,
                                out _))
                        {
                            TryParseBragiChargingStatus(statusResp.AsSpan(0, statusLen), out isCharging);
                        }

                        seenProducts.Add(descriptor.ProductId);
                        results.Add(new WirelessDeviceReading(
                            Id: id,
                            Name: name,
                            Kind: kind,
                            BatteryLevel: batteryPercent,
                            IsConnected: true,
                            IsCharging: isCharging,
                            IsStale: false,
                            Source: WirelessBatterySource.CorsairHid,
                            SampledAt: now));
                        break;
                    }
                }
                else if (descriptor.UsagePage == LegacyHeadsetUsagePage)
                {
                    var req = BuildLegacyHeadsetRequest(outLen);
                    var resp = new byte[inLen];
                    if (session.TryTransactInterrupt(
                            req,
                            resp,
                            timeoutMs: 220,
                            validator: IsValidLegacyHeadsetReply,
                            out var readLen,
                            out _)
                        && TryParseLegacyHeadsetReply(resp.AsSpan(0, readLen), id, name, now, out var reading)
                        && reading is not null)
                    {
                        seenProducts.Add(descriptor.ProductId);
                        results.Add(reading);
                    }
                }
            }
        }

        return results;
    }

    internal static byte[] BuildBragiGetPropertyRequest(
        byte endpoint = BragiSubDeviceEndpoint,
        ushort propertyId = BragiPropertyBatteryLevel,
        int reportLength = 64)
    {
        var len = Math.Max(reportLength, 64);
        var packet = new byte[len];
        var offset = len == 65 ? 1 : 0;
        packet[offset] = endpoint;
        packet[offset + 1] = BragiCommandGetProperty;
        packet[offset + 2] = (byte)(propertyId & 0xFF);
        packet[offset + 3] = (byte)(propertyId >> 8);
        return packet;
    }

    internal static byte[] BuildLegacyHeadsetRequest(int reportLength = 64)
    {
        var len = Math.Max(reportLength, 64);
        var packet = new byte[len];
        var offset = len == 65 ? 1 : 0;
        packet[offset] = LegacyHeadsetCommand;
        packet[offset + 1] = LegacyHeadsetSubCommand;
        return packet;
    }

    private static ReadOnlySpan<byte> NormalizeFraming(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 7 && raw[0] == 0x00 && raw[1] is 0x00 or BragiHostEndpoint or BragiSubDeviceEndpoint or LegacyHeadsetCommand)
        {
            if (raw[1] is BragiHostEndpoint or BragiSubDeviceEndpoint or LegacyHeadsetCommand
                || (raw[1] == 0x00 && raw[2] == BragiCommandGetProperty))
            {
                return raw[1..];
            }
        }
        return raw;
    }

    internal static bool IsValidBragiPropertyReply(ReadOnlySpan<byte> raw)
    {
        var reply = NormalizeFraming(raw);
        // Bragi get-property response: [endpoint/0x00, 0x02, status=0x00, valueLo, valueHi, ...]
        return reply.Length >= 6
               && reply[1] == BragiCommandGetProperty
               && reply[2] == 0x00;
    }

    internal static bool TryParseBragiBatteryLevel(ReadOnlySpan<byte> rawReply, out int batteryPercent)
    {
        batteryPercent = -1;
        var reply = NormalizeFraming(rawReply);
        if (!IsValidBragiPropertyReply(reply)) return false;

        var rawValue = reply[3] | (reply[4] << 8);
        if (rawValue is <= 0 or > 1000) return false;

        // Bragi encodes battery level either in permille (0..1000 -> 0..100%) or direct percent (1..100).
        batteryPercent = rawValue > 100
            ? Math.Clamp((int)Math.Round(rawValue / 10.0), 0, 100)
            : rawValue;
        return batteryPercent is >= 0 and <= 100;
    }

    internal static bool TryParseBragiChargingStatus(ReadOnlySpan<byte> rawReply, out bool isCharging)
    {
        isCharging = false;
        var reply = NormalizeFraming(rawReply);
        if (!IsValidBragiPropertyReply(reply)) return false;

        var status = reply[3];
        if (status > 4) return false;
        isCharging = status is 1 or 2;
        return true;
    }

    internal static bool IsValidLegacyHeadsetReply(ReadOnlySpan<byte> raw)
    {
        var reply = NormalizeFraming(raw);
        return reply.Length >= 5
               && reply[0] == LegacyHeadsetCommand
               && reply[1] == LegacyHeadsetSubCommand;
    }

    internal static bool TryParseLegacyHeadsetReply(
        ReadOnlySpan<byte> rawReply,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        var reply = NormalizeFraming(rawReply);
        if (!IsValidLegacyHeadsetReply(reply)) return false;

        var rawLevel = reply[4] & 0x7F;
        var status = reply[2];
        var isCharging = (reply[4] & 0x80) != 0 || status is 0x02 or 0x04 or 0x05;
        if (rawLevel is 0 or > 100) return false;

        reading = new WirelessDeviceReading(
            Id: id,
            Name: string.IsNullOrWhiteSpace(name) ? "Corsair Wireless Headset" : name.Trim(),
            Kind: BluetoothDeviceKind.Headphones,
            BatteryLevel: rawLevel,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.CorsairHid,
            SampledAt: sampledAt);
        return true;
    }

    internal static BluetoothDeviceKind ClassifyCorsairKind(string name)
    {
        var classified = BluetoothBatteryWidget.ClassifyNode(name, string.Empty);
        if (classified != BluetoothDeviceKind.Generic) return classified;
        if (name.Contains("Virtuoso", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HS80", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HS70", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HS65", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HS55", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Void", StringComparison.OrdinalIgnoreCase))
        {
            return BluetoothDeviceKind.Headphones;
        }
        if (name.Contains("K65", StringComparison.OrdinalIgnoreCase)
            || name.Contains("K70", StringComparison.OrdinalIgnoreCase)
            || name.Contains("K100", StringComparison.OrdinalIgnoreCase))
        {
            return BluetoothDeviceKind.Keyboard;
        }
        return BluetoothDeviceKind.Mouse;
    }
}
