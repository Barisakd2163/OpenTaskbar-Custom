using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

internal static class CrashReportChecks
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenTaskbar-crash-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            NestedExceptionAndIdentity(root);
            IndependentReports(root);
            SafeSettingsOnly(root);
            UnavailableStoragePreservesOriginal(root);
            BrokenSettingsStillPreserveException(root);
            return 5;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void NestedExceptionAndIdentity(string root)
    {
        var exception = CaptureNestedException();
        var path = RequireReport(exception, "regression-origin", Path.Combine(root, "nested"));
        var text = File.ReadAllText(path);
        var assembly = typeof(AppLog).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        Require(text.Contains(exception.ToString(), StringComparison.Ordinal), "Full nested exception and both stacks must be persisted.");
        Require(text.Contains(nameof(ThrowInner), StringComparison.Ordinal), "The inner throwing frame must survive.");
        Require(text.Contains(nameof(ThrowOuter), StringComparison.Ordinal), "The outer throwing frame must survive.");
        Require(text.Contains("Build: " + version, StringComparison.Ordinal), "Use the actual informational build identity, including its commit suffix.");
        Require(text.Contains("Module ID: " + assembly.ManifestModule.ModuleVersionId, StringComparison.Ordinal), "The report must identify the exact module.");
        Require(text.Contains("Executable: " + Environment.ProcessPath, StringComparison.Ordinal), "The executable must be identified.");
        Require(text.Contains("PID: " + Environment.ProcessId, StringComparison.Ordinal), "The process must be identified.");
        Require(text.Contains("Managed thread: " + Environment.CurrentManagedThreadId, StringComparison.Ordinal), "The crashing thread must be identified.");
        Require(text.Contains("Origin: regression-origin", StringComparison.Ordinal), "The crash origin must be persisted.");
    }

    private static void IndependentReports(string root)
    {
        var directory = Path.Combine(root, "independent");
        var exception = CaptureNestedException();
        var first = RequireReport(exception, "first-origin", directory);
        var original = File.ReadAllText(first);
        var second = RequireReport(exception, "second-origin", directory);
        Require(first != second, "Each crash must have its own report path.");
        Require(Directory.GetFiles(directory).Length == 2, "Both reports must remain on disk.");
        Require(File.ReadAllText(first) == original, "A subsequent crash must not replace earlier evidence.");
        Require(File.ReadAllText(second).Contains("Origin: second-origin", StringComparison.Ordinal), "The second report must contain its own crash.");
    }

    private static void SafeSettingsOnly(string root)
    {
        const string secret = "PRIVATE-DO-NOT-RECORD";
        var settings = new DockSettings(
            PinOrder: [@"C:\" + secret + @"\application.exe"],
            Widgets: [DockWidgetService.MediaId, DockWidgetService.AudioId, secret],
            InstalledWidgets: [secret], WeatherCity: secret, TargetDisplay: secret,
            StackFolders: [secret], ActiveProfile: secret, ThemePack: secret,
            WidgetOrder: [secret], DockItemOrder: [secret],
            CustomLauncherHotkey: secret, CustomDockHotkey: secret,
            PinnedTrayIcons: [secret], IgnoredWirelessDevices: [secret],
            WirelessDeviceAliases: new Dictionary<string, string> { [secret] = secret },
            DynamicIslandPlacements: new Dictionary<string, string> { [secret] = secret },
            DynamicIslandWidgetOrders: new Dictionary<string, IReadOnlyList<string>> { [secret] = [secret] },
            AppGroups: [new DockAppGroup(secret, secret, [secret])],
            Profiles: [new DockProfile(secret, [secret])],
            AutoHide: AutoHideMode.Overlay, MirrorMainDock: true, DynamicIslandEnabled: true);
        var path = CrashReport.TryWrite(CaptureNestedException(), "safe-settings", settings, Path.Combine(root, "privacy"));
        Require(path is not null, "A safe settings report must be written.");
        var text = File.ReadAllText(path!);
        Require(!text.Contains(secret, StringComparison.Ordinal), "Free-form settings, paths and widget IDs must never enter a crash report.");
        Require(!text.Contains("PinOrder", StringComparison.Ordinal) && !text.Contains("WeatherCity", StringComparison.Ordinal), "Settings must not be serialized wholesale.");
        Require(text.Contains("Enabled widgets: Media Audio", StringComparison.Ordinal), "Known enabled widget IDs must be available for diagnosis.");
        Require(text.Contains("Auto-hide: Overlay; mirror=True", StringComparison.Ordinal), "Selected layout settings must be available for diagnosis.");
        Require(text.Contains("Dynamic Island: enabled=True", StringComparison.Ordinal), "Selected island settings must be available for diagnosis.");
    }

    private static void UnavailableStoragePreservesOriginal(string root)
    {
        var blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "leave this file unchanged");
        var exception = CaptureNestedException();
        var original = exception.ToString();
        var failedPath = CrashReport.TryWrite(exception, "unavailable-storage", directory: Path.Combine(blocker, "Crashes"));
        Require(failedPath is null, "Unavailable storage must be reported as a failed write.");
        Require(File.ReadAllText(blocker) == "leave this file unchanged", "A failed write must not damage the obstructing file.");
        var recoveredPath = RequireReport(exception, "subsequent-recovery", Path.Combine(root, "available"));
        Require(File.ReadAllText(recoveredPath).Contains(original, StringComparison.Ordinal), "Error handling after a storage failure must retain the original exception and stacks.");
    }

    private static void BrokenSettingsStillPreserveException(string root)
    {
        var exception = CaptureNestedException();
        var settings = new DockSettings(Widgets: new UnreadableWidgetList());
        var path = CrashReport.TryWrite(exception, "broken-settings", settings, Path.Combine(root, "broken-settings"));
        Require(path is not null, "Optional settings context must not prevent writing core crash evidence.");
        var text = File.ReadAllText(path!);
        Require(text.Contains(exception.ToString(), StringComparison.Ordinal), "The original exception must survive a context-gathering failure.");
        Require(text.Contains("Settings context unavailable: InvalidOperationException", StringComparison.Ordinal), "A missing optional context must be distinguished from the original crash.");
        Require(!text.Contains("PRIVATE-CONTEXT-ERROR", StringComparison.Ordinal), "Context failures must not disclose free-form exception messages.");
    }

    private static string RequireReport(Exception exception, string origin, string directory) =>
        CrashReport.TryWrite(exception, origin, directory: directory)
        ?? throw new InvalidOperationException("Expected an on-disk crash report.");

    private static Exception CaptureNestedException()
    {
        try { ThrowOuter(); }
        catch (InvalidOperationException exception) { return exception; }
        throw new InvalidOperationException("The nested exception fixture did not throw.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOuter()
    {
        try { ThrowInner(); }
        catch (FormatException inner) { throw new InvalidOperationException("outer-regression-failure", inner); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInner() => throw new FormatException("inner-regression-failure");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class UnreadableWidgetList : IReadOnlyList<string>
    {
        public int Count => throw new InvalidOperationException("PRIVATE-CONTEXT-ERROR");
        public string this[int index] => throw new InvalidOperationException("PRIVATE-CONTEXT-ERROR");
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("PRIVATE-CONTEXT-ERROR");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
