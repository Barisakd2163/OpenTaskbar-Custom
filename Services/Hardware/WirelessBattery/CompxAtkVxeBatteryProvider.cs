using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room Compx / Nordic 2.4 GHz wireless peripheral battery provider covering
/// ATK, VXE, VGN, MCHOSE, Kysona, Darmoshark, Zaopin, Incott, Ajazz, Attack Shark,
/// Lamzu, Ninjutso, Scyrox, and WLMouse wireless receivers.
/// Supports both the 17-byte Compx vendor collection (UsagePage 0xFF02, Report ID 0x08, Command 0x04)
/// and the 64-byte Nordic/Compx vendor collection (UsagePage 0xFF02 / 0xFF00, Command 0x04).
/// </summary>
internal sealed class CompxAtkVxeBatteryProvider : IWirelessBatteryProvider
{
    public const ushort CompxVendorId = 0x3554;       // Compx / VXE / VGN / Kysona / Attack Shark / Zaopin / Incott
    public const ushort AtkVendorId = 0x373B;         // ATK / VXE / VGN
    public const ushort MchoseVendorId = 0x36A7;      // MCHOSE (A5 / AX5 / G3 / L7 / K99)
    public const ushort ScyroxVendorId = 0x36B0;      // Scyrox (V6 / V8)
    public const ushort WlMouseVendorId = 0x3710;     // WLMouse (Beast X / Fabulous Beasts / Sword X)
    public const ushort LamzuVendorId = 0x372E;       // Lamzu (Atlantis / Maya / Thorn / Paro)
    public const ushort NinjutsoVendorId = 0x359B;    // Ninjutso (Sora / Sora V2 / Origin One X)
    public const ushort TelinkAjazzVendorId = 0x248A; // Ajazz / Attack Shark / Darmoshark Telink 2.4G
    public const ushort YzwDarmosharkVendorId = 0x3151; // Darmoshark / Zaopin / Incott

    public const ushort AtkA9UltraPid = 0x1119;

    public const ushort VendorUsagePage = 0xFF02;
    public const ushort AlternateVendorUsagePage = 0xFF00;

    public const int ReportByteLength = 17;
    public const int NordicReportByteLength = 64;
    public const int NordicFramedReportByteLength = 65;

    public const byte ReportId = 0x08;
    public const byte BatteryQueryCommand = 0x04;
    public const byte BatteryQueryChecksum = 0x49; // 0x4D - 0x04 = 0x49

    public WirelessBatterySource Source => WirelessBatterySource.CompxAtkVxe;

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

    internal static IReadOnlyList<WirelessDeviceReading> ProbeHidDevices(CancellationToken cancellationToken = default) =>
        ProbeHidDevices(Win32HidTransport.EnumerateInterfaces((vid, _) => IsSupportedVendor(vid)), cancellationToken);

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
            if (!IsMatchingInterface(descriptor)) continue;
            if (seenProducts.Contains((descriptor.VendorId, descriptor.ProductId))) continue;

            if (!Win32HidTransport.TryOpenSession(descriptor.DevicePath, featureOnly: false, out var session, out _)
                || session is null)
            {
                continue;
            }

            using (session)
            {
                var deviceName = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName
                    : GetDefaultModelName(descriptor.VendorId, descriptor.ProductId);
                var deviceId = $"compx:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (IsMatchingCompx17Interface(descriptor))
                {
                    var request = BuildBatteryRequest();
                    var response = new byte[ReportByteLength];
                    if (session.TryTransactInterrupt(
                            request,
                            response,
                            timeoutMs: 250,
                            validator: IsValidBatteryReplyHeader,
                            out var bytesRead,
                            out _)
                        && TryParseBatteryReply(response.AsSpan(0, bytesRead), deviceId, deviceName, now, out var reading)
                        && reading is not null)
                    {
                        seenProducts.Add((descriptor.VendorId, descriptor.ProductId));
                        results.Add(reading);
                    }
                }
                else if (IsMatchingNordic64Interface(descriptor))
                {
                    var outLen = Math.Max((int)descriptor.OutputReportLength, NordicReportByteLength);
                    var inLen = Math.Max((int)descriptor.InputReportLength, NordicReportByteLength);
                    var request = BuildNordic64BatteryRequest(outLen);
                    var response = new byte[inLen];
                    if (session.TryTransactInterrupt(
                            request,
                            response,
                            timeoutMs: 250,
                            validator: IsValidNordic64ReplyHeader,
                            out var bytesRead,
                            out _)
                        && TryParseNordic64BatteryReply(response.AsSpan(0, bytesRead), deviceId, deviceName, now, out var reading)
                        && reading is not null)
                    {
                        seenProducts.Add((descriptor.VendorId, descriptor.ProductId));
                        results.Add(reading);
                    }
                }
            }
        }

        return results;
    }

    internal static bool IsSupportedVendor(ushort vendorId) =>
        vendorId is CompxVendorId
            or AtkVendorId
            or MchoseVendorId
            or ScyroxVendorId
            or WlMouseVendorId
            or LamzuVendorId
            or NinjutsoVendorId
            or TelinkAjazzVendorId
            or YzwDarmosharkVendorId;

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        !WirelessBatteryAggregator.IsIgnoredWiredDevice(descriptor.VendorId, descriptor.ProductId, descriptor.ProductName)
        && !WirelessKeyboard24gProvider.IsSupportedKeyboard(descriptor.VendorId, descriptor.ProductId)
        && (IsMatchingCompx17Interface(descriptor) || IsMatchingNordic64Interface(descriptor));

    internal static bool IsMatchingCompx17Interface(HidInterfaceDescriptor descriptor) =>
        IsSupportedVendor(descriptor.VendorId)
        && descriptor.UsagePage == VendorUsagePage
        && descriptor.InputReportLength == ReportByteLength
        && descriptor.OutputReportLength == ReportByteLength;

    internal static bool IsMatchingNordic64Interface(HidInterfaceDescriptor descriptor) =>
        IsSupportedVendor(descriptor.VendorId)
        && descriptor.UsagePage is VendorUsagePage or AlternateVendorUsagePage
        && descriptor.InputReportLength is NordicReportByteLength or NordicFramedReportByteLength
        && descriptor.OutputReportLength is NordicReportByteLength or NordicFramedReportByteLength;

    internal static byte[] BuildBatteryRequest()
    {
        var packet = new byte[ReportByteLength];
        packet[0] = ReportId;
        packet[1] = BatteryQueryCommand;
        packet[16] = BatteryQueryChecksum;
        return packet;
    }

    internal static byte[] BuildNordic64BatteryRequest(int reportLength = NordicReportByteLength)
    {
        var length = reportLength == NordicFramedReportByteLength ? NordicFramedReportByteLength : NordicReportByteLength;
        var packet = new byte[length];
        packet[0] = ReportId;
        packet[1] = BatteryQueryCommand;
        packet[16] = BatteryQueryChecksum;
        return packet;
    }

    internal static byte ComputeChecksum(ReadOnlySpan<byte> packet)
    {
        var sum = 0;
        var limit = Math.Min(packet.Length, ReportByteLength - 1);
        for (var i = 1; i < limit; i++)
            sum += packet[i];
        return (byte)((0x4D - (sum & 0xFF)) & 0xFF);
    }

    internal static bool IsValidBatteryReplyHeader(ReadOnlySpan<byte> reply) =>
        reply.Length >= 10
        && reply[0] == ReportId
        && reply[1] == BatteryQueryCommand;

    internal static bool IsValidNordic64ReplyHeader(ReadOnlySpan<byte> reply)
    {
        var payload = NormalizeNordic64Framing(reply);
        return payload.Length >= 10
               && payload[0] is ReportId or BatteryQueryCommand
               && (payload[0] == BatteryQueryCommand || payload[1] == BatteryQueryCommand);
    }

    private static ReadOnlySpan<byte> NormalizeNordic64Framing(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 11 && raw[0] == 0x00 && raw[1] is ReportId or BatteryQueryCommand)
            return raw[1..];
        return raw;
    }

    internal static bool TryParseBatteryReply(
        ReadOnlySpan<byte> reply,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        if (reply.Length < 10) return false;
        if (reply[0] != ReportId || reply[1] != BatteryQueryCommand)
            return false;
        // The device sends a 17-byte report whose final byte is the packet checksum.
        // Without this check a corrupted or spoofed report becomes a valid battery reading.
        if (reply.Length >= ReportByteLength && ComputeChecksum(reply) != reply[ReportByteLength - 1])
            return false;

        var batteryPercent = (int)reply[6];
        if (batteryPercent is < 0 or > 100)
            return false;

        var chargeFlag = reply[7];
        if (chargeFlag > 1)
            return false;

        var isCharging = chargeFlag == 1;
        var voltageMv = (reply[8] << 8) | reply[9];

        // Reject all-zero payload responses so sleeping or uninitialized endpoints never produce a fake 0% reading.
        if (batteryPercent == 0 && !isCharging && voltageMv == 0)
            return false;

        var trimmedName = string.IsNullOrWhiteSpace(name) ? "ATK Wireless Mouse" : name.Trim();
        var kind = ClassifyDeviceKind(trimmedName);

        reading = new WirelessDeviceReading(
            Id: id,
            Name: trimmedName,
            Kind: kind,
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.CompxAtkVxe,
            SampledAt: sampledAt,
            VoltageMv: voltageMv > 0 ? voltageMv : null);
        return true;
    }

    internal static bool TryParseNordic64BatteryReply(
        ReadOnlySpan<byte> rawReply,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        var reply = NormalizeNordic64Framing(rawReply);
        if (reply.Length < 10) return false;

        // If the 64-byte packet embeds a standard 17-byte Compx header + checksum at [0..16], parse it directly.
        if (reply[0] == ReportId && reply[1] == BatteryQueryCommand && ComputeChecksum(reply[..ReportByteLength]) == reply[ReportByteLength - 1])
        {
            return TryParseBatteryReply(reply[..ReportByteLength], id, name, sampledAt, out reading);
        }

        if (reply[0] != ReportId || reply[1] != BatteryQueryCommand)
            return false;

        var batteryPercent = (int)reply[6];
        var chargeFlag = reply[7];
        if (batteryPercent is < 0 or > 100 || chargeFlag > 1)
            return false;

        var isCharging = chargeFlag == 1;
        var voltageMv = (reply[8] << 8) | reply[9];
        if (batteryPercent == 0 && !isCharging && voltageMv == 0)
            return false;

        var trimmedName = string.IsNullOrWhiteSpace(name) ? "Wireless Gaming Mouse" : name.Trim();
        var kind = ClassifyDeviceKind(trimmedName);

        reading = new WirelessDeviceReading(
            Id: id,
            Name: trimmedName,
            Kind: kind,
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.CompxAtkVxe,
            SampledAt: sampledAt,
            VoltageMv: voltageMv is >= 2800 and <= 4500 ? voltageMv : null);
        return true;
    }

    internal static BluetoothDeviceKind ClassifyDeviceKind(string name)
    {
        var classified = BluetoothBatteryWidget.ClassifyNode(name, string.Empty);
        if (classified != BluetoothDeviceKind.Generic) return classified;
        if (name.Contains("K99", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G75", StringComparison.OrdinalIgnoreCase)
            || name.Contains("V75", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RS7", StringComparison.OrdinalIgnoreCase))
        {
            return BluetoothDeviceKind.Keyboard;
        }
        if (name.Contains("M1", StringComparison.OrdinalIgnoreCase) && name.Contains("Headset", StringComparison.OrdinalIgnoreCase))
            return BluetoothDeviceKind.Headphones;
        return BluetoothDeviceKind.Mouse;
    }

    internal static string GetDefaultModelName(ushort vendorId, ushort productId) => (vendorId, productId) switch
    {
        (AtkVendorId, AtkA9UltraPid) => "ATK A9 Ultra",
        (AtkVendorId, 0x1101) => "ATK F1 Pro Max",
        (AtkVendorId, 0x1102) => "ATK X1 Ultra",
        (AtkVendorId, 0x1108) => "ATK Z1 Ultra",
        (AtkVendorId, _) => "ATK Wireless Mouse",
        (MchoseVendorId, _) => "MCHOSE Wireless Mouse",
        (ScyroxVendorId, _) => "Scyrox Wireless Mouse",
        (WlMouseVendorId, _) => "WLMouse Beast X Wireless",
        (LamzuVendorId, _) => "Lamzu Wireless Mouse",
        (NinjutsoVendorId, _) => "Ninjutso Sora Wireless",
        (TelinkAjazzVendorId, _) => "Ajazz / Attack Shark Wireless Mouse",
        (YzwDarmosharkVendorId, _) => "Darmoshark / Zaopin Wireless Mouse",
        (CompxVendorId, 0xF58A) => "VXE Dragonfly R1 Pro",
        (CompxVendorId, 0xF58B) => "VXE MAD R Major",
        (CompxVendorId, 0xF505) => "VGN Dragonfly F1 MOBA",
        _ => "VXE Wireless Mouse"
    };
}
