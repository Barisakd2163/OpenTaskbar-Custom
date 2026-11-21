using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

internal static class FeatureAndMemoryChecks
{
    public static int Run(Application app)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var previousLanguage = UiText.Current.Language;
        try
        {
            ThemeManager.Apply(AppTheme.Dark);
            UiText.Current.SetLanguage(AppLanguage.English);

            // 1. Lazy WidgetCoordinator parity & memory invariant
            var strip = new StackPanel();
            using (var lazyCoordinator = new WidgetCoordinator(strip, Dispatcher.CurrentDispatcher, _ => { }, lazy: true))
            {
                lazyCoordinator.Apply(new DockSettings(Widgets: []));
                Check(strip.Children.Count == 0, "Lazy WidgetCoordinator must instantiate 0 widgets when none are enabled.");

                lazyCoordinator.Apply(new DockSettings(Widgets: ["Clock", "Focus"]));
                Check(strip.Children.Count == 2 && strip.Children.OfType<ClockWidget>().Any() && strip.Children.OfType<FocusWidget>().Any(),
                    "Lazy WidgetCoordinator must instantiate only enabled widgets on demand.");

                var reordered = false;
                var firstWidget = lazyCoordinator.CurrentOrder[0];
                lazyCoordinator.OrderChanged = _ => reordered = true;
                lazyCoordinator.ReorderWidgets(0, 1);
                Check(reordered && lazyCoordinator.CurrentOrder[0] != firstWidget,
                    "Lazy WidgetCoordinator must support drag reordering.");

                // Enable all live widgets + SystemMeters.All
                lazyCoordinator.Apply(new DockSettings(
                    Widgets: WidgetCoordinator.DefaultWidgetOrder,
                    StatsMeters: SystemMeters.All));
                Check(strip.Children.Count == WidgetCoordinator.DefaultWidgetOrder.Length,
                    "Enabling all live widgets must materialize all controls.");
            }

            var aiLoads = 0;
            using (var ai = new AiUsageWidget(AiProviders.OpenCode, (_, _) =>
            {
                aiLoads++;
                return Task.FromResult<(AiUsageReport?, string?)>((null, "ErrorAiUsageSignIn"));
            }))
            {
                ai.Tick(DateTime.Now);
                Check(aiLoads == 0, "An inactive AI usage widget must not load.");
                ai.Activate();
                Check(aiLoads == 1 && ai.Visibility == Visibility.Visible
                    && AiCards(ai) is [{ ToolTip: string hint }] && hint.Contains("opencode", StringComparison.Ordinal),
                    "AI Usage must keep sign-in failures visible with a localized CLI hint.");
                ai.Tick(DateTime.Now);
                Check(aiLoads == 1, "A sign-in failure must not poll again without changed credentials.");
                ai.SetCompactState(true);
                Check(ai.BuildHoverFlyoutContent() is not null, "AI Usage must use the shared hover flyout.");
                ai.Deactivate();
                ai.Tick(DateTime.Now.AddHours(1));
                Check(aiLoads == 1, "Inactive AI widgets must not call their loader.");
            }

            var pending = new TaskCompletionSource<(AiUsageReport?, string?)>();
            CancellationToken aiCancellation = default;
            using (var ai = new AiUsageWidget(AiProviders.Codex, (_, cancellation) =>
            {
                aiCancellation = cancellation;
                return pending.Task;
            }))
            {
                ai.Activate();
                ai.Deactivate();
                Check(aiCancellation.IsCancellationRequested, "Disabling an AI widget must cancel its pending request.");
                pending.SetResult((new(AiProviders.Codex, "fixture", [new("AiUsageSession", 0.5, null, 18000)], DateTimeOffset.UtcNow), null));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Check(AiCards(ai) is [{ ToolTip: "Codex" }], "A late AI response must not restore a disabled widget.");
            }

            var aiNow = DateTimeOffset.UtcNow;
            AiUsageWidget CreateAiWidget(AiProviders provider) => new(provider, (p, _) => Task.FromResult<(AiUsageReport?, string?)>((new(p, "Plus",
                [new("AiUsageSession", 0.42, aiNow.AddHours(3), 18000), new("AiUsageWeekly", p == AiProviders.Codex ? 0.9 : 0.29,
                    aiNow.AddDays(5), 604800)], aiNow) { LimitReached = p == AiProviders.Codex }, null)));
            using (var claude = CreateAiWidget(AiProviders.Claude))
            using (var codex = CreateAiWidget(AiProviders.Codex))
            {
                claude.Activate();
                codex.Activate();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Check(AiCards(claude) is [{ Content: StackPanel { Children: [System.Windows.Shapes.Path { Data: not null }, TextBlock { Text: "Claude" }, StackPanel { Children: [StackPanel, StackPanel] }] } } claudeCard]
                    && claudeCard.ToolTip is string claudeTip && claudeTip.StartsWith("Claude Code 42%", StringComparison.Ordinal)
                    && claudeTip.Contains("Weekly 29%", StringComparison.Ordinal) && claudeTip.Contains("Resets in", StringComparison.Ordinal)
                    && AiCards(codex) is [{ Content: StackPanel { Children: [_, TextBlock { Text: "Codex" }, StackPanel { Children: [StackPanel, StackPanel] }] } } codexCard]
                    && codexCard.ToolTip is string codexTip && codexTip.Contains(UiText.Get("AiUsageLimitReached"), StringComparison.Ordinal),
                    "Each AI usage widget must show one card with logo, name, usage indicators and a one-line summary.");
                claude.ApplySettings(new DockSettings(AiUsageRing: AiUsageRing.Weekly));
                Check(AiCards(claude)[0].ToolTip is string weeklyTip && weeklyTip.StartsWith("Claude Code 29%", StringComparison.Ordinal),
                    "The ring setting must choose the window the dock shows.");
                claude.SetCompactState(true);
                Check(AiCards(claude) is [{ Content: StackPanel { Children: [_, TextBlock { Visibility: Visibility.Collapsed }, _] } }],
                    "Compact AI Usage must keep the logo and indicators and drop the name.");
                Check(claude.BuildHoverFlyoutContent() is not null && codex.BuildHoverFlyoutContent() is not null,
                    "Claude and Codex must use the shared hover flyout.");
            }

            // 2. SystemStats + on-demand SystemSensors parity & disposal
            var stats = new SystemStats();
            _ = stats.SampleCpu();
            Check(SystemStats.SampleMemory() is >= 0 and <= 1, "SystemStats.SampleMemory must report valid load.");
            _ = SystemStats.SampleBattery();

            using (var sensors = new SystemSensors(SystemMeters.None))
            {
                sensors.Configure(SystemMeters.Gpu);
                _ = sensors.SampleGpuUsage();
                sensors.Configure(SystemMeters.GpuTemperature);
                _ = sensors.SampleGpuTemperature();
                sensors.Configure(SystemMeters.All);
                _ = sensors.SampleGpuUsage();
                _ = sensors.SampleGpuTemperature();
                sensors.Configure(SystemMeters.None);
            }
            Check(true, "SystemSensors must configure, sample, and dispose cleanly across all SystemMeters combinations.");

            // 3. Shared AudioEndpointMonitor reference counting
            using (var a1 = AudioEndpointMonitor.AcquireShared())
            {
                using (var a2 = AudioEndpointMonitor.AcquireShared())
                {
                    Check(a1.HasDevice == a2.HasDevice && Math.Abs(a1.Volume - a2.Volume) < 0.001f && a1.Muted == a2.Muted,
                        "Shared AudioEndpointMonitor handles must observe identical audio state.");
                }
                _ = a1.Volume;
            }
            Check(true, "Shared AudioEndpointMonitor handles must survive partial release and clean final disposal.");

            checks += AudioMixerChecks.Run();

            // 4. WindowThumbnailPreview lazy creation & disposal
            var preview = new WindowThumbnailPreview((Style)app.FindResource("DockButton"));
            Check(!preview.IsVisible && preview.SourceHandle == 0 && preview.ActiveHandles.Count == 0,
                "WindowThumbnailPreview must start hidden with zero handles before first hover.");
            Check(!preview.ShowForWindows([], strip, null),
                "WindowThumbnailPreview must not open when given no valid windows.");
            preview.Hide();
            preview.Dispose();
            Check(true, "WindowThumbnailPreview must dispose cleanly without ever creating HWNDs when unused.");

            // 5. WeatherService frozen cached brushes
            var clear1 = WeatherService.GetConditionBackground(0);
            var clear2 = WeatherService.GetConditionBackground(0);
            Check(clear1.IsFrozen && ReferenceEquals(clear1, clear2),
                "WeatherService condition brushes must be frozen and cached.");

            // 6. DockFlyout visual-tree cleanup & weak target release
            var (flyout, contentRef) = ExerciseDockFlyout(app, Check);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Check(!contentRef.IsAlive, "DockFlyout must release its child visual tree on close.");
            var popupField = typeof(DockFlyout).GetField("_popup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var popup = (System.Windows.Controls.Primitives.Popup)popupField.GetValue(flyout)!;
            Check(popup.PlacementTarget is null, "DockFlyout must clear PlacementTarget on close.");
            GC.KeepAlive(flyout);

            // 7. Shared ContextMenu + LauncherWindow lifecycle verification
            var sharedMenu = new ContextMenu
            {
                Items = { new MenuItem { Header = "Open" }, new MenuItem { Header = "Close" } }
            };
            var dockItem1 = new Button { ContextMenu = sharedMenu, DataContext = new DockApp("App1", "a1.exe") };
            var dockItem2 = new Button { ContextMenu = sharedMenu, DataContext = new DockApp("App2", "a2.exe") };
            Check(ReferenceEquals(dockItem1.ContextMenu, dockItem2.ContextMenu),
                "Shared ContextMenu must be reused across dock items without per-item duplication.");

            // Verify MediaWidget lifecycle, density and tick safety (V19 uses dedicated volume hover popover, no generic flyout)
            using (var mediaWidget = new MediaWidget())
            {
                mediaWidget.Activate();
                Check(mediaWidget.BuildHoverFlyoutContent() is null, "MediaWidget must use its dedicated volume popover instead of a generic flyout.");
                Check(mediaWidget.SupportsCompactMode, "MediaWidget must declare compact mode support.");
                Check(mediaWidget.FullWidthEstimate > mediaWidget.CompactWidthEstimate, "MediaWidget full width must exceed compact width.");
                mediaWidget.Tick(DateTime.Now);
                mediaWidget.Deactivate();
            }





            // 8. Heavy DockSettingsStore validation & round-trip
            var tempSettings = Path.Combine(Path.GetTempPath(), $"ot-heavy-{Guid.NewGuid():N}.json");
            try
            {
                var store = new DockSettingsStore(tempSettings);
                var heavy = new DockSettings(
                    Position: DockPosition.Center,
                    Theme: AppTheme.Dark,
                    Language: AppLanguage.English,
                    Widgets: ["Media", "SystemStats", "Focus", "Weather", "Audio", "QuickSettings", "Clock", "Storage", "DayProgress", "Stopwatch", "Countdown"],
                    InstalledWidgets: ["Media", "SystemStats", "Focus", "Weather", "Audio", "QuickSettings", "Clock", "Storage", "DayProgress", "Stopwatch", "Countdown"],
                    ShowRecycleBin: true,
                    ShowOnAllMonitors: true,
                    StatsMeters: SystemMeters.All,
                    Profiles: [new DockProfile("Work", [@"C:\Games\game.exe"])],
                    ActiveProfile: "Work");
                Check(store.Save(heavy) is null, "Heavy DockSettings must save without errors.");
                var (loaded, loadErr) = store.Load();
                Check(loadErr is null && loaded.StatsMeters == SystemMeters.All && loaded.Widgets?.Count == 11 && loaded.ShowOnAllMonitors,
                    "Heavy DockSettings must round-trip all 11 widgets, SystemMeters.All, and multi-monitor flags.");
            }
            finally
            {
                try { File.Delete(tempSettings); } catch (IOException) { }
            }

            // 9. TrayIcons PNG DecodePixelWidth capping (caps > 32px without upscaling <= 32px)
            var decodeMethod = typeof(TrayIcons).GetMethod("DecodeSnapshot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var smallPng = EncodeSolidPng(16);
            var largePng = EncodeSolidPng(256);
            var decodedSmall = (BitmapSource?)decodeMethod.Invoke(null, [smallPng]);
            var decodedLarge = (BitmapSource?)decodeMethod.Invoke(null, [largePng]);
            Check(decodedSmall?.PixelWidth == 16, "Small tray PNG snapshots must not be upscaled.");
            Check(decodedLarge?.PixelWidth == 32, "Oversized tray PNG snapshots must be downsampled to 32px.");

            // 10. NativeTaskbarWindow & Live TrayHost checks
            var explorerTaskbar = NativeTaskbarWindow.Find();
            if (explorerTaskbar != 0)
            {
                GetWindowThreadProcessId(explorerTaskbar, out var ownerPid);
                Check(ownerPid != (uint)Environment.ProcessId, "NativeTaskbarWindow.Find must never return a window owned by OpenTaskbar.");
            }
            Check(!TrayHost.IsBroadcasting, "TrayHost.IsBroadcasting must be false when idle.");
            var testTray = new OpenTaskbar.Views.Shell.TrayShellButton();
            var liveEmptyContent = testTray.BuildLiveContent(Array.Empty<ManagedShell.WindowsTray.NotifyIcon>());
            Check(liveEmptyContent is StackPanel sp && sp.Children.Count >= 2,
                "TrayShellButton.BuildLiveContent must render a heading, empty caption and settings for an empty icon list.");

            // 11. Dock profiles lifecycle, migration, sync, validation, and localization checks
            CheckDockProfiles(Check);

            // 12. Focus mode toggle, non-destructive restore, widget polling stop, profile interaction, and localization checks
            CheckFocusModeFlags(Check);
            CheckFocusModeWidgets(Check);
            CheckFocusModeProfiles(Check);
            CheckFocusModePersistenceAndSettingsWindow(app.Dispatcher, Check);
            CheckLocalization("Focus Mode localization", ["FocusMode", "FocusModeDescription"], Check);
        }
        finally
        {
            UiText.Current.SetLanguage(previousLanguage);
        }

        return checks;
    }

    private static void CheckFocusModeFlags(Action<bool, string> Check)
    {
        var focused = new DockSettings(AnimationsEnabled: true, NotificationBadgesEnabled: true, FocusModeEnabled: true);
        Check(!focused.EffectiveAnimationsEnabled && !focused.EffectiveNotificationBadgesEnabled,
            "Focus Mode must suppress effective animations and notification badges.");
        Check(focused.AnimationsEnabled && focused.NotificationBadgesEnabled,
            "Focus Mode must preserve enabled animation and notification badge preferences.");

        var restored = focused with { FocusModeEnabled = false };
        Check(restored.EffectiveAnimationsEnabled && restored.EffectiveNotificationBadgesEnabled,
            "Turning off Focus Mode must restore enabled animations and notification badges.");

        foreach (var (animations, badges) in new[] { (false, true), (true, false), (false, false) })
        {
            var settings = restored with { AnimationsEnabled = animations, NotificationBadgesEnabled = badges };
            var suppressed = settings with { FocusModeEnabled = true };
            var resumed = suppressed with { FocusModeEnabled = false };
            Check(!suppressed.EffectiveAnimationsEnabled && !suppressed.EffectiveNotificationBadgesEnabled
                && resumed.EffectiveAnimationsEnabled == animations && resumed.EffectiveNotificationBadgesEnabled == badges,
                $"Toggling Focus Mode must honor AnimationsEnabled={animations} and NotificationBadgesEnabled={badges}.");
        }

        using var json = JsonDocument.Parse(SerializeSettings(focused));
        Check(!json.RootElement.TryGetProperty(nameof(DockSettings.EffectiveAnimationsEnabled), out _)
            && !json.RootElement.TryGetProperty(nameof(DockSettings.EffectiveNotificationBadgesEnabled), out _),
            "Serialized settings must omit both computed Focus Mode properties.");
    }

    private static void CheckFocusModeWidgets(Action<bool, string> Check)
    {
        var strip = new StackPanel();
        using var coordinator = new WidgetCoordinator(strip, Dispatcher.CurrentDispatcher, _ => { }, lazy: true);
        var isActive = typeof(OpenTaskbar.Widgets.DockWidget)
            .GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing DockWidget.IsActive property.");
        var timer = typeof(WidgetCoordinator).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(coordinator) as DispatcherTimer
            ?? throw new InvalidOperationException("Missing WidgetCoordinator polling timer.");

        var settings = new DockSettings(Widgets: ["Clock", "Focus"], FocusModeEnabled: false);
        coordinator.Apply(settings);
        var widgets = coordinator.ActiveWidgets;
        Check(widgets.Count == 2 && widgets.GetValueOrDefault("Clock") is ClockWidget
            && widgets.GetValueOrDefault("Focus") is FocusWidget,
            "The lazy coordinator must create only the Clock and Focus widgets.");
        CheckWidgetState(active: true, Visibility.Visible, "Before enabling Focus Mode");

        var focused = settings with { FocusModeEnabled = true };
        coordinator.Apply(focused);
        CheckWidgetState(active: false, Visibility.Collapsed, "While Focus Mode is enabled");
        Check(focused.Widgets is ["Clock", "Focus"] && settings.Widgets is ["Clock", "Focus"],
            "Enabling Focus Mode must preserve the configured Clock and Focus widgets.");

        coordinator.Apply(focused with { FocusModeEnabled = false });
        CheckWidgetState(active: true, Visibility.Visible, "After disabling Focus Mode");
        var restored = coordinator.ActiveWidgets;
        Check(restored.Count == widgets.Count
            && widgets.All(pair => ReferenceEquals(pair.Value, restored.GetValueOrDefault(pair.Key))
                && strip.Children.Contains(pair.Value)),
            "Disabling Focus Mode must reactivate the original widget instances.");

        void CheckWidgetState(bool active, Visibility visibility, string context)
        {
            Check(coordinator.HasActiveWidgets == active, $"{context}, HasActiveWidgets must be {active}.");
            Check(timer.IsEnabled == active, $"{context}, the widget polling timer's enabled state must be {active}.");
            foreach (var (id, widget) in widgets)
            {
                Check(isActive.GetValue(widget) is bool widgetActive && widgetActive == active,
                    $"{context}, {id}.IsActive must be {active}.");
                Check(widget.Visibility == visibility, $"{context}, {id} must be {visibility}.");
            }
        }
    }

    private static void CheckFocusModeProfiles(Action<bool, string> Check)
    {
        var defaults = DockProfileService.EnsureDefaultProfile(new DockSettings(
            Widgets: ["Clock"], InstalledWidgets: ["Clock", "Focus"], FocusModeEnabled: true));
        var (work, createError) = DockProfileService.Create(defaults, "Work");
        Check(createError is null && work is { ActiveProfile: "Work", FocusModeEnabled: true, Widgets: ["Clock"] },
            "Creating a profile in Focus Mode must preserve the global toggle and the enabled widget list.");
        Check(work.Profiles?.SingleOrDefault(profile => profile.Name == "Work")?.Widgets is ["Clock"],
            "Creating a profile in Focus Mode must capture its configured widgets.");

        var (restoredDefault, defaultError) = DockProfileService.Switch(work with { Widgets = ["Focus"] }, "Default");
        Check(defaultError is null && restoredDefault is { ActiveProfile: "Default", FocusModeEnabled: true, Widgets: ["Clock"] },
            "Switching to Default must keep Focus Mode enabled and restore its Clock widget.");
        var (restoredWork, workError) = DockProfileService.Switch(restoredDefault, "Work");
        Check(workError is null && restoredWork is { ActiveProfile: "Work", FocusModeEnabled: true, Widgets: ["Focus"] },
            "Switching back to Work must keep Focus Mode enabled and restore its Focus widget.");

        var unfocusedWork = restoredWork with { FocusModeEnabled = false };
        Check(unfocusedWork.Widgets is ["Focus"], "Turning off Focus Mode must preserve Work's Focus widget.");
        var (unfocusedDefault, switchError) = DockProfileService.Switch(unfocusedWork, "Default");
        Check(switchError is null && unfocusedDefault is { ActiveProfile: "Default", FocusModeEnabled: false, Widgets: ["Clock"] },
            "Switching after disabling Focus Mode must keep it off and restore Default's Clock widget.");
        Check(unfocusedDefault.Profiles is [{ Name: "Default", Widgets: ["Clock"] }, { Name: "Work", Widgets: ["Focus"] }],
            "Each profile must retain its configured widget snapshot through Focus Mode toggles and profile switches.");
    }

    private static void CheckFocusModePersistenceAndSettingsWindow(Dispatcher dispatcher, Action<bool, string> Check)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ot-focus-mode-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockSettingsStore(tempPath);
            File.WriteAllText(tempPath, "{}");
            var (legacy, legacyError) = store.Load();
            Check(legacyError is null && !legacy.FocusModeEnabled, "An empty settings object must load with Focus Mode off.");

            var initial = new DockSettings(Theme: ThemeManager.Current, Language: UiText.Current.Language,
                AnimationsEnabled: true, Widgets: ["Clock"], InstalledWidgets: ["Clock"],
                ThemePack: ThemePacks.Current?.Id, FocusModeEnabled: false);
            foreach (var enabled in new[] { true, false })
            {
                Check(store.Save(initial with { FocusModeEnabled = enabled }) is null,
                    $"Settings must save cleanly with FocusModeEnabled={enabled}.");
                var (loaded, loadError) = store.Load();
                Check(loadError is null && loaded.FocusModeEnabled == enabled,
                    $"Reloading settings must preserve FocusModeEnabled={enabled}.");
            }

            CheckFocusModeSettingsWindow(new ApplicationPreferences(store), dispatcher, Check);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static void CheckFocusModeSettingsWindow(ApplicationPreferences preferences, Dispatcher dispatcher, Action<bool, string> Check)
    {
        Check(preferences.LoadError is null, "The Focus Mode SettingsWindow fixture must load cleanly.");
        var changeCount = 0;
        preferences.Changed += (_, _) => changeCount++;
        var window = new SettingsWindow(preferences)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            window.Show();
            PumpDispatcher();
            Check(window.FocusModeToggle.IsChecked == false && window.AnimationsToggle.IsChecked == true,
                "SettingsWindow must initially show Focus Mode off and animations enabled.");
            Check(changeCount == 0, "Opening SettingsWindow must leave the Focus Mode preferences untouched.");

            Check(preferences.Save(preferences.Current with { FocusModeEnabled = true }) is null,
                "Enabling Focus Mode externally must save cleanly.");
            PumpDispatcher();
            Check(window.FocusModeToggle.IsChecked == true && window.AnimationsToggle.IsChecked == true,
                "SettingsWindow must reflect externally enabled Focus Mode and keep the animation toggle checked.");
            Check(changeCount == 1, "Syncing enabled Focus Mode must raise only the external Changed event.");

            Check(preferences.Save(preferences.Current with { FocusModeEnabled = false }) is null,
                "Disabling Focus Mode externally must save cleanly.");
            PumpDispatcher();
            Check(window.FocusModeToggle.IsChecked == false && window.AnimationsToggle.IsChecked == true,
                "SettingsWindow must reflect externally disabled Focus Mode and keep the animation toggle checked.");
            Check(preferences.Current is { FocusModeEnabled: false, AnimationsEnabled: true, Widgets: ["Clock"], InstalledWidgets: ["Clock"] },
                "External Focus Mode toggles must preserve widget and animation preferences.");
        }
        finally
        {
            window.CloseForShutdown();
        }

        Check(changeCount == 2, "Each external Focus Mode toggle must raise exactly one Changed event through window close.");

        void PumpDispatcher()
        {
            // Allow the settings window's debounced autosave to fire if syncing queued one.
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, dispatcher);
            try
            {
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                timer.Stop();
            }
        }
    }

    private static void CheckDockProfiles(Action<bool, string> Check)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ot-profiles-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockSettingsStore(tempPath);
            Check(!File.Exists(tempPath), "The first-launch profile fixture must start without a settings file.");
            var (missingFile, missingError) = store.Load();
            Check(missingError is null && HasDefaultProfile(missingFile),
                "Loading a missing settings file must seed and activate exactly one Default profile.");

            File.WriteAllText(tempPath, "{}");
            var (emptyJson, emptyError) = store.Load();
            Check(emptyError is null && HasDefaultProfile(emptyJson),
                "Loading an empty settings object must seed and activate exactly one Default profile.");

            foreach (var settings in new[] { new DockSettings(Profiles: null), new DockSettings(Profiles: []) })
            {
                var seeded = DockProfileService.EnsureDefaultProfile(settings);
                Check(HasDefaultProfile(seeded), "EnsureDefaultProfile must seed Default for null and empty profile lists.");
            }

            CheckLegacyDockProfiles(store, tempPath, Check);
            CheckDockProfileLifecycle(store, Check);
        }
        finally
        {
            File.Delete(tempPath);
        }

        CheckDockProfileValidation(Check);
        CheckLocalization("dock profile localization",
            ["ProfileRename", "ProfileRenamePrompt", "ErrorProfileNameEmpty", "ErrorProfileNotFound", "ErrorProfileDeleteLast"], Check);

        static bool HasDefaultProfile(DockSettings settings) =>
            settings is { ActiveProfile: "Default", Profiles: [{ Name: "Default" }] };
    }

    private static void CheckLegacyDockProfiles(DockSettingsStore store, string tempPath, Action<bool, string> Check)
    {
        File.WriteAllText(tempPath, """
            {
              "Profiles": [
                {"Name":"Work","HiddenApps":["C:\\Games\\game.exe"]},
                {"Name":"Gaming","HiddenApps":[]}
              ],
              "ActiveProfile": "Work",
              "Widgets": ["Clock"],
              "InstalledWidgets": ["Clock", "Weather"],
              "WidgetOrder": ["Weather", "Clock"],
              "DockItemOrder": ["widget:Clock", "start"],
              "DockWidthMode": "Percent",
              "DockWidthPixels": 1100,
              "DockWidthPercent": 75,
              "Theme": "Dark",
              "ThemePack": "Neon",
              "ThemeApplicationIcons": false,
              "LauncherHotkey": "CtrlSpace",
              "DockHotkey": "WinAltD",
              "AutoHide": "Always"
            }
            """);
        var (legacy, loadError) = store.Load();
        Check(loadError is null && legacy is { ActiveProfile: "Work", Profiles: [{ Name: "Work" }, { Name: "Gaming" }] },
            "Legacy two-property profiles must deserialize with their original names and active profile.");
        Check(legacy is { Widgets: ["Clock"], DockWidthMode: DockWidthMode.Percent, ThemePack: "Neon" },
            "Loading legacy profiles must retain the existing live widget, width, and theme settings.");
        var legacyWork = legacy.Profiles![0];
        var legacyGaming = legacy.Profiles![1];
        Check(legacyWork.HiddenApps.SequenceEqual([@"C:\Games\game.exe"]) && legacyGaming.HiddenApps.Count == 0,
            "Legacy profiles must deserialize both populated and empty hidden-app lists.");
        CheckProfileFields(new DockProfile("Work", []), legacyWork, "Deserializing legacy Work", Check);
        CheckProfileFields(new DockProfile("Gaming", []), legacyGaming, "Deserializing legacy Gaming", Check);
        var beforeEnsure = SerializeSettings(legacy);
        var ensured = DockProfileService.EnsureDefaultProfile(legacy);
        Check(SerializeSettings(ensured) == beforeEnsure && SerializeSettings(legacy) == beforeEnsure,
            "EnsureDefaultProfile must preserve existing profiles and their active name.");

        var (gaming, gamingError) = DockProfileService.Switch(legacy, "Gaming");
        Check(gamingError is null && gaming.ActiveProfile == "Gaming",
            "Switching to a legacy profile must update ActiveProfile.");
        CheckProfileFields(LiveProfileFields(legacy), LiveProfileFields(gaming), "Switching to legacy Gaming", Check);

        var edited = gaming with { Theme = AppTheme.Light, DockWidthPercent = 55 };
        var synced = DockProfileService.SyncActive(edited);
        Check(synced.ActiveProfile == "Gaming" && synced.Profiles![1].HiddenApps.Count == 0,
            "SyncActive must keep the legacy target active and preserve its hidden apps.");
        CheckProfileFields(LiveProfileFields(edited), synced.Profiles![1], "Syncing legacy Gaming", Check);
        CheckProfileFields(LiveProfileFields(legacy), synced.Profiles![0], "Preserving the outgoing Work snapshot", Check);

        var (work, workError) = DockProfileService.Switch(synced, "Work");
        Check(workError is null && work.ActiveProfile == "Work"
            && work.Profiles![0].HiddenApps.SequenceEqual([@"C:\Games\game.exe"]),
            "Switching back to Work must restore its active name and keep its legacy hidden apps.");
        CheckProfileFields(LiveProfileFields(legacy), LiveProfileFields(work), "Returning to legacy Work", Check);
    }

    private static void CheckDockProfileLifecycle(DockSettingsStore store, Action<bool, string> Check)
    {
        var defaultFields = new DockSettings(
            Widgets: ["Clock"], InstalledWidgets: ["Clock", "Weather"], WidgetOrder: ["Weather", "Clock"],
            DockItemOrder: null, DockWidthMode: DockWidthMode.Auto, DockWidthPixels: 900, DockWidthPercent: 60,
            Theme: AppTheme.System, ThemePack: null, ThemeApplicationIcons: true,
            LauncherHotkey: HotkeyChoice.AltSpace, DockHotkey: HotkeyChoice.CtrlAltD, AutoHide: AutoHideMode.Disabled);
        var workFields = new DockSettings(
            Widgets: ["Focus"], InstalledWidgets: ["Focus", "Storage"], WidgetOrder: ["Storage", "Focus"],
            DockItemOrder: ["widget:Focus", "start"], DockWidthMode: DockWidthMode.Pixels, DockWidthPixels: 1200, DockWidthPercent: 70,
            Theme: AppTheme.Light, ThemePack: "Neon", ThemeApplicationIcons: false,
            LauncherHotkey: HotkeyChoice.CtrlSpace, DockHotkey: HotkeyChoice.WinAltD, AutoHide: AutoHideMode.Smart);
        var gamingFields = new DockSettings(
            Widgets: ["Media"], InstalledWidgets: ["Media", "Audio"], WidgetOrder: ["Audio", "Media"],
            DockItemOrder: ["start", "widget:Media"], DockWidthMode: DockWidthMode.Percent, DockWidthPixels: 1600, DockWidthPercent: 85,
            Theme: AppTheme.Dark, ThemePack: "Arcade", ThemeApplicationIcons: true,
            LauncherHotkey: HotkeyChoice.CtrlAltSpace, DockHotkey: HotkeyChoice.Disabled, AutoHide: AutoHideMode.Always);
        string[] defaultHidden = [@"C:\Apps\default.exe"];
        string[] workHidden = [@"C:\Apps\work.exe"];
        string[] gamingHidden = [@"C:\Games\game.exe"];

        var defaults = DockProfileService.UpdateHiddenApps(
            DockProfileService.EnsureDefaultProfile(defaultFields), "Default", defaultHidden);
        var (createdWork, createWorkError) = DockProfileService.Create(defaults, "Work");
        Check(createWorkError is null && createdWork is { ActiveProfile: "Work", Profiles: { Count: 2 } },
            "Create must append and activate Work.");
        CheckProfileSnapshot(createdWork, "Work", WithoutWidgets(defaultFields), [], "Creating Work from Default", Check);

        var work = DockProfileService.UpdateHiddenApps(
            workFields with { Profiles = createdWork.Profiles, ActiveProfile = createdWork.ActiveProfile }, "Work",
            [workHidden[0], @"c:\apps\WORK.exe"]);
        var (createdGaming, createGamingError) = DockProfileService.Create(work, "Gaming");
        Check(createGamingError is null && createdGaming is { ActiveProfile: "Gaming", Profiles: { Count: 3 } },
            "Create must append and activate Gaming.");
        CheckProfileSnapshot(createdGaming, "Gaming", WithoutWidgets(workFields), [], "Creating Gaming from Work", Check);
        CheckProfileSnapshot(createdGaming, "Work", workFields, workHidden, "Capturing Work before creating Gaming", Check);

        var gaming = DockProfileService.UpdateHiddenApps(
            gamingFields with { Profiles = createdGaming.Profiles, ActiveProfile = createdGaming.ActiveProfile }, "Gaming", gamingHidden);
        var workActive = SwitchTo(gaming, "Work", workFields, workHidden);

        // Edit globals after creating all snapshots so stale profile data cannot satisfy the isolation checks.
        var globals = workActive with
        {
            Language = AppLanguage.Japanese,
            Position = DockPosition.Right,
            Edge = DockEdge.Left,
            Size = DockSize.Large,
            DockScale = 1.25,
            WeatherCity = "Istanbul",
            CheckUpdates = false
        };
        var defaultActive = SwitchTo(globals, "Default", defaultFields, defaultHidden);
        Check(defaultActive is { ThemePack: null, DockItemOrder: null, DockWidthMode: DockWidthMode.Auto },
            "Switching from Work to Default must clear ThemePack and DockItemOrder and restore automatic width.");
        var gamingActive = SwitchTo(defaultActive, "Gaming", gamingFields, gamingHidden);
        workActive = SwitchTo(gamingActive, "Work", workFields, workHidden);

        CheckDockProfileRenameAndDelete(workActive, defaultActive, workFields, workHidden, gamingFields, gamingHidden, Check);
        CheckDockProfilePersistence(store, workActive, defaultFields, defaultHidden, Check);

        static DockSettings WithoutWidgets(DockSettings settings) => settings with
        {
            Widgets = [],
            InstalledWidgets = [],
            WidgetOrder = null,
            DockItemOrder = settings.DockItemOrder?
                .Where(token => !token.StartsWith("widget:", StringComparison.OrdinalIgnoreCase)).ToArray()
        };

        DockSettings SwitchTo(DockSettings source, string name, DockSettings expected, IReadOnlyList<string> hiddenApps)
        {
            var (switched, error) = DockProfileService.Switch(source, name);
            Check(error is null && switched.ActiveProfile == name, $"Switch must activate {name}.");
            CheckProfileFields(LiveProfileFields(expected), LiveProfileFields(switched), $"Switching to {name}", Check);
            CheckProfileSnapshot(switched, name, expected, hiddenApps, $"Restoring the {name} snapshot", Check);
            CheckGlobalSettings(source, switched, $"Switching to {name}", Check);
            return switched;
        }
    }

    private static void CheckDockProfileRenameAndDelete(
        DockSettings work, DockSettings defaults,
        DockSettings workFields, IReadOnlyList<string> workHidden,
        DockSettings gamingFields, IReadOnlyList<string> gamingHidden, Action<bool, string> Check)
    {
        var (renamedActive, activeError) = DockProfileService.Rename(work, "Work", "  Workroom  ");
        Check(activeError is null && renamedActive.ActiveProfile == "Workroom"
            && renamedActive.Profiles!.Select(profile => profile.Name).SequenceEqual(["Default", "Workroom", "Gaming"]),
            "Renaming the active profile must trim the name and update ActiveProfile.");
        CheckProfileSnapshot(renamedActive, "Workroom", workFields, workHidden, "Renaming active Work", Check);

        var (caseOnly, caseError) = DockProfileService.Rename(renamedActive, "Workroom", "WORKROOM");
        Check(caseError is null && caseOnly.ActiveProfile == "WORKROOM",
            "Renaming a profile with only a case change must succeed and update the active name.");
        CheckProfileSnapshot(caseOnly, "WORKROOM", workFields, workHidden, "Changing the case of Workroom", Check);

        var (renamedInactive, inactiveError) = DockProfileService.Rename(caseOnly, "Gaming", "  Play  ");
        Check(inactiveError is null && renamedInactive.ActiveProfile == "WORKROOM"
            && renamedInactive.Profiles!.Select(profile => profile.Name).SequenceEqual(["Default", "WORKROOM", "Play"]),
            "Renaming an inactive profile must trim the name and preserve ActiveProfile.");
        CheckProfileSnapshot(renamedInactive, "Play", gamingFields, gamingHidden, "Renaming inactive Gaming", Check);
        CheckProfileFields(LiveProfileFields(work), LiveProfileFields(renamedInactive), "Renaming profiles", Check);

        var beforeRename = SerializeSettings(renamedInactive);
        foreach (var (currentName, newName, expectedError) in new[]
        {
            ("Play", " workroom ", "ErrorProfileExists"),
            ("WORKROOM", "", "ErrorProfileNameEmpty"),
            ("WORKROOM", " \t ", "ErrorProfileNameEmpty"),
            ("Missing", "Personal", "ErrorProfileNotFound")
        })
        {
            var (unchanged, error) = DockProfileService.Rename(renamedInactive, currentName, newName);
            Check(error == expectedError && SerializeSettings(unchanged) == beforeRename
                && SerializeSettings(renamedInactive) == beforeRename,
                $"Rename must return {expectedError} and preserve the original settings.");
        }

        var (removedInactive, removeInactiveError) = DockProfileService.Delete(work, "Gaming");
        Check(removeInactiveError is null && removedInactive.ActiveProfile == "Work"
            && removedInactive.Profiles!.Select(profile => profile.Name).SequenceEqual(["Default", "Work"]),
            "Deleting inactive Gaming must remove it and keep Work active.");
        CheckProfileFields(LiveProfileFields(work), LiveProfileFields(removedInactive), "Deleting inactive Gaming", Check);

        var (removedActive, removeActiveError) = DockProfileService.Delete(defaults, "Default");
        Check(removeActiveError is null && removedActive.ActiveProfile == "Work"
            && removedActive.Profiles!.Select(profile => profile.Name).SequenceEqual(["Work", "Gaming"]),
            "Deleting active Default must activate Work, the first remaining profile.");
        CheckProfileFields(LiveProfileFields(workFields), LiveProfileFields(removedActive), "Deleting active Default", Check);
        CheckProfileSnapshot(removedActive, "Work", workFields, workHidden, "Activating Work after deletion", Check);
        CheckGlobalSettings(defaults, removedActive, "Deleting active Default", Check);

        var (lastProfile, removeGamingError) = DockProfileService.Delete(removedActive, "Gaming");
        Check(removeGamingError is null && lastProfile is { ActiveProfile: "Work", Profiles: [{ Name: "Work" }] },
            "Deleting the other inactive profile must leave only Work.");
        var beforeDelete = SerializeSettings(lastProfile);
        var (refused, lastError) = DockProfileService.Delete(lastProfile, "Work");
        Check(lastError == "ErrorProfileDeleteLast" && SerializeSettings(refused) == beforeDelete
            && SerializeSettings(lastProfile) == beforeDelete,
            "Deleting the last profile must return ErrorProfileDeleteLast and preserve the settings.");
    }

    private static void CheckDockProfilePersistence(
        DockSettingsStore store, DockSettings work, DockSettings defaultFields, IReadOnlyList<string> defaultHidden,
        Action<bool, string> Check)
    {
        Check(store.Save(work) is null, "The Work profile fixture must save to the temporary settings file.");
        var preferences = new ApplicationPreferences(store);
        Check(preferences.LoadError is null && preferences.Current.ActiveProfile == "Work",
            "ApplicationPreferences must open the settings file with Work active.");
        CheckProfileFields(LiveProfileFields(work), LiveProfileFields(preferences.Current), "Opening Work before editing", Check);

        var edited = preferences.Current with
        {
            Widgets = ["Countdown"],
            InstalledWidgets = ["Countdown", "DayProgress"],
            WidgetOrder = ["DayProgress", "Countdown"],
            DockItemOrder = ["widget:Countdown", "search", "start"],
            DockWidthMode = DockWidthMode.Fill,
            DockWidthPixels = 1420,
            DockWidthPercent = 92,
            Theme = AppTheme.Dark,
            ThemePack = null,
            ThemeApplicationIcons = true,
            LauncherHotkey = HotkeyChoice.Disabled,
            DockHotkey = HotkeyChoice.CtrlAltD,
            AutoHide = AutoHideMode.Always
        };
        string[] hiddenApps = [@"C:\Apps\edited.exe"];
        edited = DockProfileService.UpdateHiddenApps(edited, "Work", hiddenApps);

        var previousLanguage = UiText.Current.Language;
        var previousPack = ThemePacks.Current?.Id;
        var previousTheme = ThemeManager.Current;
        try
        {
            Check(preferences.Save(edited) is null && preferences.Current.ActiveProfile == "Work",
                "ApplicationPreferences.Save must persist the edits with Work active.");
            CheckProfileSnapshot(preferences.Current, "Work", edited, hiddenApps, "Saving the active Work snapshot", Check);

            var (defaults, defaultError) = DockProfileService.Switch(preferences.Current, "Default");
            Check(defaultError is null && defaults.ActiveProfile == "Default", "Switching away from saved Work must activate Default.");
            CheckProfileFields(LiveProfileFields(defaultFields), LiveProfileFields(defaults), "Switching away from saved Work", Check);
            CheckProfileSnapshot(defaults, "Default", defaultFields, defaultHidden, "Preserving Default after saving Work", Check);
            var (restoredWork, workError) = DockProfileService.Switch(defaults, "Work");
            Check(workError is null && restoredWork.ActiveProfile == "Work", "Switching back must reactivate saved Work.");
            CheckProfileFields(LiveProfileFields(edited), LiveProfileFields(restoredWork), "Switching back to saved Work", Check);
            CheckProfileSnapshot(restoredWork, "Work", edited, hiddenApps, "Restoring saved Work", Check);

            var (reloaded, loadError) = store.Load();
            Check(loadError is null && reloaded.ActiveProfile == "Work", "Reloading the settings file must retain Work as active.");
            CheckProfileFields(LiveProfileFields(edited), LiveProfileFields(reloaded), "Reloading live Work settings from disk", Check);
            CheckProfileSnapshot(reloaded, "Work", edited, hiddenApps, "Reloading the Work snapshot from disk", Check);
            CheckProfileSnapshot(reloaded, "Default", defaultFields, defaultHidden, "Reloading the Default snapshot from disk", Check);
            CheckGlobalSettings(work, reloaded, "Reloading Work from disk", Check);
        }
        finally
        {
            UiText.Current.SetLanguage(previousLanguage);
            ThemeManager.Apply(previousTheme);
            ThemePacks.Select(previousPack);
            ThemePacks.ApplyWidgets(previousTheme);
        }
    }

    private static void CheckDockProfileValidation(Action<bool, string> Check)
    {
        var profile = new DockProfile("Work", [], Widgets: ["Clock"], InstalledWidgets: ["Clock"]);
        var valid = new DockSettings(Profiles: [profile], ActiveProfile: "Work");
        Check(DockSettingsStore.Validate(valid), "The valid profile fixture must pass settings validation.");
        Check(!DockSettingsStore.Validate(valid with { Profiles = [profile, profile with { Name = "WORK" }] }),
            "Validation must reject profile names that differ only in case.");

        foreach (var (invalid, description) in new (DockProfile, string)[]
        {
            (profile with { Name = "" }, "an empty name"),
            (profile with { Name = " " }, "a whitespace-only name"),
            (profile with { Widgets = ["InvalidWidget"], InstalledWidgets = null }, "an invalid enabled widget ID"),
            (profile with { InstalledWidgets = ["Clock", "InvalidWidget"] }, "an invalid installed widget ID"),
            (profile with { InstalledWidgets = ["Weather"] }, "an enabled widget missing from InstalledWidgets"),
            (profile with { DockWidthPixels = 99 }, "a pixel width below 100"),
            (profile with { DockWidthPixels = 5001 }, "a pixel width above 5000"),
            (profile with { DockWidthPercent = 9 }, "a width percentage below 10"),
            (profile with { DockWidthPercent = 101 }, "a width percentage above 100"),
            (profile with { DockWidthMode = (DockWidthMode)99 }, "an undefined width mode"),
            (profile with { Theme = (AppTheme)99 }, "an undefined theme"),
            (profile with { LauncherHotkey = (HotkeyChoice)99 }, "an undefined launcher hotkey"),
            (profile with { DockHotkey = (HotkeyChoice)99 }, "an undefined dock hotkey"),
            (profile with { AutoHide = (AutoHideMode)99 }, "an undefined auto-hide mode")
        })
        {
            Check(!DockSettingsStore.Validate(valid with { Profiles = [invalid] }),
                $"Profile validation must reject {description}.");
        }
    }

    private static void CheckLocalization(string context, IReadOnlyList<string> keys, Action<bool, string> Check)
    {
        var assembly = typeof(LanguageCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("OpenTaskbar.Localization.Languages.", StringComparison.Ordinal))
            .ToArray();
        Check(resources.Length == 14, $"There must be 14 embedded language resources for {context}.");
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            Check(stream.ReadByte() == 0x7B, $"{resource} must start with '{{' and have no UTF-8 BOM.");
            stream.Position = 0;
            using var document = JsonDocument.Parse(stream);
            foreach (var key in keys)
            {
                Check(document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
                    $"{resource} must have a non-empty translation for {key}.");
            }
        }
    }

    private static void CheckProfileSnapshot(
        DockSettings settings, string name, DockSettings expected, IReadOnlyList<string> hiddenApps,
        string context, Action<bool, string> Check)
    {
        var profile = settings.Profiles?.SingleOrDefault(item => item.Name == name);
        Check(profile is not null && profile.HiddenApps.SequenceEqual(hiddenApps), $"{context} must preserve the {name} hidden apps.");
        CheckProfileFields(LiveProfileFields(expected), profile!, context, Check);
    }

    private static DockProfile LiveProfileFields(DockSettings settings) => new(
        Name: string.Empty, HiddenApps: [], Widgets: settings.Widgets, InstalledWidgets: settings.InstalledWidgets,
        WidgetOrder: settings.WidgetOrder, DockItemOrder: settings.DockItemOrder, DockWidthMode: settings.DockWidthMode,
        DockWidthPixels: settings.DockWidthPixels, DockWidthPercent: settings.DockWidthPercent, Theme: settings.Theme,
        ThemePack: settings.ThemePack, ThemeApplicationIcons: settings.ThemeApplicationIcons, LauncherHotkey: settings.LauncherHotkey,
        DockHotkey: settings.DockHotkey, AutoHide: settings.AutoHide);

    private static void CheckProfileFields(DockProfile expected, DockProfile actual, string context, Action<bool, string> Check)
    {
        Check(SameOrder(expected.Widgets, actual.Widgets), $"{context} must preserve Widgets.");
        Check(SameOrder(expected.InstalledWidgets, actual.InstalledWidgets), $"{context} must preserve InstalledWidgets.");
        Check(SameOrder(expected.WidgetOrder, actual.WidgetOrder), $"{context} must preserve WidgetOrder.");
        Check(SameOrder(expected.DockItemOrder, actual.DockItemOrder), $"{context} must preserve DockItemOrder.");
        Check(actual.DockWidthMode == expected.DockWidthMode && actual.DockWidthPixels == expected.DockWidthPixels
            && actual.DockWidthPercent == expected.DockWidthPercent, $"{context} must preserve width mode, pixels, and percentage.");
        Check(actual.Theme == expected.Theme && actual.ThemePack == expected.ThemePack
            && actual.ThemeApplicationIcons == expected.ThemeApplicationIcons, $"{context} must preserve theme, theme pack, and icon theming.");
        Check(actual.LauncherHotkey == expected.LauncherHotkey && actual.DockHotkey == expected.DockHotkey,
            $"{context} must preserve both hotkeys.");
        Check(actual.AutoHide == expected.AutoHide, $"{context} must preserve AutoHide.");

        static bool SameOrder(IReadOnlyList<string>? expected, IReadOnlyList<string>? actual) =>
            expected is null ? actual is null : actual is not null && expected.SequenceEqual(actual);
    }

    private static void CheckGlobalSettings(DockSettings expected, DockSettings actual, string context, Action<bool, string> Check)
    {
        Check(actual.Language == expected.Language && actual.Position == expected.Position && actual.Edge == expected.Edge
            && actual.Size == expected.Size && actual.DockScale == expected.DockScale && actual.WeatherCity == expected.WeatherCity
            && actual.CheckUpdates == expected.CheckUpdates, $"{context} must preserve all global settings.");
    }

    private static string SerializeSettings(DockSettings settings) =>
        JsonSerializer.Serialize(settings, DockSettingsStore.SerializerOptions);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DockFlyout Flyout, WeakReference ContentRef) ExerciseDockFlyout(
        Application app,
        Action<bool, string> check)
    {
        var flyout = new DockFlyout();
        var anchor = new Button();
        var content = new Border { Width = 40, Height = 40 };
        var hostWin = new Window { Content = anchor, Width = 100, Height = 60, ShowInTaskbar = false };
        hostWin.Show();
        try
        {
            flyout.Show(anchor, content, animate: false);
            check(flyout.IsOpen, "DockFlyout must open when shown.");
            flyout.Close();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            check(!flyout.IsOpen, "DockFlyout must close cleanly.");
            check(flyout.RecentlyClosed(anchor), "DockFlyout must still detect recently closed target via WeakReference.");
        }
        finally
        {
            hostWin.Close();
        }

        return (flyout, new WeakReference(content));
    }

    private static byte[] EncodeSolidPng(int size)
    {
        var pixels = new byte[size * size * 4];
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static Button[] AiCards(AiUsageWidget widget) =>
        widget.Content is Button { Visibility: Visibility.Visible } card ? [card] : [];

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
