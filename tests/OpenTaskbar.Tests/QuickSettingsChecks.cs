using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using OpenTaskbar;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

internal static class QuickSettingsChecks
{
    internal static int Run()
    {
        var now = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
        var emptyState = Convert.FromHexString("434201000A0201002A2A000000");
        Check(NightLightControl.DecodeState(emptyState) == false,
            "Windows' empty Night Light envelope must be treated as off.");

        var on = NightLightControl.EncodeChange(emptyState, true, now);
        Check(on is not null && NightLightControl.DecodeState(on) == true,
            "Enabling Night Light must write a parseable running state without an OS settings edit.");
        if (on is null) throw new InvalidOperationException("Night Light change was not encoded.");
        var off = NightLightControl.EncodeChange(on, false, now.AddSeconds(1));
        Check(off is not null && NightLightControl.DecodeState(off) == false,
            "Disabling Night Light must clear the running state.");
        Check(ReferenceEquals(on, NightLightControl.EncodeChange(on, true, now)),
            "An identical Night Light request must not rewrite the registry record.");
        Check(NightLightControl.EncodeChange(on[..^1], false, now) is null,
            "A malformed record must not be overwritten.");

        return 5;
    }

    internal static int RunUi()
    {
        using var releaseWrite = new ManualResetEventSlim(false);
        var on = 0;
        var writes = 0;
        using var widget = new QuickSettingsWidget
        {
            ReadNightLight = () => Volatile.Read(ref on) == 1,
            WriteNightLight = target =>
            {
                if (Interlocked.Increment(ref writes) == 1 && releaseWrite.Wait(TimeSpan.FromSeconds(5)))
                    Volatile.Write(ref on, target ? 1 : 0);
                return true;
            }
        };

        var host = new Window { Content = widget, Width = 300, Height = 100, ShowInTaskbar = false };
        host.Show();
        try
        {
            widget.Activate();
            var toggle = widget.FindName("NightLightToggle") as ToggleButton
                ?? throw new InvalidOperationException("Night Light must expose its dock button.");
            Click(toggle);
            Check(!toggle.IsEnabled && toggle.IsChecked == false
                && AutomationProperties.GetItemStatus(toggle) == UiText.Get("QuickTurningOn"),
                "The button must stay on the last Windows state and reject input while turning on.");

            Click(toggle);
            releaseWrite.Set();
            PumpUntil(() => toggle.IsEnabled);
            Check(writes == 1 && toggle.IsChecked == true,
                "A second click during the transition must not write, and confirmation must enable the button.");

            Click(toggle);
            Check(!toggle.IsEnabled && toggle.IsChecked == true
                && AutomationProperties.GetItemStatus(toggle) == UiText.Get("QuickTurningOff"),
                "Turning off must remain checked until Windows confirms the new state.");
            PumpUntil(() => toggle.IsEnabled);
            Check(writes == 2 && toggle.IsChecked == true
                && AutomationProperties.GetItemStatus(toggle).Contains(UiText.Get("QuickChangeFailed"), StringComparison.Ordinal),
                "An unconfirmed write must restore the actual state and advertise the failure.");

            var more = widget.FindName("MoreButton") as Button
                ?? throw new InvalidOperationException("Quick settings must expose its list button.");
            more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => DockFlyout.IsAnyOpen);
            Check(more.IsEnabled && DockFlyout.IsAnyOpen,
                "The dock list must open after a failed system toggle.");
            return 5;
        }
        finally
        {
            releaseWrite.Set();
            widget.Deactivate();
            host.Close();
        }
    }

    private static void Click(ToggleButton button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void PumpUntil(Func<bool> predicate)
    {
        var frame = new DispatcherFrame();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (predicate() || timeout.Elapsed >= TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(predicate(), "The quick settings transition did not settle.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
