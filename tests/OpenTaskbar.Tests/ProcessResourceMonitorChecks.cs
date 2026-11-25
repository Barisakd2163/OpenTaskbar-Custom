using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using OpenTaskbar.Services;

internal static class ProcessResourceMonitorChecks
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenTaskbar-ResourceChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            BaselineAndDisposal(root);
            RotationIsBounded(root);
            TimerStopsWithSession(root);
            PartialEvidenceIsPreserved(root);
            RotationFailureCanRecover(root);
            return 5;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void BaselineAndDisposal(string root)
    {
        var directory = Path.Combine(root, "lifecycle");
        var session = new ProcessResourceMonitor.Session(directory, TimeSpan.FromMinutes(1));
        var path = CurrentPath(directory);
        try
        {
            var lines = ReadLines(path);
            Require(lines.Length == 1, "Starting a sampler must immediately persist one baseline, before any timer tick.");
            CheckRealSample(lines[0]);
            Require(session.CaptureNow() && ReadLines(path).Length == 2,
                "An explicit best-effort crash-time capture must append a complete sample.");
        }
        finally { session.Dispose(); }
        session.Dispose();
        Require(!session.CaptureNow() && ReadLines(path).Length == 2,
            "Disposal must be idempotent and disable later sampling.");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Require(exclusive.Length > 0, "Stopping the sampler must release its file handle while retaining evidence.");
    }

    private static void RotationIsBounded(string root)
    {
        var directory = Path.Combine(root, "rotation");
        using (var session = new ProcessResourceMonitor.Session(directory, TimeSpan.FromMinutes(1), 1024))
            for (var index = 0; index < 30; index++) Require(session.CaptureNow(), "A healthy sampler must keep recording through rotation.");
        var paths = Directory.GetFiles(directory, "*.jsonl");
        Require(paths.Length == 2 && File.Exists(OldPath(directory)), "Rotation must retain exactly the current and previous log.");
        var totalLines = 0;
        foreach (var path in paths)
        {
            Require(new FileInfo(path).Length <= 1024, "Both rotating resource logs must remain within the configured bound.");
            foreach (var line in ReadLines(path)) { CheckRealSample(line); totalLines++; }
        }
        Require(totalLines is > 1 and < 31, "Rotation must retain recent complete samples instead of growing without limit.");
    }

    private static void TimerStopsWithSession(string root)
    {
        var directory = Path.Combine(root, "timer");
        var session = new ProcessResourceMonitor.Session(directory, TimeSpan.FromMilliseconds(20));
        try
        {
            Require(SpinWait.SpinUntil(() => ReadLines(CurrentPath(directory)).Length >= 3, TimeSpan.FromSeconds(3)),
                "The background timer must capture real samples after its startup baseline.");
        }
        finally { session.Dispose(); }
        var afterStop = File.ReadAllBytes(CurrentPath(directory));
        Thread.Sleep(80);
        Require(File.ReadAllBytes(CurrentPath(directory)).AsSpan().SequenceEqual(afterStop),
            "Stopping must prevent even an already-queued callback from appending later samples.");
    }

    private static void PartialEvidenceIsPreserved(string root)
    {
        var directory = Path.Combine(root, "partial");
        Directory.CreateDirectory(directory);
        const string partial = "{\"pid\":";
        File.WriteAllText(CurrentPath(directory), partial);
        using (var session = new ProcessResourceMonitor.Session(directory, TimeSpan.FromMinutes(1)))
            Require(session.CaptureNow(), "Sampling must resume beside a kill-truncated prior record.");
        var lines = ReadLines(CurrentPath(directory));
        Require(lines.Length == 3 && lines[0] == partial,
            "A partial raw record must remain intact and must not be merged into the next valid JSON object.");
        CheckRealSample(lines[1]);
        CheckRealSample(lines[2]);
    }

    private static void RotationFailureCanRecover(string root)
    {
        var directory = Path.Combine(root, "storage-error");
        using var session = new ProcessResourceMonitor.Session(directory, TimeSpan.FromMinutes(1), 1024);
        Directory.CreateDirectory(OldPath(directory)); // Prevent replacing the rotation target; do not alter the raw log.
        var sawFailure = false;
        for (var index = 0; index < 10; index++)
            if (!session.CaptureNow()) { sawFailure = true; break; }
        Require(sawFailure && ReadLines(CurrentPath(directory)).Length > 0,
            "Rotation errors must be reported as an unavailable sample without destroying prior evidence or escaping into the app.");
        Directory.Delete(OldPath(directory));
        Require(session.CaptureNow(), "Sampling must recover when temporarily unavailable storage becomes usable.");
        foreach (var line in ReadLines(CurrentPath(directory))) CheckRealSample(line);
    }

    private static void CheckRealSample(string line)
    {
        using var document = JsonDocument.Parse(line);
        var sample = document.RootElement;
        Require(sample.GetProperty("pid").GetInt32() == Environment.ProcessId
            && sample.GetProperty("utc").GetDateTimeOffset().Offset == TimeSpan.Zero
            && !string.IsNullOrWhiteSpace(sample.GetProperty("build").GetString()),
            "Samples must identify this process, its cached deployed build, and a UTC timestamp.");
        foreach (var name in new[] { "gdiObjects", "userObjects", "gdiPeakObjects", "userPeakObjects", "handles",
                     "privateBytes", "workingSetBytes", "managedBytes" })
        {
            var counter = sample.GetProperty(name);
            Require(counter.ValueKind == JsonValueKind.Number && counter.GetInt64() >= 0,
                $"A healthy Windows sample must contain a real nonnegative {name} counter.");
        }
        Require(sample.GetProperty("handles").GetInt64() > 0 && sample.GetProperty("privateBytes").GetInt64() > 0,
            "Real process counters must not be substituted with zero placeholders.");
        Require(sample.GetProperty("gdiPeakObjects").GetInt64() >= sample.GetProperty("gdiObjects").GetInt64()
            && sample.GetProperty("userPeakObjects").GetInt64() >= sample.GetProperty("userObjects").GetInt64(),
            "Peak counters must include at least the currently observed GUI objects.");
        Require(!sample.TryGetProperty("error", out _), "Healthy resource capture must not silently emit unavailable metrics.");
    }

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string CurrentPath(string directory) => Path.Combine(directory, $"resources-{Environment.ProcessId}.jsonl");
    private static string OldPath(string directory) => Path.Combine(directory, $"resources-{Environment.ProcessId}.old.jsonl");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
