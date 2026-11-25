using System.Diagnostics;
using System.IO;
using System.Text;

namespace OpenTaskbar.Services;

/// <summary>Runs outside the crashing WPF process; also drains pending reports on the next normal startup.</summary>
internal static class CrashAssessmentRunner
{
    internal static int Run(string? directory = null)
    {
        var root = directory ?? CrashReport.DefaultDirectory;
        try
        {
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root) || !HasInputs(root)) return 0;
            // Independent analyzer processes may race at startup/exit. Serialize collection as well as publication.
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
            using var gate = new Mutex(false, @"Local\OpenTaskbar.AssessmentRunner." + identity);
            var acquired = false;
            try
            {
                try { acquired = gate.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new TimeoutException("Another analyzer still owns this backlog; raw reports remain pending.");
                CrashWindowsEvents.Collect(root);
                CrashAssessment.Analyze(root);
                var oldError = Path.Combine(root, "Analysis", "analyzer-error.txt");
                if (File.Exists(oldError)) File.Delete(oldError);
                return 0;
            }
            finally
            {
                if (acquired) gate.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            // The app is already gone: preserve a distinct analyzer failure instead of hiding it as a successful assessment.
            try
            {
                var output = Path.Combine(root, "Analysis");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "analyzer-error.txt"),
                    $"UTC: {DateTimeOffset.UtcNow:O}\nLocal analysis failed; raw crash reports were not changed.\n{ex}\n");
            }
            catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"Crash analyzer evidence unavailable: {storageError.GetType().Name}");
            }
            return 1;
        }
    }

    internal static void SchedulePending()
    {
        try
        {
            var root = CrashReport.DefaultDirectory;
            if (!Directory.Exists(root) || !HasInputs(root)) return;
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                AppLog.Warning("Pending crash analysis could not start: executable identity unavailable.");
                return;
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--analyze-crashes");
            using var process = Process.Start(start);
            AppLog.Info($"Pending crash analysis launched independently (PID {process?.Id}); reports remain local.");
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Pending crash analysis could not start ({ex.GetType().Name}); raw reports remain pending.");
        }
    }

    private static bool HasInputs(string directory) => Directory.EnumerateFiles(directory, "crash-*.txt").Any()
        || Directory.EnumerateFiles(directory, "exit-*.json").Any();
}
