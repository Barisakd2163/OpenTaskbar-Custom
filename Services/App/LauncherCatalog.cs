using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public enum LauncherKind { App, Setting, Recent, Calculator, FileSearch, WebSearch }

public sealed record LauncherEntry(string Name, string Target, LauncherKind Kind, string? Detail = null);

/// <summary>
/// Search sources for the launcher: every app in Shell's Applications folder (desktop and Store apps alike), Windows
/// settings pages, recent files, an arithmetic evaluator, and hand-offs to Windows file search and the web.
/// </summary>
public sealed class LauncherCatalog
{
    private static readonly (string TextKey, string Uri)[] SettingsPages =
    [
        ("LauncherDisplay", "ms-settings:display"),
        ("LauncherSound", "ms-settings:sound"),
        ("LauncherNotifications", "ms-settings:notifications"),
        ("LauncherPower", "ms-settings:powersleep"),
        ("LauncherStorage", "ms-settings:storagesense"),
        ("LauncherBluetooth", "ms-settings:bluetooth"),
        ("LauncherPrinters", "ms-settings:printers"),
        ("LauncherMouse", "ms-settings:mousetouchpad"),
        ("LauncherWifi", "ms-settings:network-wifi"),
        ("LauncherNetwork", "ms-settings:network-status"),
        ("LauncherVpn", "ms-settings:network-vpn"),
        ("LauncherPersonalization", "ms-settings:personalization"),
        ("LauncherBackground", "ms-settings:personalization-background"),
        ("LauncherColors", "ms-settings:colors"),
        ("LauncherApps", "ms-settings:appsfeatures"),
        ("LauncherDefaultApps", "ms-settings:defaultapps"),
        ("LauncherStartupApps", "ms-settings:startupapps"),
        ("LauncherAccounts", "ms-settings:yourinfo"),
        ("LauncherDateTime", "ms-settings:dateandtime"),
        ("LauncherLanguageRegion", "ms-settings:regionlanguage"),
        ("LauncherGaming", "ms-settings:gaming-gamebar"),
        ("LauncherAccessibility", "ms-settings:easeofaccess"),
        ("LauncherPrivacy", "ms-settings:privacy"),
        ("LauncherWindowsUpdate", "ms-settings:windowsupdate"),
        ("LauncherWindowsSecurity", "windowsdefender:"),
        ("LauncherTaskbar", "ms-settings:taskbar"),
        ("LauncherAbout", "ms-settings:about"),
        ("LauncherControlPanel", "control"),
        ("LauncherTaskManager", "taskmgr")
    ];

    private IReadOnlyList<LauncherEntry> _apps = [];
    private DateTime _loadedAt = DateTime.MinValue;
    private Task? _loading;

    public bool IsLoaded => _loadedAt != DateTime.MinValue;

    /// <summary>Reloads the app list in the background when it is older than a few minutes.</summary>
    public Task RefreshAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        if (DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(5)) return Task.CompletedTask;
        var completion = new TaskCompletionSource();
        // Shell.Application is an apartment-threaded COM object.
        var thread = new Thread(() =>
        {
            try
            {
                _apps = LoadApps();
                _loadedAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                AppLog.Warning($"Launcher app list unavailable: {ex.Message}");
            }
            finally
            {
                completion.SetResult();
            }
        }) { IsBackground = true, Name = "Launcher catalog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _loading = completion.Task;
        return _loading;
    }

    private static List<LauncherEntry> LoadApps()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new COMException("Shell.Application is not registered.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var apps = new List<LauncherEntry>();
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                // Uninstallers and help links clutter results.
                if (UiText.GetTranslations("LauncherUninstall").Any(label => name.Contains(label, StringComparison.OrdinalIgnoreCase))) continue;
                apps.Add(new LauncherEntry(name, "shell:AppsFolder\\" + path, LauncherKind.App));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        return apps.DistinctBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public IReadOnlyList<LauncherEntry> Search(string query, AppLanguage language, int limit)
    {
        var culture = language.Culture;
        query = query.Trim();
        var results = new List<(LauncherEntry Entry, int Score)>();
        if (query.Length == 0)
        {
            return RecentFiles(culture).Take(limit).ToList();
        }

        if (TryCalculate(query, out var value))
        {
            results.Add((new LauncherEntry(value, value, LauncherKind.Calculator, query + " ="), 1000));
        }

        foreach (var app in _apps)
        {
            var score = Score(app.Name, query);
            if (score > 0) results.Add((app, score + 20));
        }

        foreach (var page in SettingsPages)
        {
            var name = UiText.Get(page.TextKey);
            var score = UiText.GetTranslations(page.TextKey).Max(translation => Score(translation, query));
            if (score > 0) results.Add((new LauncherEntry(name, page.Uri, LauncherKind.Setting), score));
        }

        foreach (var recent in RecentFiles(culture))
        {
            var score = Score(recent.Name, query);
            if (score > 0) results.Add((recent, score - 10));
        }

        var ordered = results.OrderByDescending(result => result.Score)
            .ThenBy(result => result.Entry.Name.Length)
            .Select(result => result.Entry)
            .Take(Math.Max(1, limit - 2))
            .ToList();
        ordered.Add(new LauncherEntry(query, "search-ms:query=" + Uri.EscapeDataString(query), LauncherKind.FileSearch));
        ordered.Add(new LauncherEntry(query, "https://www.google.com/search?q=" + Uri.EscapeDataString(query), LauncherKind.WebSearch));
        return ordered;
    }

    private static IEnumerable<LauncherEntry> RecentFiles(CultureInfo culture)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder).EnumerateFiles("*.lnk").OrderByDescending(file => file.LastWriteTimeUtc).Take(40).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return new LauncherEntry(Path.GetFileNameWithoutExtension(file.Name), file.FullName, LauncherKind.Recent,
                file.LastWriteTime.ToString("g", culture));
        }
    }

    /// <summary>Prefix beats word-prefix beats initials beats substring; zero means no match.</summary>
    private static int Score(string name, string query)
    {
        var compare = UiText.Current.Language.Culture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        if (compare.IsPrefix(name, query, options)) return 300 - Math.Min(100, name.Length);
        var words = name.Split([' ', '-', '_', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => compare.IsPrefix(word, query, options))) return 200 - Math.Min(100, name.Length);
        var initials = new string(words.Select(word => word[0]).ToArray());
        if (query.Length >= 2 && compare.IsPrefix(initials, query, options)) return 150;
        if (compare.IndexOf(name, query, options) >= 0) return 100 - Math.Min(90, name.Length);
        return 0;
    }

    private static bool TryCalculate(string query, out string value)
    {
        value = string.Empty;
        var expression = query.TrimEnd('=').Replace(',', '.').Replace('×', '*').Replace('x', '*').Replace('÷', '/');
        if (!expression.Any(char.IsDigit) || !expression.Any(c => "+-*/%^()".Contains(c))
            || expression.Any(c => !(char.IsDigit(c) || char.IsWhiteSpace(c) || "+-*/%().".Contains(c))))
        {
            return false;
        }

        try
        {
            using var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            var result = Convert.ToDouble(table.Compute(expression, null), CultureInfo.InvariantCulture);
            if (double.IsNaN(result) || double.IsInfinity(result)) return false;
            value = result.ToString("G12", UiText.Current.Language.Culture);
            return true;
        }
        catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or InvalidCastException or OverflowException or FormatException or DivideByZeroException)
        {
            return false;
        }
    }

    public static string? Launch(LauncherEntry entry)
    {
        if (entry.Kind == LauncherKind.Calculator)
        {
            try
            {
                System.Windows.Clipboard.SetText(entry.Target);
                return null;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
            {
                // Another app holds the clipboard open.
                AppLog.Warning($"Clipboard unavailable: {ex.Message}");
                return UiText.Get("ErrorClipboardCopy");
            }
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Warning($"Launcher could not open {entry.Target}: {ex.Message}");
            return UiText.Get("ErrorOpenItem");
        }
    }
}
