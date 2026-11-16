namespace OpenTaskbar.Models;

public enum DockPosition
{
    Left,
    Center,
    Right
}

public enum AppTheme { Dark, Light }
public enum AppLanguage { Turkish, English }
public enum AutoHideMode { Disabled, Smart, Always }

public sealed record DockSettings(
    DockPosition Position = DockPosition.Left,
    IReadOnlyList<string>? PinOrder = null,
    AppTheme Theme = AppTheme.Dark,
    AppLanguage Language = AppLanguage.Turkish,
    bool AnimationsEnabled = true,
    bool PreviewsEnabled = true,
    bool NotificationBadgesEnabled = true,
    AutoHideMode AutoHide = AutoHideMode.Disabled,
    double SurfaceOpacity = 0.9,
    IReadOnlyList<string>? Widgets = null);
