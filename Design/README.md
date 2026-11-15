# OpenTaskbar Design System

OpenTaskbar uses a lightweight, vector-driven design system implemented in pure WPF XAML without third-party UI dependencies.

---

## Design Tokens

### Layout & Dimensions

| Token | Value | Description |
| :--- | :--- | :--- |
| `DockWindowHeight` | `78 DIP` | Total height of the transparent window container |
| `DockHeadroom` | `20 DIP` | Transparent top headroom allowing unclipped icon magnification |
| `_nativeTaskbarHeight` | `58 DIP` | Base height of the floating dock container shelf |
| `DockCellSize` | `52 DIP` | Width of an individual dock application item cell |
| `DockCellHeight` | `44 DIP` | Height of an individual dock application item cell |
| `DockIconSize` | `32 DIP` | Standard icon display size (capped at 34 DIP on magnification) |
| `DockHoverScale` | `1.28x` | Peak magnification scale for the hovered dock item |
| `DockRadius` | `20 DIP` | Pill container corner radius (`CornerRadius="20"`) |
| `ControlRadius` | `12 DIP` | Standard control and button corner radius |
| `PopupRadius` | `10 DIP` | Context menu and window preview corner radius |
| `PreviewGap` | `18 DIP` | Vertical elevation gap between dock icons and thumbnail preview cards |
| `StatusDotSize` | `4 DIP` | Running and active application indicator dot diameter |

### Typography

- **Font Family:** `Segoe UI` across all UI surfaces for native Windows consistency, legibility, and full Unicode/Turkish diacritic support.
- **Sizes:**
  - Body and menu items: `13 DIP`
  - Secondary labels, badges, and Jump List category headers: `12 DIP`
  - Titles and section headings: `15 DIP`

---

## Visual Materials

- **Translucent Smoked Glass:** Deep translucent charcoal acrylic-style background (`#D916191F` in Dark theme, `#D9F3F4F6` in Light theme).
- **Specular Top Highlight:** 1 DIP top border highlight (`#4DFFFFFF`) simulating light reflection on glass edges.
- **Ambient Shadow:** Soft multi-layer drop shadow (`BlurRadius="24"`, `Direction="270"`, `Color="#000000"`, `Opacity="0.45"`) separating the dock from desktop wallpapers.
- **Status Indicators:**
  - Running application: Muted 4 DIP circular dot (`#94A3B8`).
  - Active (foreground) application: Bright illuminated 4 DIP dot (`#38BDF8`).
  - Alert / notification: Upper-right red badge (`#EF4444`).
- **Icons:** Vector outline paths based on the Lucide icon set (24x24 coordinate box, 1.6 DIP stroke width, rounded caps) for all context menus, tray items, and dock controls.

---

## Interaction & Motion

### Continuous Fisheye Magnification
- Pointer movement across the dock calculates a continuous parabolic/cosine wave scaling factor:
  $$S(d) = 1.0 + (S_{peak} - 1.0) \times \cos\left(\frac{\pi \cdot d}{2 \cdot R}\right)^2$$
- Magnification smoothly affects the hovered icon and immediate neighbors within an 84 DIP influence radius ($R$).
- Items scale anchored at `(0.5, 0.95)`, expanding upward into the 20 DIP transparent headroom without horizontal layout displacement or icon clipping.
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

### Launch Bounce
- Clicking an application icon triggers a 420 ms damped vertical launch bounce animation (-12 DIP peak) on its translate transform to provide tactile feedback while the application opens.

---

## Project Structure

- `Theme.xaml`: Core design system definitions, standard sizing tokens, brushes, and base control templates.
- `Palettes/Dark.xaml`: Dark theme color palette and material brushes.
- `Palettes/Light.xaml`: Light theme color palette and material brushes.
- `ContextMenus.xaml`: Vector outline icon styles, Jump List headers, and menu item templates.
- `SettingsControls.xaml`: Segmented controls, toggles, radio buttons, and preferences layout.
