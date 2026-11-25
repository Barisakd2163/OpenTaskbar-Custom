using System.Text.Json;
using OpenTaskbar.Services;

internal static class CrashWindowsEventChecks
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }

        var gdi = Event(1001, "OpenTaskbar.exe", "GDIObjectLeak", "8332");
        Check(CrashWindowsEvents.TryReadEntry(gdi, out var entry)
            && entry!.Pid == 8332 && entry.EventName == "GDIObjectLeak"
            && entry.Utc == DateTimeOffset.Parse("2026-11-25T18:12:44.3137467Z"),
            "GDI leak event is attributed to the app process at its UTC timestamp");

        Check(!CrashWindowsEvents.TryReadEntry(Event(1001, "OpenTaskbar.Tests.exe", "GDIObjectLeak", "8332"), out _),
            "Test executable failures do not contaminate app crash assessments");
        Check(!CrashWindowsEvents.TryReadEntry(Event(1001, "OtherApp.exe", "GDIObjectLeak", "8332"), out _),
            "Unrelated application resource warnings are excluded");

        Check(CrashWindowsEvents.TryReadEntry(Event(1001, "OpenTaskbar.exe", "APPCRASH", "9999"), out entry)
            && entry!.Pid is null, "WER worker execution PID is not mistaken for the crashed app PID");
        Check(CrashWindowsEvents.TryReadEntry(Event(1000, "OpenTaskbar.exe", null, "9999", "0x208C"), out entry)
            && entry!.Pid == 8332 && entry.EventName == "ApplicationError",
            "ApplicationError hexadecimal target PID takes precedence over reporter PID");
        Check(CrashWindowsEvents.TryReadEntry(Event(1002, @"C:\Private\OpenTaskbar.exe", null, "9999"), out entry)
            && entry!.Application == "OpenTaskbar.exe" && entry.Pid is null,
            "Hang events retain app identity without retaining its full local path");
        Check(!CrashWindowsEvents.TryReadEntry("<Event><broken", out _),
            "Malformed event XML is rejected without fabricating evidence");
        CrashWindowsEvents.TryReadEntry(gdi, out entry);
        Check(!JsonSerializer.Serialize(entry).Contains("private-command-canary", StringComparison.Ordinal),
            "Unrelated event payload and machine identity are not retained");
        return checks;
    }

    private static string Event(int id, string application, string? eventName, string reporterPid, string? targetPid = null) => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System><Provider Name="Windows Error Reporting"/><EventID>{id}</EventID>
            <TimeCreated SystemTime="2026-11-25T18:12:44.3137467Z"/>
            <Execution ProcessID="{reporterPid}"/><Computer>private-command-canary</Computer></System>
          <EventData><Data Name="P1">{application}</Data>
            {(eventName is null ? "" : $"<Data Name=\"EventName\">{eventName}</Data>")}
            {(targetPid is null ? "" : $"<Data Name=\"ProcessId\">{targetPid}</Data>")}
            <Data Name="CommandLine">private-command-canary</Data></EventData>
        </Event>
        """;
}
