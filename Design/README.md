# OpenTaskbar interface system

`Theme.xaml` owns the shared material, color, type, and size resources. The dock, menus, tooltips, and preview use these resources; there are no additional UI packages.

## Visual rules

- Preserve the native colors of application icons. Use neutral charcoal surfaces and restrained white highlights around them.
- Use Segoe UI for Windows menu familiarity, small-size readability, and Turkish glyph coverage. Body text is 13 DIPs; secondary labels are 12 DIPs.
- Size the transparent input window to the monitor's entire reserved bottom taskbar strip. Read its height from the monitor work area; when Explorer exposes no reserved strip, use the previously measured horizontal native taskbar height (48 DIPs before a native measurement is available).
- Derive equal top/bottom gaps from 8% of strip height, bounded to 2–6 DIPs. At 48 DIPs, the surface is 40 DIPs high with 4 DIP gaps. Keep 18 DIP internal horizontal padding and 12 DIP external edge clearance for surface context-menu access.
- Derive cell height and icon size from the available surface height, capped at 28 DIPs for icons. Reserve a separate 5 DIP row for status markers. Keep preferred cell widths at 44 DIPs; reduce cell widths only when the application count requires it.
- A muted dot means running; a bright 16 DIP line means foreground. A red badge in the upper right means a Windows system alert.
- Hover and press change the surface without changing layout. Disable the default focus adorner and use a dark slate fill for keyboard focus; application buttons never add a white outline.
- Pointer magnification scales only the hovered icon layer to 1.22 over 120 ms. Neighbors stay at their normal size. Keep hit targets, status markers, dock width, and window position unchanged. Disable magnification when Windows client-area animation is disabled.
- The material is translucent tinted glass with a subtle vertical light change. It does not implement desktop backdrop blur. The previous unverified DWM Acrylic request has been removed.
- Size the visible surface to content including border and insets. Empty status text consumes no width. Do not use a Viewbox or scale the entire UI after layout changes. The full reserved strip accepts the dock context menu, including outside the visible surface.

## Interaction rules

- Update notification and foreground state on existing application objects. Do not recreate buttons or replay entry animations for state changes.
- Use short opacity and scale transitions only for application arrival/removal. Respect the Windows client-area animation preference.
- Preview entrance fades and scales the live DWM thumbnail from 95% over 160 ms with cubic ease-out; the owned header fades over the same interval. Stop the entrance timer on completion, failure, replacement, hide, and disposal. Reduced motion shows the preview immediately.
- The first cell is Windows Start. It sends the documented `WM_SYSCOMMAND / SC_TASKLIST` command to activate the native Start menu; it does not emulate a keyboard chord.
- Render the Start logo as four blue square panes instead of using a private-use font glyph.
- Application and dock context menus are separate resources. Application menus contain only application actions; dock alignment is available only from the surface menu. Use system icon-font glyphs in a stable leading column and a translucent charcoal menu material.
- Where exposed by the application, read real Win32 menu-bar and system-menu commands into separate submenus. Bound menu reads to five levels and 100 entries, and owner initialization to 80 ms. Validate the original menu/command before dispatch; omit unsupported, owner-drawn, or inaccessible entries. This does not expose private application UI or custom Jump List tasks.
- Hide the strip and close previews/menus when the foreground application covers its monitor. React to AppBar full-screen notifications, foreground changes, and debounced foreground geometry changes. A maximized work-area window and a full-screen window on another monitor do not suppress this strip.
- Menus use WPF outside-click dismissal, receive foreground focus when opened, and close when a different process receives foreground activation. Track only one open root menu.
- Application clicks get a 150 ms, 2 DIP lift feedback. Restoring a minimized window uses Windows' native restore path. Custom animation of another process's already-visible window is not implemented: `AnimateWindow` requires ownership by the calling thread.
- Mouse clicks on dock/preview surfaces do not steal foreground activation before the command runs. Explicit application commands activate their selected target.
- Show window title and application icon in a 40 DIP preview header. Reserve thumbnail space below it. Its close button sends a normal close request only to the previewed window.
- Hide the thumbnail before opening an application context menu. Disable the redundant name tooltip while a window preview is available.

## Verification limits

Release compilation checks XAML and C# consistency. Startup checks show process liveness only. A local cropped desktop capture verified normal 48 DIP strip placement with a bottom gap; Computer Use was not used. Full-screen transitions, menu command execution, pointer/keyboard input, multiple-monitor DPI, dense application overflow, and high-contrast mode still require rendered Windows verification.

## Platform references

- [Apple Desktop & Dock settings](https://support.apple.com/en-gb/guide/mac-help/mchlp1119/mac): pointer magnification behavior. OpenTaskbar uses a restrained interpretation that fits its taskbar strip.
- [Microsoft WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand): native Start command.
- [Microsoft DWM thumbnail properties](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_thumbnail_properties): thumbnail destination and opacity used by the entrance animation.
