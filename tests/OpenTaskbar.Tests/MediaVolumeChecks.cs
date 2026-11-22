using OpenTaskbar.Services;

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

        // AUMID -> process name matching used by the media widget's per-app volume control.
        Check(AppAudioSessionMatcher.Matches("spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            "Media volume matcher must map a Spotify AUMID to the spotify.exe audio session.");
        Check(AppAudioSessionMatcher.Matches("chrome.exe", "Chrome.Chrome!Chrome"),
            "Media volume matcher must map a Chrome AUMID to the chrome.exe audio session.");
        Check(!AppAudioSessionMatcher.Matches("explorer.exe", "SpotifyAB.SpotifyMusic!Spotify"),
            "Media volume matcher must not bind an unrelated audio session.");
        Check(!AppAudioSessionMatcher.Matches("", "Spotify"),
            "Media volume matcher must reject empty app tokens.");

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

        return checks;
    }
}
