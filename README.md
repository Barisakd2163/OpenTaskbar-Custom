# OpenTaskbar

OpenTaskbar is a lightweight, macOS-inspired floating dock and taskbar replacement for Windows 10 and Windows 11. It replaces the default Windows taskbar with a modern floating dock featuring continuous fisheye magnification, live multi-window thumbnail previews, native Jump Lists, drag-and-drop icon reordering, and smart auto-hiding.

---

## Features

- **macOS-Style Floating Dock:** Smooth rounded pill container (`CornerRadius="20"`) with translucent smoked glass material, specular top highlight, and ambient shadow.
- **Continuous Fisheye Magnification:** Smooth parabolic wave scaling across hovered and adjacent icons (up to 1.28x) without layout jitter.
- **Multi-Window Live Thumbnail Previews:** Interactive live DWM window preview cards for open applications with one-click window activation and individual window close controls.
- **Smart Auto-Hide (IntelliHide):** Automatically slides down off-screen when an active or maximized window overlaps the dock, and stays visible on the desktop. Smoothly reveals when the mouse touches the bottom screen edge.
- **Drag-and-Drop Reordering:** Reorder dock icons interactively with persistent order saved automatically.
- **Dock Pinning:** Pin running applications or unpin shortcuts directly from the context menu. Classic taskbar shortcuts are imported from the user's Windows pin folder.
- **Native Windows Jump Lists:** View recent/frequent destinations and published application tasks directly in the context menu via lightweight STA Shell reading.
- **Crash Recovery & State Journaling:** Automatically restores the native Windows taskbar on exit or unexpected shutdown using a local state journal (`taskbar-state.txt`). Reconnects on Explorer restarts (`TaskbarCreated`).
- **Dedicated Settings Window:** Customize dock alignment (Left, Center, Right), theme (Dark/Light), auto-hide mode (Disabled, Smart, Always), animations, previews, and Windows startup.
- **Vector Icons:** Clean vector outline icons (Lucide icon set, 24x24 coordinate space) for context menus and dock controls.

## Requirements

- Windows 10 (version 2004 / build 19041 or later) or Windows 11
- Windows x64 architecture
- .NET 10 Desktop Runtime (or .NET 10 SDK when building from source)

## Run from Source

With the .NET 10 SDK installed:

```powershell
dotnet run --project OpenTaskbar.csproj
```

To build a Release binary:

```powershell
dotnet build OpenTaskbar.csproj -c Release
```

The output executable is located at:
`bin\x64\Release\net10.0-windows\OpenTaskbar.exe`

## Configuration and Storage

- `%LOCALAPPDATA%\OpenTaskbar\settings.json`: User preferences, dock alignment, theme, auto-hide mode, and pin order.
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
- `MainWindow.xaml`: Dock UI surface, Start button, application icon items, and context menus.
- `SettingsWindow.xaml`: Tabbed preferences window.

## License

OpenTaskbar is distributed under the MIT License. See [LICENSE](LICENSE).
