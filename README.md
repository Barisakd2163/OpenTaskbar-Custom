# OpenTaskbar

OpenTaskbar is a lightweight, macOS-inspired floating dock and taskbar replacement for Windows 10 and Windows 11. It replaces the default Windows taskbar with a modern floating dock featuring continuous fisheye magnification, live multi-window thumbnail previews, native Jump Lists, drag-and-drop icon reordering, and smart auto-hiding.

---

## Features

- **macOS-Style Floating Dock:** Smooth rounded pill container with high-resolution application icons (extracted at up to 128x128) in three sizes: Compact (28 DIP icons, fits the Windows taskbar height, default), Medium (34 DIP), and Large (42 DIP). Translucent smoked glass material, specular top highlight, and ambient shadow.
- **Continuous Fisheye Magnification:** Smooth parabolic wave scaling across hovered and adjacent icons (up to 1.28x, bounded by the current cell dimensions) expanding into transparent headroom without layout jitter. Application icons retain a fixed 42 DIP base size in 52 DIP cells when the dock narrows or widgets are added; hover magnification remains transient and can be turned off separately from other animations.
- **Multi-Window Live Thumbnail Previews:** Interactive live DWM window preview cards for open applications with one-click window activation and individual window close controls.
- **Smart Auto-Hide (IntelliHide):** Automatically slides down off-screen when an active or maximized window overlaps the dock, and stays visible on the desktop. Smoothly reveals when the mouse touches the bottom screen edge. While hidden, only a 3 DIP edge strip receives input, so windows underneath stay clickable.
- **Reserved Desktop Space:** With auto-hide disabled, the dock queries Windows for the existing bottom reservation and adds only the missing shelf height. Maximized windows stop above the shelf. The separate hover headroom does not enlarge this reservation. Auto-hide releases the additional reservation and keeps the hidden shelf transparent to pointer input outside its edge trigger. Closing the dock unregisters its reservation and restores the original Windows taskbar state.
- **Drag-and-Drop Reordering:** Reorder dock icons interactively with persistent order saved automatically.
- **Dock Pinning:** Pin running applications or unpin shortcuts directly from the context menu. Classic taskbar shortcuts are imported from the user's Windows pin folder.
- **Native Windows Jump Lists:** View recent/frequent destinations and published application tasks directly in the context menu via lightweight STA Shell reading.
- **Alignment and Screen Edge:** Settings → Appearance → Dock alignment picks Left, Center (default), Right, or Full width (edge to edge, widgets pinned to the far end). Screen edge puts the dock at the bottom, or in a full-height column on the left or right (side docks show apps only; widgets need the bottom shelf).
- **Every Display:** Other monitors get a slim dock on their taskbar strip listing the windows on that display, with a clock.
- **Launcher:** Alt+Space (configurable) opens a search box for every installed desktop and Store app, Windows settings pages, recent files, and arithmetic (Enter copies the result), with hand-offs to Windows file search and the web. The Start button can open it instead of the Windows Start menu, and can sit first, last, or be hidden.
- **Keyboard and Mouse:** Ctrl+Alt+D moves keyboard focus to the dock (arrows, Home/End, Enter, Esc). Shift+click or middle-click opens a new window, Ctrl+Shift+click runs as administrator. The wheel cycles an app's windows over its icon and changes the volume elsewhere on the dock.
- **Folder Stacks and Recycle Bin:** Folders (Downloads by default) open as a grid of their newest files and badge new arrivals. The Recycle Bin shows full/empty, accepts dropped files, and empties with Windows' own confirmation.
- **Profiles:** Named views (e.g. Work, Gaming) that hide pinned apps from the dock without removing the Windows pins.
- **Virtual Desktops:** A widget with previous/next/new desktop and Task View; optionally list only windows on the current desktop.
- **Backup and Updates:** Export/import settings plus pinned apps as one JSON file. A daily check of GitHub releases (`alztrk/opentaskbar`) announces new versions; nothing is downloaded automatically.
- **Dock Widgets:** Each can be turned on in Settings → Widgets.
  - Media controls for the current system media session (title, artist, artwork, previous/play-pause/next).
  - Battery, processor, and memory meters.
  - A Pomodoro-style focus timer with configurable focus, break, and session counts.
  - The default sound device, with its volume and mute.
  - Wi-Fi and Bluetooth toggles.
  - Shortcuts for display projection, mobile hotspot, Quick Settings, and the notification center.
  - Clock and date; clicking opens a month calendar.
  - Weather from Open-Meteo for a city set in Settings, or Windows location when none is set.
  - Background apps: running apps that own a notification-area icon.
- **Tray Replacement:** The Windows notification area is hidden with the taskbar. The widgets open Windows' own flyouts (Quick Settings, notifications, projection). Live tray icons are not hosted: the Background apps widget lists running tray apps from Windows' own records and reopens them, which brings up the window of most single-instance apps.
- **Crash Recovery & State Journaling:** Restores the native Windows taskbar, including secondary-monitor taskbars, on exit, session end, or unhandled errors. It uses a local state journal (`taskbar-state.txt`). If the process is killed outright, the next launch restores it. Reconnects on Explorer restarts (`TaskbarCreated`).
- **Dedicated Settings Window:** A compact window with custom title-bar controls. Customize alignment (Left, Center, Right), theme (Dark/Light), auto-hide, animations, previews, and Windows startup. General settings fit at the default size; smaller windows keep all options reachable by scrolling.
- **Optional Dock Widgets:** Search the Settings catalog and install, hide, or remove widgets. New installations start with no widgets enabled. The catalog includes local time, date, battery status, physical memory use, available system-drive space (GiB), elapsed day percentage, a stopwatch, and a countdown timer. System values refresh every 15 seconds and follow the selected Turkish or English language. Unreadable system data is shown as unavailable. The stopwatch supports start/pause, resume, and reset; while visible and running it refreshes every second. Both time widgets share one refresh clock while any visible time widget is running. Their session state survives visibility changes and resets when the app exits. Time widgets show a large value and one start/pause action. Click the title or value to open the shared details panel; an unconfigured timer's quick action opens it too. Enter 1–1440 minutes in the panel; no duration is prefilled. Invalid input displays an error and disables Start. The panel also provides Reset, closes with Escape or an outside click, and keeps auto-hide from dismissing the dock while open. Completion is shown in the tile title and as a live status in the panel.
- **Resizable Dock With Scrolling:** Drag either dock edge to set its width while the opposite edge stays fixed. The minimum surface width fits one complete application cell. Icons keep their size as content overflows. Hold Shift or Ctrl while using the mouse wheel, or drag with the middle button, to move horizontally. The scrollbar is hidden, and the outside left/right border areas provide resizing without decorative handles.
- **Glass & Surface Opacity:** Settings use native Desktop Acrylic on Windows 11 build 22621 or later. Adjust background opacity from 60–100% for settings, the dock, and context menus without fading text or icons. Changes preview live, save on Apply, and revert when discarded. Unsupported Windows versions and high-contrast mode use an opaque settings surface.
- **Vector Icons:** Clean vector outline icons (Lucide icon set, 24x24 coordinate space) for context menus and dock controls.

## Requirements

- Windows 10 (version 2004 / build 19041 or later) or Windows 11
- Windows x64 architecture
- The release `OpenTaskbar.exe` is self-contained and needs no .NET installation. Building from source requires the .NET 10 SDK.

## Run the Release

1. Download the latest `OpenTaskbar.exe` asset from the [GitHub Releases](https://github.com/alztrk/opentaskbar/releases) page.
2. Save it in a folder you control and run it. Installation is not required.
3. OpenTaskbar replaces the Windows taskbar with a floating dock.

## Run from Source

With the .NET 10 SDK installed:

```powershell
dotnet run --project OpenTaskbar.csproj
```

For visual development without replacing the native Windows taskbar:

```powershell
dotnet run --project OpenTaskbar.csproj -- --preview --settings
```

Preview runs the same dock and settings UI with live local data in a separately discoverable window. It can coexist with the regular dock and uses `%LOCALAPPDATA%\OpenTaskbar\preview-settings.json`, a separate empty-by-default configuration. `--settings` opens Settings on startup and also works in regular mode. Only one instance of each mode can run at a time.

To inspect the real taskbar replacement workflow, use `--inspect --settings` instead. This uses the normal settings file and native taskbar replacement; the dock remains visible in Alt+Tab so desktop inspection tools can target it. Exit OpenTaskbar to restore the native Windows taskbar before switching between regular and inspection launches. Inspection shares the regular instance lock.

To create a self-contained portable executable:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\publish-portable.ps1"
```

The output is written to `dist\OpenTaskbar.exe`.

## Behavior Checks

The dependency-free Windows check project uses a controlled `TimeProvider` for deterministic timer and stopwatch checks, plus isolated temporary files for settings round trips. WPF component checks measure and render the real widget controls without opening desktop windows. Test source files are excluded from the application build.

```powershell
dotnet run --project tests/OpenTaskbar.Tests/OpenTaskbar.Tests.csproj
```

These checks cover state transitions, duration boundaries, completion, independent widget state, session retention, localization, settings validation, widget actions, narrow catalog layouts, and text contrast in both themes. Dock checks exercise rendered hover/bounce clipping, horizontal scrolling, and reservation calculations at 100–200% DPI. Native popup focus, outside-click dismissal, screen-edge placement, and taskbar behavior still require desktop inspection.

See [CHANGELOG.md](CHANGELOG.md) for release changes.

## Configuration and Storage

- `%LOCALAPPDATA%\OpenTaskbar\settings.json`: Preferences, layout, alignment, edge, theme, auto-hide, pin order, enabled widgets (`Widgets`), installed widgets (`InstalledWidgets`), widget visibility, focus timer lengths, hotkeys, stacks, profiles, weather city, and `SurfaceOpacity` (0.6–1.0, default 0.9). Settings missing from older files receive their defaults.
- The first launch uses the Windows UI language: Turkish for Turkish system cultures, otherwise English, the other supported language. This also applies to older settings files without a `Language` property. An explicitly saved language always takes precedence; change it in Settings to override the initial selection. An invalid stored language remains a settings error.
- `%LOCALAPPDATA%\OpenTaskbar\taskbar-state.txt`: Safety journal that preserves the original Windows taskbar state for crash recovery.
- `%LOCALAPPDATA%\OpenTaskbar\log.txt`: Diagnostic log (startup, errors, crash recovery). Rotated to `log.old.txt` above 1 MB.
- `Software\Microsoft\Windows\CurrentVersion\Run\OpenTaskbar`: Current-user Windows registry key used when "Start with Windows" is enabled.

## Project Structure

- `Design/`: Shared XAML styles, materials, typography, widget styles (`WidgetControls.xaml`, `Widgets.xaml`), and dark/light color palettes (`Palettes/`).
- `Models/`: Application data structures (`DockApp.cs`, `DockSettings.cs`, `TrackedWindow.cs`, `ApplicationJumpList.cs`).
- `Widgets/`: Dock widgets (`DockWidget` base class, `WidgetCoordinator`, `RingMeter`, and one control per widget).
- `Views/Widgets/`: Compact time tiles, shared details panel, and popup lifetime.
- `Services/`:
  - `TaskbarReplacementSession.cs`: Native and secondary-monitor taskbar suppression, AppBar registration, full-screen detection, and monitor layout.
  - `DockMetrics.cs`: Single source of dock geometry and motion values, exposed to XAML through `Design/Theme.xaml`.
  - `AppLog.cs`: File logging for diagnostics and crash recovery.
  - `CoreAudio.cs`, `SystemStats.cs`, `ShellShortcuts.cs`: Default sound device, system meters, and Windows flyout/settings shortcuts used by widgets.
  - `DockAutoHideController.cs`: Smart auto-hide, window overlap evaluation, and bottom edge trigger.
  - `WindowThumbnailPreview.cs`: DWM live multi-window thumbnail strip and preview cards.
  - `NativeWindowTracker.cs`: Win32 window event hooks and alert state tracking.
  - `ApplicationJumpListService.cs`: Background Jump List reader and task invocation.
  - `WindowsTaskbarPinImporter.cs`: Windows shortcut file importer and resolver.
  - `DockSettingsStore.cs` & `ApplicationPreferences.cs`: Settings persistence and state management.
  - `DockWidgetService.cs`: Widget catalog and local system data providers.
  - `UiText.cs` & `ThemeManager.cs`: Turkish/English interface text and runtime theme/opacity materials.
- `MainWindow.xaml`: Dock UI surface, Start button, application icon items, and context menus.
- `SettingsWindow.xaml`: Tabbed preferences window.

## License

OpenTaskbar is distributed under the MIT License. See [LICENSE](LICENSE).
