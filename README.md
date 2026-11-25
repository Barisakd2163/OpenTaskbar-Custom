# OpenTaskbar Custom

[OpenTaskbar](https://github.com/alztrk/opentaskbar) projesinin **v0.3.4 tabanlı, MIT lisanslı** kişisel fork'u. Orijinal proje ve katkıda bulunanların telif bildirimleri korunur.

## Bu fork'ta neler değişti?

- **Boşluksuz, görünür dock:** “Üstte göster (alan ayırma)” modu dock'u görünür tutar; büyütülmüş pencereler arkasına kadar uzanabilir. Aynalanan ikinci ekranda da geçerlidir.
- **Pencere geçişlerinde donma düzeltmesi:** Başka uygulamaların giriş kuyruklarına bağlanan etkinleştirme yolu kaldırıldı. Bu yol dock'u ve Brave dahil uygulamaların pencere önizlemelerini kilitleyebiliyordu.
- **İki ekran senkronizasyonu:** Başlangıçta yüklenen sabit uygulamalar ve daha sonra gelen simgeler ikinci dock'a da aktarılır.
- **Tutarlı hover görünümü:** Uygulama, grup ve klasör simgeleri Başlat düğmesiyle aynı temaya uyumlu, yuvarlatılmış hover arka planını ve geçiş sürelerini kullanır.
- **Çökme düzeltmeleri:** Geçici olarak eksik medya oturumu verileri güvenli biçimde ele alınır; dock yönü değişirken otomatik (`NaN`) düğme boyutları kaydırma alanı sınırlarına taşınmaz.

### Doğrulama

Çökme ve otomatik değerlendirme yolları için `--crash-regressions` ile 69 hedefli kontrol geçti: eksik medya verisi ve toparlanma, gerçek dock yönü geçişleri, hata imzası/tekrar gruplama, Windows olaylarının doğru sürece bağlanması, kaynak kaydı ve watchdog yaşam döngüsü. Buna, acil görev çubuğu kurtarmasının çökme analizini yanlışlıkla kapatmaması da dahil. Daha önce 19 otomatik gizleme/Overlay yaşam döngüsü kontrolü ve yanıt vermeyen pencere etkinleştirmesine yönelik 1 regresyon kontrolü de geçti. İki ekrandaki dock, Brave'in canlı pencere önizlemesi ve hover arka planı gerçek Windows masaüstünde doğrulandı. Tam test paketi bu çalışma sırasında çalıştırılmadı.

İzole uçtan uca senaryoda native ve managed watchdog, gerçek alt süreç çıkışlarından sonra GUI açmadan değerlendirme üretti; Windows görev çubuğu görünür kaldı. Temiz kapanış sahte çökme kaydı üretmedi. Son gerçek WPF/GDI raporu Windows olaylarıyla eşleştirilerek değerlendirildi.

### Çökme tanıları

- Başlangıç kaydı gerçek sürümü/commit kimliğini, çalıştırılan EXE'yi ve süreç kimliğini içerir.
- Beklenmeyen .NET/UI hataları `%LOCALAPPDATA%\OpenTaskbar\Crashes` altında ayrı yerel raporlara yazılır. Raporlar exception/inner exception ve stack trace ile çalışma ortamı ve sınırlı dock/widget bağlamını içerir.
- Watchdog beklenmeyen kapanıştan sonra önce Windows görev çubuğunu geri getirir, sonra ayrı bir süreçte `--analyze-crashes` çalıştırır. Analiz WPF uygulamasını başlatmaz; dock'u otomatik yeniden başlatmaz. Sonraki normal açılış da bekleyen raporları değerlendirir.
- Sonuçlar `Crashes\Analysis\latest.md` ve `latest.json` dosyalarındadır: hata imzası, tekrar/sürüm sayıları, kanıtlar, güven düzeyi ve bilinmeyenler. GDI uyarısı ile RAM yetersizliği birbirine karıştırılmaz; watchdog zaman aşımı tek başına kanıtlanmış çökme sayılmaz.
- Beş saniyede bir sınırlı boyutlu GDI/USER nesnesi, süreç handle ve bellek sayaçları tutulur. İlgili Windows Application olaylarından yalnız seçili alanlar alınır; erişilemeyen veya eksik kanıt raporda belirtilir.
- Portable sürümlerde gömülü debug sembolleri tutulur; stack trace mümkün olduğunda kaynak dosyası ve satır numarasını gösterir.
- Raporlar otomatik yüklenmez. Exception metni ve stack trace yerel yollar içerebileceği için paylaşmadan önce içeriğini gözden geçirin; ayar dosyasının tamamı, hesap bilgileri ve medya başlıkları tanı bağlamına eklenmez.
- Bu hatalara yönelik odaklı kontroller: `dotnet run --project tests/OpenTaskbar.Tests/OpenTaskbar.Tests.csproj -c Release -- --crash-regressions`
- Elle yeniden değerlendirme: `OpenTaskbar.exe --analyze-crashes`. Başka bir rapor klasörü için `--crash-directory="C:\Raporlar"` eklenebilir.
- Otomatik değerlendirme kontrolleri: `dotnet run --project tests/OpenTaskbar.Tests/OpenTaskbar.Tests.csproj -c Release -- --crash-analysis-only`

[Commit geçmişi](https://github.com/Barisakd2163/OpenTaskbar-Custom/commits/custom/overlay-dock) · [Değişiklik günlüğü](CHANGELOG.md) · [MIT lisansı](LICENSE)

## Orijinal OpenTaskbar

Aşağıdaki özellikler, görseller ve genel kullanım yönergeleri orijinal projeden korunmuştur; fork'a özgü değişiklikler yukarıda listelenmiştir.

<div align="center">

**A modern, customizable floating dock and modular widget system for Windows 10 and Windows 11.**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?style=flat-square&logo=windows)](https://github.com/alztrk/opentaskbar)
[![Runtime](https://img.shields.io/badge/.NET-10.0%20WPF-512BD4?style=flat-square&logo=dotnet)](https://github.com/alztrk/opentaskbar)
[![Architecture](https://img.shields.io/badge/Architecture-x64-blue?style=flat-square)](https://github.com/alztrk/opentaskbar)
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)
[![Tests](https://img.shields.io/badge/Tests-see%20suite-brightgreen?style=flat-square)](tests/OpenTaskbar.Tests)

<br />

<img src="docs/screenshots/hero-desktop-2026-11.jpg" alt="OpenTaskbar Desktop Showcase" width="100%" />

<br />

<img src="docs/screenshots/hero-dock-crop-2026-11.png" alt="OpenTaskbar Floating Dock" width="100%" />

</div>

---

## Overview

OpenTaskbar is a lightweight, macOS-inspired floating dock and taskbar replacement for Windows 10 and Windows 11. Built with pure WPF vector rendering, it delivers high performance, fluid magnification, live window thumbnail previews, modular hardware and media widgets, and foolproof native taskbar recovery.

---

## Key Features

### Optical Glass Dock
- **Aurora Visual Language:** Dark (Midnight Obsidian) and Light glass materials with top specular sheen, 1px optical bevel, and soft ambient drop shadows.
- **Dock Edge Placement:** Position the dock at the Bottom, Left, or Right edge of your primary screen, with independent alignment (Center, Left, Right, or Full-Width).
- **Size, Scale & Corner Radius:** Choose between Compact (48 DIP), Medium (58 DIP), and Large (66 DIP) presets, fine-tune scale from 75% to 150%, and customize frame corner radius from 0 px (sharp) to 32 px while screen edge spacing adjusts automatically from the active profile.
- **Per-Surface Materials:** Configure the dock, menus, widgets and Settings window independently. Each surface has Glass (translucent), Solid (fully opaque) or None (background, rim and shadow completely removed) with its own 0–100% opacity and live preview.
- **Two-Sided Dock Resizing & Edge Scrolling:** Drag either outer border to narrow or expand the dock while keeping widgets at full width. Overflowing items scroll smoothly via mouse wheel, middle-click panning, or clickable edge chevrons with directional gradient scrims aligned to the outer dock borders.

### Theme Packs
- **Theme Packs:** Restyle the dock with six bundled themes installed in `%LOCALAPPDATA%\OpenTaskbar\Themes` on first launch, or add your own theme packs there. Select them in Settings → Appearance → Theme.
- **Languages:** The first launch follows the Windows display language when OpenTaskbar has a translation for it. Choose a language in Settings → Appearance; see [docs/localization.md](docs/localization.md) to add another locale.
- **Icon Packs:** Replace app, Start button and Recycle Bin icons; match by executable name, full path, Store app ID, or simply name a file after the executable (`firefox.png`).
- **Fallback Plates:** Apps without a pack image are placed on a Leaf, Petal, Rounded, or Circle plate so the whole dock matches.
- **Widget Packs:** Replace design tokens (colors, gradients, corner radii, sizes, fonts) for the dock, widgets, menus, and Settings. See [docs/themes.md](docs/themes.md).
- **Prism Theme:** A ready-made theme in [themes/prism](themes/prism) with category icons on lit squircle plates and a graphite, violet-accented style. No application logos; duotone glyphs from Phosphor (MIT).

### Interaction and Motion
- **macOS-Style Fisheye Magnification:** Spring-physics raised-cosine magnification (`DockFisheyeLayout`) that smoothly expands icon slots and the floating dock frame when monitor space allows, caps peak scale automatically to stay overlap-free on full-width or scrolling docks, and dynamically lifts thumbnail previews clear of magnified icons.
- **Launch Feedback:** Tactile vertical launch bounce animation on application click.
- **Drag-and-Drop & App Groups:** Drag applications directly onto the dock to pin, reorder freely across apps/stacks/widgets, organize apps into collapsible dock groups with member count badges, or drag out to unpin.

### Intelligent Window Management
- **Live DWM Thumbnails:** Interactive live window thumbnail previews with window titles and close buttons.
- **Native Jump Lists:** Right-click any application to open its native Windows Jump List with pinned tasks and recent files.
- **Foreground Highlighting:** Active foreground windows are clearly distinguished with illuminated accent capsules, independent of keyboard focus.
- **Packaged App Icons:** Running Store and packaged apps without executable icon resources show their Start menu icon. Apps hosted by ApplicationFrameHost share one dock cell, which shows the icon of the topmost hosted app.
- **Multi-Monitor Docks:** Optional secondary docks for multi-monitor setups.

### Smart Auto-Hide (IntelliHide)
- **Four Dock Visibility Modes:** Disabled (always visible with reserved screen space), Overlay (always visible without reserved space, so maximized applications extend behind the dock), Smart (IntelliHide), and Always (hidden until edge hover). Overlay also applies to mirrored docks.
- **IntelliHide Logic:** The dock stays visible on the desktop and behind non-overlapping windows. When an active window overlaps the dock region, the dock smoothly slides off-screen.
- **Fullscreen Detection:** Automatically yields and hides when full-screen applications, borderless games, or browser videos are playing.

### Modular Widget System
- **AI Usage Providers:** Dedicated per-provider widgets for Claude, Codex, Antigravity, OpenCode, GitHub Copilot, DeepSeek, and OpenRouter with session/weekly quota bars or rings, reset countdowns, and read-only local credential discovery without modifying, logging, or exporting credentials.
- **Bluetooth Battery:** Live Windows PnP battery levels for connected Bluetooth headsets, mice, keyboards, controllers, watches, and phones with automatic device-type icons and active-node filtering.
- **Real-Time Hardware Meters:** CPU usage, RAM usage, GPU usage, and GPU temperature (NVIDIA) rings with customizable thresholds.
- **Media Controls:** Now Playing track information, artist metadata, track seek bar, and volume slider.
- **Clock, Calendar and Weather:** Unified date and time capsule with detailed optical glass weather and multi-day forecast flyout.
- **Quick Settings and Connectivity:** Wi-Fi, Bluetooth and Night Light controls sit beside shortcuts to native Windows quick actions and settings, including networks, casting, airplane mode, mobile hotspot, accessibility, captions and focus. Direct controls show while Windows applies a change. Audio offers output/input selection and a per-app volume mixer.
- **Productivity Widgets:** Focus Pomodoro timer, Stopwatch, Countdown timer, Storage drive usage, Network Speed, and Day Progress bar.
- **System Tray Management:** Background tray apps manager with native tray context menus, window restore, and confirmed process termination.
- **Interactive Catalog:** Built-in widget catalog grouped under inline category headings with live widget previews and expandable configuration drawers with instant auto-save.
- **Optional Dynamic Island:** When enabled, the top-attached island remains visible on every connected display, even before widgets are assigned. Assign each widget to the dock or a chosen display's island; assigned widgets move off that display's dock. The island supports per-display paging, visibility rules, and full-screen hiding.

### Extras and Integration
- **Search Button:** A Start-adjacent dock button that opens the native Windows Search flyout (Win+S), with the same cell size as app icons and a Settings toggle.
- **Permanent Shell Buttons:** Task View with previous/next/new-desktop shortcuts, a background-apps (tray) button with its native-style flyout, and a slim Show Desktop strip (Win+D).
- **Folder Stacks:** Quick access to folders (such as Downloads) with 2x2 miniature icon previews.
- **Recycle Bin Integration:** Live recycle bin icon supporting drag-and-drop file deletion and one-click emptying.
- **Dock Menu:** Right-click the dock for Windows Search, folder stacks, profiles, Task Manager, update checks, and settings.

### Foolproof Taskbar Recovery
- **Zero Risk:** State journaling records every taskbar replacement action. On exit, system shutdown, or unexpected termination, OpenTaskbar automatically restores the native Windows taskbar.
- **Verified Restore:** Recovery re-asserts the original taskbar state and visibility several times and keeps the journal until the taskbar is actually visible, so an Explorer restart cannot silently lose the recovery state.
- **Self-Healing Startup:** A pending journal is repaired after the singleton check and before the dock starts, including for `--inspect` sessions.
- **Crash Watchdog:** A detached lightweight native C Win32 helper (`tools/watchdog/watchdog.c`) handles cases no in-process handler can (End Task, `taskkill /F`, FailFast, or unexpected exit), restores the Windows taskbar, and exits immediately.
- **Manual Escape Hatch:** `OpenTaskbar.exe --restore` forces the native taskbar back at any time, even when OpenTaskbar is not running.

---

## Visual Showcase

<div align="center">

| Dock Settings | Themes Gallery |
| :---: | :---: |
| <img src="docs/screenshots/settings-general.jpg" alt="OpenTaskbar Dock Settings" width="460" /> | <img src="docs/screenshots/settings-themes.jpg" alt="OpenTaskbar Themes Gallery" width="460" /> |

</div>

### Star Growth

<div align="center">

<img src="https://api.star-history.com/svg?repos=alztrk/opentaskbar&type=Date" alt="OpenTaskbar star growth over time" width="100%" />

</div>

---

## Keyboard Shortcuts

| Shortcut | Description |
| :--- | :--- |
| `Shift + Mouse Wheel` | Scroll dock contents horizontally |
| `Ctrl + Mouse Wheel` | Scroll dock contents horizontally (high precision) |
| `Middle Click + Drag` | Pan overflowing dock contents |
| `Alt + F4` | Close window preview card |
| `Esc` | Close active flyout or stack |

---

## Requirements

- **Operating System:** Windows 10 (version 2004 / build 19041+) or Windows 11 (x64)
- **Runtime:** .NET 10 Desktop Runtime (included in self-contained portable releases)

---

## Getting Started

### Portable Release

Download the `OpenTaskbar-*-win-x64.exe` or `OpenTaskbar-*-win-x86.exe` asset for your architecture from [GitHub Releases](https://github.com/alztrk/opentaskbar/releases) and launch it directly. No installer or administrator privileges required. The in-app updater automatically picks the matching architecture.

### Build from Source

```powershell
# Clone the repository
git clone https://github.com/alztrk/opentaskbar.git
cd opentaskbar

# Run directly from source
dotnet run --project OpenTaskbar.csproj

# Run with the Settings window open on startup
dotnet run --project OpenTaskbar.csproj -- --settings

# Build self-contained portable executable (release\OpenTaskbar-x64.exe)
powershell -ExecutionPolicy Bypass -File ".\scripts\publish-portable.ps1"

# Build the 32-bit variant (release\OpenTaskbar-x86.exe) into the same folder
powershell -ExecutionPolicy Bypass -File ".\scripts\publish-portable.ps1" -Runtime win-x86

# Build MSIX packages: appends _x64 or _x86 to the package name (release\msix\)
powershell -ExecutionPolicy Bypass -File ".\scripts\publish-msix.ps1" -Runtime win-x86

# Package both builds into release\: versioned EXEs/ZIPs, checksums.txt,
# release-manifest.json and release-notes.md (same step CI uses)
powershell -ExecutionPolicy Bypass -File ".\scripts\package-release.ps1" -Tag "v0.3.2"

# Run automated behavior and visual verification tests (full suite, used by CI)
dotnet run --project tests/OpenTaskbar.Tests/OpenTaskbar.Tests.csproj

# Run only pure logic checks: no windows, no synthetic input, no desktop side effects
dotnet run --project tests/OpenTaskbar.Tests/OpenTaskbar.Tests.csproj -- --headless
```

The full suite verifies WPF controls and therefore needs a Windows desktop session. The `--headless` mode skips those visual checks and runs the fast, side-effect-free logic tests.

---

## Configuration

User preferences, pinned item order, and widget configurations are stored locally in `%LOCALAPPDATA%\OpenTaskbar\`:

- `settings.json` : User preferences, dock scaling, pinned applications, and active widgets.
- `taskbar-state.txt` : Native Windows taskbar state recovery journal.
- `log.txt` : Diagnostic and operational log.
- `Themes\` : Theme packs (icon and widget packs). See [docs/themes.md](docs/themes.md).

---

## Design System

For in-depth documentation on OpenTaskbar's design tokens, materials, typography, and layout metrics, see [Design/README.md](Design/README.md). All shipped UI resources live under `Design/*`.

---

## License

Distributed under the MIT License. See [LICENSE](LICENSE) for details.
