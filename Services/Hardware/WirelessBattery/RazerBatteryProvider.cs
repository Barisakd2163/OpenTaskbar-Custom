using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room Razer wireless peripheral battery provider using the 90-byte HID Feature Report protocol
/// (VID 0x1532, Command Class 0x07, Command IDs 0x80 for battery level and 0x84 for charging state).
/// </summary>
internal sealed class RazerBatteryProvider : IWirelessBatteryProvider
{
    public const ushort RazerVendorId = 0x1532;
    public const int RazerReportPayloadLength = 90;
    public const int WindowsFeatureBufferLength = 91; // 1-byte Report ID (0x00) + 90-byte Razer payload

    public const byte StatusNewCommand = 0x00;
    public const byte StatusSuccess = 0x02;

    public const byte CommandClassPower = 0x07;
    public const byte CommandIdGetBatteryLevel = 0x80;
    public const byte CommandIdGetChargingStatus = 0x84;

    private static readonly byte[] CandidateTransactionIds = [0x1F, 0x3F, 0xFF];

    public WirelessBatterySource Source => WirelessBatterySource.RazerHid;

    public Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ProbeHidDevices(cancellationToken), cancellationToken);

    internal static IReadOnlyList<WirelessDeviceReading> ProbeHidDevices(CancellationToken cancellationToken = default)
    {
        var results = new List<WirelessDeviceReading>();
        var interfaces = Win32HidTransport.EnumerateInterfaces((vid, _) => vid == RazerVendorId);
        var now = DateTimeOffset.UtcNow;
        var seenProducts = new HashSet<ushort>();

        foreach (var descriptor in interfaces)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!IsMatchingInterface(descriptor)) continue;
            if (seenProducts.Contains(descriptor.ProductId)) continue;

            if (!Win32HidTransport.TryOpenSession(descriptor.DevicePath, featureOnly: true, out var session, out _)
                || session is null)
            {
                continue;
            }

            using (session)
            {
                var bufferLen = Math.Max((int)descriptor.FeatureReportLength, WindowsFeatureBufferLength);
                foreach (var txId in CandidateTransactionIds)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!TryQueryCommand(session, CommandIdGetBatteryLevel, txId, bufferLen, out var batArg1)
                        || batArg1 == 0)
                    {
                        continue;
                    }

                    var batteryPercent = ScaleRawByteToPercent(batArg1);
                    var isCharging = TryQueryCommand(session, CommandIdGetChargingStatus, txId, bufferLen, out var chargeArg1)
                                     && chargeArg1 != 0;

                    var name = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                        ? descriptor.ProductName
                        : "Razer Wireless";
                    var kind = ClassifyRazerKind(name);

                    seenProducts.Add(descriptor.ProductId);
                    results.Add(new WirelessDeviceReading(
                        Id: $"razer:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}",
                        Name: name.Trim(),
                        Kind: kind,
                        BatteryLevel: batteryPercent,
                        IsConnected: true,
                        IsCharging: isCharging,
                        IsStale: false,
                        Source: WirelessBatterySource.RazerHid,
                        SampledAt: now));
                    break;
                }
            }
        }

        return results;
    }

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        descriptor.VendorId == RazerVendorId
        && descriptor.FeatureReportLength >= RazerReportPayloadLength;

    /// <summary>
    /// Builds a 90-byte Razer payload (or 91-byte Windows Feature Report buffer when <paramref name="includeReportId"/> is true).
    /// </summary>
    internal static byte[] BuildCommandReport(
        byte commandId,
        byte transactionId = 0x1F,
        bool includeReportId = false)
    {
        var payload = new byte[RazerReportPayloadLength];
        payload[0] = StatusNewCommand;
        payload[1] = transactionId;
        payload[2] = 0x00;
        payload[3] = 0x00;
        payload[4] = 0x00;
        payload[5] = 0x02; // Data size: 2 bytes
        payload[6] = CommandClassPower;
        payload[7] = commandId;
        payload[88] = ComputeXorCrc(payload);
        payload[89] = 0x00;

        if (!includeReportId) return payload;

        var framed = new byte[WindowsFeatureBufferLength];
        framed[0] = 0x00;
        payload.CopyTo(framed, 1);
        return framed;
    }

    /// <summary>
    /// Computes the Razer XOR checksum over bytes 2..87 inclusive of the 90-byte payload.
    /// </summary>
    internal static byte ComputeXorCrc(ReadOnlySpan<byte> payload90)
    {
        var span = NormalizePayload(payload90);
        if (span.Length < RazerReportPayloadLength) return 0;

        byte crc = 0;
        for (var i = 2; i <= 87; i++)
            crc ^= span[i];
        return crc;
    }

    internal static ReadOnlySpan<byte> NormalizePayload(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= WindowsFeatureBufferLength && raw[0] == 0x00 && raw[1] is StatusNewCommand or StatusSuccess or 0x01 or 0x03)
            return raw.Slice(1, RazerReportPayloadLength);
        return raw.Length >= RazerReportPayloadLength ? raw[..RazerReportPayloadLength] : raw;
    }

    internal static int ScaleRawByteToPercent(byte rawValue) =>
        Math.Clamp((int)Math.Round(rawValue * 100.0 / 255.0), 0, 100);

    internal static bool TryParseCommandReply(
        ReadOnlySpan<byte> rawReply,
        byte expectedCommandId,
        out byte arg1Value)
    {
        arg1Value = 0;
        var payload = NormalizePayload(rawReply);
        if (payload.Length < RazerReportPayloadLength) return false;
        if (payload[0] != StatusSuccess) return false;
        if (payload[6] != CommandClassPower || payload[7] != expectedCommandId) return false;
        if (payload[88] != ComputeXorCrc(payload)) return false;

        arg1Value = payload[9];
        return true;
    }

    private static bool TryQueryCommand(
        Win32HidTransport.HidDeviceSession session,
        byte commandId,
        byte transactionId,
        int featureBufferLength,
        out byte arg1Value)
    {
        arg1Value = 0;
        var includeReportId = featureBufferLength >= WindowsFeatureBufferLength;
        var request = BuildCommandReport(commandId, transactionId, includeReportId);
        if (request.Length < featureBufferLength)
        {
            var resized = new byte[featureBufferLength];
            request.CopyTo(resized, 0);
            request = resized;
        }

        if (!session.TrySetFeature(request, out _))
            return false;

        Thread.Sleep(15);

        var response = new byte[featureBufferLength];
        if (!session.TryGetFeature(response, out _))
            return false;

        return TryParseCommandReply(response, commandId, out arg1Value);
    }

    private static BluetoothDeviceKind ClassifyRazerKind(string name)
    {
        var classified = BluetoothBatteryWidget.ClassifyNode(name, string.Empty);
        if (classified != BluetoothDeviceKind.Generic) return classified;
        if (name.Contains("BlackShark", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Barracuda", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kraken", StringComparison.OrdinalIgnoreCase))
            return BluetoothDeviceKind.Headphones;
        if (name.Contains("BlackWidow", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DeathStalker", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Huntsman", StringComparison.OrdinalIgnoreCase))
            return BluetoothDeviceKind.Keyboard;
        if (name.Contains("Wolverine", StringComparison.OrdinalIgnoreCase))
            return BluetoothDeviceKind.Gamepad;
        return BluetoothDeviceKind.Mouse;
    }
}
