using System.IO;
using System.Windows;
using System.Windows.Threading;
using OpenTaskbar.Services;

namespace OpenTaskbar.Services;

/// <summary>
/// Dev-only hot reload for data files. Watches theme packs and localization JSON under a source root and applies
/// changes in place. XAML is never reloaded at runtime, so the UI keeps the exact compiled (release) appearance.
/// Normal users never start this service.
/// </summary>
internal sealed class DevReloadService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string _sourceRoot;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, DateTime> _lastReload = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public DevReloadService(Dispatcher dispatcher, string sourceRoot)
    {
        _dispatcher = dispatcher;
        _sourceRoot = Path.GetFullPath(sourceRoot);

        Watch(Path.Combine(_sourceRoot, "Services", "Localization", "Languages"), ".json", OnLocalizationChanged);
        Watch(Path.Combine(_sourceRoot, "themes"), ".json", OnThemeChanged);
        Watch(Path.Combine(_sourceRoot, "themes"), ".png", OnThemeChanged);
    }

    private void Watch(string directory, string filter, Action<string> handler)
    {
        if (!Directory.Exists(directory)) return;
        var watcher = new FileSystemWatcher(directory, "*" + filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = true
        };
        watcher.Changed += (_, e) => handler(e.FullPath);
        watcher.Created += (_, e) => handler(e.FullPath);
        watcher.Deleted += (_, e) => handler(e.FullPath);
        watcher.Renamed += (_, e) => handler(e.FullPath);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void OnLocalizationChanged(string path) => Schedule("loc", () =>
    {
        LanguageCatalog.Reload(_sourceRoot);
        AppLog.Info("DevReload: localization reloaded.");
    });

    private void OnThemeChanged(string path) => Schedule("theme", () =>
    {
        var app = (App)Application.Current;
        ThemePacks.Select(app.Preferences.Current.ThemePack, reload: true);
        ThemePacks.ApplyWidgets(app.Preferences.Current.Theme);
        AppLog.Info("DevReload: theme pack reloaded.");
    });

    private void Schedule(string key, Action action)
    {
        // Coalesce editor save bursts and run on the UI thread.
        var now = DateTime.UtcNow;
        lock (_lastReload)
        {
            if (_lastReload.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < 250) return;
            _lastReload[key] = now;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception ex) { AppLog.Warning($"DevReload: {key} failed: {ex.Message}"); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
