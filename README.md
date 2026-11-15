# OpenTaskbar

OpenTaskbar is a lightweight Windows taskbar replacement. It overlays the reserved bottom taskbar area with a transparent dock and shows pinned applications as icons.

## Current features

- Shows only pinned applications and activates their existing window, or launches the application when no window is open.
- Uses each pinned executable's embedded application icon and avoids substituting the generic Windows window icon.
- Shows a DWM live thumbnail above a pinned application while its icon is hovered.
- Shrinks the dock to its icon group and places it at the selected left, center, or right position.
- Shows a notification dot on a pinned application's icon when one of its windows raises a Windows system alert; the dot clears when the window becomes foreground.
- Uses a compact black context menu. Pinned applications can be opened, closed gracefully, or force-terminated after confirmation; supported browsers also offer new and private windows.
- Opens dock settings from the right-click menu and saves the left, center, or right alignment in `%LOCALAPPDATA%\OpenTaskbar\settings.json`.
- Stores pinned paths in `%LOCALAPPDATA%\OpenTaskbar\pinned-apps.json`.
- Removes a pin from its context menu.
- Hides the Explorer taskbar while the dock overlays its reserved bottom area, so moving the pointer to the bottom edge does not reveal the old taskbar. It stores the prior taskbar state so normal exit can restore it.
- Uses Windows accessibility events to match pinned applications with open windows and update notification badges. It enumerates windows once at startup and briefly coalesces bursts of change events.
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
- Browser tab favicon badges are private to the browser's page UI and are not exposed as a system notification signal. Supporting them requires an integration such as a browser extension; OpenTaskbar does not infer unread state from screenshots or page content.
- Owned dialogs and untitled windows are omitted from activation and hover previews. A pinned executable with no embedded icon is shown without a generic replacement icon.
- Pins refer to executable paths; moving or removing an executable requires removing and adding the pin again.
- Automatic sign-in startup, adding new pins from the dock, and window grouping are not implemented.

## License

OpenTaskbar is distributed under the MIT License. See [LICENSE](LICENSE).
