# OpenTaskbar

OpenTaskbar is a lightweight Windows taskbar replacement. It overlays the reserved bottom taskbar area with a transparent dock and shows Windows-pinned applications plus currently open applications as icons.

## Current features

- Includes a Windows Start button, subtle pointer magnification for application icons, and a short live-preview entrance animation. Motion follows the Windows client-area animation setting.
- Shows Windows-pinned applications and open applications without duplicating an application that is already pinned.
- Hides the replacement strip, previews, and menus while a foreground application occupies its monitor in full screen, and restores the strip after focus returns to a regular window.
- Shows new app icons fully visible on their first rendered frame with a short scale transition; closing icons fade and scale out. Motion follows the Windows client-area animation setting.
- Uses each application's executable icon and avoids shortcut-arrow overlays or generic Windows window icons.
- Shows a DWM live thumbnail above an open application while its icon is hovered; clicking the preview activates its window, while the top-right close control sends that window a normal close request.
- Clicking an application's dock icon activates it, or minimizes its foreground window when the same application is already active.
- Sizes the visible dock to its icon and status content, without a scroll bar or whole-interface scaling, and places it at the selected left, center, or right position within the measured taskbar strip. The remaining strip also accepts the dock's right-click settings menu.
- Shows a notification dot on an application's icon when one of its windows raises a Windows system alert; the dot clears when the window becomes foreground.
- Uses separate translucent context menus for applications and the dock surface. Application menus include icons and only application commands; the dock menu contains alignment settings. Applications can be opened, closed gracefully, or force-terminated after confirmation; pinned shortcuts can be removed.
- Reads the application's real Windows Jump List when its context menu opens. Published tasks, custom categories, pinned destinations, and available recent/frequent destinations appear directly in the application context menu under non-interactive section headings, without additional Jump List submenus. Shell items retain their original commands and resource icons; browser commands are no longer guessed from executable names.
- Includes real application menu-bar and system-menu commands when an application exposes standard Win32 menus. Applications that draw private menus do not expose those commands through this integration.
- Reads classic application shortcuts from the current user's Windows taskbar pin folder at startup, so the dock follows the Windows pin list without keeping a separate copy.
- Opens dock settings from the right-click menu and saves the left, center, or right alignment in `%LOCALAPPDATA%\OpenTaskbar\settings.json`.
- Hides the Explorer taskbar while the dock overlays its reserved bottom area, so moving the pointer to the bottom edge does not reveal the old taskbar. It stores the prior taskbar state so normal exit can restore it.
- Choose “OpenTaskbar’dan çık” from the dock surface menu to exit and show the native Windows taskbar with its previous auto-hide state. Restoration disables replacement callbacks before changing Shell state and also runs on application exit, Windows session ending, and managed process exit. Forceful process termination cannot execute these callbacks; the recovery record remains for the next startup.
- Uses Windows accessibility events to match eligible open windows with application icons and update notification badges. It omits owned and tool windows, Windows system-app helper windows, and Explorer shell windows that are not File Explorer windows, and briefly coalesces bursts of change events.
- Runs without administrator rights or network access. Taskbar replacement changes only the current user's Windows taskbar state.

## Interface system

Shared interface resources are defined in `Design/Theme.xaml`. See [the interface rules](Design/README.md) for dimensions, states, materials, and validation limits. The dock uses translucent tinted glass; desktop backdrop blur is not currently implemented.

## Performance approach

The app does no periodic polling while idle. It does not load system telemetry, weather, media, finance, or clipboard modules. Window show, destroy, foreground, and previously untracked window-title events request a refresh on the next input-priority dispatcher turn. Other changes share a one-shot 60 ms timer that is not restarted by later events, so a stream of events cannot continually postpone the refresh. Actual display latency still depends on Windows scheduling and icon extraction. No third-party packages are required.

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
- Owned dialogs, tool windows, Windows system-app helpers, Explorer shell windows, and untitled windows are omitted from the dock and hover previews. User-opened File Explorer windows remain eligible; a pinned executable with no embedded icon is shown without a generic replacement icon.
- Pins come from Windows shortcut files; moving or removing an executable requires updating its Windows taskbar shortcut.
- Open windows are shown even when they are not pinned. Packaged Windows taskbar pins stored only as application IDs are not imported unless that application is running.
- Automatic sign-in startup, adding new pins from the dock, and window grouping are not implemented.
- Jump List tasks and custom categories require undocumented x64 Windows Shell interfaces. The local Windows 11 build supports the implemented interface versions; other builds can show an explicit unsupported/unavailable state. Windows updates may change these interfaces. Applications that publish no Jump List show an empty state, and private in-app menus are not extracted.
- Jump Lists are read on demand on a dedicated STA thread, with a three-second UI timeout and bounded category/item counts. Cancellation cannot interrupt an already-running native COM call, but late results are discarded and native objects are released on that thread. Task execution and packaged-app activation require interactive Windows verification.

## License

OpenTaskbar is distributed under the MIT License. See [LICENSE](LICENSE).
