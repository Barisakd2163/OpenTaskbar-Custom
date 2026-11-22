using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room Compx / ATK / VXE / VGN 2.4 GHz wireless mouse battery provider.
/// Queries the 17-byte vendor HID collection (UsagePage 0xFF02, Report ID 0x08, Command 0x04).
/// </summary>
internal sealed class CompxAtkVxeBatteryProvider : IWirelessBatteryProvider
{
    public const ushort CompxVendorId = 0x3554;
    public const ushort AtkVendorId = 0x373B;
    public const ushort AtkA9UltraPid = 0x1119;
    public const ushort VendorUsagePage = 0xFF02;
    public const int ReportByteLength = 17;

    public const byte ReportId = 0x08;
    public const byte BatteryQueryCommand = 0x04;
    public const byte BatteryQueryChecksum = 0x49; // 0x4D - 0x04 = 0x49

    public WirelessBatterySource Source => WirelessBatterySource.CompxAtkVxe;

    public Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ProbeHidDevices(cancellationToken), cancellationToken);

    internal static IReadOnlyList<WirelessDeviceReading> ProbeHidDevices(CancellationToken cancellationToken = default)
    {
        var results = new List<WirelessDeviceReading>();
        var interfaces = Win32HidTransport.EnumerateInterfaces((vid, _) => IsSupportedVendor(vid));
        var now = DateTimeOffset.UtcNow;

        foreach (var descriptor in interfaces)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!IsMatchingInterface(descriptor)) continue;

            if (!Win32HidTransport.TryOpenSession(descriptor.DevicePath, featureOnly: false, out var session, out _)
                || session is null)
            {
                continue;
            }

            using (session)
            {
                var request = BuildBatteryRequest();
                var response = new byte[ReportByteLength];
                if (!session.TryTransactInterrupt(
                        request,
                        response,
                        timeoutMs: 250,
                        validator: IsValidBatteryReplyHeader,
                        out var bytesRead,
                        out _))
                {
                    continue;
                }

                var deviceName = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName
                    : GetDefaultModelName(descriptor.VendorId, descriptor.ProductId);
                var deviceId = $"compx:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (TryParseBatteryReply(response.AsSpan(0, bytesRead), deviceId, deviceName, now, out var reading)
                    && reading is not null)
                {
                    results.Add(reading);
                }
            }
        }

        return results;
    }

    internal static bool IsSupportedVendor(ushort vendorId) =>
        vendorId is CompxVendorId or AtkVendorId;

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        IsSupportedVendor(descriptor.VendorId)
        && descriptor.UsagePage == VendorUsagePage
        && descriptor.InputReportLength == ReportByteLength
        && descriptor.OutputReportLength == ReportByteLength;

    internal static byte[] BuildBatteryRequest()
    {
        var packet = new byte[ReportByteLength];
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

    private static BluetoothDeviceKind ClassifyDeviceKind(string name)
    {
        var classified = BluetoothBatteryWidget.ClassifyNode(name, string.Empty);
        return classified != BluetoothDeviceKind.Generic ? classified : BluetoothDeviceKind.Mouse;
    }

    private static string GetDefaultModelName(ushort vendorId, ushort productId) => (vendorId, productId) switch
    {
        (AtkVendorId, AtkA9UltraPid) => "ATK A9 Ultra",
        (AtkVendorId, _) => "ATK Wireless Mouse",
        _ => "VXE Wireless Mouse"
    };
}
