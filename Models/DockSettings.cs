namespace OpenTaskbar.Models;

public enum DockPosition
{
    Left,
    Center,
    Right
}

public sealed record DockSettings(
    DockPosition Position = DockPosition.Left,
    IReadOnlyList<string>? PinOrder = null);
