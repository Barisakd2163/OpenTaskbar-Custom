using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using OpenTaskbar.Services;

internal static class WatchdogAssessmentChecks
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenTaskbar-WatchdogChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CleanExitAndHeartbeat();
            RealExitCode();
            UnavailableParentIsNotACrash();
            EvidenceIsAtomicAndIndependent(root);
            UnavailableStoragePreservesEvidence(root);
            StrictProtocol();
            return 6;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CleanExitAndHeartbeat()
    {
        using var clean = new AutoResetEvent(true);
        using var heartbeat = new AutoResetEvent(false);
        var cleanResult = TaskbarRecoveryWatchdog.ObserveParent(Environment.ProcessId, clean, heartbeat, 10, 2);
        Require(cleanResult.CleanExit && cleanResult.ExitCode is null, "A clean signal must suppress abnormal-exit evidence.");

        var timeout = TaskbarRecoveryWatchdog.ObserveParent(Environment.ProcessId, clean, heartbeat, 10, 2);
        Require(!timeout.CleanExit && timeout.Reason == "heartbeat-timeout" && timeout.ExitCode is null,
            "Absent heartbeats from a still-live process must not be reported as a process crash or zero exit code.");

        var pulse = new Timer(_ => heartbeat.Set(), null, 0, 5);
        try
        {
            var observation = Task.Run(() => TaskbarRecoveryWatchdog.ObserveParent(Environment.ProcessId, clean, heartbeat, 50, 3));
            Thread.Sleep(100);
            clean.Set();
            Require(observation.Wait(TimeSpan.FromSeconds(3)) && observation.Result.CleanExit,
                "Continuing heartbeats must keep observation alive until the intentional clean exit.");
        }
        finally { pulse.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void RealExitCode()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("exit 37");
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Could not create the isolated exit-code child.");
        // Keep its real process handle alive so the terminated PID cannot be recycled before observation.
        _ = child.Handle;
        Require(child.WaitForExit(5000), "The isolated exit-code child did not exit.");
        var result = TaskbarRecoveryWatchdog.ObserveParent(child.Id, null, null);
        Require(!result.CleanExit && result.Reason == "process-exited" && result.ExitCode == 37,
            "A signaled process must retain its actual nonzero exit code even without heartbeat events.");
    }

    private static void UnavailableParentIsNotACrash()
    {
        var result = TaskbarRecoveryWatchdog.ObserveParent(int.MaxValue, null, null);
        Require(!result.CleanExit && result.Reason == "parent-unavailable" && result.ExitCode is null,
            "A missing/query-inaccessible parent must not manufacture a crash or an exit code.");
    }

    private static void EvidenceIsAtomicAndIndependent(string root)
    {
        var directory = Path.Combine(root, "evidence");
        Parallel.For(0, 8, index =>
        {
            var result = new TaskbarRecoveryWatchdog.Observation(false, "process-exited", 0xC0000005);
            Require(TaskbarRecoveryWatchdog.TryWriteExitEvidence(directory, 4242, result) is not null,
                "Each concurrent exit observation must receive its own completed evidence file.");
        });
        var paths = Directory.GetFiles(directory, "exit-4242-*.json");
        Require(paths.Length == 8 && Directory.GetFiles(directory, "*.tmp").Length == 0,
            "Concurrent writes must preserve every record and leave no partially published JSON files.");
        foreach (var path in paths)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var record = document.RootElement;
            Require(record.GetProperty("pid").GetInt32() == 4242
                && record.GetProperty("exitCode").GetString() == "0xC0000005"
                && record.GetProperty("reason").GetString() == "process-exited"
                && record.GetProperty("utc").GetDateTimeOffset().Offset == TimeSpan.Zero,
                "Exit evidence must preserve the PID, unsigned Windows status, UTC and confirmed-exit classification.");
        }
        var timeoutPath = TaskbarRecoveryWatchdog.TryWriteExitEvidence(directory, 4242,
            new(false, "heartbeat-timeout", null)) ?? throw new InvalidOperationException("Missing timeout evidence.");
        using var timeout = JsonDocument.Parse(File.ReadAllText(timeoutPath));
        Require(timeout.RootElement.GetProperty("exitCode").ValueKind == JsonValueKind.Null,
            "Timeout evidence must explicitly mark the unknown exit code.");
        Require(TaskbarRecoveryWatchdog.TryWriteExitEvidence(directory, 4242, new(true, "clean-exit", null)) is null
            && Directory.GetFiles(directory, "*.json").Length == 9, "Clean exits must not create abnormal evidence.");
    }

    private static void UnavailableStoragePreservesEvidence(string root)
    {
        var blocker = Path.Combine(root, "storage-blocker");
        File.WriteAllText(blocker, "preserve-me");
        Require(TaskbarRecoveryWatchdog.TryWriteExitEvidence(Path.Combine(blocker, "Crashes"), 4242,
            new(false, "parent-unavailable", null)) is null && File.ReadAllText(blocker) == "preserve-me",
            "Unavailable diagnostic storage must fail safely without overwriting unrelated data.");
    }

    private static void StrictProtocol()
    {
        foreach (var argument in new[] { "--watchdog=42:a/b", "--watchdog=42:a b", "--watchdog=42:a:b",
                     "--watchdog=42:" + new string('a', 128) })
            Require(!TaskbarRecoveryWatchdog.TryParseArguments([argument], out _, out _),
                "Managed and native watchdogs must reject unsafe or truncated event tokens.");
        try
        {
            TaskbarRecoveryWatchdog.BuildAnalyzerStartInfo("relative.exe");
            throw new InvalidOperationException("A relative analyzer executable was accepted.");
        }
        catch (ArgumentException) { }
        try
        {
            TaskbarRecoveryWatchdog.BuildAnalyzerStartInfo(Environment.ProcessPath!, "relative-history");
            throw new InvalidOperationException("A relative evidence directory was accepted.");
        }
        catch (ArgumentException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
