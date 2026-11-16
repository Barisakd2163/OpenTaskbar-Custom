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
    public const string StopwatchId = "Stopwatch";

    private sealed record WidgetDefinition(string Id, string LabelKey, Func<CultureInfo, string>? Read);

    private static readonly WidgetDefinition[] Definitions =
    [
        new(ClockId, "WidgetClock", culture => DateTime.Now.ToString("HH:mm", culture)),
        new(DateId, "WidgetDate", culture => DateTime.Now.ToString("ddd, d MMM", culture)),
        new(BatteryId, "WidgetBattery", _ => ReadBatteryStatus()),
        new("Memory", "WidgetMemory", SystemWidgetData.ReadMemory),
        new("Storage", "WidgetStorage", SystemWidgetData.ReadStorage),
        new("DayProgress", "WidgetDayProgress", culture =>
            (DateTime.Now.TimeOfDay.TotalSeconds / TimeSpan.FromDays(1).TotalSeconds).ToString("P0", culture)),
        new(StopwatchId, "WidgetStopwatch", null)
    ];

    public static IReadOnlyCollection<string> SupportedIds { get; } = Definitions.Select(item => item.Id).ToArray();
    private static readonly IReadOnlyDictionary<string, WidgetDefinition> DefinitionsById =
        Definitions.ToDictionary(item => item.Id, StringComparer.Ordinal);

    public static IReadOnlyList<DockWidget> CreateWidgets(IReadOnlyList<string>? selected, StopwatchDockWidget stopwatch) =>
        Definitions.Where(definition => selected?.Contains(definition.Id, StringComparer.Ordinal) == true)
            .Select(definition => definition.Id == StopwatchId ? stopwatch : new DockWidget(definition.Id, UiText.Get(definition.LabelKey)))
            .ToArray();

    public static IReadOnlyList<DockWidgetOption> CreateOptions(IReadOnlyList<string>? installed, IReadOnlyList<string>? selected) =>
        Definitions.Select(definition => new DockWidgetOption(
            definition.Id,
            UiText.Get(definition.LabelKey),
            UiText.Get(definition.LabelKey + "Description"),
            installed?.Contains(definition.Id, StringComparer.Ordinal) == true,
            selected?.Contains(definition.Id, StringComparer.Ordinal) == true))
            .ToArray();

    public static void Refresh(IEnumerable<DockWidget> widgets, AppLanguage language)
    {
        var culture = CultureInfo.GetCultureInfo(language == AppLanguage.Turkish ? "tr-TR" : "en-US");
        foreach (var widget in widgets)
        {
            if (widget is StopwatchDockWidget stopwatch)
                stopwatch.Refresh();
            else if (DefinitionsById.TryGetValue(widget.Id, out var definition) && definition.Read is not null)
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
