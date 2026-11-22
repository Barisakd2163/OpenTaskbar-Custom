using System.IO;
using System.Net.Http;
using System.Text.Json;
using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room SteelSeries wireless headset battery provider.
/// Primary path queries the USB Base Station / Receiver directly over Win32 HID (VID 0x1038, UsagePage 0xFFC0).
/// Optional fallback queries the local SteelSeries GG loopback endpoint (strictly 127.0.0.1).
/// </summary>
internal sealed class SteelSeriesBatteryProvider : IWirelessBatteryProvider
{
    public const ushort SteelSeriesVendorId = 0x1038;
    public const ushort NovaProWirelessPid = 0x12E0;
    public const ushort NovaProWirelessXboxPid = 0x12E5;
    public const ushort VendorUsagePage = 0xFFC0;

    public const byte StatusOffline = 0x01;
    public const byte StatusCharging = 0x02;
    public const byte StatusDischarging = 0x08;

    public WirelessBatterySource Source => WirelessBatterySource.SteelSeriesHid;

    public async Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(CancellationToken cancellationToken)
    {
        var hidReadings = await Task.Run(() => ProbeHidDevices(cancellationToken), CancellationToken.None).ConfigureAwait(false);
        if (hidReadings.Any(r => r.IsConnected && r.BatteryLevel >= 0) || cancellationToken.IsCancellationRequested)
            return hidReadings;

        var ggReadings = await ProbeGgLoopbackAsync(cancellationToken).ConfigureAwait(false);
        return ggReadings.Count > 0 ? ggReadings : hidReadings;
    }

    internal static IReadOnlyList<WirelessDeviceReading> ProbeHidDevices(CancellationToken cancellationToken = default)
    {
        var results = new List<WirelessDeviceReading>();
        var interfaces = Win32HidTransport.EnumerateInterfaces((vid, _) => vid == SteelSeriesVendorId);
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
                var outLen = Math.Max((int)descriptor.OutputReportLength, 64);
                var inLen = Math.Max((int)descriptor.InputReportLength, 64);
                var request = BuildBatteryRequest(outLen, prependZeroReportId: outLen == 65);
                var response = new byte[inLen];

                var ok = session.TryTransactInterrupt(
                    request,
                    response,
                    timeoutMs: 250,
                    validator: IsValidNovaProPacket,
                    out var bytesRead,
                    out var err);

                if (!ok && outLen == 64 && err == Win32HidTransport.ERROR_INVALID_PARAMETER)
                {
                    var framedRequest = BuildBatteryRequest(64, prependZeroReportId: true);
                    ok = session.TryTransactInterrupt(
                        framedRequest,
                        response,
                        timeoutMs: 250,
                        validator: IsValidNovaProPacket,
                        out bytesRead,
                        out _);
                }

                if (!ok || bytesRead <= 0) continue;

                var deviceName = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName
                    : GetDefaultModelName(descriptor.ProductId);
                var deviceId = $"steelseries:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (TryParseNovaProReply(response.AsSpan(0, bytesRead), deviceId, deviceName, now, out var reading)
                    && reading is not null)
                {
                    results.Add(reading);
                }
            }
        }

        return results;
    }

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        descriptor.VendorId == SteelSeriesVendorId
        && descriptor.UsagePage == VendorUsagePage
        && descriptor.InputReportLength is 64 or 65
        && descriptor.OutputReportLength is 64 or 65;

    internal static byte[] BuildBatteryRequest(int reportLength = 64, bool prependZeroReportId = false)
    {
        var length = Math.Max(reportLength, 64);
        var buffer = new byte[length];
        if (prependZeroReportId)
        {
            buffer[0] = 0x00;
            buffer[1] = 0x06;
            buffer[2] = 0xB0;
        }
        else
        {
            buffer[0] = 0x06;
            buffer[1] = 0xB0;
        }
        return buffer;
    }

    internal static bool IsValidNovaProPacket(ReadOnlySpan<byte> raw)
    {
        var payload = NormalizeReportFraming(raw);
        return payload.Length >= 16
               && payload[0] is 0x06 or 0x07
               && payload[1] == 0xB0;
    }

    internal static ReadOnlySpan<byte> NormalizeReportFraming(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 17 && raw[0] == 0x00 && raw[1] is 0x06 or 0x07 && raw[2] == 0xB0)
            return raw[1..];
        return raw;
    }

    internal static int ScaleStepToPercent(int step) =>
        Math.Clamp((int)Math.Round(Math.Clamp(step, 0, 8) * 100.0 / 8.0), 0, 100);

    internal static bool TryParseNovaProReply(
        ReadOnlySpan<byte> rawReply,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        var reply = NormalizeReportFraming(rawReply);
        if (reply.Length < 16) return false;
        if (reply[0] is not (0x06 or 0x07) || reply[1] != 0xB0) return false;

        var rawHeadsetLevel = reply[6];
        var rawSpareLevel = reply[7];
        var status = reply[15];

        if (rawHeadsetLevel > 8) return false;
        if (status is not (StatusOffline or StatusCharging or StatusDischarging))
            return false;

        var isConnected = status is StatusCharging or StatusDischarging;
        var isCharging = status == StatusCharging;
        var batteryPercent = ScaleStepToPercent(rawHeadsetLevel);
        int? sparePercent = rawSpareLevel <= 8 ? ScaleStepToPercent(rawSpareLevel) : null;

        reading = new WirelessDeviceReading(
            Id: id,
            Name: string.IsNullOrWhiteSpace(name) ? "Arctis Nova Pro Wireless" : name.Trim(),
            Kind: BluetoothDeviceKind.Headphones,
            BatteryLevel: batteryPercent,
            IsConnected: isConnected,
            IsCharging: isCharging,
            IsStale: false,
            Source: WirelessBatterySource.SteelSeriesHid,
            SampledAt: sampledAt,
            SecondaryBatteryLevel: sparePercent);
        return true;
    }

    internal static bool TryParseCorePropsLoopbackUri(string json, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("address", out var addrProp)
                && TryValidateLoopbackEndpoint(addrProp.GetString(), useHttps: false, out endpoint))
            {
                return true;
            }

            if (root.TryGetProperty("encryptedAddress", out var encProp)
                && TryValidateLoopbackEndpoint(encProp.GetString(), useHttps: true, out endpoint))
            {
                return true;
            }

            if (root.TryGetProperty("ggEncryptedAddress", out var ggProp)
                && TryValidateLoopbackEndpoint(ggProp.GetString(), useHttps: true, out endpoint))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    internal static bool TryValidateLoopbackEndpoint(string? hostPort, bool useHttps, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(hostPort)) return false;
        var trimmed = hostPort.Trim();

        // Strictly accept only 127.0.0.1:<port> with no path, query, userinfo, or alternate hostname.
        var colon = trimmed.IndexOf(':');
        if (colon <= 0 || colon != trimmed.LastIndexOf(':')) return false;

        var host = trimmed[..colon];
        var portPart = trimmed[(colon + 1)..];
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal)) return false;
        if (!int.TryParse(portPart, out var port) || port is <= 0 or > 65535) return false;

        var scheme = useHttps ? "https" : "http";
        return Uri.TryCreate($"{scheme}://127.0.0.1:{port}", UriKind.Absolute, out endpoint);
    }

    private static async Task<IReadOnlyList<WirelessDeviceReading>> ProbeGgLoopbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var corePropsPath = Path.Combine(programData, "SteelSeries", "SteelSeries Engine 3", "coreProps.json");
            if (!File.Exists(corePropsPath)) return [];

            var json = await File.ReadAllTextAsync(corePropsPath, cancellationToken).ConfigureAwait(false);
            if (!TryParseCorePropsLoopbackUri(json, out var baseUri) || baseUri is null)
                return [];

            var allowedPort = baseUri.Port;
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            };

            if (string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                handler.ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                    request?.RequestUri is { Host: "127.0.0.1" } uri && uri.Port == allowedPort;
            }

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(1)
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(950));

            foreach (var relativePath in new[] { "/battery", "/devices", "/game_metadata" })
            {
                try
                {
                    var targetUri = new Uri(baseUri, relativePath);
                    if (!string.Equals(targetUri.Host, "127.0.0.1", StringComparison.Ordinal) || targetUri.Port != allowedPort)
                        continue;

                    using var response = await client.GetAsync(targetUri, cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;

                    var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    var parsed = ParseGgBatteryJson(body, DateTimeOffset.UtcNow);
                    if (parsed.Count > 0) return parsed;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return [];
    }

    internal static IReadOnlyList<WirelessDeviceReading> ParseGgBatteryJson(string json, DateTimeOffset sampledAt)
    {
        var results = new List<WirelessDeviceReading>();
        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            ExtractGgDevices(doc.RootElement, results, sampledAt);
        }
        catch
        {
        }

        return results;
    }

    private static void ExtractGgDevices(JsonElement element, List<WirelessDeviceReading> results, DateTimeOffset sampledAt)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            int? battery = null;
            if (element.TryGetProperty("battery", out var batProp))
            {
                if (batProp.ValueKind == JsonValueKind.Number && batProp.TryGetInt32(out var bVal))
                    battery = bVal;
                else if (batProp.ValueKind == JsonValueKind.Object
                         && batProp.TryGetProperty("level", out var lvlProp)
                         && lvlProp.TryGetInt32(out var lVal))
                    battery = lVal;
            }
            else if (element.TryGetProperty("batteryPercent", out var bpProp) && bpProp.TryGetInt32(out var bpVal))
            {
                battery = bpVal;
            }

            if (battery is >= 0 and <= 100)
            {
                var name = element.TryGetProperty("displayName", out var dn) ? dn.GetString()
                    : element.TryGetProperty("name", out var n) ? n.GetString()
                    : "SteelSeries Wireless";
                var charging = element.TryGetProperty("charging", out var ch) && ch.ValueKind == JsonValueKind.True
                    || element.TryGetProperty("isCharging", out var ich) && ich.ValueKind == JsonValueKind.True;
                var connected = !element.TryGetProperty("connected", out var conn) || conn.ValueKind != JsonValueKind.False;

                results.Add(new WirelessDeviceReading(
                    Id: $"steelseries:gg:{(name ?? "device").ToLowerInvariant()}",
                    Name: string.IsNullOrWhiteSpace(name) ? "SteelSeries Wireless" : name.Trim(),
                    Kind: BluetoothBatteryWidget.ClassifyNode(name ?? "Headset", ""),
                    BatteryLevel: battery.Value,
                    IsConnected: connected,
                    IsCharging: charging,
                    IsStale: false,
                    Source: WirelessBatterySource.SteelSeriesGg,
                    SampledAt: sampledAt));
                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    ExtractGgDevices(prop.Value, results, sampledAt);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractGgDevices(item, results, sampledAt);
        }
    }

    private static string GetDefaultModelName(ushort productId) => productId switch
    {
        NovaProWirelessPid or NovaProWirelessXboxPid => "Arctis Nova Pro Wireless",
        _ => "SteelSeries Wireless Headset"
    };
}
