using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>Local, best-effort crash evidence, independent of the rotating application log.</summary>
public static class CrashReport
{
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTaskbar", "Crashes");

    internal static string BuildVersion => typeof(AppLog).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppLog).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// Records the original failure before recovery starts. Returns the report path, or null if storage is unavailable.
    /// The optional directory permits isolated diagnostics checks without touching the user's crash history.
    /// </summary>
    public static string? TryWrite(Exception? exception, string origin, DockSettings? settings = null, string? directory = null)
    {
        if (exception is null) AppLog.Error($"Unhandled exception on {origin}: no managed exception was supplied.");
        else AppLog.ReportError($"Unhandled exception on {origin}", exception);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var assembly = typeof(AppLog).Assembly;
            var report = new StringBuilder();
            report.AppendLine("OpenTaskbar local crash report")
                .AppendLine("Local only; no automatic upload. Review before sharing: exception messages/stacks and executable identity may contain local paths or other private data.")
                .AppendLine($"UTC: {now:O}")
                .AppendLine($"Origin: {origin}")
                .AppendLine($"Build: {BuildVersion}")
                .AppendLine($"Assembly version: {assembly.GetName().Version}")
                .AppendLine($"Module ID: {assembly.ManifestModule.ModuleVersionId}")
                .AppendLine($"Executable: {Environment.ProcessPath ?? "unknown"}")
                .AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}")
                .AppendLine($"OS: {RuntimeInformation.OSDescription}")
                .AppendLine($"Architecture: process={RuntimeInformation.ProcessArchitecture}; OS={RuntimeInformation.OSArchitecture}")
                .AppendLine($"PID: {Environment.ProcessId}")
                .AppendLine($"Managed thread: {Environment.CurrentManagedThreadId}")
                .AppendLine()
                .AppendLine("Exception (including inner exceptions):")
                .AppendLine(exception?.ToString() ?? "No managed exception was supplied.")
                .AppendLine();

            // An explicit allowlist: never serialize settings, free-form widget IDs, display names,
            // paths, profiles, locations, credentials or media metadata.
            try { AppendSettings(report, settings); }
            catch (Exception contextError)
            {
                report.AppendLine($"Settings context unavailable: {contextError.GetType().Name}");
            }

            directory ??= DefaultDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                $"crash-{now:yyyyMMdd-HHmmss-fffffff}-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                       4096, FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(report.ToString());
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            AppLog.Error($"Local crash report: {path}");
            return path;
        }
        catch (Exception reportError)
        {
            // A storage/formatting failure must not replace the original exception or block recovery.
            AppLog.Warning($"Local crash report unavailable ({reportError.GetType().Name}); see original exception in log.");
            return null;
        }
    }

    private static void AppendSettings(StringBuilder report, DockSettings? settings)
    {
        if (settings is null)
        {
            report.AppendLine("Settings: not loaded");
            return;
        }

        report.AppendLine("Safe layout/widget context:")
            .AppendLine($"Layout: {settings.Layout}; edge={settings.Edge}; position={settings.Position}; alignment={settings.ContentAlignment}; size={settings.Size}")
            .AppendLine($"Auto-hide: {settings.AutoHide}; mirror={settings.MirrorMainDock}; all-monitors={settings.ShowOnAllMonitors}; secondary-widgets={settings.SecondaryWidgets}")
            .AppendLine(FormattableString.Invariant($"Dimensions: scale={settings.DockScale}; width-mode={settings.DockWidthMode}; width-px={settings.DockWidthPixels}; width-percent={settings.DockWidthPercent}; height-mode={settings.DockHeightMode}; height-px={settings.DockHeightPixels}; height-percent={settings.DockHeightPercent}"))
            .AppendLine($"Widget density: {settings.GlobalWidgetDensity}")
            .AppendLine($"Dynamic Island: enabled={settings.DynamicIslandEnabled}; visibility={settings.DynamicIslandVisibility}; auto-hide={settings.DynamicIslandAutoHide}; hide-fullscreen={settings.DynamicIslandHideInFullscreen}");

        report.Append("Enabled widgets:");
        // Output catalog IDs only, not arbitrary strings from persisted settings.
        foreach (var id in DockWidgetService.SupportedIds)
        {
            if (settings.Widgets?.Contains(id, StringComparer.Ordinal) == true)
                report.Append(' ').Append(id);
        }
        report.AppendLine()
            .AppendLine($"Legacy widget flags: media={settings.ShowMedia}; clock={settings.ShowClock}; stats={settings.ShowSystemStats}; focus={settings.ShowFocus}; audio={settings.ShowAudio}; quick-settings={settings.ShowQuickSettings}; weather={settings.ShowWeather}");
    }
}
