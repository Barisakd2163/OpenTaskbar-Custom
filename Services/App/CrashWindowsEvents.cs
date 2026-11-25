using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace OpenTaskbar.Services;

/// <summary>Selected Application-log evidence only; never retain full event XML or rendered messages.</summary>
internal static class CrashWindowsEvents
{
    private const int MaximumEvents = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record Entry(DateTimeOffset Utc, int EventId, string Provider,
        string EventName, string Application, int? Pid);
    private sealed record Evidence(DateTimeOffset CollectedUtc, IReadOnlyList<Entry> Events, string? Error);

    internal static void Collect(string directory)
    {
        var path = Path.Combine(directory, "windows-events.json");
        var entries = new List<Entry>();
        string? error = null;
        if (File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length <= 2 * 1024 * 1024)
                    entries.AddRange(JsonSerializer.Deserialize<Evidence>(File.ReadAllText(path), JsonOptions)?.Events ?? []);
                else error = "Previous Windows-event cache exceeded the 2 MiB read limit.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                error = $"Previous Windows-event cache unavailable ({ex.GetType().Name}).";
            }
        }

        try
        {
            // Bound both history and work. Existing selected evidence survives Windows log rotation.
            var since = DateTimeOffset.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[(EventID=1000 or EventID=1001 or EventID=1002) and TimeCreated[@SystemTime >= '{since}']] and EventData[Data[@Name='P1']='OpenTaskbar.exe' or Data[@Name='AppName']='OpenTaskbar.exe']]")
                { ReverseDirection = true };
            using var reader = new EventLogReader(query) { BatchSize = 64 };
            var elapsed = Stopwatch.StartNew();
            var scanned = 0;
            while (scanned < MaximumEvents && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                using var record = reader.ReadEvent(TimeSpan.FromMilliseconds(250));
                if (record is null) break;
                scanned++;
                if (TryReadEntry(record.ToXml(), out var entry)) entries.Add(entry!);
            }
            if (scanned == MaximumEvents || elapsed.Elapsed >= TimeSpan.FromSeconds(5))
                error = "Windows-event scan was limited to 2048 events / 5 seconds; evidence may be incomplete.";
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Windows Application log unavailable ({ex.GetType().Name}); absence of events is not evidence of absence.";
        }

        var selected = entries.Distinct().OrderByDescending(entry => entry.Utc).ToArray();
        if (selected.Length > MaximumEvents)
        {
            selected = selected[..MaximumEvents];
            error = "Selected Windows-event cache was limited to the latest 2048 records.";
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Evidence(DateTimeOffset.UtcNow, selected, error), JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static bool TryReadEntry(string xml, out Entry? entry)
    {
        entry = null;
        try
        {
            var root = XElement.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var system = root.Element(ns + "System");
            if (system is null || !int.TryParse(system.Element(ns + "EventID")?.Value, out var eventId)
                || eventId is not (1000 or 1001 or 1002)
                || !DateTimeOffset.TryParse(system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)) return false;

            string? application = null;
            string? eventName = null;
            int? pid = null;
            foreach (var data in root.Element(ns + "EventData")?.Elements(ns + "Data") ?? [])
            {
                switch (data.Attribute("Name")?.Value)
                {
                    case "P1":
                    case "AppName":
                    case "ApplicationName":
                        application ??= data.Value;
                        break;
                    case "EventName": eventName = data.Value; break;
                    case "ProcessId": pid = ParseProcessId(data.Value); break;
                }
            }
            if (!string.Equals(Path.GetFileName(application), "OpenTaskbar.exe", StringComparison.OrdinalIgnoreCase)) return false;
            eventName ??= eventId switch { 1000 => "ApplicationError", 1002 => "ApplicationHang", _ => "WindowsErrorReporting" };
            // GDIObjectLeak is emitted in the leaking process. Other WER execution PIDs can be WerFault,
            // so do not attach those worker PIDs to the crashed application.
            if (eventId == 1001 && string.Equals(eventName, "GDIObjectLeak", StringComparison.Ordinal))
                pid ??= ParseProcessId(system.Element(ns + "Execution")?.Attribute("ProcessID")?.Value);
            entry = new Entry(utc.ToUniversalTime(), eventId,
                system.Element(ns + "Provider")?.Attribute("Name")?.Value ?? "unknown", eventName, "OpenTaskbar.exe", pid);
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException)
        {
            return false;
        }
    }

    private static int? ParseProcessId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(hex ? value.AsSpan(2) : value.AsSpan(), hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
    }
}
