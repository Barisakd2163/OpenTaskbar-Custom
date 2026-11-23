using OpenTaskbar.Models;
using OpenTaskbar.Services;

internal static class RunningAppProjectionChecks
{
    private const string First = @"C:\Apps\First.exe";
    private const string Second = @"C:\Apps\Second.exe";
    private const string Pinned = @"C:\Apps\Pinned.exe";
    private const string Missing = @"C:\Apps\Missing.exe";

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        IReadOnlyList<TrackedWindow> windows =
        [
            new(1, "First", First),
            new(2, "Second window", First.ToLowerInvariant(), HasNotification: true),
            new(3, "Second", Second),
            new(4, "Pinned", Pinned),
            new(5, "Missing", Missing),
            new(6, "No path", null)
        ];
        var onDisk = new HashSet<string>([First, Second, Pinned], StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>([Pinned], StringComparer.OrdinalIgnoreCase);
        bool Exists(string path) => onDisk.Contains(path);

        var candidates = RunningAppProjection.Project(windows, excluded, foreground: 2,
            notificationBadgesEnabled: true, Exists);
        Check(candidates.Count == 2, "Only real, unpinned apps with a window become running app candidates.");
        Check(string.Equals(candidates[0].ExecutablePath, First, StringComparison.OrdinalIgnoreCase),
            "Candidates keep the enumeration order of their first window.");
        Check(candidates[0].WindowCount == 2 && candidates[0].IconWindow == 1,
            "Windows are grouped by executable and the topmost one supplies the icon.");
        Check(candidates[0].HasNotification && candidates[0].IsForeground,
            "Notification and foreground state come from any window in the group.");
        Check(candidates[1].WindowCount == 1 && !candidates[1].HasNotification && !candidates[1].IsForeground,
            "An unrelated app keeps its own state.");

        // One app can spawn helpers with different executables (e.g. FortiClient tray/popup).
        // Windows groups them by AppUserModelID; secondary docks must do the same so closing one
        // window does not make a "new" app button appear for a helper process.
        const string Helper = @"C:\Apps\VendorHelper.exe";
        IReadOnlyList<TrackedWindow> aumidWindows =
        [
            new(10, "Main", First, AppUserModelId: "Vendor.Suite_abc!App"),
            new(11, "Helper", Helper, AppUserModelId: "Vendor.Suite_abc!App"),
            new(12, "Other", Second, AppUserModelId: "Other.App_xyz!App")
        ];
        var aumidExists = new HashSet<string>([First, Helper, Second], StringComparer.OrdinalIgnoreCase);
        var aumidCandidates = RunningAppProjection.Project(aumidWindows,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), foreground: 11,
            notificationBadgesEnabled: true, aumidExists.Contains);
        Check(aumidCandidates.Count == 2 && aumidCandidates[0].WindowCount == 2
              && string.Equals(aumidCandidates[0].ExecutablePath, First, StringComparison.OrdinalIgnoreCase),
            "Windows sharing an AppUserModelID group into one candidate even with different executables.");
        Check(aumidCandidates[0].IsForeground,
            "A helper window carrying the same AUMID still marks the group as foreground.");

        const string packagedAumid = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";
        var hostedWindow = new TrackedWindow(13, "Settings", @"C:\Windows\System32\ApplicationFrameHost.exe",
            AppUserModelId: packagedAumid);
        Check(RunningAppProjection.MatchesWindow(hostedWindow, @"C:\Windows\SystemApps\Settings.exe", packagedAumid),
            "A packaged app window hosted by ApplicationFrameHost must match its pinned app identity for previews.");
        Check(!RunningAppProjection.MatchesWindow(hostedWindow, @"C:\Windows\SystemApps\Settings.exe", "Other.App_xyz!App"),
            "A packaged app window must not match another app's preview by host executable alone.");

        var quiet = RunningAppProjection.Project(windows, excluded, foreground: 2,
            notificationBadgesEnabled: false, Exists);
        Check(quiet.All(candidate => !candidate.HasNotification),
            "Disabled notification badges must never light a running app.");
        Check(quiet.Any(candidate => candidate.IsForeground),
            "Foreground highlighting is independent of notification badges.");

        var noForeground = RunningAppProjection.Project(windows,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, false, Exists);
        Check(noForeground.Count == 3 && noForeground.All(candidate => !candidate.IsForeground),
            "A zero foreground handle must clear every active app indicator.");
        Check(windows.Count == 6, "Projection must not mutate the snapshot it reads.");

        const string spotifyAumid = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify";
        const string spotifyExe = @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.0_x64__zpdnekdrzrea0\Spotify.exe";
        Check(AppShortcutResolver.IsPackagedAppUserModelId(spotifyAumid),
            "Store apps with non-!App entrypoints (such as !Spotify) must be recognized as packaged AUMIDs.");
        Check(!AppShortcutResolver.IsPackagedAppUserModelId("com.squirrel.Discord.Discord"),
            "Win32 Squirrel AUMIDs must not be treated as Store packaged AUMIDs.");
        var spotifyLaunch = AppShortcutResolver.Resolve(spotifyExe, spotifyAumid, "Spotify");
        Check(string.Equals(spotifyLaunch.Target, $"shell:AppsFolder\\{spotifyAumid}", StringComparison.OrdinalIgnoreCase),
            "Packaged Store apps like Spotify must resolve to shell:AppsFolder\\{AUMID}.");

        return checks;
    }
}
