using System.Windows;
using System.Windows.Controls;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

internal static class AudioMixerChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var level = 0.6f;
        var muted = true;
        var releases = 0;
        var audio = new AudioWidget
        {
            AppSessionSource = () =>
            [
                new AppAudioSession(1357, "Test Player", null, level, muted,
                    next => { level = next; return true; },
                    next => { muted = next; return true; },
                    () => releases++)
            ]
        };

        try
        {
            var panel = (Border)audio.BuildFlyoutContent();
            var row = AppRow(panel, "Test Player");
            var slider = row.Children.OfType<Slider>().Single();
            var mute = row.Children.OfType<Button>().Single();

            slider.Value = 45;
            Check(Math.Abs(level - 0.45f) < 0.001f,
                "Moving an app slider must write the new volume to its audio session.");
            Check(!muted, "Moving the volume above zero must actually unmute the app.");
            Check(releases == 0, "An audio session must remain alive while its slider is open.");

            mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(muted, "The app mute button must write the mute state to the audio session.");

            _ = audio.BuildFlyoutContent();
            Check(releases == 1, "Rebuilding the mixer must release previous audio sessions.");
        }
        finally
        {
            audio.Dispose();
        }
        Check(releases == 2, "Closing the widget must release the current audio session exactly once.");

        var failures = 0;
        var failing = new AudioWidget
        {
            AppSessionSource = () =>
            [
                new AppAudioSession(2468, "Failing Player", null, 0.8f, false,
                    _ => false, _ => false, () => failures++)
            ]
        };
        try
        {
            var panel = (Border)failing.BuildFlyoutContent();
            var row = AppRow(panel, "Failing Player");
            var slider = row.Children.OfType<Slider>().Single();
            slider.Value = 20;
            Check(Math.Abs(slider.Value - 80) < 0.01,
                "A failed app volume write must return the slider to the last confirmed level.");
            row.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Math.Abs(slider.Value - 80) < 0.01,
                "A failed mute write must not change the displayed volume.");
        }
        finally
        {
            failing.Dispose();
        }
        Check(failures == 1, "A failed mixer session must still be released.");

        return checks;
    }

    private static Grid AppRow(DependencyObject panel, string name) =>
        Children<Grid>(panel).Single(grid =>
            grid.Children.OfType<TextBlock>().Any(text => text.Text == name));

    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}
