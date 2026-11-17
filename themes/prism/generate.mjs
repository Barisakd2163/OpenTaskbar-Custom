// Generates the Prism theme's icons: a lit squircle plate per app category with a white Phosphor duotone glyph.
// No brand logos are used, so the theme carries no trademarks.
//
// Usage (from any folder that has the two packages installed):
//   npm install --no-save @phosphor-icons/core@2.1.1 @resvg/resvg-js@2.6.2
//   node <repo>/themes/prism/generate.mjs
import { createRequire } from 'node:module';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(join(process.cwd(), 'noop.js'));
const { Resvg } = require('@resvg/resvg-js');
// The package exports only its index, so walk up from it to the package folder.
let phosphorRoot = dirname(require.resolve('@phosphor-icons/core'));
while (!phosphorRoot.replaceAll('\\', '/').endsWith('@phosphor-icons/core')) phosphorRoot = dirname(phosphorRoot);
const phosphor = join(phosphorRoot, 'assets', 'duotone');

const here = dirname(fileURLToPath(import.meta.url));
const iconDir = join(here, 'icons');
const imageDir = join(iconDir, 'icons');
mkdirSync(imageDir, { recursive: true });

const SIZE = 256;
const INSET = 14;          // room for the drop shadow
const EXPONENT = 5;        // superellipse: 2 is a circle, higher is squarer
const GLYPH = 132;         // glyph box on the 256 canvas

// Every plate shares saturation and lightness steps, so only the hue tells categories apart.
// [hue, saturation] — saturation 0-100; neutrals use a low saturation.
const tone = {
  violet: [262, 82], blue: [217, 88], sky: [199, 90], cyan: [188, 80], teal: [170, 72], green: [146, 64],
  lime: [96, 58], amber: [40, 94], orange: [24, 92], coral: [8, 86], rose: [346, 82], pink: [322, 74],
  indigo: [236, 74], yellow: [50, 92], slate: [218, 20], graphite: [222, 14], sand: [32, 46],
};

// name: [Phosphor glyph, tone, ...keys]
const categories = {
  start:       ['squares-four', 'violet', '@start'],
  browser:     ['globe-hemisphere-west', 'blue', 'brave.exe', 'firefox.exe', 'chrome.exe', 'msedge.exe', 'opera.exe', 'vivaldi.exe',
                'iexplore.exe', 'librewolf.exe', 'waterfox.exe', 'zen.exe', 'Arc.exe', 'floorp.exe'],
  files:       ['folder-simple', 'amber', 'explorer.exe', 'Files.exe', 'TOTALCMD64.EXE', 'doublecmd.exe'],
  terminal:    ['terminal-window', 'graphite', 'WindowsTerminal.exe', 'Microsoft.WindowsTerminal', 'OpenConsole.exe',
                'powershell.exe', 'pwsh.exe', 'cmd.exe', 'putty.exe', 'wsl.exe', 'wslhost.exe', 'ubuntu.exe',
                'CanonicalGroupLimited.Ubuntu', 'alacritty.exe', 'wezterm-gui.exe', 'mintty.exe'],
  settings:    ['gear-six', 'slate', 'SystemSettings.exe', 'windows.immersivecontrolpanel', 'control.exe'],
  system:      ['gauge', 'teal', 'Taskmgr.exe', 'perfmon.exe', 'resmon.exe', 'mmc.exe', 'regedit.exe', 'ProcessHacker.exe',
                'nectarlink-desktop.exe'],
  music:       ['music-notes', 'green', 'Spotify.exe', 'SpotifyAB.SpotifyMusic', 'wmplayer.exe', 'Microsoft.ZuneMusic',
                'foobar2000.exe', 'Deezer.exe', 'AppleMusic.exe', 'TIDAL.exe', 'MusicBee.exe', 'AIMP.exe'],
  video:       ['film-slate', 'coral', 'vlc.exe', 'mpc-hc64.exe', 'mpv.exe', 'PotPlayerMini64.exe', 'Microsoft.ZuneVideo',
                'Microsoft.Media.Player.exe', '4DF9E0F8.Netflix', 'obs64.exe'],
  chat:        ['chat-circle-dots', 'sky', 'Telegram.exe', 'TelegramMessengerLLP.TelegramDesktop', 'WhatsApp.exe',
                'WhatsApp.Root.exe', '5319275A.WhatsAppDesktop', 'Signal.exe', 'Discord.exe', 'slack.exe', 'ms-teams.exe', 'Teams.exe',
                'MSTeams', 'Element.exe', 'Viber.exe', 'Skype.exe', 'WeChat.exe', 'Weixin.exe', 'LINE.exe', 'Threema.exe', 'Zoom.exe'],
  mail:        ['envelope-simple', 'indigo', 'OUTLOOK.EXE', 'olk.exe', 'Microsoft.OutlookForWindows', 'thunderbird.exe', 'HxOutlook.exe',
                'microsoft.windowscommunicationsapps', 'Proton Mail.exe', 'Mailspring.exe'],
  code:        ['code', 'violet', 'Code.exe', 'Code - Insiders.exe', 'devenv.exe', 'Antigravity.exe', 'Cursor.exe',
                'idea64.exe', 'pycharm64.exe', 'rider64.exe', 'webstorm64.exe', 'clion64.exe', 'studio64.exe', 'sublime_text.exe',
                'Zed.exe', 'GitHubDesktop.exe'],
  notes:       ['note-pencil', 'yellow', 'notepad.exe', 'Notepad.exe', 'Microsoft.WindowsNotepad', 'notepad++.exe',
                'Obsidian.exe', 'ONENOTE.EXE', 'Microsoft.Office.OneNote', 'Microsoft.MicrosoftStickyNotes', 'Evernote.exe', 'Notion.exe',
                'Joplin.exe', 'Microsoft.Todos'],
  documents:   ['file-text', 'blue', 'WINWORD.EXE', 'soffice.exe', 'soffice.bin', 'wps.exe', 'Acrobat.exe', 'AcroRd32.exe',
                'SumatraPDF.exe', 'FoxitPDFReader.exe'],
  spreadsheet: ['table', 'green', 'EXCEL.EXE', 'et.exe'],
  slides:      ['presentation-chart', 'orange', 'POWERPNT.EXE', 'wpp.exe'],
  games:       ['game-controller', 'pink', 'steam.exe', 'steamwebhelper.exe', 'EpicGamesLauncher.exe', 'Battle.net.exe',
                'GalaxyClient.exe', 'XboxPcApp.exe', 'Microsoft.GamingApp', 'RiotClientServices.exe', 'EADesktop.exe', 'upc.exe'],
  photos:      ['image', 'teal', 'Photos.exe', 'Microsoft.Windows.Photos', 'mspaint.exe', 'Microsoft.Paint',
                'Photoshop.exe', 'Lightroom.exe', 'gimp-2.10.exe', 'gimp-3.0.exe', 'krita.exe', 'PaintDotNet.exe', 'Figma.exe'],
  camera:      ['camera', 'graphite', 'WindowsCamera.exe', 'Microsoft.WindowsCamera'],
  calculator:  ['calculator', 'orange', 'CalculatorApp.exe', 'calc.exe', 'Microsoft.WindowsCalculator'],
  calendar:    ['calendar-blank', 'rose', 'HxCalendarAppImm.exe'],
  clock:       ['alarm', 'rose', 'Time.exe', 'Microsoft.WindowsAlarms'],
  store:       ['shopping-bag-open', 'sky', 'WinStore.App.exe', 'Microsoft.WindowsStore'],
  packages:    ['package', 'sand', 'Microsoft.DesktopAppInstaller', 'AppInstaller.exe'],
  remote:      ['screencast', 'cyan', 'AnyDesk.exe', 'TeamViewer.exe', 'mstsc.exe', 'msrdc.exe',
                'Microsoft.RemoteDesktop', 'RustDesk.exe', 'parsecd.exe'],
  cloud:       ['cloud', 'sky', 'OneDrive.exe', 'Dropbox.exe', 'GoogleDriveFS.exe', 'Nextcloud.exe', 'MEGAsync.exe'],
  passwords:   ['key', 'sand', 'KeePass.exe', 'KeePassXC.exe', 'Bitwarden.exe', '1Password.exe', 'Proton Pass.exe'],
  assistant:   ['sparkle', 'pink', 'Claude.exe', 'Gemini.exe', 'ChatGPT.exe', 'Copilot.exe', 'Microsoft.Copilot',
                'Perplexity.exe'],
  downloads:   ['download-simple', 'teal', 'qbittorrent.exe', 'IDMan.exe', 'transmission-qt.exe', 'JDownloader2.exe'],
  screenshot:  ['crop', 'lime', 'SnippingTool.exe', 'Microsoft.ScreenSketch', 'ShareX.exe', 'Greenshot.exe'],
  search:      ['magnifying-glass', 'slate', 'Everything.exe', 'Everything64.exe'],
  weather:     ['cloud-sun', 'cyan', 'Microsoft.BingWeather'],
  maps:        ['map-trifold', 'lime', 'Maps.exe', 'Microsoft.WindowsMaps'],
  phone:       ['device-mobile', 'indigo', 'PhoneExperienceHost.exe', 'Microsoft.YourPhone'],
  recorder:    ['microphone', 'rose', 'SoundRecorder.exe', 'Microsoft.WindowsSoundRecorder', 'audacity.exe'],
  books:       ['book-open-text', 'sand', 'Kindle.exe', 'calibre.exe'],
  trash:       ['trash', 'slate', '@recyclebin'],
  'trash-full':['trash', 'coral', '@recyclebin-full'],
};

const hsl = (h, s, l) => `hsl(${h} ${s}% ${l}%)`;

// Points of a superellipse |x|^n + |y|^n = r^n, as one closed path.
function squircle(cx, cy, r) {
  const steps = 160;
  const points = [];
  for (let i = 0; i < steps; i++) {
    const t = (i / steps) * Math.PI * 2;
    const c = Math.cos(t), s = Math.sin(t);
    points.push([cx + r * Math.sign(c) * Math.abs(c) ** (2 / EXPONENT), cy + r * Math.sign(s) * Math.abs(s) ** (2 / EXPONENT)]);
  }
  return 'M' + points.map(([x, y]) => `${x.toFixed(2)},${y.toFixed(2)}`).join('L') + 'Z';
}

function glyph(name) {
  const svg = readFileSync(join(phosphor, `${name}-duotone.svg`), 'utf8');
  return svg.slice(svg.indexOf('>') + 1, svg.lastIndexOf('</svg>'))
    // The duotone back layer is 20% in Phosphor; a little more reads better on a coloured plate.
    .replace(/opacity="0\.2"/g, 'opacity="0.32"');
}

function plate(name, toneName) {
  const [h, s] = tone[toneName];
  const shape = squircle(SIZE / 2, SIZE / 2, SIZE / 2 - INSET);
  const scale = GLYPH / 256;
  const offset = (SIZE - GLYPH) / 2;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${SIZE}" height="${SIZE}" viewBox="0 0 ${SIZE} ${SIZE}">
  <defs>
    <linearGradient id="base" x1="0.15" y1="0" x2="0.85" y2="1">
      <stop offset="0" stop-color="${hsl(h - 6, s, 66)}"/>
      <stop offset="0.55" stop-color="${hsl(h, s, 52)}"/>
      <stop offset="1" stop-color="${hsl(h + 8, Math.min(100, s + 4), 38)}"/>
    </linearGradient>
    <radialGradient id="light" cx="0.28" cy="0.18" r="0.75">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.38"/>
      <stop offset="0.6" stop-color="#FFFFFF" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="depth" x1="0" y1="0.55" x2="0" y2="1">
      <stop offset="0" stop-color="#000000" stop-opacity="0"/>
      <stop offset="1" stop-color="#000000" stop-opacity="0.22"/>
    </linearGradient>
    <linearGradient id="rim" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.55"/>
      <stop offset="0.45" stop-color="#FFFFFF" stop-opacity="0.08"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0.18"/>
    </linearGradient>
    <filter id="drop" x="-20%" y="-20%" width="140%" height="140%">
      <feGaussianBlur stdDeviation="5"/>
    </filter>
    <filter id="glyphShadow" x="-30%" y="-30%" width="160%" height="160%">
      <feDropShadow dx="0" dy="5" stdDeviation="6" flood-color="${hsl(h + 8, s, 22)}" flood-opacity="0.55"/>
    </filter>
    <clipPath id="clip"><path d="${shape}"/></clipPath>
  </defs>
  <path d="${shape}" fill="#000000" opacity="0.32" transform="translate(0 5)" filter="url(#drop)"/>
  <g clip-path="url(#clip)">
    <path d="${shape}" fill="url(#base)"/>
    <path d="${shape}" fill="url(#light)"/>
    <path d="${shape}" fill="url(#depth)"/>
  </g>
  <path d="${shape}" fill="none" stroke="url(#rim)" stroke-width="2.5"/>
  <g filter="url(#glyphShadow)" fill="#FFFFFF" transform="translate(${offset} ${offset}) scale(${scale})">${glyph(name)}</g>
</svg>`;
}

const icons = {};
for (const [file, [glyphName, toneName, ...keys]] of Object.entries(categories)) {
  const png = new Resvg(plate(glyphName, toneName), { fitTo: { mode: 'width', value: SIZE } }).render().asPng();
  writeFileSync(join(imageDir, `${file}.png`), png);
  for (const key of keys) icons[key] = `icons/${file}.png`;
}

const pack = {
  icons,
  // Unknown apps sit on a squircle-like rounded plate tinted from their own icon.
  fallback: { shape: 'Rounded', cornerRadius: 0.3, background: 'auto', iconScale: 0.6 },
};
writeFileSync(join(iconDir, 'pack.json'), JSON.stringify(pack, null, 2) + '\n');
console.log(`${Object.keys(categories).length} icons, ${Object.keys(icons).length} keys`);
