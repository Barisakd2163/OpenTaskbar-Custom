using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room Logitech HID++ 2.0 wireless battery provider.
/// Primary path communicates directly over vendor HID++ collections (VID 0x046D, UsagePage 0xFF00).
/// Optional fallback queries the local G HUB WebSocket service strictly on 127.0.0.1:9010.
/// </summary>
internal sealed class LogitechBatteryProvider : IWirelessBatteryProvider
{
    public const ushort LogitechVendorId = 0x046D;
    public const ushort HidPlusPlusUsagePage = 0xFF00;

    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;
    public const int ShortReportLength = 7;
    public const int LongReportLength = 20;
    public const byte DefaultSoftwareId = 0x0A;

    public const ushort FeatureRoot = 0x0000;
    public const ushort FeatureDeviceNameType = 0x0005;
    public const ushort FeatureBatteryStatus = 0x1000;
    public const ushort FeatureBatteryVoltage = 0x1001;
    public const ushort FeatureUnifiedBattery = 0x1004;

    private static readonly byte[] CandidateDeviceIndices = [0xFF, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

    public WirelessBatterySource Source => WirelessBatterySource.LogitechHidPlusPlus;

    public async Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(CancellationToken cancellationToken)
    {
        var interfaces = await Task.Run(
            () => Win32HidTransport.EnumerateInterfaces((vid, _) => vid == LogitechVendorId),
            cancellationToken).ConfigureAwait(false);

        if (interfaces.Count == 0) return [];

        var hidReadings = await Task.Run(
            () => ProbeHidPlusPlusInterfaces(interfaces, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (hidReadings.Count > 0) return hidReadings;

        return await ProbeGHubLoopbackAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        descriptor.VendorId == LogitechVendorId
        && descriptor.UsagePage == HidPlusPlusUsagePage
        && (descriptor.InputReportLength is ShortReportLength or LongReportLength
            || descriptor.OutputReportLength is ShortReportLength or LongReportLength);

    internal static byte[] BuildRootGetFeatureRequest(
        byte deviceIndex,
        ushort featureId,
        int reportLength = LongReportLength,
        byte softwareId = DefaultSoftwareId)
    {
        var length = reportLength == ShortReportLength ? ShortReportLength : LongReportLength;
        var packet = new byte[length];
        packet[0] = length == ShortReportLength ? ShortReportId : LongReportId;
        packet[1] = deviceIndex;
        packet[2] = 0x00; // IRoot is always at feature index 0x00
        packet[3] = (byte)(softwareId & 0x0F); // Function 0x00: GetFeature
        packet[4] = (byte)(featureId >> 8);
        packet[5] = (byte)(featureId & 0xFF);
        return packet;
    }

    internal static byte[] BuildFeatureCallRequest(
        byte deviceIndex,
        byte featureIndex,
        byte functionId,
        byte param0 = 0,
        byte param1 = 0,
        byte param2 = 0,
        int reportLength = LongReportLength,
        byte softwareId = DefaultSoftwareId)
    {
        var length = reportLength == ShortReportLength ? ShortReportLength : LongReportLength;
        var packet = new byte[length];
        packet[0] = length == ShortReportLength ? ShortReportId : LongReportId;
        packet[1] = deviceIndex;
        packet[2] = featureIndex;
        packet[3] = (byte)(((functionId & 0x0F) << 4) | (softwareId & 0x0F));
        packet[4] = param0;
        packet[5] = param1;
        packet[6] = param2;
        return packet;
    }

    internal static bool IsErrorPacket(ReadOnlySpan<byte> reply) =>
        reply.Length >= 4
        && reply[0] is ShortReportId or LongReportId
        && reply[2] is 0x8F or 0xFF;

    internal static bool TryParseRootFeatureIndex(
        ReadOnlySpan<byte> reply,
        byte expectedDeviceIndex,
        out byte featureIndex,
        byte softwareId = DefaultSoftwareId)
    {
        featureIndex = 0;
        if (reply.Length < 5) return false;
        if (reply[0] is not (ShortReportId or LongReportId)) return false;
        if (reply[1] != expectedDeviceIndex || reply[2] != 0x00) return false;
        if ((reply[3] & 0x0F) != (softwareId & 0x0F)) return false;

        featureIndex = reply[4];
        return featureIndex != 0;
    }

    internal static bool TryParseUnifiedBatteryReply(
        ReadOnlySpan<byte> reply,
        byte expectedDeviceIndex,
        byte expectedFeatureIndex,
        out int batteryPercent,
        out bool isCharging)
    {
        batteryPercent = -1;
        isCharging = false;
        if (reply.Length < 7) return false;
        if (reply[0] is not (ShortReportId or LongReportId)) return false;
        if (reply[1] != expectedDeviceIndex || reply[2] != expectedFeatureIndex) return false;

        var soc = (int)reply[4];
        var coarseLevel = reply[5];
        var chargingStatus = reply[6];

        if (soc is > 0 and <= 100)
        {
            batteryPercent = soc;
        }
        else if (coarseLevel != 0)
        {
            batteryPercent = (coarseLevel & 0x08) != 0 ? 90
                : (coarseLevel & 0x04) != 0 ? 50
                : (coarseLevel & 0x02) != 0 ? 20
                : (coarseLevel & 0x01) != 0 ? 5
                : -1;
        }
        else if (soc == 0 && chargingStatus is >= 1 and <= 4)
        {
            batteryPercent = 0;
        }

        if (batteryPercent < 0) return false;
        isCharging = chargingStatus is >= 1 and <= 4;
        return true;
    }

    internal static bool TryParseBatteryStatusReply(
        ReadOnlySpan<byte> reply,
        byte expectedDeviceIndex,
        byte expectedFeatureIndex,
        out int batteryPercent,
        out bool isCharging)
    {
        batteryPercent = -1;
        isCharging = false;
        if (reply.Length < 7) return false;
        if (reply[0] is not (ShortReportId or LongReportId)) return false;
        if (reply[1] != expectedDeviceIndex || reply[2] != expectedFeatureIndex) return false;

        var level = (int)reply[4];
        var status = reply[6];
        if (level is <= 0 or > 100)
        {
            if (level == 0 && status is >= 1 and <= 4)
                batteryPercent = 0;
            else
                return false;
        }
        else
        {
            batteryPercent = level;
        }

        isCharging = status is >= 1 and <= 4;
        return true;
    }

    internal static bool TryParseBatteryVoltageReply(
        ReadOnlySpan<byte> reply,
        byte expectedDeviceIndex,
        byte expectedFeatureIndex,
        out int batteryPercent,
        out bool isCharging,
        out int voltageMv)
    {
        batteryPercent = -1;
        isCharging = false;
        voltageMv = 0;
        if (reply.Length < 7) return false;
        if (reply[0] is not (ShortReportId or LongReportId)) return false;
        if (reply[1] != expectedDeviceIndex || reply[2] != expectedFeatureIndex) return false;

        voltageMv = (reply[4] << 8) | reply[5];
        if (voltageMv is < 2800 or > 4500) return false;

        var flags = reply[6];
        isCharging = (flags & 0x80) != 0;
        batteryPercent = EstimatePercentFromVoltageMv(voltageMv);
        return true;
    }

    internal static int EstimatePercentFromVoltageMv(int mv)
    {
        if (mv >= 4180) return 100;
        if (mv <= 3500) return 0;

        // Piecewise Li-ion discharge approximation (3500 mV -> 0%, 3700 mV -> 20%, 3950 mV -> 75%, 4180 mV -> 100%).
        if (mv < 3700)
            return Math.Clamp((int)Math.Round((mv - 3500) * 20.0 / 200.0), 0, 20);
        if (mv < 3950)
            return Math.Clamp(20 + (int)Math.Round((mv - 3700) * 55.0 / 250.0), 20, 75);
        return Math.Clamp(75 + (int)Math.Round((mv - 3950) * 25.0 / 230.0), 75, 100);
    }

    private static IReadOnlyList<WirelessDeviceReading> ProbeHidPlusPlusInterfaces(
        IReadOnlyList<HidInterfaceDescriptor> allInterfaces,
        CancellationToken cancellationToken)
    {
        var results = new List<WirelessDeviceReading>();
        var matching = allInterfaces
            .Where(IsMatchingInterface)
            .OrderByDescending(d => d.OutputReportLength)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var seenDeviceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in matching)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var reportLen = descriptor.OutputReportLength is ShortReportLength or LongReportLength
                ? (int)descriptor.OutputReportLength
                : descriptor.InputReportLength is ShortReportLength or LongReportLength
                    ? (int)descriptor.InputReportLength
                    : LongReportLength;

            if (!Win32HidTransport.TryOpenSession(descriptor.DevicePath, featureOnly: false, out var session, out _)
                || session is null)
            {
                continue;
            }

            using (session)
            {
                foreach (var deviceIndex in CandidateDeviceIndices)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var slotKey = $"{descriptor.ProductId:X4}:{deviceIndex:X2}";
                    if (seenDeviceKeys.Contains(slotKey)) continue;

                    if (TryProbeDeviceSlot(session, descriptor, deviceIndex, reportLen, now, out var reading)
                        && reading is not null)
                    {
                        seenDeviceKeys.Add(slotKey);
                        results.Add(reading);
                    }
                }
            }
        }

        return results;
    }

    private static bool TryProbeDeviceSlot(
        Win32HidTransport.HidDeviceSession session,
        HidInterfaceDescriptor descriptor,
        byte deviceIndex,
        int reportLength,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        int batteryPercent = -1;
        bool isCharging = false;
        int? voltageMv = null;

        if (TryResolveFeatureIndex(session, deviceIndex, FeatureUnifiedBattery, reportLength, out var unifiedIdx))
        {
            var req = BuildFeatureCallRequest(deviceIndex, unifiedIdx, functionId: 0x01, reportLength: reportLength);
            var resp = new byte[Math.Max((int)descriptor.InputReportLength, reportLength)];
            if (TryTransactHidPlusPlus(session, req, resp, deviceIndex, unifiedIdx, out var readLen)
                && TryParseUnifiedBatteryReply(resp.AsSpan(0, readLen), deviceIndex, unifiedIdx, out batteryPercent, out isCharging))
            {
                // Unified battery succeeded.
            }
        }

        if (batteryPercent < 0
            && TryResolveFeatureIndex(session, deviceIndex, FeatureBatteryStatus, reportLength, out var statusIdx))
        {
            var req = BuildFeatureCallRequest(deviceIndex, statusIdx, functionId: 0x00, reportLength: reportLength);
            var resp = new byte[Math.Max((int)descriptor.InputReportLength, reportLength)];
            if (TryTransactHidPlusPlus(session, req, resp, deviceIndex, statusIdx, out var readLen))
            {
                TryParseBatteryStatusReply(resp.AsSpan(0, readLen), deviceIndex, statusIdx, out batteryPercent, out isCharging);
            }
        }

        if (batteryPercent < 0
            && TryResolveFeatureIndex(session, deviceIndex, FeatureBatteryVoltage, reportLength, out var voltageIdx))
        {
            var req = BuildFeatureCallRequest(deviceIndex, voltageIdx, functionId: 0x00, reportLength: reportLength);
            var resp = new byte[Math.Max((int)descriptor.InputReportLength, reportLength)];
            if (TryTransactHidPlusPlus(session, req, resp, deviceIndex, voltageIdx, out var readLen)
                && TryParseBatteryVoltageReply(resp.AsSpan(0, readLen), deviceIndex, voltageIdx, out batteryPercent, out isCharging, out var mv))
            {
                voltageMv = mv;
            }
        }

        if (batteryPercent < 0) return false;

        var (queriedName, queriedKind) = TryReadDeviceNameAndKind(session, descriptor, deviceIndex, reportLength);
        var finalName = !string.IsNullOrWhiteSpace(queriedName)
            ? queriedName
            : !string.IsNullOrWhiteSpace(descriptor.ProductName)
                ? descriptor.ProductName
                : "Logitech Wireless";

        var finalKind = queriedKind != BluetoothDeviceKind.Generic
            ? queriedKind
            : ClassifyLogitechKind(finalName);

        reading = new WirelessDeviceReading(
            Id: $"logitech:{descriptor.ProductId:X4}:{deviceIndex:X2}",
            Name: finalName,
            Kind: finalKind,
            BatteryLevel: batteryPercent,
            IsConnected: true,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.LogitechHidPlusPlus,
            SampledAt: sampledAt,
            VoltageMv: voltageMv);
        return true;
    }

    private static bool TryResolveFeatureIndex(
        Win32HidTransport.HidDeviceSession session,
        byte deviceIndex,
        ushort featureId,
        int reportLength,
        out byte featureIndex)
    {
        featureIndex = 0;
        var req = BuildRootGetFeatureRequest(deviceIndex, featureId, reportLength);
        var resp = new byte[Math.Max(reportLength, LongReportLength)];
        if (!TryTransactHidPlusPlus(session, req, resp, deviceIndex, expectedFeatureIndex: 0x00, out var readLen))
            return false;

        return TryParseRootFeatureIndex(resp.AsSpan(0, readLen), deviceIndex, out featureIndex);
    }

    private static (string? Name, BluetoothDeviceKind Kind) TryReadDeviceNameAndKind(
        Win32HidTransport.HidDeviceSession session,
        HidInterfaceDescriptor descriptor,
        byte deviceIndex,
        int reportLength)
    {
        if (!TryResolveFeatureIndex(session, deviceIndex, FeatureDeviceNameType, reportLength, out var nameFeatIdx))
            return (null, BluetoothDeviceKind.Generic);

        var respLen = Math.Max((int)descriptor.InputReportLength, reportLength);
        var resp = new byte[respLen];

        // Function 0x00: GetDeviceNameCount
        var countReq = BuildFeatureCallRequest(deviceIndex, nameFeatIdx, functionId: 0x00, reportLength: reportLength);
        string? name = null;
        if (TryTransactHidPlusPlus(session, countReq, resp, deviceIndex, nameFeatIdx, out var readLen) && readLen >= 5)
        {
            var totalLen = Math.Min((int)resp[4], 64);
            if (totalLen > 0)
            {
                var nameBytes = new List<byte>(totalLen);
                while (nameBytes.Count < totalLen)
                {
                    var sliceReq = BuildFeatureCallRequest(
                        deviceIndex,
                        nameFeatIdx,
                        functionId: 0x01,
                        param0: (byte)nameBytes.Count,
                        reportLength: reportLength);
                    if (!TryTransactHidPlusPlus(session, sliceReq, resp, deviceIndex, nameFeatIdx, out var sliceRead)
                        || sliceRead <= 4)
                    {
                        break;
                    }

                    var chunkAvailable = sliceRead - 4;
                    var needed = Math.Min(chunkAvailable, totalLen - nameBytes.Count);
                    if (needed <= 0) break;
                    for (var i = 0; i < needed; i++)
                    {
                        var b = resp[4 + i];
                        if (b == 0) break;
                        nameBytes.Add(b);
                    }
                    if (resp[4] == 0) break;
                }

                if (nameBytes.Count > 0)
                    name = Encoding.UTF8.GetString(nameBytes.ToArray()).Trim();
            }
        }

        // Function 0x02: GetDeviceType
        var kind = BluetoothDeviceKind.Generic;
        var typeReq = BuildFeatureCallRequest(deviceIndex, nameFeatIdx, functionId: 0x02, reportLength: reportLength);
        if (TryTransactHidPlusPlus(session, typeReq, resp, deviceIndex, nameFeatIdx, out var typeRead) && typeRead >= 5)
        {
            kind = MapHidPlusPlusDeviceType(resp[4]);
        }

        return (name, kind);
    }

    internal static BluetoothDeviceKind MapHidPlusPlusDeviceType(byte deviceType) => deviceType switch
    {
        0 or 1 or 2 => BluetoothDeviceKind.Keyboard,
        3 or 4 => BluetoothDeviceKind.Mouse,
        5 or 6 => BluetoothDeviceKind.Gamepad,
        8 or 9 => BluetoothDeviceKind.Headphones,
        _ => BluetoothDeviceKind.Generic
    };

    private static bool TryTransactHidPlusPlus(
        Win32HidTransport.HidDeviceSession session,
        ReadOnlySpan<byte> request,
        Span<byte> response,
        byte deviceIndex,
        byte expectedFeatureIndex,
        out int bytesRead)
    {
        return session.TryTransactInterrupt(
            request,
            response,
            timeoutMs: 180,
            validator: packet =>
                packet.Length >= 4
                && packet[0] is ShortReportId or LongReportId
                && packet[1] == deviceIndex
                && (packet[2] == expectedFeatureIndex || IsErrorPacket(packet)),
            out bytesRead,
            out _)
            && !IsErrorPacket(response[..bytesRead]);
    }

    internal static bool IsValidGHubLoopbackUri(Uri? uri) =>
        uri is not null
        && string.Equals(uri.Scheme, "ws", StringComparison.Ordinal)
        && string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
        && uri.Port == 9010;

    private static async Task<IReadOnlyList<WirelessDeviceReading>> ProbeGHubLoopbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Process.GetProcessesByName("lghub_agent").Length == 0)
                return [];

            var uri = new Uri("ws://127.0.0.1:9010");
            if (!IsValidGHubLoopbackUri(uri)) return [];

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(800));

            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "file://");
            ws.Options.UseDefaultCredentials = false;
            ws.Options.Proxy = null;

            await ws.ConnectAsync(uri, cts.Token).ConfigureAwait(false);
            var reqBytes = Encoding.UTF8.GetBytes("{\"msgId\":\"ot-1\",\"verb\":\"GET\",\"path\":\"/devices/list\"}");
            await ws.SendAsync(reqBytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token).ConfigureAwait(false);

            var recvBuf = new byte[16384];
            var result = await ws.ReceiveAsync(recvBuf, cts.Token).ConfigureAwait(false);
            if (result.Count <= 0) return [];

            var json = Encoding.UTF8.GetString(recvBuf, 0, result.Count);
            return ParseGHubJson(json, DateTimeOffset.UtcNow);
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<WirelessDeviceReading> ParseGHubJson(string json, DateTimeOffset sampledAt)
    {
        var results = new List<WirelessDeviceReading>();
        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            ExtractGHubDevices(doc.RootElement, results, sampledAt);
        }
        catch
        {
        }

        return results;
    }

    private static void ExtractGHubDevices(JsonElement element, List<WirelessDeviceReading> results, DateTimeOffset sampledAt)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            int? percentage = null;
            if (element.TryGetProperty("percentage", out var pProp) && pProp.TryGetInt32(out var pVal))
                percentage = pVal;
            else if (element.TryGetProperty("batteryPercentage", out var bpProp) && bpProp.TryGetInt32(out var bpVal))
                percentage = bpVal;

            if (percentage is >= 0 and <= 100)
            {
                var name = element.TryGetProperty("displayName", out var dn) ? dn.GetString()
                    : element.TryGetProperty("name", out var n) ? n.GetString()
                    : "Logitech Wireless";
                var id = element.TryGetProperty("deviceId", out var idProp) ? idProp.GetString() : name;
                var charging = element.TryGetProperty("charging", out var ch) && ch.ValueKind == JsonValueKind.True
                    || element.TryGetProperty("isCharging", out var ich) && ich.ValueKind == JsonValueKind.True;

                var trimmedName = string.IsNullOrWhiteSpace(name) ? "Logitech Wireless" : name.Trim();
                results.Add(new WirelessDeviceReading(
                    Id: $"logitech:ghub:{(id ?? trimmedName).ToLowerInvariant()}",
                    Name: trimmedName,
                    Kind: ClassifyLogitechKind(trimmedName),
                    BatteryLevel: percentage.Value,
                    IsConnected: true,
                    IsCharging: charging,
                    IsStale: false,
                    Source: WirelessBatterySource.LogitechGHub,
                    SampledAt: sampledAt));
                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    ExtractGHubDevices(prop.Value, results, sampledAt);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractGHubDevices(item, results, sampledAt);
        }
    }

    private static BluetoothDeviceKind ClassifyLogitechKind(string name)
    {
        var classified = BluetoothBatteryWidget.ClassifyNode(name, string.Empty);
        if (classified != BluetoothDeviceKind.Generic) return classified;
        if (name.Contains("PRO X", StringComparison.OrdinalIgnoreCase) && name.Contains("2", StringComparison.OrdinalIgnoreCase))
            return BluetoothDeviceKind.Headphones;
        if (name.Contains("G733", StringComparison.OrdinalIgnoreCase) || name.Contains("G535", StringComparison.OrdinalIgnoreCase))
            return BluetoothDeviceKind.Headphones;
        if (name.Contains("G915", StringComparison.OrdinalIgnoreCase) || name.Contains("G715", StringComparison.OrdinalIgnoreCase))
            return BluetoothDeviceKind.Keyboard;
        return BluetoothDeviceKind.Mouse;
    }
}
