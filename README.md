# OpenTaskbar

OpenTaskbar is a lightweight Windows taskbar replacement. It starts as a transparent bottom-edge AppBar, hides the Windows taskbar, and shows pinned and open applications as icons.

## Current features

- Lists visible top-level windows with application icons and activates them when selected.
- Shrinks the dock to its icon group and places it at the selected left, center, or right position.
- Shows a notification dot for windows that raise a Windows system alert and clears it when that window becomes foreground.
- Shows pinned applications as icons and launches them on click.
- Opens dock settings from the right-click menu and saves the left, center, or right alignment in `%LOCALAPPDATA%\OpenTaskbar\settings.json`.
- Stores pinned paths in `%LOCALAPPDATA%\OpenTaskbar\pinned-apps.json`.
- Removes a pin from its context menu.
- Registers the dock as a bottom-edge Windows AppBar on launch and hides the Explorer taskbar while the dock is active, so moving the pointer to the bottom edge does not reveal the old taskbar. It stores the prior taskbar state so normal exit can restore it.
- Uses Windows accessibility events to update the open-window list. It enumerates windows once at startup and briefly coalesces bursts of change events.
- Runs without administrator rights or network access. Taskbar replacement changes only the current user's Windows taskbar state.

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

- Taskbar replacement depends on Windows Explorer's AppBar and auto-hide APIs. A recovery record at `%LOCALAPPDATA%\OpenTaskbar\taskbar-state.txt` preserves the original Windows taskbar state after an unexpected exit.
- The dock is positioned at the bottom edge of the monitor containing its window; its icon group can align left, center, or right.
- Applications do not expose a universal unread count or notification badge API, so notification dots appear only when Windows raises a system alert for a window.
- Owned dialogs and untitled windows are omitted. An application icon may be unavailable when Windows denies process access and the window does not provide one.
- Pins refer to executable paths; moving or removing an executable requires removing and adding the pin again.
- Automatic sign-in startup, adding new pins from the dock, and window grouping are not implemented.

## License

OpenTaskbar is distributed under the MIT License. See [LICENSE](LICENSE).
