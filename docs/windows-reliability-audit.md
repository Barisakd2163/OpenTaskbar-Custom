# Windows reliability and UX verification matrix

> Status: audit plan, **not** a claim that these cases have been reproduced or fixed. Run on real Windows 10/11 machines or VMs. Keep native taskbar restoration available during testing.

## Source-grounded review targets

- `Services/Interop/DisplayService.cs`: monitor enumeration, target selection, DPI conversion of screen coordinates, fallback when a selected display disappears.
- `Views/Windows/SecondaryDockWindow.cs`: secondary monitor app bars, bottom-edge work-area reservation, foreground fullscreen suppression, grouping windows by executable.
- `Views/Windows/MainWindow.xaml.cs`: `WM_DISPLAYCHANGE` and `SPI_SETWORKAREA` trigger secondary dock updates.
- `Services/Interop/TaskbarReplacementSession.cs`: display/DPI messages, native taskbar hide/restore, crash-recovery journal and app-bar reservations.
- `Services/Interop/VirtualDesktops.cs`: current-desktop COM check; failure currently falls back to showing windows.
- `Views/DockScrollViewer.cs`: horizontal overflow, hidden scrollbar, wheel scrolling and synchronous `UpdateLayout()` during content measurement.
- `Services/App/DockAutoHideController.cs`: reveal/hide guards, flyout and foreground transitions.

## P0: do not leave the user without a taskbar

| ID | Scenario | Expected outcome |
|---|---|---|
| REC-01 | Force-terminate OpenTaskbar while native taskbar is hidden | Native taskbar is recoverable on next startup/recovery path; document any unavoidable delay |
| REC-02 | Restart Explorer while replacement is active | Native and replacement taskbars do not permanently overlap or both disappear |
| REC-03 | Start twice / rapidly exit and restart | Only one owner of taskbar replacement and app-bar reservations |
| REC-04 | Suspend, hibernate, lock/unlock, RDP reconnect | Dock returns without orphaned reservations or permanent hidden state |
| REC-05 | Update attempt fails, elevation denied, executable locked | Existing executable remains launchable; taskbar recovery remains possible |

## P1: monitor topology, DPI and fullscreen

| ID | Scenario | Expected outcome |
|---|---|---|
| MON-01 | 1, 2 and 3 monitors, mixed resolutions and portrait orientation | One correctly positioned dock per enabled display; no duplicate reservations |
| MON-02 | Secondary monitor left/above primary (negative virtual-screen coordinates) | Correct hit testing, placement, and work-area bounds |
| MON-03 | Mixed 100%, 125%, 150%, 200% DPI; move primary target | Physical pixel and WPF DIP coordinates agree; no offset or scaling jump |
| MON-04 | Unplug/replug HDMI/DP/USB-C dock; change primary monitor | Old secondary windows/app-bars disposed; new ones created on valid handles |
| MON-05 | Disable selected target monitor while app runs | Fallback to a connected monitor; user can still open Settings |
| MON-06 | Explorer taskbar at top/left/right or auto-hide | Reservation does not assume the bottom strip is always available |
| MON-07 | Fullscreen game/video on one monitor, normal apps on another | Suppress only the affected monitor's dock; restore on Alt+Tab |
| MON-08 | Borderless fullscreen vs maximized window; HDR and refresh-rate changes | No incorrect permanent fullscreen suppression |
| MON-09 | Move one app's windows across monitors | Current-monitor filtering and click/activation choose the expected window |
| MON-10 | Change virtual desktop or move a window between desktops | Current-desktop filter follows preference; COM failure is visible in diagnostics |
| MON-11 | Change Windows display scale or work area while dock is open | Correct repositioning after `WM_DPICHANGED`, `WM_DISPLAYCHANGE` and work-area notifications |

## P1: interaction, accessibility and visual design

| ID | Scenario | Expected outcome |
|---|---|---|
| UX-01 | 0, 10, 30, 60+ running/pinned items | No inaccessible icon; overflow discoverable and controllable by keyboard |
| UX-02 | Wheel over app icon, empty dock, widget and overflow area | Predictable behavior; scrolling and volume/window actions do not conflict |
| UX-03 | Open context menu, thumbnail, widget flyout with auto-hide enabled | Dock remains visible while interacting, hides after all flyouts close |
| UX-04 | 1366x768, 1080p, 4K; 100%-200% text scaling | No clipped Settings rows, unreadable captions or offscreen dialogs |
| UX-05 | Keyboard only, screen reader, high contrast, reduced motion | Focus order, labels and control states remain understandable |
| UX-06 | RTL language and long translations | No overlap/truncation of critical controls; localization fallback is intentional |
| UX-07 | First launch, settings reset, invalid settings JSON | User understands what changed and can recover preferences |
| UX-08 | Compare dock and Settings screenshots across bundled themes | Consistent spacing, contrast, icon sizing, active/hover indicators |

## P1/P2: performance, security, delivery

- PERF-01: Record idle and active CPU, private bytes, handles, GDI objects, GPU usage, UI frame time at 10/30/60 windows, 1/2/3 displays and 1h/8h uptime.
  - Partial baseline captured on 2026-11-22 (Windows 11 build 26300, 16 cores, single 2560x1440 display, x64 self-contained publish, user's full widget set): steady-state working set 60–120 MB after idle trimming, private bytes 180–190 MB, ~31–35 threads, ~1300 handles. Idle CPU ~22% of one core, fisheye sweep ~36% of one core. Short private-byte spikes to ~275 MB occur on periodic widget/CLI polling and settle after GC. `scripts/measure-baseline.ps1 -Label <name>` produces the raw CSV and `summary.json` for A/B comparisons; `scripts/compare-perf.ps1` diffs two runs. Still open: 10/30/60-window scaling, multi-display, 1h/8h uptime, GDI objects and GPU usage.
- PERF-02: Profile `UpdateLayout()`, window tracking refreshes, thumbnail previews, image decoding, widget timers and allocations; optimize only after measurement.
  - Existing icon/flag/album-art decode caps and the bounded `RunningAppIconCache` already cover the obvious allocation sources; no single dominant leak was found in the idle/sweep baseline. The ~180 MB private footprint is mostly .NET/WPF runtime and widget visuals, not an app-level cache. Any further reduction (trimming unused assemblies, compacting GC) needs an A/B measurement before adoption.
- SEC-01: Theme ZIP traversal, duplicate entries, reserved Windows names, size limits and import/export symmetry.
- SEC-02: Update executable asset selection, SHA-256 verification, signing policy, atomic replacement, backup and rollback; coordinate with separate updater PR.
- SEC-03: Review shell command arguments, paths with special characters, log privacy and permission boundaries.
- REL-01: Release pipeline reproducibility, artifact checksums, GitHub Actions pinning and Windows test reporting.
- ARCH-01: Reduce coupling in `MainWindow`, `SettingsWindow`, `TaskbarReplacementSession` through small independently tested changes.

## Exit criteria and evidence

For each case record: OS build, display topology, DPI, target monitor, taskbar edge, exact steps, expected/actual result, screenshot/video, logs, CPU/memory/handle measurements when applicable, and regression test reference. Do not mark any case passing based on static review alone. Implement P0 fixes before broad UI refactors; split fixes into narrowly scoped PRs.
