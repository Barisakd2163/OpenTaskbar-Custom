using System.Windows;

/// <summary>Real App resources/types without starting the desktop singleton, shell replacement or background services.</summary>
internal sealed class IsolatedCheckApp : OpenTaskbar.App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Application queues startup during construction, even when a check only pumps a DispatcherFrame.
        // Resource-only checks own their window lifetime; production App.OnStartup must not run here.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }
}
