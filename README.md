# OpenTaskbar

OpenTaskbar is a lightweight, macOS-inspired floating dock and taskbar replacement for Windows 10 and Windows 11.

## Features

- **Customizable Dock:** Compact, Medium, and Large size presets with a continuous 75%–150% scale slider, Dark/Light themes, adjustable surface opacity (60%–100%), Left/Center/Right/Full-Width alignment, and Bottom/Left/Right screen edges.
- **Window Management:** Live DWM multi-window thumbnail previews, native Jump Lists, notification badges, drag-and-drop pinning/reordering, and multi-monitor secondary docks.
- **Smart Auto-Hide & Crash Recovery:** Optional auto-hide when windows overlap or when the pointer leaves. Automatically restores the native Windows taskbar on exit or crash via state journaling.
- **Built-in Launcher & Extras:** Quick app/file/settings/calculator launcher (`Alt + Space`), folder stacks with a 2x2 icon preview, Recycle Bin integration, and app profiles.
- **Optional Installable Widgets:** Install, toggle, and reorder widgets from **Settings → Widgets** (Clock & Calendar, Weather, Media Controls with progress and volume, Audio, Quick Settings, System Meters, Pomodoro Focus Timer, Virtual Desktops, Background Tray Apps, Stopwatch, Countdown Timer, Battery, Memory, Storage, and Day Progress).

## Requirements

- Windows 10 (build 19041+) or Windows 11 (x64)
- `.NET 10 SDK` (only required when building from source; the portable release is self-contained)

## Getting Started

### Portable Release

Download `OpenTaskbar.exe` from [GitHub Releases](https://github.com/alztrk/opentaskbar/releases) and run it directly.

### Build from Source

```powershell
# Run from source
dotnet run --project OpenTaskbar.csproj

# Run in preview mode with Settings open (without hiding the Windows taskbar)
dotnet run --project OpenTaskbar.csproj -- --preview --settings

# Build self-contained portable executable (outputs dist\OpenTaskbar.exe)
powershell -ExecutionPolicy Bypass -File ".\scripts\publish-portable.ps1"

# Run behavior tests
dotnet run --project tests/OpenTaskbar.Tests/OpenTaskbar.Tests.csproj
```

## Configuration

Settings and logs are stored in `%LOCALAPPDATA%\OpenTaskbar\`:

- `settings.json` – User preferences, pinned app order, and installed widgets
- `taskbar-state.txt` – Native taskbar recovery journal
- `log.txt` – Diagnostic log

## License

Distributed under the MIT License. See [LICENSE](LICENSE).
