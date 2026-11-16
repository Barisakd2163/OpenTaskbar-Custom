using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using OpenTaskbar.Services;

namespace OpenTaskbar;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    public ApplicationPreferences Preferences { get; private set; } = new(new DockSettingsStore());
    public bool IsPreview { get; private set; }
    public bool OpenSettingsOnStartup { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        IsPreview = e.Args.Contains("--preview", StringComparer.Ordinal);
        OpenSettingsOnStartup = e.Args.Contains("--settings", StringComparer.Ordinal);
        if (IsPreview)
            Preferences = new ApplicationPreferences(new DockSettingsStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenTaskbar", "preview-settings.json")));
        UiText.Current.SetLanguage(Preferences.Current.Language);
        ThemeManager.Apply(Preferences.Current.Theme, Preferences.Current.SurfaceOpacity);
        _instanceMutex = new Mutex(initiallyOwned: true,
            name: IsPreview ? "Local\\OpenTaskbar.Preview" : "Local\\OpenTaskbar.Singleton", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(UiText.Get("AlreadyRunning"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var error = (MainWindow as OpenTaskbar.MainWindow)?.RestoreWindowsTaskbar();
        if (error is not null)
        {
            Trace.TraceError(error);
            e.ApplicationExitCode = 1;
        }
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        var error = (MainWindow as OpenTaskbar.MainWindow)?.RestoreWindowsTaskbar();
        if (error is not null) Trace.TraceError(error);
        base.OnSessionEnding(e);
    }
}
