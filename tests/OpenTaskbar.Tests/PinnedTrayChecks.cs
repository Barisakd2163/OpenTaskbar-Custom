using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.WindowsTray;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views.Shell;

internal static class PinnedTrayChecks
{
    public static int RunHeadless()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        CheckIdentity(Check);
        CheckSettingsStorePersistenceAndValidation(Check);
        CheckLocalization(Check);
        CheckStripSelectionOrdering(Check);

        return checks;
    }

    public static int RunUi(Application application)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        CheckTrayFlyoutFallbackPinning(application, Check);
        CheckTrayFlyoutLivePinning(application, Check);
        CheckPinnedTrayStripUi(application, Check);

        return checks;
    }

    private static void CheckIdentity(Action<bool, string> check)
    {
        var guidA = new Guid("11111111-2222-3333-4444-555555555555");
        var guidB = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        // GUID takes priority over path + UID.
        var guidKey = TrayPinIdentity.KeyFor(guidA, @"C:\Apps\Shared.exe", 7);
        check(guidKey == "guid:11111111-2222-3333-4444-555555555555",
            $"Non-empty GUID must produce canonical guid:<D> key (got '{guidKey}').");
        check(TrayPinIdentity.IsPinned([guidKey], guidA, @"C:\Apps\Shared.exe", 7),
            "GUID pin must match the icon with the same GUID.");
        check(!TrayPinIdentity.IsPinned([guidKey], guidB, @"C:\Apps\Shared.exe", 7),
            "GUID pin must not match a different GUID even with the same executable path and UID.");

        // Path + UID disambiguates multiple live tray icons from the same executable when GUID is empty.
        var uid1Key = TrayPinIdentity.KeyFor(Guid.Empty, @"  C:\Apps\Multi.EXE  ", 1);
        var uid2Key = TrayPinIdentity.KeyFor(Guid.Empty, @"c:\apps\multi.exe", 2);
        check(uid1Key == @"path:c:\apps\multi.exe|1" && uid2Key == @"path:c:\apps\multi.exe|2",
            "Empty GUID must produce normalized lowercase path:<path>|<uid> keys.");
        check(TrayPinIdentity.IsPinned([uid1Key], Guid.Empty, @"C:\Apps\Multi.exe", 1)
            && !TrayPinIdentity.IsPinned([uid1Key], Guid.Empty, @"C:\Apps\Multi.exe", 2),
            "Path+UID pin must distinguish separate tray instances of the same executable.");

        // Missing both GUID and path is unpinnable.
        check(TrayPinIdentity.KeyFor(Guid.Empty, null, 1) == string.Empty
            && TrayPinIdentity.KeyFor(Guid.Empty, "   ", 1) == string.Empty,
            "An icon with empty GUID and blank path must be unpinnable.");

        // Fallback path match and wildcard match work reliably without false prefix matches.
        var fallbackKey = TrayPinIdentity.KeyForPath(@"C:\Apps\Discord.exe");
        check(fallbackKey == @"path:c:\apps\discord.exe",
            "KeyForPath must normalize to path:<lowercase>.");
        check(TrayPinIdentity.IsPinned([fallbackKey], guidA, @"C:\Apps\Discord.exe", 42),
            "Registry-fallback path pin must match a live icon for the same executable.");
        check(TrayPinIdentity.IsPinned([fallbackKey + "|*"], guidA, @"C:\Apps\Discord.exe", 42),
            "Wildcard path pin must match a live icon for the same executable.");
        check(!TrayPinIdentity.IsPinned([fallbackKey], Guid.Empty, @"C:\Apps\Discord.exe.bak", 42),
            "Fallback path pin must not prefix-match a different executable path.");
        check(TrayPinIdentity.IsPathPinned([uid1Key], @"C:\Apps\Multi.exe")
            && !TrayPinIdentity.IsPathPinned([uid1Key], @"C:\Apps\Multi.exe.bak"),
            "IsPathPinned must match path+UID pins for the same executable without false prefix matches.");
        check(TrayPinIdentity.FindMatchingKey([fallbackKey], guidA, @"C:\Apps\Discord.exe", 42) == fallbackKey,
            "FindMatchingKey must return the stored fallback key so live unpin removes it.");

        // TogglePin: add, append, deduplicate, case-insensitive remove, cap at 64, null when empty.
        var added = TrayPinIdentity.TogglePin(null, guidKey);
        check(added is [var first] && first == guidKey,
            "TogglePin on null list must create a single-item list.");
        var two = TrayPinIdentity.TogglePin(added, uid1Key);
        check(two is { Count: 2 } && two[0] == guidKey && two[1] == uid1Key,
            "TogglePin must append new keys in order.");
        var dedupedRemove = TrayPinIdentity.TogglePin([guidKey, guidKey.ToUpperInvariant(), uid1Key], guidKey.ToUpperInvariant());
        check(dedupedRemove is { Count: 1 } && dedupedRemove[0] == uid1Key,
            "TogglePin must deduplicate and remove matching keys case-insensitively.");
        var empty = TrayPinIdentity.TogglePin([uid1Key], uid1Key);
        check(empty is null,
            "TogglePin removing the last key must return null.");

        var full = Enumerable.Range(0, TrayPinIdentity.MaxPinnedIcons)
            .Select(i => $"path:c:\\apps\\app{i}.exe|1")
            .ToArray();
        var atCap = TrayPinIdentity.TogglePin(full, @"path:c:\apps\overflow.exe|1");
        check(atCap is { Count: TrayPinIdentity.MaxPinnedIcons }
            && !atCap.Contains(@"path:c:\apps\overflow.exe|1", StringComparer.OrdinalIgnoreCase),
            "TogglePin must enforce the 64-entry cap when adding.");
        var removedFromFull = TrayPinIdentity.TogglePin(full, full[0]);
        check(removedFromFull is { Count: TrayPinIdentity.MaxPinnedIcons - 1 },
            "TogglePin must still allow removing an entry when at the 64-entry cap.");

        check(TrayPinIdentity.CleanTitle("OneDrive\r\nUp to date", @"C:\OneDrive.exe", "Fallback") == "OneDrive"
            && TrayPinIdentity.CleanTitle("   ", @"C:\Apps\Steam.exe", "Fallback") == "Steam"
            && TrayPinIdentity.CleanTitle(null, null, "Fallback") == "Fallback",
            "CleanTitle must extract the first non-empty line, then filename, then fallback.");
    }

    private static void CheckSettingsStorePersistenceAndValidation(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentaskbar-pinned-tray-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockSettingsStore(path);

            // Backwards compatibility: missing PinnedTrayIcons property loads cleanly as null.
            File.WriteAllText(path, "{}");
            var legacy = store.Load();
            check(legacy.Error is null && legacy.Settings.PinnedTrayIcons is null,
                "Settings JSON missing PinnedTrayIcons must load cleanly with null PinnedTrayIcons.");

            // Roundtrip persistence.
            var pins = new[]
            {
                "guid:11111111-2222-3333-4444-555555555555",
                @"path:c:\apps\discord.exe|1",
                @"path:c:\apps\steam.exe"
            };
            check(store.Save(new DockSettings(PinnedTrayIcons: pins)) is null,
                "Valid PinnedTrayIcons must save without error.");
            var loaded = store.Load();
            check(loaded.Error is null && loaded.Settings.PinnedTrayIcons?.SequenceEqual(pins) == true,
                "PinnedTrayIcons must survive Save/Load roundtrip intact.");

            // Boundary validation.
            var exactlyMax = Enumerable.Range(0, TrayPinIdentity.MaxPinnedIcons).Select(i => $"path:app{i}.exe").ToArray();
            var overMax = Enumerable.Range(0, TrayPinIdentity.MaxPinnedIcons + 1).Select(i => $"path:app{i}.exe").ToArray();
            var maxLen = new string('x', TrayPinIdentity.MaxKeyLength);
            var overLen = new string('x', TrayPinIdentity.MaxKeyLength + 1);

            check(DockSettingsStore.Validate(new DockSettings(PinnedTrayIcons: null))
                && DockSettingsStore.Validate(new DockSettings(PinnedTrayIcons: []))
                && DockSettingsStore.Validate(new DockSettings(PinnedTrayIcons: exactlyMax))
                && DockSettingsStore.Validate(new DockSettings(PinnedTrayIcons: [maxLen])),
                "Null, empty, 64-entry, and 512-char PinnedTrayIcons must pass validation.");

            check(!DockSettingsStore.Validate(new DockSettings(PinnedTrayIcons: overMax))
                && store.Save(new DockSettings(PinnedTrayIcons: overMax)) is not null,
                "PinnedTrayIcons exceeding 64 entries must be rejected.");
            check(!DockSettingsStore.Validate(new DockSettings(PinnedTrayIcons: ["   "]))
                && !DockSettingsStore.Validate(new DockSettings(PinnedTrayIcons: [""])),
                "Whitespace or empty PinnedTrayIcons entries must be rejected.");
            check(!DockSettingsStore.Validate(new DockSettings(PinnedTrayIcons: [overLen])),
                "PinnedTrayIcons entries exceeding 512 characters must be rejected.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    private static void CheckLocalization(Action<bool, string> check)
    {
        var previous = UiText.Current.Language;
        try
        {
            UiText.Current.SetLanguage(AppLanguage.English);
            check(UiText.Get("PinTrayIcon") == "Pin to dock" && UiText.Get("UnpinTrayIcon") == "Unpin from dock",
                "PinTrayIcon and UnpinTrayIcon must resolve cleanly in English.");

            UiText.Current.SetLanguage(AppLanguage.Turkish);
            check(UiText.Get("PinTrayIcon") == "Dock’a sabitle" && UiText.Get("UnpinTrayIcon") == "Dock’tan kaldır",
                "PinTrayIcon and UnpinTrayIcon must resolve cleanly in Turkish.");

            foreach (var language in LanguageCatalog.Languages)
            {
                UiText.Current.SetLanguage(language);
                check(UiText.TryGet("PinTrayIcon", out var pinText) && !string.IsNullOrWhiteSpace(pinText)
                    && UiText.TryGet("UnpinTrayIcon", out var unpinText) && !string.IsNullOrWhiteSpace(unpinText),
                    $"Locale {language.CultureName} must define both PinTrayIcon and UnpinTrayIcon.");
            }
        }
        finally
        {
            UiText.Current.SetLanguage(previous);
        }
    }

    private static void CheckStripSelectionOrdering(Action<bool, string> check)
    {
        var guidA = new Guid("11111111-1111-1111-1111-111111111111");
        var guidB = new Guid("22222222-2222-2222-2222-222222222222");

        var running = new (string Name, Guid Guid, string? Path, uint Uid)[]
        {
            ("Alpha", guidA, @"C:\Apps\Alpha.exe", 1),
            ("Multi-1", Guid.Empty, @"C:\Apps\Multi.exe", 1),
            ("Multi-2", Guid.Empty, @"C:\Apps\Multi.exe", 2),
            ("Bravo", guidB, @"C:\Apps\Bravo.exe", 3)
        };

        var pinnedKeys = new[]
        {
            "guid:99999999-9999-9999-9999-999999999999", // not currently running
            @"path:c:\apps\multi.exe|2",
            "guid:11111111-1111-1111-1111-111111111111"
        };

        var selected = PinnedTrayStrip.SelectPinnedItems(pinnedKeys, running, x => (x.Guid, x.Path, x.Uid));
        check(selected.Select(x => x.Name).SequenceEqual(["Multi-2", "Alpha"]),
            "SelectPinnedItems must include only currently running pinned icons ordered by pinnedKeys and disambiguated by UID.");
    }

    private static void CheckTrayFlyoutFallbackPinning(Application application, Action<bool, string> check)
    {
        var previous = UiText.Current.Language;
        try
        {
            UiText.Current.SetLanguage(AppLanguage.English);
            IReadOnlyList<string>? pinned = null;
            var toggledKeys = new List<string>();

            var tray = new TrayShellButton
            {
                PinnedKeysProvider = () => pinned
            };
            tray.PinToggled += key =>
            {
                toggledKeys.Add(key);
                pinned = TrayPinIdentity.TogglePin(pinned, key);
            };

            var app = new TrayApp("Discord", @"C:\Apps\Discord.exe", null);

            // Unpinned state.
            var unpinnedRoot = (StackPanel)tray.BuildContent([app]);
            var unpinnedRow = unpinnedRoot.Children.OfType<DockPanel>().Single();
            var unpinnedButtons = unpinnedRow.Children.OfType<Button>().ToArray();
            check(unpinnedButtons.Length == 3,
                "Fallback tray row must contain terminate, pin, and open buttons.");
            var pinButton = unpinnedButtons[1];
            check(AutomationProperties.GetName(pinButton) == "Discord: Pin to dock",
                $"Unpinned fallback pin button must expose localized PinTrayIcon label (got '{AutomationProperties.GetName(pinButton)}').");
            check(ReferenceEquals(pinButton.Foreground, application.FindResource("TextSecondary")),
                "Unpinned pin button must use subtle TextSecondary foreground.");

            pinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(toggledKeys.Count == 1 && toggledKeys[0] == @"path:c:\apps\discord.exe"
                && pinned is [@"path:c:\apps\discord.exe"],
                "Clicking pin in BuildContent must fire PinToggled with the normalized path key.");

            // Pinned state.
            var pinnedRoot = (StackPanel)tray.BuildContent([app]);
            var pinnedRow = pinnedRoot.Children.OfType<DockPanel>().Single();
            var unpinButton = pinnedRow.Children.OfType<Button>().ToArray()[1];
            check(AutomationProperties.GetName(unpinButton) == "Discord: Unpin from dock",
                $"Pinned fallback pin button must expose localized UnpinTrayIcon label (got '{AutomationProperties.GetName(unpinButton)}').");
            check(ReferenceEquals(unpinButton.Foreground, application.FindResource("AccentBrush"))
                && ReferenceEquals(unpinButton.Background, application.FindResource("WidgetSelectedFill")),
                "Pinned pin button must show distinct accent foreground and selected fill.");

            unpinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(toggledKeys.Count == 2 && toggledKeys[1] == @"path:c:\apps\discord.exe" && pinned is null,
                "Clicking unpin in BuildContent must fire PinToggled and clear the pinned key.");
        }
        finally
        {
            UiText.Current.SetLanguage(previous);
        }
    }

    private static void CheckTrayFlyoutLivePinning(Application application, Action<bool, string> check)
    {
        var previous = UiText.Current.Language;
        try
        {
            UiText.Current.SetLanguage(AppLanguage.English);
            var guidA = new Guid("12345678-1234-1234-1234-123456789abc");
            var liveGuidIcon = CreateTestNotifyIcon(guidA, @"C:\Apps\OneDrive.exe", 1, "OneDrive\nSynced");
            var livePathIcon = CreateTestNotifyIcon(Guid.Empty, @"C:\Apps\Helper.exe", 9, "Helper");

            IReadOnlyList<string>? pinned = [TrayPinIdentity.KeyForPath(@"C:\Apps\OneDrive.exe")];
            var toggledKeys = new List<string>();

            var tray = new TrayShellButton
            {
                PinnedKeysProvider = () => pinned
            };
            tray.PinToggled += key =>
            {
                toggledKeys.Add(key);
                pinned = TrayPinIdentity.TogglePin(pinned, key);
            };

            var root = (StackPanel)tray.BuildLiveContent([liveGuidIcon, livePathIcon]);
            var rows = root.Children.OfType<DockPanel>().ToArray();
            check(rows.Length == 2, "BuildLiveContent must render one row per live icon.");

            // Row 0 (OneDrive) was pinned via fallback path key -> shows as pinned and unpinning fires that fallback key.
            var oneDrivePin = rows[0].Children.OfType<Button>().ToArray()[1];
            check(AutomationProperties.GetName(oneDrivePin) == "OneDrive: Unpin from dock"
                && ReferenceEquals(oneDrivePin.Foreground, application.FindResource("AccentBrush")),
                "Live icon matched by fallback path pin must display pinned state.");
            oneDrivePin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(toggledKeys.Last() == @"path:c:\apps\onedrive.exe" && pinned is null,
                "Unpinning a live icon matched via fallback key must emit and remove the stored fallback key.");

            // Row 1 (Helper) is unpinned -> pinning fires path|uid key.
            var helperPin = rows[1].Children.OfType<Button>().ToArray()[1];
            check(AutomationProperties.GetName(helperPin) == "Helper: Pin to dock"
                && ReferenceEquals(helperPin.Foreground, application.FindResource("TextSecondary")),
                "Unpinned live icon must display unpinned state.");
            helperPin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(toggledKeys.Last() == @"path:c:\apps\helper.exe|9"
                && pinned is [@"path:c:\apps\helper.exe|9"],
                "Pinning a live icon without GUID must emit path:<lower>|<uid>.");

            // Re-render after toggle: OneDrive is now unpinned (and re-pinning emits guid:...), Helper is pinned.
            var refreshed = (StackPanel)tray.BuildLiveContent([liveGuidIcon, livePathIcon]);
            var refreshedRows = refreshed.Children.OfType<DockPanel>().ToArray();
            var oneDriveRepin = refreshedRows[0].Children.OfType<Button>().ToArray()[1];
            var helperUnpin = refreshedRows[1].Children.OfType<Button>().ToArray()[1];
            check(AutomationProperties.GetName(oneDriveRepin) == "OneDrive: Pin to dock"
                && AutomationProperties.GetName(helperUnpin) == "Helper: Unpin from dock",
                "Rebuilt live content must reflect updated pinned and unpinned states.");

            oneDriveRepin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(toggledKeys.Last() == "guid:12345678-1234-1234-1234-123456789abc",
                "Pinning a live icon with non-empty GUID must emit guid:<D>.");
        }
        finally
        {
            UiText.Current.SetLanguage(previous);
        }
    }

    private static void CheckPinnedTrayStripUi(Application application, Action<bool, string> check)
    {
        var strip = new PinnedTrayStrip();
        var layoutChanges = 0;
        strip.LayoutChanged += () => layoutChanges++;

        check(strip.Visibility == Visibility.Collapsed && strip.VisibleCount == 0 && strip.Orientation == Orientation.Horizontal,
            "PinnedTrayStrip must start collapsed, empty, and horizontal.");

        var clicks = new List<(string Key, MouseButton Button)>();
        strip.UpdateItems(
        [
            new PinnedTrayStrip.PinnedTrayItem("k2", "Second App", null, btn => clicks.Add(("k2", btn))),
            new PinnedTrayStrip.PinnedTrayItem("k1", "First App", null, btn => clicks.Add(("k1", btn)))
        ]);

        check(strip.Visibility == Visibility.Visible && strip.VisibleCount == 2 && layoutChanges == 1,
            "Populating PinnedTrayStrip must make it visible and raise LayoutChanged.");

        var buttons = strip.Children.OfType<Button>().ToArray();
        check(buttons.Length == 2
            && Equals(buttons[0].Tag, "k2") && Equals(buttons[1].Tag, "k1")
            && AutomationProperties.GetName(buttons[0]) == "Second App"
            && Equals(buttons[0].ToolTip, "Second App"),
            "PinnedTrayStrip buttons must preserve item order, ToolTip, and AutomationProperties.Name.");

        // Left-click and right-click forwarding (right-click must mark Handled so dock context menu does not open).
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var rightUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent
        };
        buttons[1].RaiseEvent(rightUp);
        check(rightUp.Handled,
            "Right-clicking a pinned tray button must set Handled=true so the dock context menu does not swallow it.");
        check(clicks.Count == 2
            && clicks[0] == ("k2", MouseButton.Left)
            && clicks[1] == ("k1", MouseButton.Right),
            "PinnedTrayStrip must forward left-click and right-click to the clicked tray item.");

        // Updating items with the same keys must refresh tooltips/handlers in place without recreating Button instances.
        var prevBtn0 = buttons[0];
        var prevBtn1 = buttons[1];
        strip.UpdateItems(
        [
            new PinnedTrayStrip.PinnedTrayItem("k2", "Second App Updated", null, btn => clicks.Add(("k2-v2", btn))),
            new PinnedTrayStrip.PinnedTrayItem("k1", "First App Updated", null, btn => clicks.Add(("k1-v2", btn)))
        ]);
        check(strip.Children.Count == 2
            && ReferenceEquals(strip.Children[0], prevBtn0)
            && ReferenceEquals(strip.Children[1], prevBtn1)
            && Equals(prevBtn0.ToolTip, "Second App Updated")
            && AutomationProperties.GetName(prevBtn0) == "Second App Updated"
            && layoutChanges == 1,
            "Updating pinned items with matching keys must update Button state in place without raising LayoutChanged.");
        prevBtn0.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(clicks.Last() == ("k2-v2", MouseButton.Left),
            "In-place updated pinned tray button must invoke the refreshed click handler.");

        // Orientation switching Horizontal <-> Vertical.
        strip.SetOrientation(Orientation.Vertical);
        check(strip.Orientation == Orientation.Vertical
            && buttons.All(b => b.Margin == new Thickness(0, 1, 0, 1)),
            "SetOrientation(Vertical) must switch strip orientation and item margins for side docks.");

        strip.SetOrientation(Orientation.Horizontal);
        check(strip.Orientation == Orientation.Horizontal
            && buttons.All(b => b.Margin == new Thickness(1, 0, 1, 0)),
            "SetOrientation(Horizontal) must restore horizontal orientation and item margins.");

        // Icon scaling.
        strip.SetIconScale(28);
        check(strip.ButtonSize == 24 && strip.ImageSize == 18 && buttons[0].Width == 24 && buttons[0].Height == 24,
            "Compact dock icon size (28) must produce 24x24 buttons with 18x18 images.");
        strip.SetIconScale(38);
        check(strip.ButtonSize == 33 && strip.ImageSize == 22 && buttons[0].Width == 33 && buttons[0].Height == 33,
            "Large dock icon size (38) must scale compact buttons cleanly without upscaling images past 22 DIP.");

        // Disabling strip (ShowTrayButton = false) collapses immediately and takes 0 space.
        strip.IsStripEnabled = false;
        check(strip.Visibility == Visibility.Collapsed && strip.VisibleCount == 0,
            "Disabling PinnedTrayStrip must collapse it immediately.");
        strip.IsStripEnabled = true;

        // Live NotifyIcon filtering + ordering via UpdateFromIcons.
        var iconA = CreateTestNotifyIcon(new Guid("11111111-1111-1111-1111-111111111111"), @"C:\Apps\A.exe", 1, "App A");
        var iconB = CreateTestNotifyIcon(new Guid("22222222-2222-2222-2222-222222222222"), @"C:\Apps\B.exe", 2, "App B");
        var iconC = CreateTestNotifyIcon(Guid.Empty, @"C:\Apps\C.exe", 3, "App C");
        var forwarded = new List<(NotifyIcon Icon, MouseButton Button)>();
        strip.ClickHandler = (icon, btn) => forwarded.Add((icon, btn));
        strip.UpdateFromIcons([iconA, iconB, iconC], ["guid:22222222-2222-2222-2222-222222222222", @"path:c:\apps\c.exe|3"]);
        var liveButtons = strip.Children.OfType<Button>().ToArray();
        check(strip.Visibility == Visibility.Visible && liveButtons.Length == 2
            && AutomationProperties.GetName(liveButtons[0]) == "App B"
            && AutomationProperties.GetName(liveButtons[1]) == "App C",
            "UpdateFromIcons must render only running pinned icons in pinned order.");

        liveButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var liveRightUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent
        };
        liveButtons[1].RaiseEvent(liveRightUp);
        check(forwarded.Count == 2
            && ReferenceEquals(forwarded[0].Icon, iconB) && forwarded[0].Button == MouseButton.Left
            && ReferenceEquals(forwarded[1].Icon, iconC) && forwarded[1].Button == MouseButton.Right,
            "Live pinned tray buttons must forward clicks to the exact matching NotifyIcon.");

        // Path-based pins open the executable instead of relying on synthetic tray messages,
        // which some apps only answer on double-click (Steam, etc.).
        var opened = new List<TrayApp>();
        strip.OpenAppHandler = (app, _) => opened.Add(app);
        strip.ClickHandler = (_, _) => throw new InvalidOperationException(
            "A path-based pin must not forward its left-click to the tray icon.");
        var pathIcon = CreateTestNotifyIcon(Guid.Empty, @"C:\Apps\PathApp.exe", 7, "Path App");
        strip.UpdateFromIcons([pathIcon], [@"path:c:\apps\pathapp.exe|7"]);
        strip.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(opened.Count == 1 && opened[0].ExecutablePath == @"C:\Apps\PathApp.exe",
            "Left-clicking a path-based pinned tray button must open the app by executable path.");

        // A path pin matched to a live icon that ALSO exposes a GUID must keep the path strategy.
        var guidPathIcon = CreateTestNotifyIcon(new Guid("33333333-3333-3333-3333-333333333333"),
            @"C:\Apps\GuidPathApp.exe", 11, "Guid Path App");
        strip.UpdateFromIcons([guidPathIcon], [@"path:c:\apps\guidpathapp.exe"]);
        strip.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(opened.Count == 2 && opened[1].ExecutablePath == @"C:\Apps\GuidPathApp.exe",
            "A stored path pin must open by path even when the live icon has a GUID.");

        // A pin created from the background-apps flyout still gets a dock button when Windows
        // has no visible NotifyIcon for it (Discord hiding its icon while running, etc.).
        var hiddenApp = new TrayApp("Hidden App", @"C:\Apps\Hidden.exe", null);
        strip.AppsProvider = () => [hiddenApp];
        strip.UpdateFromIcons([], [@"path:c:\apps\hidden.exe"], [hiddenApp]);
        var fallbackButton = strip.Children.OfType<Button>().Single();
        check(AutomationProperties.GetName(fallbackButton) == "Hidden App",
            "A path-based pin must render from the registry app list when no live tray icon matches.");
        fallbackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(opened.Count == 3 && opened[2].ExecutablePath == @"C:\Apps\Hidden.exe",
            "Clicking a registry-backed pinned tray button must open that app by path.");

        // Clearing running icons collapses the strip.
        strip.ClickHandler = (_, _) => { };
        strip.UpdateFromIcons([], ["guid:22222222-2222-2222-2222-222222222222"]);
        check(strip.Visibility == Visibility.Collapsed && strip.VisibleCount == 0,
            "PinnedTrayStrip must collapse when no pinned tray icons are running.");

        // UIPI integrity check: un-elevated dock (Medium 0x2000) cannot forward tray input to High/System (0x3000/0x4000).
        check(!TrayHost.IsIntegrityReachable(0x2000, 0x3000)
              && !TrayHost.IsIntegrityReachable(0x2000, 0x4000)
              && TrayHost.IsIntegrityReachable(0x2000, 0x2000),
            "TrayHost.IsIntegrityReachable must reject higher-integrity windows that UIPI isolates.");
    }

    private static NotifyIcon CreateTestNotifyIcon(Guid guid, string? path, uint uid, string title)
    {
        var icon = new NotifyIcon(null!)
        {
            GUID = guid,
            Path = path ?? string.Empty,
            UID = uid,
            Title = title
        };
        return icon;
    }
}
