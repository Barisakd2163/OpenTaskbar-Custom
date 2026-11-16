using System.Globalization;
using System.Runtime.InteropServices;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public static class DockWidgetService
{
    private const byte UnknownBatteryLifePercent = 255;
    private const byte NoSystemBattery = 128;
    private const byte ChargingBatteryFlag = 8;

    public const string ClockId = "Clock";
    public const string DateId = "Date";
    public const string BatteryId = "Battery";

    private sealed record WidgetDefinition(string Id, string LabelKey, Func<CultureInfo, string> Read);

    private static readonly WidgetDefinition[] Definitions =
    [
        new(ClockId, "WidgetClock", culture => DateTime.Now.ToString("HH:mm", culture)),
        new(DateId, "WidgetDate", culture => DateTime.Now.ToString("ddd, d MMM", culture)),
        new(BatteryId, "WidgetBattery", _ => ReadBatteryStatus())
    ];

    public static IReadOnlyList<string> DefaultSelection { get; } = Definitions.Select(item => item.Id).ToArray();
    public static IReadOnlyCollection<string> SupportedIds { get; } = Definitions.Select(item => item.Id).ToArray();
    private static readonly IReadOnlyDictionary<string, WidgetDefinition> DefinitionsById =
        Definitions.ToDictionary(item => item.Id, StringComparer.Ordinal);

    public static IReadOnlyList<DockWidget> CreateWidgets(IReadOnlyList<string>? selected) =>
        Definitions.Where(definition => (selected ?? DefaultSelection).Contains(definition.Id, StringComparer.Ordinal))
            .Select(definition => new DockWidget(definition.Id, UiText.Get(definition.LabelKey)))
            .ToArray();

    public static IReadOnlyList<DockWidgetOption> CreateOptions(IReadOnlyList<string>? selected) =>
        Definitions.Select(definition => new DockWidgetOption(
            definition.Id,
            UiText.Get(definition.LabelKey),
            (selected ?? DefaultSelection).Contains(definition.Id, StringComparer.Ordinal)))
            .ToArray();

    public static void Refresh(IEnumerable<DockWidget> widgets, AppLanguage language)
    {
        var culture = CultureInfo.GetCultureInfo(language == AppLanguage.Turkish ? "tr-TR" : "en-US");
        foreach (var widget in widgets)
        {
            if (DefinitionsById.TryGetValue(widget.Id, out var definition))
                widget.Value = definition.Read(culture);
        }
    }

    private static string ReadBatteryStatus()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryLifePercent == UnknownBatteryLifePercent
            || status.BatteryFlag == NoSystemBattery)
            return UiText.Get("SystemDataUnavailable");
        var percent = $"{status.BatteryLifePercent}%";
        return (status.BatteryFlag & ChargingBatteryFlag) != 0
            ? $"{percent} · {UiText.Get("Charging")}"
            : percent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
