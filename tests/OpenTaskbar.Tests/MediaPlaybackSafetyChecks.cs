using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenTaskbar.Widgets;
using Windows.Media.Control;

internal static class MediaPlaybackSafetyChecks
{
    // Invoke on the existing UI-check STA with the application's modular resources loaded.
    public static int Run(Application app)
    {
        app.Dispatcher.VerifyAccess();
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        using var widget = new MediaWidget();
        T Control<T>(string name) where T : FrameworkElement =>
            (T)(widget.FindName(name) ?? throw new InvalidOperationException($"Missing media control {name}."));
        var play = Control<Button>("PlayPauseButton");
        var previous = Control<Button>("PreviousButton");
        var next = Control<Button>("NextButton");
        var slider = Control<Slider>("ProgressSlider");
        var elapsed = Control<TextBlock>("ElapsedText");
        var total = Control<TextBlock>("TotalText");
        var controls = new MediaPlaybackControls(true, true, false, true);
        var sample = new MediaTimelineSample(TimeSpan.Zero, TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(30), default);
        var valid = new MediaPlaybackSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            controls, sample, true, 1);
        var version = Field<int>(widget, "_sessionVersion");

        widget.ApplyPlaybackSnapshot(valid, version);
        _ = widget.BuildDynamicIslandExpandedContent();
        var islandSlider = Field<Slider>(widget, "_islandProgressSlider");
        var islandPlay = Field<Button>(widget, "_islandPlayPauseButton");
        var islandPrevious = Field<Button>(widget, "_islandPreviousButton");
        var islandNext = Field<Button>(widget, "_islandNextButton");
        Check(play.IsEnabled && previous.IsEnabled && !next.IsEnabled
            && islandPlay.IsEnabled && islandPrevious.IsEnabled && !islandNext.IsEnabled,
            "Both media surfaces must render the provider's available transport controls.");
        Check(slider.Value == 30 && slider.Maximum == 120 && slider.IsHitTestVisible
            && islandSlider.Value == 30 && islandSlider.IsEnabled
            && elapsed.Text == "0:30" && total.Text == "2:00",
            "A valid paused sample must show seekable progress on both media surfaces.");

        SetField(widget, "_draggingProgress", true);
        SetField(widget, "_islandDraggingProgress", true);
        SetField(widget, "_seekingProgress", true);
        widget.ApplyPlaybackSnapshot(valid with { Timeline = null, HasTimelineSource = false }, version);
        Check(play.IsEnabled && previous.IsEnabled && !next.IsEnabled && islandPlay.IsEnabled,
            "A missing timeline must not discard the available playback transport controls.");
        Check(slider.Value == 0 && !slider.IsEnabled && !slider.IsHitTestVisible
            && islandSlider.Value == 0 && !islandSlider.IsEnabled
            && elapsed.Text.Length == 0 && total.Text.Length == 0,
            "A missing timeline must clear stale visible progress and disable seeking on both surfaces.");
        Check(!Field<bool>(widget, "_draggingProgress") && !Field<bool>(widget, "_islandDraggingProgress")
            && !Field<bool>(widget, "_seekingProgress"),
            "Losing source data must cancel both drag states and any pending seek.");

        var recovered = valid with
        {
            Timeline = sample with { Position = TimeSpan.FromSeconds(45), End = TimeSpan.FromSeconds(180) }
        };
        widget.ApplyPlaybackSnapshot(recovered, version);
        Check(slider.Value == 45 && slider.Maximum == 180 && slider.IsHitTestVisible
            && islandSlider.Value == 45 && islandSlider.IsEnabled && total.Text == "3:00",
            "Timeline recovery must resume the new progress and seeking rather than keep a stale or hidden state.");

        widget.ApplyPlaybackSnapshot(recovered with { Controls = null }, version);
        Check(!play.IsEnabled && !previous.IsEnabled && !next.IsEnabled
            && !islandPlay.IsEnabled && !islandPrevious.IsEnabled && !islandNext.IsEnabled,
            "Missing controls must disable transport buttons on both surfaces.");
        Check(slider.Value == 45 && slider.Maximum == 180 && !slider.IsHitTestVisible
            && !islandSlider.IsEnabled && elapsed.Text == "0:45",
            "A valid timeline without controls must remain visible but not seekable.");
        widget.ApplyPlaybackSnapshot(recovered, version);
        Check(play.IsEnabled && previous.IsEnabled && islandPlay.IsEnabled && slider.IsHitTestVisible,
            "Transport controls and seeking must recover when controls reappear.");

        widget.ApplyPlaybackSnapshot(default, version);
        Check(!play.IsEnabled && !previous.IsEnabled && !next.IsEnabled && !islandPlay.IsEnabled
            && slider.Value == 0 && !slider.IsEnabled && elapsed.Text.Length == 0,
            "Absent playback info must clear transport and progress instead of retaining stale data.");
        widget.ApplyPlaybackSnapshot(recovered, version);
        Check(play.IsEnabled && slider.Value == 45 && islandSlider.Value == 45 && islandSlider.IsEnabled,
            "A temporary absence of playback info must not prevent a later valid update.");
        widget.ApplyPlaybackSnapshot(recovered with
        {
            Status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed
        }, version);
        Check(!play.IsEnabled && !islandPlay.IsEnabled && slider.Value == 0 && !slider.IsEnabled,
            "Closed playback must clear the UI even when the provider still supplies old controls and a timeline.");
        widget.ApplyPlaybackSnapshot(recovered, version);
        Check(play.IsEnabled && slider.Value == 45 && slider.IsHitTestVisible,
            "Playback must recover normally after a closed-data transition.");

        // Exercise the real binding lifecycle, then deliver an old queued snapshot in each direction.
        Invoke(widget, "BindSession", (object?)null);
        var newVersion = Field<int>(widget, "_sessionVersion");
        widget.ApplyPlaybackSnapshot(recovered, version);
        Check(newVersion != version && !play.IsEnabled && slider.Value == 0,
            "A queued callback from an earlier binding must not revive stale playback after unbinding.");
        widget.ApplyPlaybackSnapshot(recovered, newVersion);
        widget.ApplyPlaybackSnapshot(default, version);
        Check(play.IsEnabled && slider.Value == 45 && slider.IsHitTestVisible,
            "An old missing-data callback must not erase playback from the new binding.");

        // Null event senders are not the current session, even when no session is bound.
        typeof(DockWidget).GetProperty("IsActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(widget, true);
        Invoke(widget, "Session_PlaybackInfoChanged", null, null);
        Invoke(widget, "Session_TimelinePropertiesChanged", null, null);
        var frame = new DispatcherFrame();
        widget.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        Check(play.IsEnabled && slider.Value == 45 && islandSlider.Value == 45,
            "Null playback and timeline event senders must not refresh or corrupt the current rendered state.");
        return checks;
    }

    private static T Field<T>(MediaWidget widget, string name) =>
        (T)typeof(MediaWidget).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(widget)!;

    private static void SetField(MediaWidget widget, string name, object value) =>
        typeof(MediaWidget).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(widget, value);

    private static void Invoke(MediaWidget widget, string name, params object?[] arguments) =>
        typeof(MediaWidget).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, arguments);
}
