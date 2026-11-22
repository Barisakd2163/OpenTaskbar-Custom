using System.Runtime.InteropServices;
using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room XInput wireless gamepad battery provider using xinput1_4.dll (slots 0..3).
/// Reports coarse Empty/Low/Medium/Full levels via LevelOverrideText without fabricating fake exact percentages.
/// </summary>
internal sealed class XInputBatteryProvider : IWirelessBatteryProvider
{
    public const byte BatteryDevTypeGamepad = 0x00;

    public const byte BatteryTypeDisconnected = 0x00;
    public const byte BatteryTypeWired = 0x01;
    public const byte BatteryTypeAlkaline = 0x02;
    public const byte BatteryTypeNimh = 0x03;
    public const byte BatteryTypeUnknown = 0xFF;

    public const byte BatteryLevelEmpty = 0x00;
    public const byte BatteryLevelLow = 0x01;
    public const byte BatteryLevelMedium = 0x02;
    public const byte BatteryLevelFull = 0x03;

    private const uint ErrorSuccess = 0;

    public WirelessBatterySource Source => WirelessBatterySource.XInput;

    public Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ProbeControllers(cancellationToken), cancellationToken);

    internal static IReadOnlyList<WirelessDeviceReading> ProbeControllers(CancellationToken cancellationToken = default)
    {
        var results = new List<WirelessDeviceReading>();
        var now = DateTimeOffset.UtcNow;

        for (uint slot = 0; slot < 4; slot++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                if (XInputGetState(slot, out _) != ErrorSuccess)
                    continue;

                if (XInputGetBatteryInformation(slot, BatteryDevTypeGamepad, out var info) != ErrorSuccess)
                    continue;

                if (TryBuildSlotReading(slot, info.BatteryType, info.BatteryLevel, now, out var reading)
                    && reading is not null)
                {
                    results.Add(reading);
                }
            }
            catch (DllNotFoundException)
            {
                break;
            }
            catch (EntryPointNotFoundException)
            {
                break;
            }
            catch
            {
            }
        }

        return results;
    }

    internal static bool TryBuildSlotReading(
        uint slotIndex,
        byte batteryType,
        byte batteryLevel,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        if (slotIndex > 3) return false;
        if (batteryType is BatteryTypeDisconnected or BatteryTypeWired or BatteryTypeUnknown)
            return false;
        if (batteryType is not (BatteryTypeAlkaline or BatteryTypeNimh))
            return false;
        if (batteryLevel > BatteryLevelFull)
            return false;

        var (labelKey, meterBucket) = batteryLevel switch
        {
            BatteryLevelEmpty => ("WirelessBatteryLevelEmpty", 10),
            BatteryLevelLow => ("WirelessBatteryLevelLow", 20),
            BatteryLevelMedium => ("WirelessBatteryLevelMedium", 55),
            _ => ("WirelessBatteryLevelFull", 100)
        };

        var localizedText = UiText.TryGet(labelKey, out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : GetFallbackEnglishLevelText(batteryLevel);

        reading = new WirelessDeviceReading(
            Id: $"xinput:slot:{slotIndex}",
            Name: $"Xbox Wireless Controller ({slotIndex + 1})",
            Kind: BluetoothDeviceKind.Gamepad,
            BatteryLevel: meterBucket,
            IsConnected: true,
            IsCharging: false,
            IsStale: false,
            Source: WirelessBatterySource.XInput,
            SampledAt: sampledAt,
            LevelOverrideText: localizedText);
        return true;
    }

    internal static string GetFallbackEnglishLevelText(byte batteryLevel) => batteryLevel switch
    {
        BatteryLevelEmpty => "Empty",
        BatteryLevelLow => "Low",
        BatteryLevelMedium => "Medium",
        _ => "Full"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetBatteryInformation(
        uint dwUserIndex,
        byte devType,
        out XINPUT_BATTERY_INFORMATION pBatteryInformation);
}
