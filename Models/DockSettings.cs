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
public enum DockLayout { Floating, FullWidth }
public enum DockSize { Compact, Medium, Large }

// Missing JSON properties fall back to these constructor defaults, so older settings files stay valid.
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
    DockLayout Layout = DockLayout.Floating,
    DockSize Size = DockSize.Compact,
    bool MagnificationEnabled = true,
    bool ShowClock = true,
    bool ShowMedia = true,
    bool ShowSystemStats = true,
    bool ShowFocus = true,
    bool ShowAudio = true,
    bool ShowQuickSettings = true,
    int FocusMinutes = 25,
    int FocusBreakMinutes = 5,
    int FocusSessions = 4);
