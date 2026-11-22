using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>One app button's worth of tracked windows, in enumeration (Z-order) order.</summary>
public sealed record RunningAppCandidate(
    string ExecutablePath,
    IReadOnlyList<TrackedWindow> Windows,
    bool HasNotification,
    bool IsForeground,
    string? AppUserModelId = null)
{
    /// <summary>The topmost window decides the identity icon; ApplicationFrameHost hosts several Store apps.</summary>
    public nint IconWindow => Windows[0].Handle;

    public int WindowCount => Windows.Count;

    /// <summary>
    /// Stable identity for UI collections. Two different Store apps can share one host executable
    /// (ApplicationFrameHost.exe), so ExecutablePath alone is not unique once AUMID grouping is used.
    /// </summary>
    public string Identity => AppUserModelId ?? ExecutablePath;
}

/// <summary>
/// The single gate that turns tracked windows into running-app buttons. The primary dock, secondary docks,
/// notification badges and running indicators all consume this projection, so a background window can never
/// be a normal app button in one place and hidden in another.
/// </summary>
internal static class RunningAppProjection
{
    internal static IReadOnlyList<RunningAppCandidate> Project(
        IReadOnlyList<TrackedWindow> windows,
        ISet<string> excludedPaths,
        nint foreground,
        bool notificationBadgesEnabled,
        Func<string, bool> fileExists)
    {
        var groups = new Dictionary<string, List<TrackedWindow>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var path = window.ExecutablePath;
            if (string.IsNullOrEmpty(path) || excludedPaths.Contains(path) || !fileExists(path)) continue;
            // Windows groups taskbar buttons by AppUserModelID, not by executable: a single app can
            // spawn helper processes with different executables (e.g. FortiClient tray/popup helpers).
            // Use the AUMID as the grouping key when present, falling back to the executable path.
            var key = string.IsNullOrWhiteSpace(window.AppUserModelId) ? path : window.AppUserModelId!;
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups.Add(key, group);
                order.Add(key);
            }
            group.Add(window);
        }

        var candidates = new List<RunningAppCandidate>(order.Count);
        foreach (var key in order)
        {
            var group = groups[key];
            // The representative path is the first window's executable (icon + launch target).
            // The group key is the AUMID when present, otherwise the path — keep it as the stable identity.
            var path = group[0].ExecutablePath!;
            var appId = string.IsNullOrWhiteSpace(group[0].AppUserModelId) ? null : group[0].AppUserModelId;
            candidates.Add(new RunningAppCandidate(
                path,
                group,
                notificationBadgesEnabled && group.Any(window => window.HasNotification),
                group.Any(window => window.Handle == foreground),
                appId));
        }
        return candidates;
    }
}
