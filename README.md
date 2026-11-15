# OpenTaskbar

OpenTaskbar is a lightweight Windows dock prototype. It keeps the Windows taskbar visible and provides a compact place to launch pinned applications and switch to open windows.

## Current features

- Lists visible top-level windows with titles and activates them when selected.
- Pins executable files selected by the user and launches them on click.
- Stores pinned paths in `%LOCALAPPDATA%\OpenTaskbar\pinned-apps.json`.
- Removes a pin from its context menu.
- Uses Windows accessibility events to update the open-window list. It enumerates windows once at startup and briefly coalesces bursts of change events.
- Runs without administrator rights or network access.

## Performance approach

The app does no periodic polling while idle. It does not load system telemetry, weather, media, finance, or clipboard modules. The only refresh timer is a one-shot 180 ms debounce after Windows reports a window change. No third-party packages are required.

No memory or CPU benchmark has been recorded yet. Resource targets should be measured on supported Windows versions before being published as claims.

## Requirements

- Windows 10 or Windows 11, x64
- .NET 10 Desktop Runtime to run the framework-dependent build
- .NET 10 SDK to build (includes the runtime)

## Build and run

```powershell
dotnet build OpenTaskbar.csproj -c Release
dotnet run --project OpenTaskbar.csproj
```

## Current limits

- The Windows taskbar remains visible; this prototype does not alter Windows taskbar settings or Explorer state.
- The dock is positioned on the primary monitor.
- Open windows are shown by title rather than app icons. Owned dialogs and untitled windows are omitted.
- Pins refer to executable paths; moving or removing an executable requires removing and adding the pin again.
- Automatic startup, taskbar hiding, drag-and-drop pinning, multi-monitor placement, and window grouping are not implemented.

## License

OpenTaskbar is distributed under the MIT License. See [LICENSE](LICENSE).
