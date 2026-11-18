using System;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Media;

namespace OpenTaskbar;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must be configured before App or any WPF visual tree initializes
        // to prevent wpfgfx from initializing Direct3D and attaching ~160 MB of discrete GPU driver shaders.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
