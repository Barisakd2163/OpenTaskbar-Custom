using System.IO;
using System.Text.Json.Serialization;

namespace OpenTaskbar.Models;

public sealed record PinnedApp(string Name, string ExecutablePath)
{
    [JsonIgnore]
    public bool Exists => File.Exists(ExecutablePath);
}
