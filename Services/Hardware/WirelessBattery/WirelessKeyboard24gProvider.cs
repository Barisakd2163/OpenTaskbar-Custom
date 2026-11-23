using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room 2.4 GHz wireless mechanical keyboard battery provider covering
/// AULA (F75 / F87 / F99), Weikav (NUT75 / Lucky65), Akko / MonsGeek, and Royal Kludge (RK) 2.4G receivers.
/// Strictly gated by verified (VendorId, ProductId), vendor UsagePage (0xFF00 / 0xFF02), and exact report lengths.
/// Explicitly blocks wired Wraith W75 (0x2E3C:0xC368) and unknown devices.
/// </summary>
internal sealed class WirelessKeyboard24gProvider : IWirelessBatteryProvider
{
    public const ushort SinowealthVendorId = 0x258A;  // AULA / Royal Kludge / Weikav 2.4G receivers
    public const ushort CompxKeyboardVendorId = 0x3554; // Compx 2.4G keyboard receiver (PID 0xFA09)
    public const ushort MonsGeekAkkoVendorId = 0x3151;  // Akko / MonsGeek 2.4G receiver (PIDs 0x4011, 0x4015)

    public const ushort VendorUsagePageFf00 = 0xFF00;
    public const ushort VendorUsagePageFf02 = 0xFF02;

    public const int ShortVendorReportLength = 17;
    public const int LongVendorReportLength = 64;
    public const int FramedLongVendorReportLength = 65;

    internal sealed record Keyboard24gProfile(
        ushort VendorId,
        ushort ProductId,
        string DefaultName,
        ushort RequiredUsagePage,
        int ExpectedReportLength);

    private static readonly Keyboard24gProfile[] Profiles =
    [
        // AULA F75 / F87 / F99 & Royal Kludge / Weikav 2.4G receivers
        new(SinowealthVendorId, 0x010C, "AULA F75 Wireless Keyboard", VendorUsagePageFf00, LongVendorReportLength),
        new(SinowealthVendorId, 0x013C, "AULA F99 Wireless Keyboard", VendorUsagePageFf00, LongVendorReportLength),
        new(SinowealthVendorId, 0x0152, "Weikav NUT75 Wireless Keyboard", VendorUsagePageFf00, LongVendorReportLength),
        new(SinowealthVendorId, 0x00B5, "Royal Kludge Wireless Keyboard", VendorUsagePageFf00, LongVendorReportLength),

        // Compx 2.4G Tri-mode Keyboard Receiver (AULA / Weikav / MCHOSE / VGN)
        new(CompxKeyboardVendorId, 0xFA09, "2.4G Wireless Mechanical Keyboard", VendorUsagePageFf02, ShortVendorReportLength),

        // Akko / MonsGeek 2.4G Wireless Keyboard Receiver
        new(MonsGeekAkkoVendorId, 0x4011, "MonsGeek Wireless Keyboard", VendorUsagePageFf02, LongVendorReportLength),
        new(MonsGeekAkkoVendorId, 0x4015, "Akko Wireless Keyboard", VendorUsagePageFf02, LongVendorReportLength)
    ];

    public WirelessBatterySource Source => WirelessBatterySource.Keyboard24gHid;

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

    internal static bool IsSupportedKeyboard(ushort vendorId, ushort productId) =>
        TryGetProfile(vendorId, productId, out _);

    internal static bool TryGetProfile(ushort vendorId, ushort productId, out Keyboard24gProfile? profile)
    {
        profile = null;
        if (WirelessBatteryAggregator.IsIgnoredWiredDevice(vendorId, productId))
            return false;

        for (var i = 0; i < Profiles.Length; i++)
        {
            var candidate = Profiles[i];
            if (candidate.VendorId == vendorId && candidate.ProductId == productId)
            {
                profile = candidate;
                return true;
            }
        }
        return false;
    }

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor)
    {
        if (WirelessBatteryAggregator.IsIgnoredWiredDevice(descriptor.VendorId, descriptor.ProductId, descriptor.ProductName))
            return false;

        if (!TryGetProfile(descriptor.VendorId, descriptor.ProductId, out var profile) || profile is null)
            return false;

        if (descriptor.UsagePage != profile.RequiredUsagePage)
            return false;

        if (profile.ExpectedReportLength == ShortVendorReportLength)
        {
            return descriptor.InputReportLength == ShortVendorReportLength
                   && descriptor.OutputReportLength == ShortVendorReportLength;
        }

        return descriptor.InputReportLength is LongVendorReportLength or FramedLongVendorReportLength
               && descriptor.OutputReportLength is LongVendorReportLength or FramedLongVendorReportLength;
    }

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
                var outLen = Math.Max((int)descriptor.OutputReportLength, profile.ExpectedReportLength);
                var inLen = Math.Max((int)descriptor.InputReportLength, profile.ExpectedReportLength);
                var request = BuildBatteryQueryReport(profile.ExpectedReportLength, outLen);
                var response = new byte[inLen];

                if (!session.TryTransactInterrupt(
                        request,
                        response,
                        timeoutMs: 240,
                        validator: span => IsValidBatteryReplyHeader(profile.ExpectedReportLength, span),
                        out var bytesRead,
                        out _))
                {
                    continue;
                }

                var name = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName.Trim()
                    : profile.DefaultName;
                var id = $"kbd24g:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (TryParseBatteryReply(profile.ExpectedReportLength, response.AsSpan(0, bytesRead), id, name, now, out var reading)
                    && reading is not null)
                {
                    seenProducts.Add((descriptor.VendorId, descriptor.ProductId));
                    results.Add(reading);
                }
            }
        }

        return results;
    }

    internal static byte[] BuildBatteryQueryReport(int protocolReportLength, int bufferLength = 0)
    {
        if (protocolReportLength == ShortVendorReportLength)
        {
            return CompxAtkVxeBatteryProvider.BuildBatteryRequest();
        }

        var len = bufferLength >= LongVendorReportLength ? bufferLength : LongVendorReportLength;
        var packet = new byte[len];
        var offset = len == FramedLongVendorReportLength ? 1 : 0;
        packet[offset] = 0x08;
        packet[offset + 1] = 0x04;
        packet[offset + 16] = 0x49;
        return packet;
    }

    internal static bool IsValidBatteryReplyHeader(int protocolReportLength, ReadOnlySpan<byte> reply)
    {
        if (protocolReportLength == ShortVendorReportLength)
            return CompxAtkVxeBatteryProvider.IsValidBatteryReplyHeader(reply);

        var payload = NormalizeLongFraming(reply);
        return (payload.Length >= 10 && payload[0] == 0x08 && payload[1] == 0x04)
               || (payload.Length >= 9 && payload[0] == 0x04)
               || (payload.Length >= 3 && payload[0] == 0x83 && payload[1] <= 1);
    }

    private static ReadOnlySpan<byte> NormalizeLongFraming(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 4 && raw[0] == 0x00 && raw[1] is 0x08 or 0x04 or 0x83)
            return raw[1..];
        return raw;
    }

    internal static bool TryParseBatteryReply(
        int protocolReportLength,
        ReadOnlySpan<byte> rawReply,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        if (protocolReportLength == ShortVendorReportLength)
        {
            if (!CompxAtkVxeBatteryProvider.TryParseBatteryReply(rawReply, id, name, sampledAt, out var compxReading)
                || compxReading is null)
            {
                return false;
            }

            reading = compxReading with
            {
                Kind = BluetoothDeviceKind.Keyboard,
                Source = WirelessBatterySource.Keyboard24gHid
            };
            return true;
        }

        var reply = NormalizeLongFraming(rawReply);
        if (reply.Length < 3) return false;

        int batteryPercent;
        bool isCharging;

        if (reply.Length >= 10 && reply[0] == 0x08 && reply[1] == 0x04)
        {
            if (reply.Length >= ShortVendorReportLength
                && CompxAtkVxeBatteryProvider.ComputeChecksum(reply[..ShortVendorReportLength]) != reply[ShortVendorReportLength - 1])
            {
                return false;
            }
            batteryPercent = reply[6];
            if (batteryPercent is < 0 or > 100 || reply[7] > 1) return false;
            isCharging = reply[7] == 1;
            var voltageMv = (reply[8] << 8) | reply[9];
            if (batteryPercent == 0 && !isCharging && voltageMv == 0) return false;
        }
        else if (reply.Length >= 9 && reply[0] == 0x04)
        {
            batteryPercent = reply[5];
            if (batteryPercent is < 0 or > 100 || reply[6] > 1) return false;
            isCharging = reply[6] == 1;
            var voltageMv = (reply[7] << 8) | reply[8];
            if (batteryPercent == 0 && !isCharging && voltageMv == 0) return false;
        }
        else if (reply[0] == 0x83)
        {
            // Sinowealth 64-byte keyboard battery response: [0x83, chargingFlag (0/1), batteryPercent (1..100), ...]
            if (reply[1] > 1) return false;
            isCharging = reply[1] == 1;
            batteryPercent = reply[2];
            if (batteryPercent is <= 0 or > 100) return false;
        }
        else
        {
            return false;
        }

        var cleanName = string.IsNullOrWhiteSpace(name) ? "2.4G Wireless Keyboard" : name.Trim();
        if (WirelessBatteryAggregator.IsIgnoredWiredDevice(0, 0, cleanName))
            return false;

        reading = new WirelessDeviceReading(
            Id: id,
            Name: cleanName,
            Kind: BluetoothDeviceKind.Keyboard,
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.Keyboard24gHid,
            SampledAt: sampledAt);
        return true;
    }
}
