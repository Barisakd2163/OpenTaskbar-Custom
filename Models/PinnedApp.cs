using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace OpenTaskbar.Models;

public sealed record PinnedApp(string Name, string ExecutablePath)
{
    public string? LaunchArguments { get; init; }

    public string? IconPath { get; init; }

    [JsonIgnore]
    public bool Exists => File.Exists(ExecutablePath);

    [JsonIgnore]
    public BitmapSource? Icon { get; init; }

    [JsonIgnore]
    public bool HasNotification { get; init; }
}
