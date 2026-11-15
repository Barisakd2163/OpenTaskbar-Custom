# OpenTaskbar interface system

`Theme.xaml` owns the shared material, color, type, and size resources. The dock, menus, tooltips, and preview use these resources; there are no additional UI packages.

## Visual rules

- Preserve the native colors of application icons. Use neutral charcoal surfaces and restrained white highlights around them.
- Use Segoe UI for Windows menu familiarity, small-size readability, and Turkish glyph coverage. Body text is 13 DIPs; secondary labels are 12 DIPs.
- Use a 60 DIP window with 6 DIP outer insets. The surface is 48 DIPs high, with a 1 DIP border, 1 DIP vertical padding, and 44 DIP application cells.
- Keep icons at 28 DIPs. Reserve a separate 6 DIP row for running and foreground markers, rather than drawing them over the icons.
- A muted dot means running; a bright 16 DIP line means foreground. A red badge in the upper right means a Windows system alert.
- Hover and press change the surface without changing layout. Keyboard focus uses a separate high-contrast outline.
- The material is translucent tinted glass with a subtle vertical light change. It does not implement desktop backdrop blur. The previous unverified DWM Acrylic request has been removed.
- Size to content including border and insets. Empty status text consumes no width. Scale down only when the application group exceeds available width; never add scroll bars.

## Interaction rules

- Update notification and foreground state on existing application objects. Do not recreate buttons or replay entry animations for state changes.
- Use short opacity and scale transitions only for application arrival/removal. Respect the Windows client-area animation preference.
- Mouse clicks on dock/preview surfaces do not steal foreground activation before the command runs. Explicit application commands activate their selected target.
- Show window title and application icon in a 40 DIP preview header. Reserve thumbnail space below it. Its close button sends a normal close request only to the previewed window.
- Hide the thumbnail before opening an application context menu. Disable the redundant name tooltip while a window preview is available.

## Verification limits

Release compilation checks XAML and C# consistency. Startup checks show process liveness only. Pointer, keyboard, multiple-monitor DPI, narrow-screen overflow, high-contrast mode, and native thumbnail composition require rendered Windows verification; compilation alone does not prove these behaviors.
