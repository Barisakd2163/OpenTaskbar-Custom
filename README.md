# OpenTaskbar

OpenTaskbar is a lightweight, macOS-inspired floating dock and taskbar replacement for Windows 10 and Windows 11. It replaces the default Windows taskbar with a modern floating dock featuring continuous fisheye magnification, live multi-window thumbnail previews, native Jump Lists, drag-and-drop icon reordering, and smart auto-hiding.

---

## Features

- **macOS-Style Floating Dock:** Smooth rounded pill container (`CornerRadius="18"`) with prominent high-resolution application icons (42 DIP base, extracted at up to 128x128), translucent smoked glass material, specular top highlight, and ambient shadow.
- **Continuous Fisheye Magnification:** Smooth parabolic wave scaling across hovered and adjacent icons (up to 1.28x, bounded by the current cell dimensions) without layout jitter. All app icons share the same square display size and shrink together when cells compress.
- **Multi-Window Live Thumbnail Previews:** Interactive live DWM window preview cards for open applications with one-click window activation and individual window close controls.
- **Smart Auto-Hide (IntelliHide):** Automatically slides down off-screen when an active or maximized window overlaps the dock, and stays visible on the desktop. Smoothly reveals when the mouse touches the bottom screen edge.
- **Drag-and-Drop Reordering:** Reorder dock icons interactively with persistent order saved automatically.
- **Dock Pinning:** Pin running applications or unpin shortcuts directly from the context menu. Classic taskbar shortcuts are imported from the user's Windows pin folder.
- **Native Windows Jump Lists:** View recent/frequent destinations and published application tasks directly in the context menu via lightweight STA Shell reading.
- **Crash Recovery & State Journaling:** Automatically restores the native Windows taskbar on exit or unexpected shutdown using a local state journal (`taskbar-state.txt`). Reconnects on Explorer restarts (`TaskbarCreated`).
- **Dedicated Settings Window:** A compact window with custom title-bar controls. Customize alignment (Left, Center, Right), theme (Dark/Light), auto-hide, animations, previews, and Windows startup. General settings fit at the default size; smaller windows keep all options reachable by scrolling.
- **Optional Dock Widgets:** Search the Settings catalog and install, hide, or remove widgets. New installations start with no widgets enabled. The catalog includes local time, date, battery status, physical memory use, available system-drive space (GiB), elapsed day percentage, a stopwatch, and a countdown timer. System values refresh every 15 seconds and follow the selected Turkish or English language. Unreadable system data is shown as unavailable. The stopwatch supports start/pause, resume, and reset; while visible and running it refreshes every second. Both time widgets share one refresh clock while any visible time widget is running. Their session state survives visibility changes and resets when the app exits. Time widgets show a large value and one start/pause action. Click the title or value to open the shared details panel; an unconfigured timer's quick action opens it too. Enter 1–1440 minutes in the panel; no duration is prefilled. Invalid input displays an error and disables Start. The panel also provides Reset, closes with Escape or an outside click, and keeps auto-hide from dismissing the dock while open. Completion is shown in the tile title and as a live status in the panel.
- **Resizable Dock With Scrolling:** Drag either dock edge to set its width while the opposite edge stays fixed. When pinned apps and widgets exceed that width, the dock keeps them reachable through its thin horizontal scrollbar. Resize handles have a separate gutter beside the scrollable content.
- **Glass & Surface Opacity:** Settings use native Desktop Acrylic on Windows 11 build 22621 or later. Adjust background opacity from 60–100% for settings, the dock, and context menus without fading text or icons. Changes preview live, save on Apply, and revert when discarded. Unsupported Windows versions and high-contrast mode use an opaque settings surface.
- **Vector Icons:** Clean vector outline icons (Lucide icon set, 24x24 coordinate space) for context menus and dock controls.

## Requirements

- Windows 10 (version 2004 / build 19041 or later) or Windows 11
- Windows x64 architecture
- .NET 10 Desktop Runtime (or .NET 10 SDK when building from source)

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

These checks cover state transitions, duration boundaries, completion, independent widget state, session retention, localization, settings validation, and widget component actions and layout. Native popup focus, outside-click dismissal, screen-edge placement, and taskbar behavior still require desktop inspection.

## Configuration and Storage

- `%LOCALAPPDATA%\OpenTaskbar\settings.json`: Preferences, alignment, theme, auto-hide, pin order, enabled widgets (`Widgets`), installed widgets (`InstalledWidgets`), and `SurfaceOpacity` (0.6–1.0, default 0.9). Legacy explicit widget selections migrate to installed and enabled widgets. Enabled widgets must be a subset of installed widgets. Apply saves catalog changes; closing with pending changes asks whether to discard them.
- The first launch uses the Windows UI language: Turkish for Turkish system cultures, otherwise English, the other supported language. This also applies to older settings files without a `Language` property. An explicitly saved language always takes precedence; change it in Settings to override the initial selection. An invalid stored language remains a settings error.
- `%LOCALAPPDATA%\OpenTaskbar\taskbar-state.txt`: Safety journal that preserves the original Windows taskbar state for crash recovery.
- `Software\Microsoft\Windows\CurrentVersion\Run\OpenTaskbar`: Current-user Windows registry key used when "Start with Windows" is enabled.

## Project Structure

- `Design/`: Shared XAML styles, materials, typography, and dark/light color palettes (`Palettes/`).
- `Models/`: Application data structures (`DockApp.cs`, `DockSettings.cs`, `WindowInfo.cs`).
- `Services/`:
  - `TaskbarReplacementSession.cs`: Native Windows taskbar suppression, AppBar registration, and monitor layout.
  - `DockAutoHideController.cs`: Smart auto-hide, window overlap evaluation, and bottom edge trigger.
  - `WindowThumbnailPreview.cs`: DWM live multi-window thumbnail strip and preview cards.
  - `NativeWindowTracker.cs`: Win32 window event hooks and alert state tracking.
  - `ApplicationJumpListService.cs`: Background Jump List reader and task invocation.
  - `WindowsTaskbarPinImporter.cs`: Windows shortcut file importer and resolver.
  - `DockSettingsStore.cs` & `ApplicationPreferences.cs`: Settings persistence and state management.
  - `DockWidgetService.cs`: Widget catalog and local system data providers.
- `Views/Widgets/`: Compact time tiles, shared details panel, and popup lifetime.
- `Design/WidgetControls.xaml`: Widget templates, control styles, and sizing tokens.
- `MainWindow.xaml`: Dock UI surface, Start button, application icon items, and context menus.
- `SettingsWindow.xaml`: Tabbed preferences window.

## License

OpenTaskbar is distributed under the MIT License. See [LICENSE](LICENSE).
