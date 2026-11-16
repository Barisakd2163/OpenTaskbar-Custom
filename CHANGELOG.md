# Changelog

## 0.2.4 - 2026-11-16

- Refactored and modernized project architecture: partitioned windows into `Views/Windows/`, popups into `Views/Popups/`, and system services into `Services/Interop/`, `Services/Settings/`, and `Services/App/`.
- Decomposed `MainWindow` into focused partial classes for magnification, drag-and-drop, and resizing.
- Renamed data model item to `DockWidgetItem` to eliminate ambiguous name collision with widget UI base class.
- Added downward-pointing chevron buttons on configurable widget cards in Settings to expand and collapse widget-specific configuration drawers with automatic scrolling.
- Replaced the manual Apply footer with a full real-time auto-save system across all preferences and widget settings.
- Added graphics card usage, graphics card temperature and processor temperature meters to System meters, each with its own toggle.
- Added country and city selection picker for weather forecasts.
- Added automatic detection and hiding of the dock when full-screen browser videos, games, or borderless windows are active.
- Added fluent open/close and tab transition animations to the Settings window with bounded min/max dimensions.
- Highlighted the foreground application independently of keyboard focus, clearing its background when minimized or another app becomes active.
- Added normal close and confirmed process termination to background tray apps.
- Updated README documentation with authentic desktop hero showcase and visual feature gallery.

## 0.2.3 - 2026-11-16

- Kept application icons at 42 DIP inside fixed 52 DIP cells when resizing or adding widgets.
- Allowed the dock to narrow to one visible application cell, deriving its minimum width from the frame insets.
- Removed the visible horizontal scrollbar; Shift/Ctrl plus wheel and middle-button dragging scroll overflowing content.
- Replaced decorative resize grips with invisible hit targets at the outside left and right borders.
- Kept auto-hide locked during resizing and middle-button panning.

## 0.2.2 - 2026-11-16

- Kept magnified and bouncing icons inside the dock's scroll viewport while preserving horizontal clipping.
- Reserved the missing shelf height above Explorer's existing bottom space, without adding hover headroom to the desktop reservation.
- Updated the reservation when auto-hide changes and prevented recursive Shell positioning updates.
- Kept maximized captioned windows distinct from full-screen applications and released the extra reservation on exit.

## 0.2.1 - 2026-11-16

- Replaced text-heavy widget catalog rows with a wrapping gallery of real dock widget previews.
- Install widgets by selecting their preview; keep visibility and removal controls on installed items.
- Refresh local preview data while the widget settings page is visible, without running timer actions.

## 0.2.0 - 2026-11-16

- Refined dark and light dock surfaces, quieter resize handles, and softer shadows.
- Added an optional widget catalog with install, remove, show, hide, and localized search.
- Added clock, date, battery, memory, system-drive space, day progress, stopwatch, and countdown widgets.
- Added compact time tiles and shared details panels for duration input and reset.
- Improved widget catalog readability and themed input, focus, and read-only states.
- Standardized application icon bounds and added two-sided dock resizing with horizontal scrolling.
- Used the Windows UI language for initial preferences while preserving saved language choices.
- Added deterministic behavior checks and rendered WPF component checks.

## 0.1.0 - 2026-11-15

- Initial portable Windows x64 release.
