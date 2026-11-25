using System;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Media;
using OpenTaskbar.Services;

namespace OpenTaskbar;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Analysis and the managed recovery helper must work without creating a WPF application.
        if (args.Contains("--analyze-crashes", StringComparer.OrdinalIgnoreCase))
        {
            var directoryArg = args.FirstOrDefault(arg => arg.StartsWith("--crash-directory=", StringComparison.OrdinalIgnoreCase));
            Environment.ExitCode = CrashAssessmentRunner.Run(directoryArg?["--crash-directory=".Length..]);
            return;
        }

        // Watchdog and --restore paths log before App.OnStartup; initialize first so their
        // diagnostics are not silently dropped (shell recovery was previously invisible in log.txt).
        AppLog.Initialize();
        if (TaskbarRecoveryWatchdog.IsWatchdogInvocation(args))
        {
            TaskbarRecoveryWatchdog.Run(args);
            AppLog.Shutdown();
            return;
        }

        // Must be set before App or any WPF visual tree initializes. Hardware rendering is the default;
        // --software-render and the persisted preference trade frame rate for ~100 MB less private memory.
        if (args.Contains("--software-render", StringComparer.OrdinalIgnoreCase) || RenderModePreference.IsSoftware())
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        var app = new App();
        app.InitializeComponent();
        // Used by Windows CI to verify the published EXE can load WPF without starting a dock
        // or changing the native Windows taskbar.
        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase)) return;
        app.Run();
    }
}
