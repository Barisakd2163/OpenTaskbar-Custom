using System.Diagnostics;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

internal static class MediaVolumeChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        const string spotify = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify";
        Check(AppAudioSessionMatcher.Match("spotify.exe", null, spotify) > 0,
            "Media volume matcher must map a Spotify AUMID to the spotify.exe audio session.");
        Check(AppAudioSessionMatcher.Match("chrome.exe", null, "Chrome.UserData.Profile1") > 0,
            "Media volume matcher must match a Chrome profile to the Chrome audio session.");
        Check(AppAudioSessionMatcher.Match("explorer.exe", null, spotify) == 0,
            "Media volume matcher must not bind an unrelated audio session.");
        Check(AppAudioSessionMatcher.Match("SpotifyHelper.exe", null, spotify) == 0,
            "Similar names must not select a different process.");
        Check(AppAudioSessionMatcher.Match("App.exe", null, "Microsoft.WindowsMediaPlayer_8wekyb3d8bbwe!App") == 0,
            "A generic packaged application ID must not select an unrelated executable.");
        Check(AppAudioSessionMatcher.Match("", null, "Spotify") == 0,
            "Media volume matcher must reject empty app tokens.");

        var released = 0;
        AppAudioSession Chrome(int pid, AppAudioSessionState state) =>
            new(pid, "chrome", null, 0.5f, false, _ => true, _ => true, Release: () => released++)
            {
                ReadState = () => state
            };

        var inactive = Chrome(1, AppAudioSessionState.Inactive);
        var active = Chrome(2, AppAudioSessionState.Active);
        var expired = Chrome(3, AppAudioSessionState.Unavailable);
        var selected = AppAudioSessionMatcher.Select([inactive, active, expired], "Chrome");
        Check(ReferenceEquals(selected, active) && released == 2,
            "Media volume must select the active audio stream and release stale audio sessions.");
        active.Dispose();
        Check(AppAudioSessionMatcher.Select([Chrome(1, AppAudioSessionState.Active), Chrome(2, AppAudioSessionState.Active)], "Chrome") is null
            && released == 5, "Equally plausible audio sessions must be released without selecting one.");

        var storeApp = new AppAudioSession(4, "ApplicationFrameHost", null, 0.5f, false, _ => true, _ => true)
        {
            AppUserModelId = spotify,
            ReadState = () => AppAudioSessionState.Active
        };
        var nameMatch = new AppAudioSession(5, "Spotify", null, 0.5f, false, _ => true, _ => true)
        {
            ReadState = () => AppAudioSessionState.Active
        };
        Check(ReferenceEquals(AppAudioSessionMatcher.Select([nameMatch, storeApp], spotify), storeApp),
            "The exact packaged application identity must win over a process name match.");
        storeApp.Dispose();

        const string browserWindowId = "0123456789ABCDEF";
        using var libreWolf = new AppAudioSession(6, "librewolf", null, 0.5f, false, _ => true, _ => true)
        {
            WindowAppUserModelId = browserWindowId.ToLowerInvariant(),
            ReadState = () => AppAudioSessionState.Active
        };
        using var otherBrowser = new AppAudioSession(7, "librewolf", null, 0.5f, false, _ => true, _ => true)
        {
            WindowAppUserModelId = "FEDCBA9876543210",
            ReadState = () => AppAudioSessionState.Active
        };
        Check(AppAudioSessionMatcher.Match("librewolf", null, browserWindowId) == 0,
            "An opaque media identity cannot be inferred from the process name.");
        Check(ReferenceEquals(AppAudioSessionMatcher.Select([otherBrowser, libreWolf], browserWindowId), libreWolf),
            "An opaque media identity must select only the audio process with the matching window identity.");

        // Live getters must reflect writes so the widget never renders a stale volume.
        var level = 0.4f;
        var muted = false;
        var session = new AppAudioSession(1, "Spotify", null, level, muted,
            next => { level = next; return true; },
            next => { muted = next; return true; },
            GetVolume: () => level,
            GetMute: () => muted);
        Check(session.SetVolume(0.73f) && Math.Abs(session.CurrentVolume - 0.73f) < 0.001f,
            "Per-app audio session must expose the live volume after a write.");
        Check(session.SetMute(true) && session.CurrentMute,
            "Per-app audio session must expose the live mute state after a write.");
        session.Dispose();

        var timeline = new MediaTimeline();
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var unstamped = new MediaTimelineSample(TimeSpan.Zero, TimeSpan.FromSeconds(60), TimeSpan.Zero,
            new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero));
        long Timestamp(int seconds) => seconds * Stopwatch.Frequency;
        void Update(MediaTimelineSample sample, double rate, int seconds) =>
            timeline.Update(sample, rate, now.AddSeconds(seconds), Timestamp(seconds));

        Update(unstamped, 1, 0);
        Check(timeline.PositionAt(Timestamp(0)) == 0, "An unset Windows timestamp must not jump to the end.");
        Update(unstamped, 1, 8);
        Check(timeline.PositionAt(Timestamp(8)) == 8, "Polling the same timeline must not restart its clock.");
        Update(unstamped, 0, 9);
        Check(timeline.PositionAt(Timestamp(20)) == 9, "Pausing must freeze the last estimated position.");
        Update(unstamped, 2, 20);
        Check(timeline.PositionAt(Timestamp(22)) == 13, "Resume must exclude paused time and respect playback rate.");
        timeline.ConfirmSeek(40, Timestamp(22));
        Update(unstamped, 2, 24);
        Check(timeline.PositionAt(Timestamp(24)) == 44, "A confirmed seek must survive a repeated old timeline sample.");
        Check(timeline.PositionAt(Timestamp(40)) == 60, "Progress must stop at the end of the item.");

        var next = new MediaTimelineSample(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5), now.AddSeconds(41));
        Update(next, 1, 43);
        Check(timeline.Duration == 25 && timeline.PositionAt(Timestamp(43)) == 2,
            "A new timeline must start from its own position and media-clock timestamp.");
        Update(next with { Position = TimeSpan.FromSeconds(12), UpdatedAt = now.AddHours(-1) }, 1, 44);
        Check(timeline.PositionAt(Timestamp(44)) == 7, "A stale timestamp must not make a fresh position jump to the end.");
        Update(next with { Position = TimeSpan.FromSeconds(25), UpdatedAt = now.AddSeconds(35) }, 1, 45);
        Check(timeline.PositionAt(Timestamp(45)) == 20,
            "An inconsistent timestamp must not simulate completion while the source still reports playback.");
        Update(next with { End = next.Start }, 0, 46);
        Check(timeline.Duration == 0 && timeline.PositionAt(Timestamp(46)) == 0,
            "An unavailable timeline must clear the previous item's position and duration.");

        return checks;
    }
}
