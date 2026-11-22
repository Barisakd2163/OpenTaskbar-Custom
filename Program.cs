using System;
using System.Linq;

namespace OpenTaskbar;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Hardware acceleration is the default. The optional --software-render switch is handled in
        // App.OnStartup; it trades frame rate for a lower GPU-driver memory footprint on small VMs.
        var app = new App();
        app.InitializeComponent();
        // Used by Windows CI to verify the published EXE can load WPF without starting a dock
        // or changing the native Windows taskbar.
        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase)) return;
        app.Run();
    }
}
