using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace OpenTaskbar.Services;

/// <summary>
/// A tiny second process that watches the main instance and restores the native taskbar if the main process
/// disappears without saying goodbye. Managed crash handlers cannot cover End Task, taskkill /F, FailFast or
/// power loss; the only reliable recovery for those cases lives outside the dying process.
///
/// The main process owns two named auto-reset events (a heartbeat and a clean-exit signal) keyed by its PID and
/// a unique token. Recovery always precedes local evidence/analysis; a heartbeat timeout is not a proven crash.
/// </summary>
internal static class TaskbarRecoveryWatchdog
{
    private const string ArgumentPrefix = "--watchdog=";
    private const string CleanExitPrefix = @"Local\OpenTaskbar.CleanExit.";
    private const string AnalyzerExecutablePrefix = "--analyzer-exe=";
    private const string CrashDirectoryPrefix = "--crash-directory=";
    private const string HeartbeatPrefix = @"Local\OpenTaskbar.Heartbeat.";
    private const int HeartbeatIntervalMs = 1000;
    private const int HeartbeatTimeoutMs = 4000;
    // A single delayed heartbeat (GC, thread-pool starvation) must never be mistaken for a hung dock and
    // trigger a restore that would delete a live session's journals.
    private const int HeartbeatMissesRequired = 3;

    private static EventWaitHandle? _cleanExit;
    private static EventWaitHandle? _heartbeat;
    private static Timer? _heartbeatTimer;
    private static bool _started;
    private static int _fatalExit;
    internal static void MarkFatalExit() => Interlocked.Exchange(ref _fatalExit, 1);
    internal static bool IsStarted => _started;

    internal static bool IsWatchdogInvocation(string[] args) =>
        args.Any(arg => arg.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase));

    internal static string BuildArguments(int parentPid, string token) => $"{ArgumentPrefix}{parentPid}:{token}";

    internal static bool TryParseArguments(string[] args, out int parentPid, out string token)
    {
        parentPid = 0;
        token = string.Empty;
        var argument = args.FirstOrDefault(arg => arg.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase));
        if (argument is null) return false;

        var parts = argument[ArgumentPrefix.Length..].Split(':');
        if (parts.Length != 2 || parts[0].Length is 0 or > 10 || !parts[0].All(char.IsAsciiDigit)
            || !int.TryParse(parts[0], out parentPid) || parentPid <= 0
            || parts[1].Length is 0 or > 127 || !parts[1].All(char.IsAsciiLetterOrDigit))
        {
            parentPid = 0;
            return false;
        }

        token = parts[1];
        return true;
    }

    internal readonly record struct Observation(bool CleanExit, string Reason, uint? ExitCode)
    {
        internal DateTimeOffset Utc { get; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Watchdog entry point, before any WPF initialization. Never restarts the dock.</summary>
    public static void Run(string[] args)
    {
        if (!TryParseArguments(args, out var parentPid, out var token)) return;
        using var cleanExit = OpenExistingEvent(CleanExitPrefix, parentPid, token);
        using var heartbeat = OpenExistingEvent(HeartbeatPrefix, parentPid, token);
        var observation = ObserveParent(parentPid, cleanExit, heartbeat);
        if (observation.CleanExit) return;

        // Keep recovery independent of disk space, evidence parsing and analyzer availability.
        var error = RestoreCoordinator.Restore($"watchdog-{observation.Reason}");
        if (error is not null) AppLog.Error(error);
        try
        {
            var directoryArgument = args.FirstOrDefault(arg => arg.StartsWith(CrashDirectoryPrefix, StringComparison.OrdinalIgnoreCase));
            var directory = directoryArgument is null ? CrashReport.DefaultDirectory
                : directoryArgument[CrashDirectoryPrefix.Length..];
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Crash directory must be absolute.");
            TryWriteExitEvidence(directory, parentPid, observation);
            var supplied = args.FirstOrDefault(arg => arg.StartsWith(AnalyzerExecutablePrefix, StringComparison.OrdinalIgnoreCase));
            var executable = supplied is null ? Environment.ProcessPath : supplied[AnalyzerExecutablePrefix.Length..];
            if (executable is null) return;
            using var analyzer = Process.Start(BuildAnalyzerStartInfo(executable, directory));
            if (analyzer is null) AppLog.Warning("Watchdog analyzer process was not created.");
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Watchdog local analysis could not start ({ex.GetType().Name}).");
        }
    }


    internal static Observation ObserveParent(int parentPid, WaitHandle? cleanExit, WaitHandle? heartbeat,
        int heartbeatTimeoutMs = HeartbeatTimeoutMs, int missesRequired = HeartbeatMissesRequired)
    {
        if (heartbeatTimeoutMs <= 0 || missesRequired <= 0) throw new ArgumentOutOfRangeException(nameof(heartbeatTimeoutMs));
        if (cleanExit?.WaitOne(0) == true) return new(true, "clean-exit", null);
        using var process = OpenParent(parentPid);
        if (process.IsInvalid) return new(false, "parent-unavailable", null);
        using var processWait = new AutoResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false)
        };
        var waits = new List<WaitHandle>(3);
        var cleanIndex = -1;
        if (cleanExit is not null) { cleanIndex = waits.Count; waits.Add(cleanExit); }
        var processIndex = waits.Count;
        waits.Add(processWait);
        var heartbeatIndex = -1;
        if (heartbeat is not null) { heartbeatIndex = waits.Count; waits.Add(heartbeat); }
        var handles = waits.ToArray();
        var misses = 0;
        try
        {
            while (true)
            {
                var signaled = WaitHandle.WaitAny(handles, heartbeat is null ? Timeout.Infinite : heartbeatTimeoutMs);
                if (signaled == cleanIndex) return new(true, "clean-exit", null);
                if (signaled == processIndex)
                {
                    if (cleanExit?.WaitOne(0) == true) return new(true, "clean-exit", null);
                    return new(false, "process-exited", GetExitCodeProcess(process, out var code) ? code : null);
                }
                if (signaled == heartbeatIndex) { misses = 0; continue; }
                if (signaled == WaitHandle.WaitTimeout && ++misses >= missesRequired)
                {
                    if (cleanExit?.WaitOne(0) == true) return new(true, "clean-exit", null);
                    if (processWait.WaitOne(0))
                        return new(false, "process-exited", GetExitCodeProcess(process, out var code) ? code : null);
                    return new(false, "heartbeat-timeout", null);
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warning($"Watchdog could not observe the parent ({ex.GetType().Name}).");
            return new(false, "watchdog-error", null);
        }
    }

    private static SafeProcessHandle OpenParent(int pid)
    {
        var process = OpenProcess(0x00100000 | 0x1000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        if (!process.IsInvalid) return process;
        process.Dispose();
        return OpenProcess(0x00100000, false, pid); // Still recover if exit-code queries are denied.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    internal static string? TryWriteExitEvidence(string directory, int parentPid, Observation observation)
    {
        if (observation.CleanExit) return null;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"exit-{parentPid}-{Guid.NewGuid():N}.json");
            temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteNumber("pid", parentPid);
                writer.WriteString("utc", observation.Utc);
                if (observation.ExitCode is uint code) writer.WriteString("exitCode", $"0x{code:X8}");
                else writer.WriteNull("exitCode");
                writer.WriteString("reason", observation.Reason);
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Watchdog exit evidence unavailable ({ex.GetType().Name}).");
            return null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { AppLog.Warning($"Watchdog temporary evidence cleanup failed ({ex.GetType().Name})."); }
            }
        }
    }

    internal static ProcessStartInfo BuildAnalyzerStartInfo(string executable, string? crashDirectory = null)
    {
        var info = NewStartInfo(executable);
        info.ArgumentList.Add("--analyze-crashes");
        AddCrashDirectory(info, crashDirectory);
        return info;
    }

    internal static ProcessStartInfo BuildWatchdogStartInfo(string watchdogExecutable, string mainExecutable, int parentPid,
        string token, string? crashDirectory = null)
    {
        var info = NewStartInfo(watchdogExecutable);
        info.ArgumentList.Add(BuildArguments(parentPid, token));
        info.ArgumentList.Add(AnalyzerExecutablePrefix + Path.GetFullPath(mainExecutable));
        AddCrashDirectory(info, crashDirectory);
        return info;
    }

    private static void AddCrashDirectory(ProcessStartInfo info, string? directory)
    {
        directory ??= CrashReport.DefaultDirectory;
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Crash directory must be absolute.", nameof(directory));
        info.ArgumentList.Add(CrashDirectoryPrefix + Path.GetFullPath(directory));
    }

    private static ProcessStartInfo NewStartInfo(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An existing, fully qualified application executable is required.", nameof(executable));
        return new ProcessStartInfo(Path.GetFullPath(executable))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    private static EventWaitHandle? OpenExistingEvent(string prefix, int pid, string token)
    {
        try { return EventWaitHandle.OpenExisting($"{prefix}{pid}.{token}"); }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warning($"Watchdog signal unavailable ({ex.GetType().Name}); watching the process directly.");
            return null;
        }
    }

    /// <summary>Starts the watchdog once the taskbar replacement is actually active.</summary>
    public static void Start(int parentPid)
    {
        if (_started) return;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        var executablePath = Path.GetFullPath(executable);
        // `dotnet run` hosts us in dotnet.exe, which has no --watchdog entry point.
        if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return;

        var token = Guid.NewGuid().ToString("N");
        try
        {
            _cleanExit = OpenEvent(CleanExitPrefix, parentPid, token);
            _heartbeat = OpenEvent(HeartbeatPrefix, parentPid, token);
            if (_cleanExit is null || _heartbeat is null)
            {
                Cleanup();
                return;
            }

            _heartbeat.Set();
            _heartbeatTimer = new Timer(_ =>
            {
                // NotifyCleanExit disposes the timer and event while a callback may already be in flight.
                try { _heartbeat?.Set(); }
                catch (ObjectDisposedException) { }
            }, null, HeartbeatIntervalMs, HeartbeatIntervalMs);

            var watchdogExecutable = EnsureWatchdogExecutable(executablePath);
            Process? watchdog;
            try
            {
                watchdog = Process.Start(BuildWatchdogStartInfo(watchdogExecutable, executablePath, parentPid, token));
            }
            catch (Exception ex) when (!watchdogExecutable.Equals(executablePath, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warning($"Native watchdog unavailable ({ex.GetType().Name}); using the managed watchdog.");
                watchdog = Process.Start(BuildWatchdogStartInfo(executablePath, executablePath, parentPid, token));
            }
            if (watchdog is null && !watchdogExecutable.Equals(executablePath, StringComparison.OrdinalIgnoreCase))
                watchdog = Process.Start(BuildWatchdogStartInfo(executablePath, executablePath, parentPid, token));
            using var watchdogLifetime = watchdog;
            _started = watchdog is not null;
            if (!_started) Cleanup();
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Taskbar watchdog could not start: {ex}");
            Cleanup();
        }
    }

    private static string EnsureWatchdogExecutable(string mainExecutablePath)
    {
        try
        {
            // When packaged (MSIX) or deployed alongside OpenTaskbar.exe, run the signed local watchdog
            // directly instead of extracting an executable into %LOCALAPPDATA%.
            var appDir = Path.GetDirectoryName(mainExecutablePath) ?? string.Empty;
            var localExe = Path.Combine(appDir, "OpenTaskbarWatchdog.exe");
            if (File.Exists(localExe)) return localExe;

            var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTaskbar");
            Directory.CreateDirectory(targetDir);
            var watchdogExe = Path.Combine(targetDir, "OpenTaskbarWatchdog.exe");

            using var resourceStream = typeof(TaskbarRecoveryWatchdog).Assembly
                .GetManifestResourceStream("OpenTaskbar.Resources.OpenTaskbarWatchdog.exe");

            if (resourceStream is not null)
            {
                try
                {
                    var resourceBytes = new byte[resourceStream.Length];
                    var totalRead = 0;
                    while (totalRead < resourceBytes.Length)
                    {
                        var read = resourceStream.Read(resourceBytes, totalRead, resourceBytes.Length - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    var needsWrite = true;
                    if (File.Exists(watchdogExe))
                    {
                        var existing = File.ReadAllBytes(watchdogExe);
                        if (existing.AsSpan().SequenceEqual(resourceBytes))
                        {
                            needsWrite = false;
                        }
                    }

                    if (needsWrite)
                    {
                        // A kill mid-write must not leave a truncated watchdog that cannot start next time.
                        var temporary = watchdogExe + ".tmp";
                        File.WriteAllBytes(temporary, resourceBytes);
                        File.Move(temporary, watchdogExe, overwrite: true);
                    }
                }
                catch (IOException)
                {
                    // Fall through: only return a path that actually exists and can be launched.
                }
                if (File.Exists(watchdogExe)) return watchdogExe;
            }

            return mainExecutablePath;
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not prepare native watchdog binary: {ex}");
            return mainExecutablePath;
        }
    }

    /// <summary>Called only for a successful, intentional exit; fatal/nonzero exits must leave the watchdog armed.</summary>
    public static void NotifyCleanExit()
    {
        // EmergencyRestore also calls Session.Stop(), which normally dismisses the watchdog.
        // Successful shell recovery is not a clean application exit after a fatal exception.
        if (Volatile.Read(ref _fatalExit) != 0) return;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        try
        {
            _cleanExit?.Set();
        }
        catch (ObjectDisposedException)
        {
        }
        Cleanup();
    }

    private static EventWaitHandle? OpenEvent(string prefix, int pid, string token)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, $"{prefix}{pid}.{token}", out _);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException)
        {
            AppLog.Warning($"Taskbar watchdog event could not be created: {ex}");
            return null;
        }
    }

    private static void Cleanup()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _cleanExit?.Dispose();
        _cleanExit = null;
        _heartbeat?.Dispose();
        _heartbeat = null;
        _started = false;
    }
}
