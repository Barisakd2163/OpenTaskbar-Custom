using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTaskbar.Services;

internal static class CrashAssessmentChecks
{
    private static readonly DateTimeOffset CrashUtc = DateTimeOffset.Parse("2026-11-25T18:13:09.4364728Z", CultureInfo.InvariantCulture);
    private const string Oom = "System.OutOfMemoryException: Insufficient memory to continue the execution of the program.\n" +
        "   at System.Windows.Media.Composition.DUCE.Channel.SyncFlush()\n" +
        "   at System.Windows.Interop.HwndTarget.UpdateWindowSettings(Boolean enableRenderTarget, Nullable`1 channelSet)\n" +
        "   at System.Windows.Interop.HwndTarget.OnShowWindow(IntPtr wparam, IntPtr lparam)";
    private const string Media = "System.NullReferenceException: Object reference not set to an instance of an object.\n" +
        "   at OpenTaskbar.Widgets.MediaWidget.RefreshPlayback() in D:\\source\\MediaWidget.xaml.cs:line 120";

    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenTaskbar-assessment-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            EmptyHistory(Path.Combine(root, "empty"));
            RepeatedReruns(Path.Combine(root, "reruns"));
            CrossBuildGrouping(Path.Combine(root, "builds"));
            CurrentOomAndGdi(Path.Combine(root, "gdi"));
            MissingEvidence(Path.Combine(root, "missing"));
            MalformedAlongsideValid(Path.Combine(root, "malformed"));
            UnknownAndMemory(Path.Combine(root, "unknown"));
            NativeCorrelation(Path.Combine(root, "native"));
            UnprovenNativeExit(Path.Combine(root, "orphan"));
            ResourceTrends(Path.Combine(root, "resources"));
            RejectUnrelatedWer(Path.Combine(root, "unrelated"));
            NanSizing(Path.Combine(root, "nan"));
            WatchdogObservationErrors(Path.Combine(root, "watchdog-errors"));
            ConcurrentAnalyzer(Path.Combine(root, "concurrent"));
            NewestEvidenceSurvivesCap(Path.Combine(root, "history"));
            return 15;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void EmptyHistory(string directory)
    {
        Directory.CreateDirectory(directory);
        Require(CrashAssessment.Analyze(directory) is null, "Empty history is not a crash.");
        Require(!Directory.Exists(Path.Combine(directory, "Analysis")), "Empty history must not manufacture a report.");
    }

    private static void RepeatedReruns(string directory)
    {
        var rawPath = Report(directory, "one", Media);
        var raw = File.ReadAllText(rawPath);
        using var first = Analyze(directory);
        using var second = Analyze(directory);
        Require(OnlyGroup(first).GetProperty("count").GetInt32() == 1, "One input is one occurrence.");
        Require(OnlyGroup(second).GetProperty("count").GetInt32() == 1, "Reruns must not increment counts.");
        Require(first.RootElement.GetProperty("assessmentId").GetString() == second.RootElement.GetProperty("assessmentId").GetString(), "Unchanged evidence must have a stable assessment ID.");
        Require(File.ReadAllText(rawPath) == raw, "Assessment must preserve raw evidence.");
        Require(Directory.GetFiles(Path.Combine(directory, "Analysis"), "*.tmp").Length == 0, "Atomic writes must not leave temp files on success.");
    }

    private static void CrossBuildGrouping(string directory)
    {
        Report(directory, "first", Media, "0.3.4+first", 101);
        Report(directory, "second", Media.Replace("line 120", "line 999", StringComparison.Ordinal), "0.3.5+second", 202, CrashUtc.AddDays(1));
        using var result = Analyze(directory);
        var group = OnlyGroup(result);
        Require(group.GetProperty("count").GetInt32() == 2, "Timestamp/PID/build/source line changes must not split repeats.");
        Require(group.GetProperty("category").GetString() == "media-refresh-null", "Media refresh null has its own rule.");
        var builds = group.GetProperty("builds").EnumerateArray().ToArray();
        Require(builds.Length == 2 && builds.All(b => b.GetProperty("count").GetInt32() == 1), "Per-build counts must be retained.");
        Require(result.RootElement.GetProperty("latestReportId").GetString() == "crash-second.txt", "Latest means UTC, not filename scan order.");
    }

    private static void CurrentOomAndGdi(string directory)
    {
        Report(directory, "current", Oom, "0.3.4-overlay.5+f535798", 8332);
        WriteEvents(directory, new
        {
            utc = "2026-11-25T18:12:44.3137467Z", eventId = 1001,
            provider = "Windows Error Reporting", eventName = "GDIObjectLeak", application = "OpenTaskbar.exe", pid = 8332
        });
        using var result = Analyze(directory);
        var group = OnlyGroup(result);
        Require(group.GetProperty("category").GetString() == "wpf-rendering-resource-exhaustion", "DUCE/HwndTarget OOM is not prior media NRE or generic memory classification.");
        Require(group.GetProperty("confidence").GetString() == "high", "Correlated WER GDI leak is strong resource-exhaustion evidence.");
        Require(group.GetProperty("summary").GetString()!.Contains("GDIObjectLeak", StringComparison.Ordinal), "Summary must explain the GDI evidence.");
        Require(Contains(group, "evidence", "SyncFlush") && Contains(group, "evidence", "windows-events.json"), "Both exception frames and WER filename must be evidence.");
        Require(Contains(group, "caveats", "sızıntıyı başlatan metot") && Contains(group, "caveats", "RAM"), "Do not invent the leaking method or assert generic RAM shortage.");
        Require(!Contains(group, "caveats", "olayında PID yok"), "An actual matching event PID must not be described as missing.");
        Require(group.GetProperty("nextSteps").GetArrayLength() > 0, "Assessment must provide diagnostic next steps.");
    }

    private static void MissingEvidence(string directory)
    {
        Report(directory, "missing", Oom);
        using var result = Analyze(directory);
        var group = OnlyGroup(result);
        Require(group.GetProperty("confidence").GetString() == "moderate", "Exception-only OOM must not claim proven GDI exhaustion.");
        Require(Contains(group, "caveats", "kaynak örneği yok") && Contains(group, "caveats", "Windows/WER"), "Missing optional evidence must be explicit.");
        WriteEvents(directory, new { utc = CrashUtc, eventId = 1001, provider = "Windows Error Reporting", eventName = "GDIObjectLeak", application = "OpenTaskbar.exe" });
        using var withoutPid = Analyze(directory);
        Require(Contains(OnlyGroup(withoutPid), "caveats", "olayında PID yok"), "Application/time-only correlation must explicitly caveat absent PID.");
        Require(OnlyGroup(withoutPid).GetProperty("confidence").GetString() == "moderate", "Missing PID must not prove the GDI event belongs to this process.");
    }

    private static void MalformedAlongsideValid(string directory)
    {
        Report(directory, "valid", Media);
        File.WriteAllText(Path.Combine(directory, "crash-partial.txt"), "OpenTaskbar local crash report\nUTC: unfinished");
        File.WriteAllText(Path.Combine(directory, "exit-broken.json"), "{\"pid\":");
        File.WriteAllText(Path.Combine(directory, "resources-8332.jsonl"), "{partial\n");
        using var result = Analyze(directory);
        Require(OnlyGroup(result).GetProperty("count").GetInt32() == 1, "Malformed inputs must not discard or inflate the valid occurrence.");
        Require(result.RootElement.GetProperty("scanWarnings").GetArrayLength() >= 3, "Malformed report, exit and resources must be visible warnings.");
        Require(File.Exists(Path.Combine(directory, "crash-partial.txt")), "Do not delete malformed raw evidence.");
    }

    private static void UnknownAndMemory(string directory)
    {
        Report(directory, "unknown", "System.InvalidOperationException: arbitrary message\n   at OpenTaskbar.CustomComponent.Break()", pid: 1);
        Report(directory, "memory", "System.OutOfMemoryException: allocation failed\n   at OpenTaskbar.BufferOwner.Allocate()", pid: 2);
        using var result = Analyze(directory);
        var groups = result.RootElement.GetProperty("groups").EnumerateArray().ToArray();
        Require(groups.Length == 2, "Different exception paths must not collapse.");
        var unknown = groups.Single(g => g.GetProperty("category").GetString() == "unknown-managed-failure");
        Require(unknown.GetProperty("confidence").GetString() == "low", "Unknown signature must remain unknown, not an invented diagnosis.");
        Require(groups.Any(g => g.GetProperty("category").GetString() == "memory-error"), "Non-WPF OOM must use general memory-error category.");
    }

    private static void NativeCorrelation(string directory)
    {
        Report(directory, "managed", Oom, pid: 8332);
        WriteExit(directory, "matching", 8332, CrashUtc.AddSeconds(2), "0xE0434352", "process-exited");
        WriteExit(directory, "reused-pid", 8332, CrashUtc.AddHours(1), 1, "process-exited");
        File.WriteAllText(Path.Combine(directory, "resources-8332.jsonl"), JsonSerializer.Serialize(new
        {
            pid = 8332, utc = CrashUtc.AddHours(1).AddSeconds(-1), build = "native-build", gdiObjects = 20
        }) + "\n");
        using var result = Analyze(directory);
        var groups = result.RootElement.GetProperty("groups").EnumerateArray().ToArray();
        Require(groups.Sum(g => g.GetProperty("count").GetInt32()) == 2, "Matching native exit must not double-count managed crash; distant reused PID remains separate.");
        var managed = groups.Single(g => g.GetProperty("category").GetString() == "wpf-rendering-resource-exhaustion");
        Require(Contains(managed, "evidence", "exit-matching.json"), "Native exit must attach to its managed occurrence.");
        Require(managed.GetProperty("count").GetInt32() == 1, "Native and managed are evidence of one occurrence.");
        var native = groups.Single(g => g.GetProperty("category").GetString() == "unexplained-process-exit");
        Require(native.GetProperty("builds")[0].GetProperty("build").GetString() == "native-build",
            "Resource samples preserve deployed build identity when no managed report survives.");
    }

    private static void UnprovenNativeExit(string directory)
    {
        Directory.CreateDirectory(directory);
        WriteExit(directory, "terminated", 42, CrashUtc, 1, "process-exited");
        WriteExit(directory, "timeout", 43, CrashUtc.AddMinutes(1), null, "heartbeat-timeout");
        WriteExit(directory, "zero-without-clean-handshake", 44, CrashUtc, 0, "process-exited");
        using var result = Analyze(directory);
        var groups = result.RootElement.GetProperty("groups").EnumerateArray().ToArray();
        Require(groups.Sum(g => g.GetProperty("count").GetInt32()) == 3, "No clean handshake is still an unexplained exit, even with code zero; nullable timeout code must be supported.");
        Require(groups.All(g => g.GetProperty("category").GetString() == "unexplained-process-exit" && g.GetProperty("confidence").GetString() == "low"), "Exit-only records cannot prove an application bug.");
        Require(groups.All(g => Contains(g, "caveats", "End Task")), "User End Task must remain a possible explanation.");
    }

    private static void ResourceTrends(string directory)
    {
        Report(directory, "resources", Oom, pid: 8332);
        var first = new { pid = 8332, utc = CrashUtc.AddMinutes(-2), gdiObjects = 100, userObjects = 20, handles = 50, privateBytes = 1000, workingSetBytes = 800, managedBytes = 400 };
        var last = new { pid = 8332, utc = CrashUtc.AddSeconds(-1), gdiObjects = 9900, userObjects = 25, handles = 70, privateBytes = 1500, workingSetBytes = 900, managedBytes = 450, gdiPeakObjects = 9950, userPeakObjects = 30 };
        File.WriteAllText(Path.Combine(directory, "resources-8332.old.jsonl"), JsonSerializer.Serialize(first) + "\n{partial\n");
        File.WriteAllText(Path.Combine(directory, "resources-8332.jsonl"), JsonSerializer.Serialize(last) + "\n");
        using var result = Analyze(directory);
        var group = OnlyGroup(result);
        var resources = group.GetProperty("occurrences")[0].GetProperty("resources");
        Require(resources.GetProperty("sampleCount").GetInt32() == 2, "Current and rotated samples must correlate despite a partial line.");
        Require(resources.GetProperty("peakGdiObjects").GetInt64() == 9900, "GDI peak must be measured, not guessed.");
        Require(resources.GetProperty("processPeakGdiObjects").GetInt64() == 9950, "Reported lifetime peak must remain distinct from the observed window peak.");
        Require(resources.GetProperty("first").GetProperty("gdiObjects").GetInt64() == 100 && resources.GetProperty("last").GetProperty("gdiObjects").GetInt64() == 9900, "First/last metrics must show observed growth.");
        Require(Contains(group, "caveats", "artış tek başına"), "Measured growth must not invent leak attribution.");
    }

    private static void RejectUnrelatedWer(string directory)
    {
        Report(directory, "oom", Oom, pid: 8332);
        var events = new object[]
        {
            new { utc = CrashUtc, eventId = 1001, provider = "Windows Error Reporting", eventName = "GDIObjectLeak", application = "Other.exe", pid = 8332 },
            new { utc = CrashUtc, eventId = 1001, provider = "Windows Error Reporting", eventName = "GDIObjectLeak", application = "OpenTaskbar.exe", pid = 9999 },
            new { utc = CrashUtc.AddHours(-1), eventId = 1001, provider = "Windows Error Reporting", eventName = "GDIObjectLeak", application = "OpenTaskbar.exe", pid = 8332 }
        };
        WriteEvents(directory, events);
        using var result = Analyze(directory);
        Require(OnlyGroup(result).GetProperty("confidence").GetString() == "moderate", "Wrong application, PID or time must not strengthen the diagnosis.");
    }

    private static void NanSizing(string directory)
    {
        Report(directory, "nan", "System.ArgumentException: NaN is not a valid value for property MaxWidth.\n   at OpenTaskbar.MainWindow.UpdateDockDimensions()");
        using var result = Analyze(directory);
        Require(OnlyGroup(result).GetProperty("category").GetString() == "dock-sizing-nan", "NaN dock sizing must not be misclassified as media/memory error.");
        Report(directory, "not-nan", "System.InvalidOperationException: Banana is not available.\n   at OpenTaskbar.MainWindow.UpdateDockDimensions()");
        using var unrelated = Analyze(directory);
        Require(unrelated.RootElement.GetProperty("groups").EnumerateArray().Any(g => g.GetProperty("category").GetString() == "unknown-managed-failure"),
            "An incidental NaN substring must not be diagnosed as invalid dock sizing.");
    }

    private static void WatchdogObservationErrors(string directory)
    {
        Directory.CreateDirectory(directory);
        WriteExit(directory, "unavailable", 12, CrashUtc, null, "parent-unavailable");
        WriteExit(directory, "error", 12, CrashUtc, null, "watchdog-error");
        using var result = Analyze(directory);
        Require(result.RootElement.GetProperty("groups").GetArrayLength() == 0, "Watchdog observation errors do not prove process exit.");
        Require(result.RootElement.GetProperty("scanWarnings").GetArrayLength() == 2, "Watchdog observation failures must remain visible.");
    }

    private static void ConcurrentAnalyzer(string directory)
    {
        Report(directory, "one", Media);
        var normalized = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var lockId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var gate = new Mutex(false, @"Local\OpenTaskbar.CrashAssessment." + lockId);
            gate.WaitOne();
            try { held.Set(); release.Wait(); }
            finally { gate.ReleaseMutex(); }
        });
        try
        {
            Require(held.Wait(TimeSpan.FromSeconds(10)), "Named mutex holder must start.");
            Require(CrashAssessment.Analyze(directory) is null, "Concurrent analyzer must not write overlapping output.");
            Require(!Directory.Exists(Path.Combine(directory, "Analysis")), "Busy analyzer must not publish a partial snapshot.");
        }
        finally { release.Set(); holder.GetAwaiter().GetResult(); }
        using var result = Analyze(directory);
        Require(OnlyGroup(result).GetProperty("count").GetInt32() == 1, "Evidence must remain processable after the lock is released.");
    }

    private static void NewestEvidenceSurvivesCap(string directory)
    {
        Directory.CreateDirectory(directory);
        for (var i = 0; i < 1024; i++)
        {
            var old = Path.Combine(directory, $"crash-old-{i:D4}.txt");
            File.WriteAllText(old, "partial old report");
            File.SetLastWriteTimeUtc(old, CrashUtc.AddDays(-1).UtcDateTime);
        }
        var newest = Report(directory, "newest", Oom);
        File.SetLastWriteTimeUtc(newest, CrashUtc.UtcDateTime);
        using var result = Analyze(directory);
        Require(OnlyGroup(result).GetProperty("count").GetInt32() == 1
            && result.RootElement.GetProperty("latestReportId").GetString() == "crash-newest.txt",
            "A full diagnostic history must never hide the newest crash.");
    }

    private static string Report(string directory, string id, string exception, string build = "test-build", int pid = 8332, DateTimeOffset? utc = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "crash-" + id + ".txt");
        File.WriteAllText(path, $"OpenTaskbar local crash report\nUTC: {utc ?? CrashUtc:O}\nOrigin: regression\nBuild: {build}\nPID: {pid}\n\nException (including inner exceptions):\n{exception}\n\nSafe layout/widget context:\nLayout: Dock\n");
        return path;
    }

    private static void WriteExit(string directory, string id, int pid, DateTimeOffset utc, object? exitCode, string reason) =>
        File.WriteAllText(Path.Combine(directory, "exit-" + id + ".json"), JsonSerializer.Serialize(new { pid, utc, exitCode, reason }));

    private static void WriteEvents(string directory, params object[] events) =>
        File.WriteAllText(Path.Combine(directory, "windows-events.json"), JsonSerializer.Serialize(new { events }));

    private static JsonDocument Analyze(string directory)
    {
        var markdown = CrashAssessment.Analyze(directory);
        Require(markdown == Path.Combine(directory, "Analysis", "latest.md") && File.Exists(markdown), "Analyzer must publish readable latest.md.");
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "Analysis", "latest.json")));
        Require(result.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "Output schema must be versioned.");
        Require(File.ReadAllText(markdown!).Contains(result.RootElement.GetProperty("assessmentId").GetString()!, StringComparison.Ordinal), "JSON and Markdown must carry the same snapshot identity.");
        return result;
    }

    private static JsonElement OnlyGroup(JsonDocument result)
    {
        var groups = result.RootElement.GetProperty("groups");
        Require(groups.GetArrayLength() == 1, "Expected exactly one failure group.");
        return groups[0];
    }

    private static bool Contains(JsonElement group, string property, string text) =>
        group.GetProperty(property).EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Contains(text, StringComparison.Ordinal)
            : value.ToString().Contains(text, StringComparison.Ordinal));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
