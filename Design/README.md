# OpenTaskbar interface system

`Theme.xaml` owns the shared material, color, type, and size resources. The dock, menus, tooltips, and preview use these resources; there are no additional UI packages.

## Visual rules

- Preserve the native colors of application icons. Use neutral charcoal surfaces and restrained white highlights around them.
- Use Segoe UI for Windows menu familiarity, small-size readability, and Turkish glyph coverage. Body text is 13 DIPs; secondary labels are 12 DIPs.
- Size the transparent input window to the monitor's entire reserved bottom taskbar strip. Read its height from the monitor work area; when Explorer exposes no reserved strip, use the previously measured horizontal native taskbar height (48 DIPs before a native measurement is available).
- The dock floats above the monitor's bottom edge as a pill/shelf container with an 18 DIP corner radius and a 6–10 DIP bottom clearance. An ambient drop shadow and a 1 DIP specular top-edge highlight (`#4DFFFFFF`) provide depth over wallpapers.
- Derive cell height and icon size from the available surface height, capped at 28 DIPs for icons. Reserve a separate 6 DIP row for status markers. Keep preferred cell widths at 44 DIPs; reduce cell widths only when the application count requires it.
- A muted 4 DIP circular dot means running; an illuminated bright dot means foreground. A red badge in the upper right means a Windows system alert.
- Hover and press change the surface without changing layout. Disable the default focus adorner and use a dark slate fill for keyboard focus; application buttons never add a white outline.
- Pointer magnification applies a continuous parabolic/cosine fisheye wave across hovered and neighboring icons (up to 1.36x peak) anchored at their base (`0.5, 0.95`), keeping status dots stationary. Disable magnification when Windows client-area animation is disabled.
- The material is translucent tinted smoked glass with a specular top-edge reflection. It does not implement desktop backdrop blur. The previous unverified DWM Acrylic request has been removed.
- Size the visible surface to content including border and insets. Empty status text consumes no width. Do not use a Viewbox or scale the entire UI after layout changes. The full reserved strip accepts the dock context menu, including outside the visible surface.

## Interaction rules

- Update notification and foreground state on existing application objects. Do not recreate buttons or replay entry animations for state changes.
- Discover shown/foreground windows on the next dispatcher turn. Coalesce other events without resetting their 60 ms timer. Publish the fresh window snapshot before refreshing foreground-derived state. Foreground-only changes update indicators without rebuilding the application list.
- New icons start fully opaque; retain only their brief scale entrance. Cancelling an exit must always restore opacity and hit testing, even if the button currently has an activation transform instead of its scale transform.
- Use short opacity and scale transitions only for application arrival/removal. Respect the Windows client-area animation preference.
- Preview entrance fades and scales the live DWM thumbnail from 95% over 160 ms with cubic ease-out; the owned header fades over the same interval. Stop the entrance timer on completion, failure, replacement, hide, and disposal. Reduced motion shows the preview immediately.
- The first cell is Windows Start. It sends the documented `WM_SYSCOMMAND / SC_TASKLIST` command to activate the native Start menu; it does not emulate a keyboard chord.
- Render the Start logo as four blue square panes instead of using a private-use font glyph.
- Application and dock context menus are separate resources. Application menus contain only application actions; dock alignment is available only from the surface menu. Use vector outline icons (Lucide icon set, 24x24 coordinate space, 1.6 DIP stroke width with rounded caps) in a stable leading column and a translucent charcoal menu material.
- Where exposed by the application, read real Win32 menu-bar and system-menu commands into separate submenus. Bound menu reads to five levels and 100 entries, and owner initialization to 80 ms. Validate the original menu/command before dispatch; omit unsupported, owner-drawn, or inaccessible entries. Private application UI is not extracted.
- Populate Jump List sections directly in the application context menu, before the standard application actions. Use non-interactive category headings and separators instead of nested Jump List submenus. Replace previous entries on each load; keep loading, empty, unmatched identity, unsupported platform, timeout, and read failure states in the same root menu. Resolve the selected window's explicit AppUserModelID or use the Shell application resolver; resolve a stopped pinned app through its shortcut. Read native categories/tasks and automatic destinations on a lazy STA worker. Limit reads to 32 native categories, 40 entries per group, and 100 commands overall.
- Keep original Jump List Shell objects alive only for the current menu session. A selected command is queued before closing the menu, then invoked through the original Shell default command. Packaged task links use their AppsFolder target and published activation context. Dispose stale/cancelled sessions on the worker; never reconstruct guessed browser arguments or log task titles, destinations, or launch arguments. Resource icons are optional and frozen before crossing threads.
- Hide the strip and close previews/menus when the foreground application covers its monitor. React to AppBar full-screen notifications, foreground changes, and debounced foreground geometry changes. A maximized work-area window and a full-screen window on another monitor do not suppress this strip.
- Menus use WPF outside-click dismissal, receive foreground focus when opened, and close when a different process receives foreground activation. Track only one open root menu.
- Application clicks receive a 420 ms damped vertical launch bounce animation (-14 DIP peak) on the icon's translate transform. Restoring a minimized window uses Windows' native restore path. Custom animation of another process's already-visible window is not implemented: `AnimateWindow` requires ownership by the calling thread.
- Mouse clicks on dock/preview surfaces do not steal foreground activation before the command runs. Explicit application commands activate their selected target.
- Snapshot the foreground application on preview mouse-down, before button focus and thumbnail dismissal. Use that snapshot to select and minimize the active window. Ignore OpenTaskbar-owned foreground events when remembering the last external window. Minimize with `ShowWindowAsync`; success means Windows accepted the request, not that the transition has already completed. Clear pointer snapshots on keyboard input.
- Show window title and application icon in preview cards. Reserve thumbnail space below them. Multiple open windows of the same application are shown side-by-side in interactive preview cards. Its close button sends a normal close request only to the previewed window.
- Hide the thumbnail before opening an application context menu. Disable the redundant name tooltip while a window preview is available.
- Exit from the dock surface menu closes the main window. Disable hidden-state enforcement before restoring Explorer so reentrant Shell notifications cannot hide it again. Preserve the session and recovery record on restoration failure; ordinary window close remains cancellable. Reuse the restoration operation for application exit, session ending, and managed process exit. Force-kill termination cannot run in-process cleanup.

## Verification limits

Release compilation checks XAML and C# consistency. Startup checks show process liveness only. A local cropped desktop capture verified normal strip placement with a bottom gap; Computer Use was not used. Full-screen transitions, menu command execution, pointer/keyboard input, multiple-monitor DPI, dense application overflow, and high-contrast mode still require rendered Windows verification.

A read-only Jump List diagnostic on local Windows 11 retrieved two groups with ten entries for LibreWolf and two groups with nine entries for ChatGPT. It did not invoke any command or log destination titles. This verifies native reading on that build, not interactive command execution or compatibility with every Windows version.

## Platform references

- [Apple Desktop & Dock settings](https://support.apple.com/en-gb/guide/mac-help/mchlp1119/mac): pointer magnification behavior. OpenTaskbar uses a restrained interpretation that fits its taskbar strip.
- [Microsoft WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand): native Start command.
- [Microsoft DWM thumbnail properties](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_thumbnail_properties): thumbnail destination and opacity used by the entrance animation.
- [Open-Shell Jump List implementation](https://github.com/Open-Shell/Open-Shell-Menu/blob/master/Src/StartMenu/StartMenuDLL/JumpLists.cpp): reference for undocumented destination-list COM interface layouts and packaged task activation.
- [System Informer application resolver definitions](https://github.com/winsiderss/systeminformer/blob/master/phlib/include/appresolverp.h): reference for the native application identity resolver ABI.
- [Microsoft IContextMenu::InvokeCommand](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-icontextmenu-invokecommand): invoking Shell commands and Unicode command parameters.
