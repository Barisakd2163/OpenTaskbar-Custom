# OpenTaskbar Design System

OpenTaskbar uses a lightweight, vector-driven design system implemented in pure WPF XAML without third-party UI dependencies.

---

## Design Tokens

### Layout & Dimensions

| Token | Value | Description |
| :--- | :--- | :--- |
| `DockWindowHeight` | `98 DIP` | Initial and preview window height; replacement mode derives its height from the shelf and headroom |
| `DockHeadroom` | `24 DIP` | Transparent top headroom included inside the scroll viewport for magnification and launch bounce; excluded from the desktop reservation |
| `DockMinimumShelfHeight` | `74 DIP` | Minimum shelf shared by the replacement window and dock layout; accommodates icon indicators, widget controls, padding, borders, and scrolling even when Explorer's taskbar is shorter |
| `DockCellSize` | `52 DIP` | Fixed width of each application cell; never compressed to fit content |
| `DockCellHeight` | `52 DIP` | Height of an individual dock application item cell |
| `DockIconSize` | `42 DIP` | Prominent icon display size with balanced cell margins |
| `DockHoverScale` | `1.28x` | Maximum magnification scale; constrained to fit the current icon cell |
| `DockRadius` | `18 DIP` | Pill container corner radius (`CornerRadius="18"`) |
| `ControlRadius` | `12 DIP` | Standard control and button corner radius |
| `PopupRadius` | `10 DIP` | Context menu and window preview corner radius |
| `PreviewGap` | `18 DIP` | Vertical elevation gap between dock icons and thumbnail preview cards |
| `StatusDotSize` | `4 DIP / 5 DIP` | Running and active application indicator dot diameter |
| `DockResizeGripWidth` | `8 DIP` | Invisible pointer target on each outside border; cursor indicates resizing |

### Typography

- **Font Family:** `Segoe UI` across all UI surfaces for native Windows consistency, legibility, and full Unicode/Turkish diacritic support.
- **Sizes:**
  - Body and menu items: `13 DIP`
  - Secondary labels, badges, and Jump List category headers: `12 DIP`
  - Titles and section headings: `15 DIP`
  - Widget values: `18 DIP`; timed values: `20 DIP`; detailed time: `32 DIP`

---

## Visual Materials

- **Settings Glass:** Native Desktop Acrylic (`DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW`) on Windows 11 build 22621+. `WindowChrome` provides the custom 48 DIP caption and native resize border. Text and icons remain opaque while settings, dock, and menu background brushes share the persisted surface opacity (60–100%, default 90%). No layered-window opacity or software blur is used. Unsupported systems and high-contrast mode use an opaque settings surface.
- **Compact Settings Layout:** One caption, a 148 DIP navigation column, 52 DIP general rows, and a fixed Apply footer. Remove introductory and persistent save-hint copy. Short auto-hide options form a vertical list. Uniform choice groups share aligned edges. The default 740 × 600 DIP window fits all general settings; smaller sizes scroll to the final option. Custom maximize sizing follows the current monitor work area and DPI.
- **Preference Feedback:** Enable Apply only for edited preferences or recoverable settings-load errors. Surface-opacity changes preview immediately and revert to saved preferences on close, including after pin reordering. Keep startup permission errors accessible on the disabled switch and show save errors/results in the footer.

- **Translucent Smoked Glass:** High-contrast charcoal and pearl materials keep the dock distinct from the wallpaper while retaining a light surface tint.
- **Specular Top Highlight:** A bright top border and a softer lower edge define the dock silhouette against varied wallpapers.
- **Ambient Shadow:** One soft drop shadow (`BlurRadius="16"`, `ShadowDepth="2"`, `Direction="270"`, `Color="#000000"`, `Opacity="0.25"`) separates the dock from the wallpaper. Outside border hit targets remain invisible and show the resize cursor.
- **Status Indicators:**
  - Running application: Muted 4 DIP circular dot (`#94A3B8`).
  - Active (foreground) application: Bright illuminated 4 DIP dot (`#38BDF8`).
  - Alert / notification: Upper-right red badge (`#EF4444`).
- **Widgets:** The optional catalog includes clock, date, battery, memory, system-drive space, day progress, stopwatch, and countdown widgets. The settings gallery renders the same widget templates as the dock, using local data. Selecting an available preview installs and enables it in the edit buffer; Apply persists the selection. Installed previews expose a visibility switch and a separate remove action. Preview content does not receive pointer or keyboard actions, so browsing never starts a timer. The gallery wraps with the available width and omits descriptive paragraphs. Installed and visible selections are separate. Time widgets use a compact value-first tile with one quick action; duration editing and reset live in a shared details panel. `Design/WidgetControls.xaml` owns widget styles and tokens, while `Views/Widgets/` separates tiles, timed details, the panel frame, and popup lifetime. Popup placement uses WPF's screen-edge adjustment, and an open panel locks dock auto-hide. Native popup focus, dismissal, and placement still require desktop verification.
- **Icons:** Context menus use vector outline paths based on the Lucide icon set (24x24 coordinate box, 1.6 DIP stroke width, rounded caps). Compact time actions and window captions use built-in Segoe MDL2 Assets glyphs, available on supported Windows 10 and 11 versions.

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
- **Edge Reveal:** Moving the mouse into the bottom 3 DIP trigger area instantly reveals the dock. Mouse leave triggers a 400 ms debounce before sliding down with a 220 ms cubic ease transition.

### Dock sizing and overflow

- Drag either dock edge to resize the visible surface. The opposite edge stays anchored during the drag.
- Application icons remain 42 DIP inside fixed 52 DIP cells when the dock narrows or widgets are added. The minimum visible surface is one cell plus its frame insets, 86 DIP with the default tokens.
- Overflow uses a hidden horizontal scroll viewport. Shift/Ctrl plus wheel moves horizontally, including high-resolution wheel deltas; middle-button dragging pans the content. No scrollbar row is reserved. `Views/DockScrollViewer.cs` expands the viewport upward and offsets its content by the same headroom. `Design/DockControls.xaml` keeps that offset inside the scroll presenter, preserving horizontal clipping at the resize gutters.
- Native mode queries the approved AppBar rectangle to measure space already reserved below it, then reserves only the missing shelf height. Hover headroom remains transparent above the shelf and does not reduce the desktop work area. Repeated position notifications do not accumulate another reservation.
- Invisible hit targets cover the outside left/right borders. They do not consume content padding or display extra lines. Resizing and middle-button panning lock auto-hide until the gesture ends.
- The dock width is bounded by the active monitor; cell and icon sizes remain fixed.

### Launch Bounce
- Clicking an application icon triggers a 420 ms damped vertical launch bounce animation (-12 DIP peak) on its translate transform to provide tactile feedback while the application opens.

---

## Project Structure

- `Theme.xaml`: Core design system definitions, standard sizing tokens, brushes, and base control templates.
- `Palettes/Dark.xaml`: Dark theme color palette and material brushes.
- `Palettes/Light.xaml`: Light theme color palette and material brushes.
- `ContextMenus.xaml`: Vector outline icon styles, Jump List headers, and menu item templates.
- `SettingsControls.xaml`: Segmented controls, toggles, radio buttons, and preferences layout.

### Settings navigation

The 160 DIP sidebar uses 44 DIP minimum-height tabs with a consistent 16 DIP Segoe MDL2 icon column. Selection is marked by a slim accent bar and a subtle neutral surface; hover and keyboard focus have separate treatments. Disabled Widgets retains its label and icon without hover emphasis. Native TabControl keyboard/selection behavior is retained. Decorative navigation glyphs are non-focusable template children; the actual tab header supplies its accessible name.

## Settings verification

Release compilation and temporary-file checks cover legacy opacity defaults, finite/range validation, saved values, pin preservation, live material changes, unchanged text opacity, preview rollback, and Apply state. WPF renders were inspected in dark/light, Turkish/English, default and minimum sizes. A native Windows 11 probe confirmed Acrylic type 3, maximize bounds within the monitor work area, and restore/minimize states; a cropped screen capture verified the actual custom window. Pointer dragging, resize grips, keyboard shortcuts, multiple-monitor DPI transitions, and Windows transparency/power-policy variations still require interactive validation. Computer Use was not used.

References: [Microsoft WindowChrome](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome), [Microsoft DWM system backdrop types](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type).
