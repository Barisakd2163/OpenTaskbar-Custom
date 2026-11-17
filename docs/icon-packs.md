# Icon Packs

OpenTaskbar can replace dock icons with an icon pack. No packs ship with the app: a pack is a folder you keep on your own computer.

## Where packs live

```
%LOCALAPPDATA%\OpenTaskbar\IconPacks\
└── my-pack\
    ├── pack.json
    ├── firefox.png        ← matched to firefox.exe without a mapping
    └── icons\
        └── trash.png
```

Pick a pack in **Settings → Appearance → Icon pack**. **Open folder** opens the `IconPacks` folder; **Reload** rereads the selected pack after you change its files.

## pack.json

```jsonc
{
  "name": "My Pack",
  "author": "You",
  "description": "Optional.",
  "icons": {
    "firefox.exe": "icons/browser.png",
    "Microsoft.WindowsTerminal": "icons/terminal.png",
    "@recyclebin": "icons/trash.png",
    "@recyclebin-full": "icons/trash-full.png"
  },
  "fallback": {
    "shape": "Leaf",
    "sharpCorner": "TopRight",
    "cornerRadius": 0.5,
    "background": "auto",
    "iconScale": 0.64
  }
}
```

Comments and trailing commas are allowed.

### Icon keys

For each app, the first key that matches wins:

| Key | Example | Matches |
| :--- | :--- | :--- |
| AppUserModelID | `Microsoft.WindowsTerminal_8wekyb3d8bbwe!App` | One Store or packaged app |
| Package name | `Microsoft.WindowsTerminal` | The part of the ID before `_` |
| Full path | `C:\Program Files\App\app.exe` | One install of an app |
| File name | `firefox.exe` | Any `firefox.exe` |
| Name | `firefox` | Same, without the extension |
| Special item | `@recyclebin`, `@recyclebin-full` | The Recycle Bin, empty or full |

Keys are not case-sensitive. Values are paths relative to the pack folder; a path that leads outside the pack folder is ignored.

An image named after the executable, such as `firefox.png` in the pack folder or in `icons\`, is used without a mapping.

Supported image formats: PNG, ICO, JPG and BMP. A square PNG of 128 to 256 pixels works best.

### Fallback plate

Apps that the pack has no image for keep their own icon. With a fallback `shape`, that icon is drawn on a plate, so the whole dock matches the pack:

| Field | Values | Default |
| :--- | :--- | :--- |
| `shape` | `None`, `Leaf`, `Rounded`, `Circle` | `None` |
| `sharpCorner` | `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight` (Leaf only) | `TopRight` |
| `cornerRadius` | `0` to `0.5`, a fraction of the icon size | `0.5` |
| `background` | `auto` (a pale tint of the icon) or a color such as `#FFFFFF` | `auto` |
| `iconScale` | `0.2` to `1.0`, the size of the icon on the plate | `0.64` |

`Leaf` rounds three corners and keeps one square, like Sailfish OS app icons.

## Licensing

Only use icons whose license allows it. Icon packs stay out of this repository; a starter `pack.json` is in [icon-pack-template](icon-pack-template/pack.json).
