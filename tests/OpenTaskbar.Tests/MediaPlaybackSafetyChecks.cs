using System.Reflection;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTaskbar;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
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
        typeof(OpenTaskbar.Widgets.DockWidget).GetProperty("IsActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(widget, true);
        Invoke(widget, "Session_PlaybackInfoChanged", null, null);
        Invoke(widget, "Session_TimelinePropertiesChanged", null, null);
        var frame = new DispatcherFrame();
        widget.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        Check(play.IsEnabled && slider.Value == 45 && islandSlider.Value == 45,
            "Null playback and timeline event senders must not refresh or corrupt the current rendered state.");

        // A callback already queued before hiding must not paint or rebind audio behind a game.
        var volume = Control<TextBlock>("VolumePercentText");
        volume.Text = "67%";
        Invoke(widget, "Audio_VolumeChanged");
        widget.SetDockVisible(false);
        widget.ApplyPlaybackSnapshot(default, newVersion);
        Field<OpenTaskbar.Widgets.MediaTimeline>(widget, "_timeline").Update(
            sample with { Position = TimeSpan.FromSeconds(95) }, 0, DateTimeOffset.UtcNow, 0);
        Invoke(widget, "UpdateProgressUI");
        frame = new DispatcherFrame();
        widget.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        Check(play.IsEnabled && slider.Value == 45 && islandSlider.Value == 45
            && elapsed.Text == "0:45" && volume.Text == "67%",
            "Hidden media must ignore queued audio and playback callbacks and stop writing interpolated progress.");

        // No provider survives this unbinding: reveal must clear the old frame immediately.
        Invoke(widget, "BindSession", (object?)null);
        widget.SetDockVisible(true);
        Check(!play.IsEnabled && slider.Value == 0 && !islandPlay.IsEnabled && islandSlider.Value == 0
            && elapsed.Text.Length == 0 && volume.Text == "—",
            "Revealing after a session disappears while hidden must clear stale playback and volume on both surfaces.");
        var revealVersion = Field<int>(widget, "_sessionVersion");
        widget.ApplyPlaybackSnapshot(recovered, revealVersion);
        Check(play.IsEnabled && slider.Value == 45 && islandSlider.Value == 45,
            "A fresh snapshot after reveal must immediately restore both media surfaces.");
        widget.ApplyPlaybackSnapshot(default, newVersion);
        Check(play.IsEnabled && slider.Value == 45,
            "A pre-hide binding's late callback must not erase the newly revealed session.");
        return checks;
    }

    public static int RunDockVisibility(App app)
    {
        app.Dispatcher.VerifyAccess();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var preferencesField = typeof(App).GetField("_preferences", flags)!;
        var originalPreferences = preferencesField.GetValue(app);
        var inspectionProperty = typeof(App).GetProperty(nameof(App.IsInspection))!;
        var originalInspection = app.IsInspection;
        var originalMediaActive = ShellShortcuts.IsMediaWidgetActive;
        var preferences = new ApplicationPreferences(new DockSettingsStore(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json")));
        var settings = new DockSettings(Widgets: [], InstalledWidgets: [], ShowOnAllMonitors: false,
            ShowRecycleBin: false, CheckUpdates: false, AnimationsEnabled: false, MagnificationEnabled: false);
        typeof(ApplicationPreferences).GetProperty(nameof(ApplicationPreferences.Current))!.SetValue(preferences, settings);
        preferencesField.SetValue(app, preferences);
        inspectionProperty.SetValue(app, true);
        var windows = new List<MainWindow>();
        try
        {
            // No Show/EnsureHandle: these real windows never attach AppBars or alter Explorer.
            var primary = new MainWindow();
            windows.Add(primary);
            windows.Add((MainWindow)typeof(MainWindow).GetConstructor(flags, null,
                [typeof(MainWindow), typeof(nint)], null)!.Invoke([primary, (nint)0]));
            foreach (var window in windows)
            {
                var coordinator = (WidgetCoordinator)typeof(MainWindow).GetField("_widgetCoordinator", flags)!.GetValue(window)!;
                var autoHide = new DockAutoHideController(window, 0, new TranslateTransform(), new Border(), () => false)
                {
                    AnimationsEnabled = false
                };
                typeof(MainWindow).GetField("_autoHideController", flags)!.SetValue(window, autoHide);
                var updateVisibility = typeof(MainWindow).GetMethod("UpdateWidgetDockVisibility", flags)!;
                autoHide.VisibilityChanged += _ => updateVisibility.Invoke(window, null);
                autoHide.AnimateHide();

                // Exercise lazy creation without activating native audio/WinRT services in the test process.
                var slots = (System.Collections.IEnumerable)typeof(WidgetCoordinator).GetField("_widgets", flags)!.GetValue(coordinator)!;
                var slot = slots.Cast<object>().Single(item =>
                    (string)item.GetType().GetProperty("Id")!.GetValue(item)! == "Media");
                var media = (MediaWidget)typeof(WidgetCoordinator).GetMethod("EnsureWidget", flags)!.Invoke(coordinator, [slot])!;
                var slider = (Slider)media.FindName("ProgressSlider")!;
                var snapshot = new MediaPlaybackSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                    new MediaPlaybackControls(true, true, true, true),
                    new MediaTimelineSample(TimeSpan.Zero, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), default), true, 1);
                var version = Field<int>(media, "_sessionVersion");
                media.ApplyPlaybackSnapshot(snapshot, version);
                Check(slider.Value == 0,
                    "Media created after either dock hides must inherit hidden presentation before receiving any snapshots.");
                autoHide.AnimateReveal();
                media.ApplyPlaybackSnapshot(snapshot, version);
                Check(slider.Value == 30, "Revealing either dock must let its media render the current snapshot immediately.");

                var fullscreen = typeof(MainWindow).GetMethod("OnFullScreenChanged", flags)!;
                fullscreen.Invoke(window, [true]);
                snapshot = snapshot with { Timeline = snapshot.Timeline!.Value with { Position = TimeSpan.FromSeconds(75) } };
                autoHide.AnimateHide();
                autoHide.AnimateReveal();
                media.ApplyPlaybackSnapshot(snapshot, version);
                Check(slider.Value == 30,
                    "An auto-hide reveal during fullscreen suppression must not resume media presentation on either dock.");
                fullscreen.Invoke(window, [false]);
                media.ApplyPlaybackSnapshot(snapshot, version);
                Check(slider.Value == 75,
                    "Fullscreen exit must resume media presentation without requiring a later auto-hide visibility event.");

                autoHide.AnimateHide();
                fullscreen.Invoke(window, [true]);
                fullscreen.Invoke(window, [false]);
                snapshot = snapshot with { Timeline = snapshot.Timeline!.Value with { Position = TimeSpan.FromSeconds(90) } };
                media.ApplyPlaybackSnapshot(snapshot, version);
                Check(slider.Value == 75, "Fullscreen exit must not reveal media while its dock remains auto-hidden.");
                autoHide.AnimateReveal();
                media.ApplyPlaybackSnapshot(snapshot, version);
                Check(slider.Value == 90, "A later reveal must render the latest media position after hidden fullscreen exit.");
            }

            var clock = new TestClock();
            using var semantic = new WidgetCoordinator(new StackPanel(), app.Dispatcher, _ => { }, lazy: true,
                createWidget: id => id == "Focus" ? new FocusWidget(clock) : WidgetCoordinator.CreateWidget(id));
            semantic.SetDockVisible(false);
            var focusSettings = settings with { Widgets = ["Focus"], FocusMinutes = 25 };
            semantic.Apply(focusSettings);
            var focus = (FocusWidget)semantic.ActiveWidgets["Focus"];
            var remaining = (TextBlock)focus.FindName("RemainingText")!;
            var play = (Button)focus.FindName("PlayButton")!;
            var tick = typeof(WidgetCoordinator).GetMethod("Timer_Tick", flags)!;
            play.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            clock.Advance(TimeSpan.FromMinutes(2));
            tick.Invoke(semantic, [null, EventArgs.Empty]);
            Check(remaining.Text == "23:00",
                "Creating the dock hidden must keep focus semantics and coordinator ticks active rather than pause the timer.");
            semantic.Apply(settings);
            semantic.SetDockVisible(true);
            semantic.SetDockVisible(false);
            semantic.Apply(focusSettings);
            play.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            clock.Advance(TimeSpan.FromMinutes(1));
            tick.Invoke(semantic, [null, EventArgs.Empty]);
            Check(remaining.Text == "22:00",
                "A widget reactivated while hidden must retain its semantic timer and continue consuming coordinator ticks.");
            return checks;
        }
        finally
        {
            foreach (var window in windows.AsEnumerable().Reverse())
            {
                var closing = typeof(MainWindow).GetMethod("Window_Closing", flags)!;
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, closing);
                window.Close();
            }
            ShellShortcuts.IsMediaWidgetActive = originalMediaActive;
            preferencesField.SetValue(app, originalPreferences);
            inspectionProperty.SetValue(app, originalInspection);
        }
    }

    private static T Field<T>(MediaWidget widget, string name) =>
        (T)typeof(MediaWidget).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(widget)!;

    private static void SetField(MediaWidget widget, string name, object value) =>
        typeof(MediaWidget).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(widget, value);

    private static void Invoke(MediaWidget widget, string name, params object?[] arguments) =>
        typeof(MediaWidget).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, arguments);
}
