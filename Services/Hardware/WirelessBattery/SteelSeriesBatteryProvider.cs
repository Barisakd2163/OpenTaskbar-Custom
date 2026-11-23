using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room SteelSeries wireless headset and mouse battery provider.
/// Primary path queries the USB Base Station / Receiver directly over Win32 HID (VID 0x1038, UsagePage 0xFFC0).
/// Covers Arctis Nova Pro Wireless (with spare dock battery), Nova 7, Nova 5, Arctis 7 / 7+ / 9 / Pro Wireless,
/// and Aerox / Rival / Prime wireless mice.
/// Optional fallback queries the local SteelSeries GG loopback endpoint (strictly 127.0.0.1).
/// </summary>
internal sealed class SteelSeriesBatteryProvider : IWirelessBatteryProvider
{
    public const ushort SteelSeriesVendorId = 0x1038;

    // Nova Pro Wireless Base Station (step 0..8 + spare battery step 0..8)
    public const ushort NovaProWirelessPid = 0x12E0;
    public const ushort NovaProWirelessXboxPid = 0x12E5;

    // Nova 7 / 7P / 7X & Arctis 7+ / 7P+ / 7X+ (step 0..4 at byte 2, status at byte 3)
    public const ushort Nova7WirelessPid = 0x2202;
    public const ushort Nova7PWirelessPid = 0x2206;
    public const ushort Nova7XWirelessPid = 0x220A;
    public const ushort Nova7DragonPid = 0x223A;
    public const ushort Nova7Gen2Pid = 0x2258;
    public const ushort Nova7Gen2AltPid = 0x227E;
    public const ushort Nova3PPid = 0x2266;
    public const ushort Nova3XPid = 0x226A;
    public const ushort Arctis7PlusPid = 0x220E;
    public const ushort Arctis7PPlusPid = 0x2212;
    public const ushort Arctis7XPlusPid = 0x2216;

    // Nova 5 / 5P / 5X (0..100% at byte 3, status at byte 2)
    public const ushort Nova5WirelessPid = 0x2232;
    public const ushort Nova5PWirelessPid = 0x2236;
    public const ushort Nova5XWirelessPid = 0x223E;

    // Arctis 1 / 7 (2017/2019), Arctis 9, Arctis Pro Wireless
    public const ushort Arctis1WirelessPid = 0x12B3;
    public const ushort Arctis1XboxPid = 0x12B6;
    public const ushort Arctis7LegacyPid = 0x1260;
    public const ushort Arctis72019Pid = 0x12AD;
    public const ushort Arctis7PPid = 0x12D5;
    public const ushort Arctis7XPid = 0x12D7;
    public const ushort Arctis9Pid = 0x12C2;
    public const ushort ArctisProWirelessPid = 0x1290;

    // Aerox / Prime / Rival Wireless Mice
    public const ushort Aerox3WirelessPid = 0x1838;
    public const ushort Aerox32022WirelessPid = 0x1852;
    public const ushort Aerox5WirelessPid = 0x1858;
    public const ushort Aerox9WirelessPid = 0x185E;
    public const ushort PrimeWirelessPid = 0x1842;
    public const ushort PrimeMiniWirelessPid = 0x1848;
    public const ushort Rival3WirelessPid = 0x1830;
    public const ushort Rival650WirelessPid = 0x172B;

    public const ushort VendorUsagePage = 0xFFC0;
    public const ushort MouseVendorUsagePage = 0xFF00;

    public const byte StatusOffline = 0x01;
    public const byte StatusCharging = 0x02;
    public const byte StatusDischarging = 0x08;

    private const long MaxCorePropsBytes = 65_536;
    private const int MaxLoopbackResponseBytes = 256 * 1024;

    private static readonly SocketsHttpHandler LoopbackHandler = new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true
        }
    };

    private static readonly HttpClient LoopbackClient = new(LoopbackHandler)
    {
        Timeout = TimeSpan.FromSeconds(1)
    };

    public WirelessBatterySource Source => WirelessBatterySource.SteelSeriesHid;

    public async Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(
        IReadOnlyList<HidInterfaceDescriptor> hidInterfaces,
        CancellationToken cancellationToken)
    {
        if (hidInterfaces.Count == 0 || !HasAnyMatchingInterface(hidInterfaces))
            return [];

        var hidReadings = await Task.Run(() => ProbeHidDevices(hidInterfaces, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        if (hidReadings.Any(r => r.IsConnected && r.BatteryLevel >= 0) || cancellationToken.IsCancellationRequested)
            return hidReadings;

        var ggReadings = await ProbeGgLoopbackAsync(cancellationToken).ConfigureAwait(false);
        return ggReadings.Count > 0 ? ggReadings : hidReadings;
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
        ProbeHidDevices(Win32HidTransport.EnumerateInterfaces((vid, _) => vid == SteelSeriesVendorId), cancellationToken);

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
                var protocol = ClassifyProtocol(descriptor.ProductId);
                var request = BuildProtocolRequest(protocol, outLen, prependZeroReportId: outLen == 65);
                var response = new byte[inLen];

                var ok = session.TryTransactInterrupt(
                    request,
                    response,
                    timeoutMs: 250,
                    validator: span => IsValidProtocolPacket(protocol, span),
                    out var bytesRead,
                    out var err);

                if (!ok && outLen == 64 && err == Win32HidTransport.ERROR_INVALID_PARAMETER)
                {
                    var framedRequest = BuildProtocolRequest(protocol, 64, prependZeroReportId: true);
                    ok = session.TryTransactInterrupt(
                        framedRequest,
                        response,
                        timeoutMs: 250,
                        validator: span => IsValidProtocolPacket(protocol, span),
                        out bytesRead,
                        out _);
                }

                if (!ok || bytesRead <= 0) continue;

                var deviceName = !string.IsNullOrWhiteSpace(descriptor.ProductName)
                    ? descriptor.ProductName
                    : GetDefaultModelName(descriptor.ProductId);
                var deviceId = $"steelseries:{descriptor.VendorId:X4}:{descriptor.ProductId:X4}";

                if (TryParseProtocolReply(protocol, response.AsSpan(0, bytesRead), deviceId, deviceName, now, out var reading)
                    && reading is not null)
                {
                    seenProducts.Add(descriptor.ProductId);
                    results.Add(reading);
                }
            }
        }

        return results;
    }

    internal enum SteelSeriesProtocolKind
    {
        NovaProBaseStation,
        Nova7AndArctis7Plus,
        Nova5Series,
        Arctis7And9Legacy,
        ArctisProWireless,
        WirelessMouse
    }

    internal static bool TryClassifyProtocol(ushort productId, out SteelSeriesProtocolKind protocol)
    {
        switch (productId)
        {
            case NovaProWirelessPid:
            case NovaProWirelessXboxPid:
                protocol = SteelSeriesProtocolKind.NovaProBaseStation;
                return true;
            case Nova7WirelessPid:
            case Nova7PWirelessPid:
            case Nova7XWirelessPid:
            case Nova7DragonPid:
            case Nova7Gen2Pid:
            case Nova7Gen2AltPid:
            case Nova3PPid:
            case Nova3XPid:
            case Arctis7PlusPid:
            case Arctis7PPlusPid:
            case Arctis7XPlusPid:
                protocol = SteelSeriesProtocolKind.Nova7AndArctis7Plus;
                return true;
            case Nova5WirelessPid:
            case Nova5PWirelessPid:
            case Nova5XWirelessPid:
                protocol = SteelSeriesProtocolKind.Nova5Series;
                return true;
            case Arctis1WirelessPid:
            case Arctis1XboxPid:
            case Arctis7LegacyPid:
            case Arctis72019Pid:
            case Arctis7PPid:
            case Arctis7XPid:
            case Arctis9Pid:
                protocol = SteelSeriesProtocolKind.Arctis7And9Legacy;
                return true;
            case ArctisProWirelessPid:
                protocol = SteelSeriesProtocolKind.ArctisProWireless;
                return true;
            case Aerox3WirelessPid:
            case Aerox32022WirelessPid:
            case Aerox5WirelessPid:
            case Aerox9WirelessPid:
            case PrimeWirelessPid:
            case PrimeMiniWirelessPid:
            case Rival3WirelessPid:
            case Rival650WirelessPid:
                protocol = SteelSeriesProtocolKind.WirelessMouse;
                return true;
            default:
                protocol = SteelSeriesProtocolKind.NovaProBaseStation;
                return false;
        }
    }

    internal static SteelSeriesProtocolKind ClassifyProtocol(ushort productId) =>
        TryClassifyProtocol(productId, out var protocol) ? protocol : SteelSeriesProtocolKind.NovaProBaseStation;

    internal static bool IsMatchingInterface(HidInterfaceDescriptor descriptor) =>
        descriptor.VendorId == SteelSeriesVendorId
        && TryClassifyProtocol(descriptor.ProductId, out _)
        && descriptor.UsagePage is VendorUsagePage or MouseVendorUsagePage
        && descriptor.InputReportLength is 64 or 65
        && descriptor.OutputReportLength is 64 or 65;

    internal static byte[] BuildBatteryRequest(int reportLength = 64, bool prependZeroReportId = false) =>
        BuildProtocolRequest(SteelSeriesProtocolKind.NovaProBaseStation, reportLength, prependZeroReportId);

    internal static byte[] BuildProtocolRequest(
        SteelSeriesProtocolKind protocol,
        int reportLength = 64,
        bool prependZeroReportId = false)
    {
        var length = Math.Max(reportLength, 64);
        var buffer = new byte[length];
        var offset = prependZeroReportId ? 1 : 0;

        switch (protocol)
        {
            case SteelSeriesProtocolKind.Arctis7And9Legacy:
                buffer[offset] = 0x06;
                buffer[offset + 1] = 0x18;
                break;
            case SteelSeriesProtocolKind.ArctisProWireless:
                buffer[offset] = 0x40;
                buffer[offset + 1] = 0xAA;
                break;
            case SteelSeriesProtocolKind.WirelessMouse:
                buffer[offset] = 0xD2;
                break;
            default:
                buffer[offset] = 0x06;
                buffer[offset + 1] = 0xB0;
                break;
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

    internal static bool IsValidProtocolPacket(SteelSeriesProtocolKind protocol, ReadOnlySpan<byte> raw) => protocol switch
    {
        SteelSeriesProtocolKind.NovaProBaseStation => IsValidNovaProPacket(raw),
        SteelSeriesProtocolKind.Nova7AndArctis7Plus or SteelSeriesProtocolKind.Nova5Series =>
            NormalizeReportFraming(raw) is { Length: >= 6 } p && (p[0] is 0x06 or 0x07 or 0xB0),
        SteelSeriesProtocolKind.Arctis7And9Legacy =>
            NormalizeReportFraming(raw) is { Length: >= 4 } p && p[0] == 0x06 && p[1] == 0x18,
        SteelSeriesProtocolKind.ArctisProWireless =>
            NormalizeReportFraming(raw) is { Length: >= 4 } p && p[0] == 0x40 && p[1] == 0xAA,
        SteelSeriesProtocolKind.WirelessMouse =>
            NormalizeReportFraming(raw) is { Length: >= 3 } p && p[0] == 0xD2,
        _ => IsValidNovaProPacket(raw)
    };

    internal static ReadOnlySpan<byte> NormalizeReportFraming(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 4 && raw[0] == 0x00 && raw[1] is 0x06 or 0x07 or 0x40 or 0xB0 or 0xD2)
            return raw[1..];
        return raw;
    }

    internal static int ScaleStepToPercent(int step) =>
        Math.Clamp((int)Math.Round(Math.Clamp(step, 0, 8) * 100.0 / 8.0), 0, 100);

    internal static int ScaleFourStepToPercent(int step) =>
        Math.Clamp((int)Math.Round(Math.Clamp(step, 0, 4) * 100.0 / 4.0), 0, 100);

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

    internal static bool TryParseProtocolReply(
        SteelSeriesProtocolKind protocol,
        ReadOnlySpan<byte> rawReply,
        string id,
        string name,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        if (protocol == SteelSeriesProtocolKind.NovaProBaseStation)
            return TryParseNovaProReply(rawReply, id, name, sampledAt, out reading);

        var reply = NormalizeReportFraming(rawReply);
        switch (protocol)
        {
            case SteelSeriesProtocolKind.Nova7AndArctis7Plus:
            {
                if (reply.Length < 4) return false;
                var offset = (reply[0] is 0x06 or 0x07 && reply[1] == 0xB0) ? 2 : (reply[0] == 0xB0 ? 1 : -1);
                if (offset < 0 || reply.Length < offset + 2) return false;

                var step = reply[offset];
                var state = reply[offset + 1];
                if (step > 4 || state > 3) return false;

                var isConnected = state != 0x00;
                var isCharging = state == 0x01;
                reading = new WirelessDeviceReading(
                    Id: id,
                    Name: string.IsNullOrWhiteSpace(name) ? "Arctis Nova 7 Wireless" : name.Trim(),
                    Kind: BluetoothDeviceKind.Headphones,
                    BatteryLevel: ScaleFourStepToPercent(step),
                    IsConnected: isConnected,
                    IsCharging: isCharging,
                    IsStale: false,
                    Source: WirelessBatterySource.SteelSeriesHid,
                    SampledAt: sampledAt);
                return true;
            }
            case SteelSeriesProtocolKind.Nova5Series:
            {
                if (reply.Length < 5) return false;
                if (reply[0] is not (0x06 or 0x07) || reply[1] != 0xB0) return false;
                var status = reply[2];
                var level = (int)reply[3];
                if (level is < 0 or > 100 || status > 3) return false;

                var isConnected = status != 0x00;
                var isCharging = status == 0x01;
                reading = new WirelessDeviceReading(
                    Id: id,
                    Name: string.IsNullOrWhiteSpace(name) ? "Arctis Nova 5 Wireless" : name.Trim(),
                    Kind: BluetoothDeviceKind.Headphones,
                    BatteryLevel: level,
                    IsConnected: isConnected,
                    IsCharging: isCharging,
                    IsStale: false,
                    Source: WirelessBatterySource.SteelSeriesHid,
                    SampledAt: sampledAt);
                return true;
            }
            case SteelSeriesProtocolKind.Arctis7And9Legacy:
            {
                if (reply.Length < 4 || reply[0] != 0x06 || reply[1] != 0x18) return false;
                var rawLevel = (int)reply[2];
                if (rawLevel is <= 0 or > 100) return false;
                var percent = rawLevel;
                var isCharging = reply.Length > 3 && reply[3] == 0x01;

                reading = new WirelessDeviceReading(
                    Id: id,
                    Name: string.IsNullOrWhiteSpace(name) ? "SteelSeries Arctis Wireless" : name.Trim(),
                    Kind: BluetoothDeviceKind.Headphones,
                    BatteryLevel: percent,
                    IsConnected: true,
                    IsCharging: isCharging,
                    IsStale: false,
                    Source: WirelessBatterySource.SteelSeriesHid,
                    SampledAt: sampledAt);
                return true;
            }
            case SteelSeriesProtocolKind.ArctisProWireless:
            {
                if (reply.Length < 4 || reply[0] != 0x40 || reply[1] != 0xAA) return false;
                var headsetStep = reply[2];
                var spareStep = reply[3];
                if (headsetStep > 4) return false;
                int? sparePercent = spareStep <= 4 ? ScaleFourStepToPercent(spareStep) : null;

                reading = new WirelessDeviceReading(
                    Id: id,
                    Name: string.IsNullOrWhiteSpace(name) ? "Arctis Pro Wireless" : name.Trim(),
                    Kind: BluetoothDeviceKind.Headphones,
                    BatteryLevel: ScaleFourStepToPercent(headsetStep),
                    IsConnected: headsetStep > 0,
                    IsCharging: false,
                    IsStale: false,
                    Source: WirelessBatterySource.SteelSeriesHid,
                    SampledAt: sampledAt,
                    SecondaryBatteryLevel: sparePercent);
                return true;
            }
            case SteelSeriesProtocolKind.WirelessMouse:
            {
                if (reply.Length < 2 || reply[0] != 0xD2) return false;
                var rawByte = reply[1];
                var isCharging = (rawByte & 0x80) != 0;
                var rawLevel = rawByte & 0x7F;
                if (rawLevel is 0 or > 100) return false;
                var percent = rawLevel;

                reading = new WirelessDeviceReading(
                    Id: id,
                    Name: string.IsNullOrWhiteSpace(name) ? "SteelSeries Wireless Mouse" : name.Trim(),
                    Kind: BluetoothDeviceKind.Mouse,
                    BatteryLevel: percent,
                    IsConnected: true,
                    IsCharging: isCharging,
                    IsStale: false,
                    Source: WirelessBatterySource.SteelSeriesHid,
                    SampledAt: sampledAt);
                return true;
            }
            default:
                return false;
        }
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

    private static bool IsSteelSeriesGgRunning()
    {
        foreach (var procName in new[] { "SteelSeriesGG", "SteelSeriesGGClient", "SteelSeriesEngine3" })
        {
            var processes = Process.GetProcessesByName(procName);
            try
            {
                if (processes.Length > 0) return true;
            }
            finally
            {
                foreach (var process in processes)
                {
                    try { process.Dispose(); }
                    catch { }
                }
            }
        }
        return false;
    }

    private static async Task<IReadOnlyList<WirelessDeviceReading>> ProbeGgLoopbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsSteelSeriesGgRunning())
                return [];

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var corePropsPath = Path.Combine(programData, "SteelSeries", "SteelSeries Engine 3", "coreProps.json");
            var corePropsInfo = new FileInfo(corePropsPath);
            if (!corePropsInfo.Exists || corePropsInfo.Length is <= 0 or > MaxCorePropsBytes)
                return [];

            var json = await File.ReadAllTextAsync(corePropsPath, cancellationToken).ConfigureAwait(false);
            if (!TryParseCorePropsLoopbackUri(json, out var baseUri) || baseUri is null)
                return [];

            var allowedPort = baseUri.Port;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(950));

            foreach (var relativePath in new[] { "/battery", "/devices", "/game_metadata" })
            {
                try
                {
                    var targetUri = new Uri(baseUri, relativePath);
                    if (!string.Equals(targetUri.Host, "127.0.0.1", StringComparison.Ordinal) || targetUri.Port != allowedPort)
                        continue;

                    using var response = await LoopbackClient.GetAsync(targetUri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;

                    var body = await ReadBoundedStringAsync(response.Content, MaxLoopbackResponseBytes, cts.Token).ConfigureAwait(false);
                    if (body is null) continue;
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

    private static async Task<string?> ReadBoundedStringAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var len && len > maxBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (ms.Length + read > maxBytes) return null;
            ms.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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
                var name = element.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString()
                    : element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()
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

    internal static string GetDefaultModelName(ushort productId) => productId switch
    {
        NovaProWirelessPid or NovaProWirelessXboxPid => "Arctis Nova Pro Wireless",
        Nova7WirelessPid or Nova7PWirelessPid or Nova7XWirelessPid or Nova7DragonPid or Nova7Gen2Pid or Nova7Gen2AltPid => "Arctis Nova 7 Wireless",
        Nova3PPid => "Arctis Nova 3P Wireless",
        Nova3XPid => "Arctis Nova 3X Wireless",
        Nova5WirelessPid or Nova5PWirelessPid or Nova5XWirelessPid => "Arctis Nova 5 Wireless",
        Arctis1WirelessPid or Arctis1XboxPid => "Arctis 1 Wireless",
        Arctis7PlusPid or Arctis7PPlusPid or Arctis7XPlusPid => "Arctis 7+ Wireless",
        Arctis7LegacyPid or Arctis72019Pid or Arctis7PPid or Arctis7XPid => "Arctis 7 Wireless",
        Arctis9Pid => "Arctis 9 Wireless",
        ArctisProWirelessPid => "Arctis Pro Wireless",
        Aerox3WirelessPid or Aerox32022WirelessPid => "Aerox 3 Wireless",
        Aerox5WirelessPid => "Aerox 5 Wireless",
        Aerox9WirelessPid => "Aerox 9 Wireless",
        PrimeWirelessPid => "Prime Wireless",
        PrimeMiniWirelessPid => "Prime Mini Wireless",
        Rival3WirelessPid => "Rival 3 Wireless",
        Rival650WirelessPid => "Rival 650 Wireless",
        _ => "SteelSeries Wireless Headset"
    };
}
