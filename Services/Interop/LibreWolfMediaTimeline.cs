using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace OpenTaskbar.Services;

internal static class LibreWolfMediaTimeline
{
    private static readonly PropertyCondition DocumentCondition =
        new(AutomationElement.ControlTypeProperty, ControlType.Document);

    private static readonly PropertyCondition SliderCondition =
        new(AutomationElement.ControlTypeProperty, ControlType.Slider);

    private static readonly PropertyCondition MusicProgressCondition =
        new(AutomationElement.AutomationIdProperty, "progress-bar");

    private static readonly string[] ClockFormats = [@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"];

    internal static (TimeSpan Position, TimeSpan Duration)? Read(string sourceAppId, string mediaTitle)
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName("librewolf");
            (TimeSpan Position, TimeSpan Duration)? result = null;
            foreach (var process in processes)
            {
                var window = process.MainWindowHandle;
                if (window == 0 || !string.Equals(NativeJumpListSession.ReadWindowAppId(window),
                        sourceAppId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var root = AutomationElement.FromHandle(window);
                foreach (AutomationElement document in root.FindAll(TreeScope.Descendants, DocumentCondition))
                {
                    if (!document.TryGetCurrentPattern(ValuePattern.Pattern, out var addressPattern)
                        || addressPattern is not ValuePattern address
                        || !MatchesDocument(address.Current.Value, document.Current.Name, mediaTitle, out var isYouTubeMusic))
                        continue;

                    foreach (AutomationElement control in document.FindAll(TreeScope.Descendants,
                        isYouTubeMusic ? MusicProgressCondition : SliderCondition))
                    {
                        if (ReadControl(control, isYouTubeMusic) is not { } timeline) continue;
                        // The same title can be open in more than one tab, or a page can have several players.
                        if (result is not null) return null;
                        result = timeline;
                    }
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or COMException or Win32Exception or UnauthorizedAccessException)
        {
            // Navigation, tab closure and accessibility availability can change during the query.
            return null;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static (TimeSpan Position, TimeSpan Duration)? ReadControl(AutomationElement control, bool isYouTubeMusic)
    {
        if (!control.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern)
            || pattern is not RangeValuePattern range)
            return null;

        var current = range.Current;
        if (isYouTubeMusic)
            return ReadRange(current.Minimum, current.Maximum, current.Value);

        // A generic slider can also represent volume. Require a clock label agreeing with its numeric range.
        return control.TryGetCurrentPattern(ValuePattern.Pattern, out var labelPattern) && labelPattern is ValuePattern label
            ? ReadClockRange(current.Minimum, current.Maximum, current.Value, label.Current.Value)
            : null;
    }

    internal static bool MatchesDocument(string address, string documentTitle, string mediaTitle, out bool isYouTubeMusic)
    {
        isYouTubeMusic = false;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || string.IsNullOrWhiteSpace(mediaTitle) || !documentTitle.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase))
            return false;

        isYouTubeMusic = uri.Host == "music.youtube.com";
        return true;
    }

    internal static (TimeSpan Position, TimeSpan Duration)? ReadClockRange(double minimum, double duration, double position, string label)
    {
        var separator = label.IndexOf('/');
        if (separator < 0
            || !TimeSpan.TryParseExact(label.AsSpan(0, separator).Trim(), ClockFormats, CultureInfo.InvariantCulture, out var displayedPosition)
            || !TimeSpan.TryParseExact(label.AsSpan(separator + 1).Trim(), ClockFormats, CultureInfo.InvariantCulture, out var displayedDuration))
            return null;

        if (Math.Floor(duration) == displayedDuration.TotalSeconds && Math.Floor(position) == displayedPosition.TotalSeconds)
            return ReadRange(minimum, duration, position);
        if (Math.Floor(duration / 1000) == displayedDuration.TotalSeconds && Math.Floor(position / 1000) == displayedPosition.TotalSeconds)
            return ReadRange(minimum, duration / 1000, position / 1000);
        return null;
    }

    internal static (TimeSpan Position, TimeSpan Duration)? ReadRange(double minimum, double duration, double position) =>
        minimum == 0 && double.IsFinite(duration) && duration > 0 && duration < TimeSpan.MaxValue.TotalSeconds
        && double.IsFinite(position) && position >= 0 && position <= duration
            ? (TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(duration))
            : null;
}
