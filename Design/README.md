# OpenTaskbar Design System

OpenTaskbar uses a lightweight, vector-driven design system implemented in pure WPF XAML without third-party UI dependencies.

---

## Design Tokens

### Layout & Dimensions

| Token | Value | Description |
| :--- | :--- | :--- |
| `DockWindowHeight` | headroom + shelf | Total height of the transparent window container |
| `DockHeadroom` | `24 DIP` | Transparent top headroom allowing unclipped icon magnification |
| Shelf | 48 / 58 / 66 DIP | Dock shelf height for Compact / Medium / Large (`DockMetrics.For`) |
| `DockCellSize` / `DockCellHeight` | 38 / 44 / 50 DIP | Square application cell per size; the axis along the dock shrinks to fit (width at the bottom, height on side docks) |
| `DockIconSize` | 28 / 34 / 42 DIP | Icon size per dock size |
| `DockHoverScale` | `1.28x` | Peak magnification scale for the hovered dock item |
| `DockRadius` | 16 / 19 / 22 DIP | Surface corner radius per dock size |
| `ControlRadius` | `12 DIP` | Standard control and button corner radius |
| `PopupRadius` | `10 DIP` | Context menu and window preview corner radius |
| `PreviewGap` | `18 DIP` | Vertical elevation gap between dock icons and thumbnail preview cards |
| Status capsule | `8 × 3 DIP / 16 × 3 DIP` | Running and foreground application indicator capsules |

### Typography

- **Font Family:** `Segoe UI` across all UI surfaces for native Windows consistency, legibility, and full Unicode/Turkish diacritic support.
- **Sizes:**
  - Body and menu items: `13 DIP`
  - Secondary labels, badges, and Jump List category headers: `12 DIP`
  - Titles and section headings: `15 DIP`

---

## Visual Materials

- **Settings Glass:** Native Desktop Acrylic (`DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW`) on Windows 11 build 22621+. `WindowChrome` provides the custom 48 DIP caption and native resize border. Text and icons remain opaque while settings, dock, and menu background brushes share the persisted surface opacity (60–100%, default 90%). No layered-window opacity or software blur is used. Unsupported systems and high-contrast mode use an opaque settings surface.
- **Compact Settings Layout:** One caption, a 148 DIP navigation column, 52 DIP general rows, and a fixed Apply footer. Remove introductory and persistent save-hint copy. Short auto-hide options form a vertical list. Uniform choice groups share aligned edges. The default 740 × 600 DIP window fits all general settings; smaller sizes scroll to the final option. Custom maximize sizing follows the current monitor work area and DPI.
- **Preference Feedback:** Enable Apply only for edited preferences or recoverable settings-load errors. Surface-opacity changes preview immediately and revert to saved preferences on close, including after pin reordering. Keep startup permission errors accessible on the disabled switch and show save errors/results in the footer.

### "Aurora" visual language

One accent gradient, teal `#5EEAD4` → sky `#38BDF8` (→ indigo `#818CF8` on the Start mark), carries every active state. Surfaces stay neutral so application icons keep their own colors.

- **Graphite Glass:** Three-stop dock material (`#CC222833` → `#D9161A22` → `#E60F1218` in Dark), its alpha set by the surface opacity. Corner radius 16 / 19 / 22 DIP per size.
- **Sheen:** A 1 DIP `DockHighlight` line on the top border, fading out before the rounded corners (`DockSheen`, bottom dock only).
- **Ambient Shadow:** `BlurRadius="30"`, `ShadowDepth="5"`, `Color="#05070B"`, `Opacity="0.5"`.
- **Hover Light:** A radial `IconHoverGlow` pool under the hovered icon instead of a hover box.
- **Status Indicators (capsules, not dots):**
  - Running application: 8 × 3 DIP capsule, `IndicatorBrush`.
  - Active (foreground) application: 16 × 3 DIP capsule in the accent gradient with an 8 DIP glow.
  - Alert / notification: Upper-right red badge.
- **Control Island:** Widgets sit in one rounded island (`WidgetIsland`, radius 14) with borderless cards; cards only tint on hover. Accent is teal `#2DD4BF` (Dark) / `#0D9488` (Light).
- **Flyouts:** Stacks, calendar, weather, background apps and the launcher share the menu glass (`MenuMaterial`, radius 14–18) and the `FlyoutButton` row style. They open away from the docked edge.
- **Icons:** Vector outline paths based on the Lucide icon set (24x24 coordinate box, 1.6 DIP stroke width, rounded caps) for context menus, widgets, and dock controls. Widgets draw them in a 24x24 canvas inside a `Viewbox` (`WidgetIcon` stroked, `WidgetGlyph` filled) so every icon scales from the same box.

---

## Interaction & Motion

### Continuous Fisheye Magnification
- Pointer movement across the dock calculates a continuous parabolic/cosine wave scaling factor:
  $$S(d) = 1.0 + (S_{peak} - 1.0) \times \cos\left(\frac{\pi \cdot d}{2 \cdot R}\right)^2$$
- Magnification smoothly affects the hovered icon and immediate neighbors within a 90 DIP influence radius ($R$).
- Items scale anchored at `(0.5, 0.95)`, expanding upward into the 24 DIP transparent headroom without horizontal layout displacement or icon clipping.
- Status dots remain stationary at the dock base.

### Multi-Window Live Thumbnails
- Interactive live DWM thumbnails are created using `DwmRegisterThumbnail`.
- Thumbnails fade and scale in from 95% over 160 ms with cubic ease-out.
- The preview strip floats 18 DIPs (`PreviewGap`) above the dock to prevent overlapping magnified icons.
- Each preview card features the application icon, window title, close button, and live window preview.

### Smart Auto-Hide (IntelliHide)
- **Disabled:** Dock remains persistently visible.
- **Smart:** Dock stays visible on the desktop and behind non-overlapping windows. When an active or maximized window overlaps the bottom dock region, the dock smoothly translates down off-screen.
- **Always:** Dock remains hidden below the screen edge by default.
- **Edge Reveal:** Moving the mouse into the bottom 3 DIP trigger area instantly reveals the dock. Mouse leave triggers a 400 ms debounce before sliding down with a 220 ms cubic ease-in-out transition; reveal uses 220 ms cubic ease-out. With animations off both are instant. While hidden, only the edge strip receives input; the reserved-area backdrop is collapsed so windows below stay clickable.
- **Full-screen detection:** Captionless windows that cover the monitor (games, video, F11 browsers) hide the dock. Maximized windows with a caption do not, because with auto-hide they also cover the whole monitor.

### Launch Bounce
- Clicking an application icon triggers a 420 ms damped vertical launch bounce animation (-12 DIP peak) on its translate transform to provide tactile feedback while the application opens.

---

## Layouts and Widgets

- **Floating:** The dock hugs its content and follows the Left/Center/Right alignment. Widgets sit after the applications inside the same surface.
- **Full width:** The surface spans the monitor with a 6 DIP side inset. Applications stay left and widgets are pinned to the right edge. Alignment is disabled.
- **Sizing:** Widgets keep their natural width. When space is short, application cells shrink first (down to 24 DIP).
- **Widget card** (`WidgetCardBorder` / `WidgetCardButton`): 46 DIP high, 12 DIP radius, a `WidgetCard` fill with a 1 DIP `WidgetCardStroke`, and 3 DIP horizontal spacing.
- **Colors:**
  - `WidgetAccent` (sky blue): meter values and the on state.
  - `WidgetAccentFill` (blue gradient): the battery tile and the volume track.
  - `WidgetWarning`: meters at 75% and above.
  - `WidgetFocusAccent` (red): meters at 90% and above, the focus phase, and low battery.
  - `WidgetOnStroke`: the outline of active Wi-Fi/Bluetooth pills.
- **Meters:** `RingMeter` draws a 3 DIP ring clockwise from 12 o'clock with a small caption (CPU/RAM) above the value.
- **Focus timer:** The card outline turns red while focusing and blue during a break. A finished phase plays the system sound and pulses the card three times (skipped when animations are off).
- **Typography:** Widget titles are 12 DIP semibold, captions 11 DIP, the clock 15 DIP, and the focus countdown 20 DIP with tabular numerals.

---

## Project Structure

- `Theme.xaml`: Core design system definitions, standard sizing tokens, brushes, and base control templates.
- `Palettes/Dark.xaml`: Dark theme color palette and material brushes.
- `Palettes/Light.xaml`: Light theme color palette and material brushes.
- `ContextMenus.xaml`: Vector outline icon styles, Jump List headers, and menu item templates.
- `SettingsControls.xaml`: Segmented controls, toggles, radio buttons, and preferences layout.
- `Widgets.xaml`: Widget tokens, card/button/pill/slider styles, and the widget icon geometries.

## Settings verification

Release compilation and temporary-file checks cover legacy opacity defaults, finite/range validation, saved values, pin preservation, live material changes, unchanged text opacity, preview rollback, and Apply state. WPF renders were inspected in dark/light, Turkish/English, default and minimum sizes. A native Windows 11 probe confirmed Acrylic type 3, maximize bounds within the monitor work area, and restore/minimize states; a cropped screen capture verified the actual custom window. Pointer dragging, resize grips, keyboard shortcuts, multiple-monitor DPI transitions, and Windows transparency/power-policy variations still require interactive validation. Computer Use was not used.

References: [Microsoft WindowChrome](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome), [Microsoft DWM system backdrop types](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type).
