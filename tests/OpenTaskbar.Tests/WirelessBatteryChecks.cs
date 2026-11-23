using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

internal static class WirelessBatteryChecks
{
    private static readonly string[] RequiredLocalizationKeys =
    [
        "WidgetBluetoothBattery",
        "WidgetBluetoothBatteryDescription",
        "WirelessBatteryCharging",
        "WirelessBatteryLastReading",
        "WirelessBatteryRefresh",
        "WirelessBatterySpareDock",
        "WirelessBatteryLevelEmpty",
        "WirelessBatteryLevelLow",
        "WirelessBatteryLevelMedium",
        "WirelessBatteryLevelFull"
    ];

    public static int Run(bool headless = false)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        CheckModelsAndCompatibility(Check);
        CheckSteelSeriesProvider(Check);
        CheckCompxAtkVxeProvider(Check);
        CheckLogitechProvider(Check);
        CheckRazerProvider(Check);
        CheckXInputProvider(Check);
        CheckAggregatorCacheAndIsolation(Check);
        CheckAllLocaleCatalogs(Check);
        CheckLiveHardwareScan(Check);

        if (!headless)
        {
            checks += RunUiChecksOnSta();
        }

        return checks;
    }

    private static void CheckModelsAndCompatibility(Action<bool, string> check)
    {
        check(WirelessDeviceReading.GetSourceLabel(WirelessBatterySource.SteelSeriesHid) == "SteelSeries"
              && WirelessDeviceReading.GetSourceLabel(WirelessBatterySource.SteelSeriesGg) == "SteelSeries"
              && WirelessDeviceReading.GetSourceLabel(WirelessBatterySource.CompxAtkVxe) == "ATK/VXE"
              && WirelessDeviceReading.GetSourceLabel(WirelessBatterySource.LogitechHidPlusPlus) == "HID++"
              && WirelessDeviceReading.GetSourceLabel(WirelessBatterySource.LogitechGHub) == "HID++"
              && WirelessDeviceReading.GetSourceLabel(WirelessBatterySource.RazerHid) == "Razer"
              && WirelessDeviceReading.GetSourceLabel(WirelessBatterySource.XInput) == "XInput"
              && WirelessDeviceReading.GetSourceLabel(WirelessBatterySource.Bluetooth) == "Bluetooth",
            "Every WirelessBatterySource must map to its compact UI badge label.");

        var ts = new DateTimeOffset(2026, 11, 22, 16, 0, 0, TimeSpan.Zero);
        var reading = new WirelessDeviceReading(
            Id: "steelseries:1038:12E0",
            Name: "Arctis Nova Pro Wireless",
            Kind: BluetoothDeviceKind.Headphones,
            BatteryLevel: 75,
            IsConnected: true,
            IsCharging: false,
            IsStale: false,
            Source: WirelessBatterySource.SteelSeriesHid,
            SampledAt: ts,
            SecondaryBatteryLevel: 100,
            VoltageMv: null);

        var info = reading.ToBatteryInfo();
        check(info.Name == "Arctis Nova Pro Wireless"
              && info.Id == "steelseries:1038:12E0"
              && info.IsConnected
              && info.BatteryLevel == 75
              && info.Kind == BluetoothDeviceKind.Headphones
              && !info.IsCharging
              && !info.IsStale
              && info.SourceLabel == "SteelSeries"
              && info.SampledAt == ts
              && info.SecondaryBatteryLevel == 100,
            "WirelessDeviceReading.ToBatteryInfo must preserve all extended metadata.");

        var legacyInfo = new BluetoothDeviceBatteryInfo("Legacy Headset", "ble:1", true, 60);
        check(legacyInfo.Kind == BluetoothDeviceKind.Generic
              && !legacyInfo.IsCharging
              && !legacyInfo.IsStale
              && legacyInfo.SourceLabel is null
              && legacyInfo.LevelOverrideText is null
              && legacyInfo.SecondaryBatteryLevel is null,
            "BluetoothDeviceBatteryInfo must remain backwards-compatible with 4-argument callers.");
    }

    private static void CheckSteelSeriesProvider(Action<bool, string> check)
    {
        var req64 = SteelSeriesBatteryProvider.BuildBatteryRequest(64, prependZeroReportId: false);
        check(req64.Length == 64 && req64[0] == 0x06 && req64[1] == 0xB0,
            "SteelSeries 64-byte query must start with [0x06, 0xB0].");

        var req65 = SteelSeriesBatteryProvider.BuildBatteryRequest(65, prependZeroReportId: true);
        check(req65.Length == 65 && req65[0] == 0x00 && req65[1] == 0x06 && req65[2] == 0xB0,
            "SteelSeries 65-byte Windows-framed query must start with [0x00, 0x06, 0xB0].");

        // Real Arctis Nova Pro Wireless 64-byte reply: headset=6/8 (75%), dock spare=8/8 (100%), status=0x08 (connected/discharging)
        var liveReply = new byte[64];
        liveReply[0] = 0x06;
        liveReply[1] = 0xB0;
        liveReply[6] = 0x06;
        liveReply[7] = 0x08;
        liveReply[15] = 0x08;

        var now = DateTimeOffset.UtcNow;
        check(SteelSeriesBatteryProvider.TryParseNovaProReply(liveReply, "ss:1", "Arctis Nova Pro Wireless", now, out var parsed)
              && parsed is not null
              && parsed.BatteryLevel == 75
              && parsed.SecondaryBatteryLevel == 100
              && parsed.IsConnected
              && !parsed.IsCharging
              && parsed.Kind == BluetoothDeviceKind.Headphones,
            "SteelSeries Nova Pro reply must decode headset 6/8 -> 75%, spare dock 8/8 -> 100%, and discharging state 0x08.");

        // Windows 0x00-prefixed 65-byte reply while charging (status = 0x02, headset = 4/8 -> 50%)
        var framedCharging = new byte[65];
        framedCharging[0] = 0x00;
        framedCharging[1] = 0x06;
        framedCharging[2] = 0xB0;
        framedCharging[7] = 0x04;
        framedCharging[8] = 0x06;
        framedCharging[16] = 0x02;
        check(SteelSeriesBatteryProvider.TryParseNovaProReply(framedCharging, "ss:2", "Arctis Nova Pro Wireless", now, out var chargingReading)
              && chargingReading is not null
              && chargingReading.BatteryLevel == 50
              && chargingReading.SecondaryBatteryLevel == 75
              && chargingReading.IsConnected
              && chargingReading.IsCharging,
            "SteelSeries parser must unwrap leading 0x00 Report ID framing and detect charging status 0x02.");

        // Headset turned off / out of range (status = 0x01)
        var offlineReply = (byte[])liveReply.Clone();
        offlineReply[15] = 0x01;
        check(SteelSeriesBatteryProvider.TryParseNovaProReply(offlineReply, "ss:3", "Arctis Nova Pro Wireless", now, out var offlineReading)
              && offlineReading is not null
              && !offlineReading.IsConnected
              && !offlineReading.IsCharging,
            "SteelSeries status 0x01 must mark headset as offline/disconnected.");

        // Invalid battery step (> 8) or invalid status must be rejected
        var invalidStep = (byte[])liveReply.Clone();
        invalidStep[6] = 99;
        check(!SteelSeriesBatteryProvider.TryParseNovaProReply(invalidStep, "ss:bad", "Invalid", now, out _),
            "SteelSeries parser must reject out-of-range battery step values (> 8).");

        // Loopback validation for SteelSeries GG coreProps.json
        check(SteelSeriesBatteryProvider.TryParseCorePropsLoopbackUri(
                  "{\"encryptedAddress\":\"127.0.0.1:50128\",\"address\":\"127.0.0.1:50116\"}", out var ggUri)
              && ggUri is not null
              && ggUri.Host == "127.0.0.1"
              && ggUri.Port == 50116,
            "coreProps.json parser must accept valid 127.0.0.1 loopback address.");
        check(!SteelSeriesBatteryProvider.TryValidateLoopbackEndpoint("localhost:50116", false, out _)
              && !SteelSeriesBatteryProvider.TryValidateLoopbackEndpoint("192.168.1.5:50116", false, out _)
              && !SteelSeriesBatteryProvider.TryValidateLoopbackEndpoint("127.0.0.1:50116/evil", false, out _),
            "SteelSeries GG loopback validator must strictly reject non-127.0.0.1 hosts and path injections.");
    }

    private static void CheckCompxAtkVxeProvider(Action<bool, string> check)
    {
        var req = CompxAtkVxeBatteryProvider.BuildBatteryRequest();
        check(req.Length == 17
              && req[0] == 0x08
              && req[1] == 0x04
              && req[16] == 0x49
              && CompxAtkVxeBatteryProvider.ComputeChecksum(req) == 0x49,
            "Compx/ATK/VXE 17-byte request must be [0x08, 0x04, 0..0, 0x49].");

        // Real ATK A9 Ultra 17-byte reply: 08-04-00-00-00-02-64-01-0F-F8-00-00-00-00-00-00-DB
        byte[] liveAtkReply =
        [
            0x08, 0x04, 0x00, 0x00, 0x00, 0x02, 0x64, 0x01,
            0x0F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDB
        ];
        var now = DateTimeOffset.UtcNow;
        check(CompxAtkVxeBatteryProvider.TryParseBatteryReply(liveAtkReply, "compx:373B:1119", "ATK A9 Ultra", now, out var atkReading)
              && atkReading is not null
              && atkReading.BatteryLevel == 100
              && atkReading.IsCharging
              && atkReading.VoltageMv == 4088
              && atkReading.Kind == BluetoothDeviceKind.Mouse
              && atkReading.Source == WirelessBatterySource.CompxAtkVxe,
            "Compx/ATK parser must decode live ATK A9 Ultra packet (100%, charging, 4088 mV).");

        // Reject wrong header, > 100%, or all-zero uninitialized payload
        byte[] badHeader = [0x09, 0x04, 0, 0, 0, 0, 50, 0, 0x0E, 0x00];
        byte[] badPercent = [0x08, 0x04, 0, 0, 0, 0, 150, 0, 0x0E, 0x00];
        byte[] zeroPayload = [0x08, 0x04, 0, 0, 0, 0, 0, 0, 0x00, 0x00];
        check(!CompxAtkVxeBatteryProvider.TryParseBatteryReply(badHeader, "bad1", "Mouse", now, out _)
              && !CompxAtkVxeBatteryProvider.TryParseBatteryReply(badPercent, "bad2", "Mouse", now, out _)
              && !CompxAtkVxeBatteryProvider.TryParseBatteryReply(zeroPayload, "bad3", "Mouse", now, out _),
            "Compx/ATK parser must reject invalid headers, >100% battery values, and all-zero sleeping packets.");
    }

    private static void CheckLogitechProvider(Action<bool, string> check)
    {
        var rootReq = LogitechBatteryProvider.BuildRootGetFeatureRequest(deviceIndex: 0x01, featureId: 0x1004);
        check(rootReq.Length == 20 && rootReq[0] == 0x11 && rootReq[1] == 0x01 && rootReq[4] == 0x10 && rootReq[5] == 0x04,
            "HID++ 2.0 IRoot GetFeature request must encode feature ID 0x1004 in big-endian.");

        byte[] rootReply = [0x11, 0x01, 0x00, 0x0A, 0x06, 0x00, 0x00];
        check(LogitechBatteryProvider.TryParseRootFeatureIndex(rootReply, 0x01, out var featIdx) && featIdx == 0x06,
            "HID++ 2.0 IRoot reply must extract resolved feature index.");

        byte[] unifiedReply = [0x11, 0x01, 0x06, 0x1A, 82, 0x04, 0x01];
        check(LogitechBatteryProvider.TryParseUnifiedBatteryReply(unifiedReply, 0x01, 0x06, out var soc, out var charging)
              && soc == 82 && charging,
            "HID++ 2.0 Unified Battery (0x1004) reply must parse state of charge and charging flag.");

        byte[] statusReply = [0x10, 0x02, 0x05, 0x0A, 45, 0x00, 0x00];
        check(LogitechBatteryProvider.TryParseBatteryStatusReply(statusReply, 0x02, 0x05, out var level, out var statusCharging)
              && level == 45 && !statusCharging,
            "HID++ 2.0 Battery Status (0x1000) short reply must parse discharge percentage.");

        byte[] voltageReply = [0x11, 0x01, 0x07, 0x0A, 0x0F, 0xA0, 0x80]; // 4000 mV, charging flag 0x80
        check(LogitechBatteryProvider.TryParseBatteryVoltageReply(voltageReply, 0x01, 0x07, out var vPct, out var vChg, out var mv)
              && mv == 4000 && vChg && vPct is >= 75 and <= 90,
            "HID++ 2.0 Battery Voltage (0x1001) reply must decode millivolts and estimate percentage.");

        byte[] errorPacket = [0x10, 0x01, 0xFF, 0x0A, 0x02, 0x00, 0x00];
        check(LogitechBatteryProvider.IsErrorPacket(errorPacket),
            "HID++ 2.0 error indicator 0xFF must be detected.");

        check(LogitechBatteryProvider.IsValidGHubLoopbackUri(new Uri("ws://127.0.0.1:9010"))
              && !LogitechBatteryProvider.IsValidGHubLoopbackUri(new Uri("ws://localhost:9010"))
              && !LogitechBatteryProvider.IsValidGHubLoopbackUri(new Uri("wss://127.0.0.1:9010"))
              && !LogitechBatteryProvider.IsValidGHubLoopbackUri(new Uri("ws://127.0.0.1:8080")),
            "G HUB WebSocket validator must strictly enforce ws://127.0.0.1:9010.");
    }

    private static void CheckRazerProvider(Action<bool, string> check)
    {
        var req90 = RazerBatteryProvider.BuildCommandReport(RazerBatteryProvider.CommandIdGetBatteryLevel, transactionId: 0x1F, includeReportId: false);
        check(req90.Length == 90
              && req90[1] == 0x1F
              && req90[5] == 0x02
              && req90[6] == 0x07
              && req90[7] == 0x80
              && req90[88] == (0x02 ^ 0x07 ^ 0x80),
            "Razer 90-byte battery request must compute XOR CRC at byte 88 over bytes 2..87.");

        var reply91 = new byte[91];
        reply91[0] = 0x00; // Windows report ID prefix
        reply91[1] = 0x02; // Status: success
        reply91[2] = 0x1F; // Transaction ID
        reply91[6] = 0x02; // Data size
        reply91[7] = 0x07; // Command class
        reply91[8] = 0x80; // Command ID
        reply91[10] = 204; // Arg1: 204 / 255 = 80%
        reply91[89] = RazerBatteryProvider.ComputeXorCrc(reply91);

        check(RazerBatteryProvider.TryParseCommandReply(reply91, 0x80, out var rawVal)
              && rawVal == 204
              && RazerBatteryProvider.ScaleRawByteToPercent(rawVal) == 80,
            "Razer parser must validate status 0x02 and XOR CRC and scale 204/255 -> 80%.");

        reply91[89] ^= 0xFF; // Corrupt CRC
        check(!RazerBatteryProvider.TryParseCommandReply(reply91, 0x80, out _),
            "Razer parser must reject replies with corrupted XOR CRC.");
    }

    private static void CheckXInputProvider(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        check(!XInputBatteryProvider.TryBuildSlotReading(0, XInputBatteryProvider.BatteryTypeDisconnected, XInputBatteryProvider.BatteryLevelFull, now, out _)
              && !XInputBatteryProvider.TryBuildSlotReading(0, XInputBatteryProvider.BatteryTypeWired, XInputBatteryProvider.BatteryLevelFull, now, out _)
              && !XInputBatteryProvider.TryBuildSlotReading(0, XInputBatteryProvider.BatteryTypeUnknown, XInputBatteryProvider.BatteryLevelFull, now, out _),
            "XInput provider must skip wired, disconnected, and unknown controllers.");

        check(XInputBatteryProvider.TryBuildSlotReading(0, XInputBatteryProvider.BatteryTypeAlkaline, XInputBatteryProvider.BatteryLevelMedium, now, out var pad)
              && pad is not null
              && pad.Kind == BluetoothDeviceKind.Gamepad
              && pad.LevelOverrideText == UiText.Get("WirelessBatteryLevelMedium")
              && XInputBatteryProvider.GetFallbackEnglishLevelText(XInputBatteryProvider.BatteryLevelEmpty) == "Empty"
              && XInputBatteryProvider.GetFallbackEnglishLevelText(XInputBatteryProvider.BatteryLevelLow) == "Low"
              && XInputBatteryProvider.GetFallbackEnglishLevelText(XInputBatteryProvider.BatteryLevelMedium) == "Medium"
              && XInputBatteryProvider.GetFallbackEnglishLevelText(XInputBatteryProvider.BatteryLevelFull) == "Full",
            "XInput wireless controller must report coarse level text via LevelOverrideText.");
    }

    private static void CheckAggregatorCacheAndIsolation(Action<bool, string> check)
    {
        check(WirelessBatteryAggregator.IsIgnoredWiredDevice(0x2E3C, 0xC368, "Wraith W75")
              && WirelessBatteryAggregator.IsIgnoredWiredDevice(0x0000, 0x0000, "Wraith W75 Keyboard"),
            "Wraith W75 (2E3C:C368) must be recognized as an ignored wired device.");

        var fakeNow = new DateTimeOffset(2026, 11, 22, 16, 0, 0, TimeSpan.Zero);
        IReadOnlyList<WirelessDeviceReading> currentAtkReadings =
        [
            new("compx:373B:1119", "ATK A9 Ultra", BluetoothDeviceKind.Mouse, 88, true, false, false, WirelessBatterySource.CompxAtkVxe, fakeNow, VoltageMv: 4010),
            new("usb:2E3C:C368", "Wraith W75", BluetoothDeviceKind.Keyboard, 100, true, false, false, WirelessBatterySource.Bluetooth, fakeNow)
        ];

        IReadOnlyList<WirelessDeviceReading> currentSsHidReadings =
        [
            new("steelseries:1038:12E0", "Arctis Nova Pro Wireless", BluetoothDeviceKind.Headphones, 75, true, false, false, WirelessBatterySource.SteelSeriesHid, fakeNow, SecondaryBatteryLevel: 100)
        ];

        IReadOnlyList<WirelessDeviceReading> currentSsGgReadings =
        [
            new("steelseries:gg:arctis", "Arctis Nova Pro Wireless", BluetoothDeviceKind.Headphones, 70, true, false, false, WirelessBatterySource.SteelSeriesGg, fakeNow)
        ];

        var atkProvider = new DelegateBatteryProvider(WirelessBatterySource.CompxAtkVxe, _ => Task.FromResult(currentAtkReadings));
        var ssHidProvider = new DelegateBatteryProvider(WirelessBatterySource.SteelSeriesHid, _ => Task.FromResult(currentSsHidReadings));
        var ssGgProvider = new DelegateBatteryProvider(WirelessBatterySource.SteelSeriesGg, _ => Task.FromResult(currentSsGgReadings));
        var throwingProvider = new DelegateBatteryProvider(WirelessBatterySource.RazerHid, _ => throw new IOException("Simulated HID failure"));

        var aggregator = new WirelessBatteryAggregator(
            bluetoothReader: _ => Task.FromResult<IReadOnlyList<WirelessDeviceReading>>([]),
            providers: [atkProvider, ssHidProvider, ssGgProvider, throwingProvider],
            clock: () => fakeNow,
            providerTimeout: TimeSpan.FromMilliseconds(300));

        // Poll 1: active devices, Wraith W75 filtered out, SteelSeriesHid preferred over SteelSeriesGg, throwing provider isolated.
        var poll1 = aggregator.PollAsync().GetAwaiter().GetResult();
        check(poll1.Count == 2
              && poll1.All(r => !r.Name.Contains("Wraith W75", StringComparison.OrdinalIgnoreCase)),
            "Aggregator must isolate failing providers, deduplicate devices, and filter out wired Wraith W75.");

        var nova1 = poll1.Single(r => r.Name == "Arctis Nova Pro Wireless");
        check(nova1.Source == WirelessBatterySource.SteelSeriesHid
              && nova1.BatteryLevel == 75
              && nova1.SecondaryBatteryLevel == 100
              && !nova1.IsStale,
            "Aggregator deduplication must prefer direct SteelSeriesHid over SteelSeriesGg and keep spare dock battery.");

        // Poll 2 (+5 min): ATK mouse goes to sleep (returns 0% / no reading), headset turns off (IsConnected=false, 0%).
        fakeNow = fakeNow.AddMinutes(5);
        currentAtkReadings = [];
        currentSsHidReadings =
        [
            new("steelseries:1038:12E0", "Arctis Nova Pro Wireless", BluetoothDeviceKind.Headphones, 0, false, false, false, WirelessBatterySource.SteelSeriesHid, fakeNow, SecondaryBatteryLevel: 100)
        ];
        currentSsGgReadings = [];

        var poll2 = aggregator.PollAsync().GetAwaiter().GetResult();
        var atkStale = poll2.Single(r => r.Name == "ATK A9 Ultra");
        var novaStale = poll2.Single(r => r.Name == "Arctis Nova Pro Wireless");
        check(atkStale.BatteryLevel == 88 && atkStale.IsStale && atkStale.IsConnected,
            "Aggregator must retain last real percentage (88%) and mark IsStale=true when wireless mouse sleeps.");
        check(novaStale.BatteryLevel == 75 && novaStale.IsStale && novaStale.SecondaryBatteryLevel == 100,
            "Aggregator must never overwrite a valid cached battery percentage (75%) with 0% when headset turns off.");

        // Poll 3 (+31 min total): stale cache entries older than 30 minutes must expire.
        fakeNow = fakeNow.AddMinutes(26);
        var poll3 = aggregator.PollAsync().GetAwaiter().GetResult();
        check(poll3.Count == 0,
            "Stale cached wireless readings must expire after 30 minutes of inactivity.");

        // Partial reading retention on soft sub-timeout: a provider that collects slot 1 and then waits until
        // the sub-timeout token cancels on slot 2 must still return its accumulated slot 1 reading.
        var partialProvider = new DelegateBatteryProvider(
            WirelessBatterySource.LogitechHidPlusPlus,
            async ct =>
            {
                var collected = new List<WirelessDeviceReading>
                {
                    new("logitech:C547:01", "Logitech PRO X Superlight", BluetoothDeviceKind.Mouse, 91, true, false, false, WirelessBatterySource.LogitechHidPlusPlus, fakeNow)
                };
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return collected;
            });

        var partialAggregator = new WirelessBatteryAggregator(
            bluetoothReader: _ => Task.FromResult<IReadOnlyList<WirelessDeviceReading>>([]),
            providers: [partialProvider],
            clock: () => fakeNow,
            providerTimeout: TimeSpan.FromMilliseconds(200));
        var partialPoll = partialAggregator.PollAsync().GetAwaiter().GetResult();
        check(partialPoll.Count == 1 && partialPoll[0].BatteryLevel == 91,
            "Aggregator soft sub-timeout must preserve partial readings collected before cancellation.");

        // Same-source same-name devices with distinct IDs must not collapse into a single entry.
        var sameNameReadings = new List<WirelessDeviceReading>
        {
            new("bt:001122334455", "DualSense Wireless Controller", BluetoothDeviceKind.Gamepad, 80, true, false, false, WirelessBatterySource.Bluetooth, fakeNow),
            new("bt:AABBCCDDEEFF", "DualSense Wireless Controller", BluetoothDeviceKind.Gamepad, 35, true, false, false, WirelessBatterySource.Bluetooth, fakeNow)
        };
        var dedupSameName = WirelessBatteryAggregator.DeduplicateReadings(sameNameReadings);
        check(dedupSameName.Count == 2
              && dedupSameName.Any(r => r.Id == "bt:001122334455" && r.BatteryLevel == 80)
              && dedupSameName.Any(r => r.Id == "bt:AABBCCDDEEFF" && r.BatteryLevel == 35),
            "DeduplicateReadings must preserve distinct same-name Bluetooth devices with different IDs.");

        var sameNameAggregator = new WirelessBatteryAggregator(
            bluetoothReader: _ => Task.FromResult<IReadOnlyList<WirelessDeviceReading>>(sameNameReadings),
            providers: [],
            clock: () => fakeNow);
        var sameNamePoll = sameNameAggregator.PollAsync().GetAwaiter().GetResult();
        check(sameNamePoll.Count == 2
              && sameNamePoll.Any(r => r.Id == "bt:001122334455" && r.BatteryLevel == 80)
              && sameNamePoll.Any(r => r.Id == "bt:AABBCCDDEEFF" && r.BatteryLevel == 35),
            "PollAsync must preserve distinct same-name devices across cache merging.");
    }

    private static void CheckAllLocaleCatalogs(Action<bool, string> check)
    {
        var assembly = typeof(LanguageCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("OpenTaskbar.Localization.Languages.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        check(resources.Length == 14, "All 14 locale JSON resources must be embedded.");

        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            check(stream.ReadByte() == 0x7B, $"{resource} must start with '{{' and have no UTF-8 BOM.");
            stream.Position = 0;

            using var document = JsonDocument.Parse(stream);
            check(document.RootElement.ValueKind == JsonValueKind.Object, $"{resource} root must be a JSON object.");

            foreach (var key in RequiredLocalizationKeys)
            {
                check(document.RootElement.TryGetProperty(key, out var val)
                      && val.ValueKind == JsonValueKind.String
                      && !string.IsNullOrWhiteSpace(val.GetString()),
                    $"{resource} must provide a non-empty translation for {key}.");
            }
        }
    }

    private static void CheckLiveHardwareScan(Action<bool, string> check)
    {
        var readings = WirelessBatteryAggregator.ReadAllReadingsAsync().GetAwaiter().GetResult();
        check(readings.All(r => !r.Name.Contains("Wraith W75", StringComparison.OrdinalIgnoreCase)
                                && !r.Id.Contains("2E3C", StringComparison.OrdinalIgnoreCase)),
            "Live WirelessBatteryAggregator scan must never include wired Wraith W75 (2E3C:C368).");
    }

    private static int RunUiChecksOnSta()
    {
        Exception? failure = null;
        var checks = 0;
        var thread = new Thread(() =>
        {
            try
            {
                checks = VerifyWidgetUi(
                    (cond, msg) =>
                    {
                        if (!cond) throw new InvalidOperationException(msg);
                    });
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return checks;
    }

    private static void EnsureWidgetResources(FrameworkElement target)
    {
        if (Application.Current is not null && Application.Current.TryFindResource("TextPrimary") is not null)
            return;

        foreach (var resource in new[]
                 {
                     "Tokens/Typography", "Tokens/Spacing", "Tokens/Radii", "Tokens/Layout", "Tokens/Motion", "Tokens/Effects",
                     "Palettes/Dark",
                     "Icons/Navigation", "Icons/Actions", "Icons/Status", "Icons/Media", "Icons/Brand", "Icons/GeometryTemplate",
                     "Controls/Tooltip", "Controls/ScrollBar",
                     "DockControls", "ContextMenus", "Widgets", "WidgetControls", "SettingsControls"
                 })
        {
            target.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/OpenTaskbar;component/Design/{resource}.xaml", UriKind.Relative)
            });
        }
    }

    private static int VerifyWidgetUi(Action<bool, string> check)
    {
        var startChecks = 0;
        void LocalCheck(bool condition, string message)
        {
            check(condition, message);
            startChecks++;
        }

        var sampledAt = DateTimeOffset.UtcNow.AddMinutes(-4);
        var widget = new BluetoothBatteryWidget
        {
            BatteryReader = () => Task.FromResult<IReadOnlyList<BluetoothDeviceBatteryInfo>>(
            [
                new("ATK A9 Ultra", "compx:373B:1119", true, 100, BluetoothDeviceKind.Mouse,
                    IsCharging: true, IsStale: false, SourceLabel: "ATK/VXE", SampledAt: DateTimeOffset.UtcNow),
                new("Arctis Nova Pro Wireless", "steelseries:1038:12E0", true, 75, BluetoothDeviceKind.Headphones,
                    IsCharging: false, IsStale: true, SourceLabel: "SteelSeries", SampledAt: sampledAt, SecondaryBatteryLevel: 100),
                new("Xbox Wireless Controller (1)", "xinput:slot:0", true, 20, BluetoothDeviceKind.Gamepad,
                    IsCharging: false, IsStale: false, SourceLabel: "XInput", LevelOverrideText: "Low")
            ])
        };
        EnsureWidgetResources(widget);

        widget.Activate();
        widget.RefreshBatteriesAsync().GetAwaiter().GetResult();

        var button = (Button)widget.Content!;
        var tiles = (StackPanel)button.Content!;
        LocalCheck(tiles.Children.Count == 3, "All 3 connected wireless devices must render as dock tiles.");

        // Tile 0: Charging mouse must preserve stack.Children[1] as TextBlock ("100%") and append charging bolt at index 2.
        var tile0 = (Border)tiles.Children[0];
        var stack0 = (StackPanel)tile0.Child!;
        LocalCheck(stack0.Children[1] is TextBlock { Text: "100%" } && stack0.Children.Count == 3,
            "Charging device tile must keep percentage TextBlock at stack.Children[1] and append charging bolt at index 2.");
        LocalCheck(Math.Abs(tile0.Opacity - 1.0) < 0.01, "Live non-stale tile must render at full opacity.");

        // Tile 1: Stale headset must render with dimmed opacity.
        var tile1 = (Border)tiles.Children[1];
        var stack1 = (StackPanel)tile1.Child!;
        LocalCheck(stack1.Children[1] is TextBlock { Text: "75%" } && tile1.Opacity < 0.8,
            "Stale device tile must render with dimmed opacity while preserving percentage TextBlock at index 1.");

        // Tile 2: XInput coarse level override must render "Low" at index 1.
        var tile2 = (Border)tiles.Children[2];
        var stack2 = (StackPanel)tile2.Child!;
        LocalCheck(stack2.Children[1] is TextBlock { Text: "Low" },
            "Coarse LevelOverrideText must be displayed in stack.Children[1].");

        // Flyout verification
        var flyout = widget.BuildHoverFlyoutContent();
        LocalCheck(flyout is StackPanel, "Hover flyout root must remain a StackPanel.");
        var allTexts = CollectTextBlocks((DependencyObject)flyout!).Select(t => t.Text).ToList();
        LocalCheck(allTexts.Contains("ATK/VXE") && allTexts.Contains("SteelSeries") && allTexts.Contains("XInput"),
            "Hover flyout must display protocol source badges for each wireless device.");
        LocalCheck(allTexts.Any(t => t.Contains("100", StringComparison.Ordinal)),
            "Hover flyout must display spare dock battery badge and percentages.");

        widget.Deactivate();
        return startChecks;
    }

    private static IEnumerable<TextBlock> CollectTextBlocks(DependencyObject root)
    {
        if (root is TextBlock tb) yield return tb;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var nested in CollectTextBlocks(child))
                yield return nested;
        }
    }

    private sealed class DelegateBatteryProvider(
        WirelessBatterySource source,
        Func<CancellationToken, Task<IReadOnlyList<WirelessDeviceReading>>> probe) : IWirelessBatteryProvider
    {
        public WirelessBatterySource Source => source;
        public Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(CancellationToken cancellationToken) => probe(cancellationToken);
    }
}
