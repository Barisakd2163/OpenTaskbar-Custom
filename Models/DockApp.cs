using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace OpenTaskbar.Models;

public sealed record DockApp(string Name, string ExecutablePath)
{
    public string? LaunchArguments { get; init; }

    [JsonIgnore]
    public string? ShortcutPath { get; init; }

    [JsonIgnore]
    public BitmapSource? Icon { get; init; }

    [JsonIgnore]
    public bool HasNotification { get; init; }

    [JsonIgnore]
    public bool IsRunning { get; init; }

    [JsonIgnore]
    public bool IsForeground { get; init; }

    [JsonIgnore]
    public int WindowCount { get; init; }
}
