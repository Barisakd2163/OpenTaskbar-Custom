using System.Diagnostics;

namespace OpenTaskbar.Widgets;

internal readonly record struct MediaTimelineSample(
    TimeSpan Start, TimeSpan End, TimeSpan Position, DateTimeOffset UpdatedAt);

internal sealed class MediaTimeline
{
    private double _anchorPosition;
    private long _anchorTimestamp;
    private double _rate;

    public MediaTimelineSample? Sample { get; private set; }
    public double Duration => Sample is { } sample ? (sample.End - sample.Start).TotalSeconds : 0;

    public void Reset()
    {
        Sample = null;
        _rate = 0;
    }

    public void Update(MediaTimelineSample sample, double rate, DateTimeOffset utcNow, long timestamp)
    {
        if (sample.End <= sample.Start)
        {
            Reset();
            return;
        }

        if (Sample != sample)
        {
            Sample = sample;
            var reported = (sample.Position - sample.Start).TotalSeconds;
            var age = (utcNow - sample.UpdatedAt).TotalSeconds;
            var advance = age * rate;
            // Some providers leave the WinRT timestamp at its 1601 epoch or recycle a stale one.
            if (sample.UpdatedAt >= DateTimeOffset.UnixEpoch && age >= 0 && advance <= Duration - reported)
                reported += advance;
            _anchorPosition = Math.Clamp(reported, 0, Duration);
        }
        else if (_rate != rate)
        {
            // Do not count time spent paused when the provider has not reported a new position.
            _anchorPosition = PositionAt(timestamp);
        }
        else
        {
            return;
        }

        _anchorTimestamp = timestamp;
        _rate = rate;
    }

    public double PositionAt(long timestamp) =>
        Math.Clamp(_anchorPosition + Stopwatch.GetElapsedTime(_anchorTimestamp, timestamp).TotalSeconds * _rate,
            0, Duration);

    public void ConfirmSeek(double seconds, long timestamp)
    {
        _anchorPosition = Math.Clamp(seconds, 0, Duration);
        _anchorTimestamp = timestamp;
    }
}
