# OpenTaskbar interface system

`Theme.xaml` owns the shared material, color, type, and size resources. The dock, menus, tooltips, and preview use these resources; there are no additional UI packages.

## Visual rules

- Preserve the native colors of application icons. Use neutral charcoal surfaces and restrained white highlights around them.
- Use Segoe UI for Windows menu familiarity, small-size readability, and Turkish glyph coverage. Body text is 13 DIPs; secondary labels are 12 DIPs.
- Use a 48 DIP window with zero vertical outer insets and 4 DIP horizontal insets. The surface has a 1 DIP border, 1 DIP vertical padding, and 44 DIP application cells. Do not add bottom margins that push the surface above the reserved taskbar strip.
- Keep icons at 28 DIPs. Reserve a separate 6 DIP row for running and foreground markers, rather than drawing them over the icons.
- A muted dot means running; a bright 16 DIP line means foreground. A red badge in the upper right means a Windows system alert.
- Hover and press change the surface without changing layout. Disable the default focus adorner and use a dark slate fill for keyboard focus; application buttons never add a white outline.
- Pointer magnification scales only the hovered icon layer to 1.22 over 120 ms. Neighbors stay at their normal size. Keep hit targets, status markers, dock width, and window position unchanged. Disable magnification when Windows client-area animation is disabled.
- The material is translucent tinted glass with a subtle vertical light change. It does not implement desktop backdrop blur. The previous unverified DWM Acrylic request has been removed.
- Size to content including border and insets. Empty status text consumes no width. Scale down only when the application group exceeds available width; never add scroll bars.

## Interaction rules

- Update notification and foreground state on existing application objects. Do not recreate buttons or replay entry animations for state changes.
- Use short opacity and scale transitions only for application arrival/removal. Respect the Windows client-area animation preference.
- Preview entrance fades and scales the live DWM thumbnail from 95% over 160 ms with cubic ease-out; the owned header fades over the same interval. Stop the entrance timer on completion, failure, replacement, hide, and disposal. Reduced motion shows the preview immediately.
- The first cell is Windows Start. It sends the documented `WM_SYSCOMMAND / SC_TASKLIST` command to activate the native Start menu; it does not emulate a keyboard chord.
- Render the Start logo as four blue square panes instead of using a private-use font glyph.
- Application and dock context menus are separate resources. Application menus contain only application actions; dock alignment is available only from the surface menu. Use system icon-font glyphs in a stable leading column and a translucent charcoal menu material.
- Menus use WPF outside-click dismissal, receive foreground focus when opened, and close when a different process receives foreground activation. Track only one open root menu.
- Application clicks get a 150 ms, 2 DIP lift feedback. Restoring a minimized window uses Windows' native restore path. Custom animation of another process's already-visible window is not implemented: `AnimateWindow` requires ownership by the calling thread.
- Mouse clicks on dock/preview surfaces do not steal foreground activation before the command runs. Explicit application commands activate their selected target.
- Show window title and application icon in a 40 DIP preview header. Reserve thumbnail space below it. Its close button sends a normal close request only to the previewed window.
- Hide the thumbnail before opening an application context menu. Disable the redundant name tooltip while a window preview is available.

## Verification limits

Release compilation checks XAML and C# consistency. Startup checks show process liveness only. Pointer, keyboard, multiple-monitor DPI, narrow-screen overflow, high-contrast mode, and native thumbnail composition require rendered Windows verification; compilation alone does not prove these behaviors.

## Platform references

- [Apple Desktop & Dock settings](https://support.apple.com/en-gb/guide/mac-help/mchlp1119/mac): pointer magnification behavior. OpenTaskbar uses a restrained interpretation that fits its taskbar strip.
- [Microsoft WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand): native Start command.
- [Microsoft DWM thumbnail properties](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_thumbnail_properties): thumbnail destination and opacity used by the entrance animation.
