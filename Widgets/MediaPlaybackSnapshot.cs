using Windows.Media.Control;

namespace OpenTaskbar.Widgets;

// Copy WinRT data before rendering: any of these provider-owned objects can be absent.
internal readonly record struct MediaPlaybackControls(
    bool PlayPause, bool Previous, bool Next, bool Seek);

internal readonly record struct MediaPlaybackSnapshot(
    GlobalSystemMediaTransportControlsSessionPlaybackStatus? Status,
    MediaPlaybackControls? Controls,
    MediaTimelineSample? Timeline,
    bool HasTimelineSource,
    double Rate)
{
    public bool IsAvailable => Status.HasValue
        && Status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
}
