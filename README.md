# DesktopLS

A lightweight Windows desktop navigation bar. Point your desktop at any folder on disk — browse, navigate back and forward, and switch between folders just like a file manager, all while keeping your icons arranged exactly where you left them.

## Features

- **Redirect the desktop to any folder** — type a path (or use autocomplete) and press Enter; the desktop immediately shows that folder's contents
- **Back / Forward / Up navigation** — full history stack with keyboard shortcuts (Alt+Left, Alt+Right, Alt+Up)
- **Refresh** — re-applies the current folder (F5)
- **Path autocomplete** — starts suggesting subdirectories as you type; press Tab to cycle through suggestions, Enter to navigate, or double-click to navigate immediately
- **Settings menu** — click ⋮ button to configure run-on-startup and auto-hide behavior
- **Auto-hide on maximize** (default: on) — toolbar automatically hides when another window is maximized on the primary monitor; can be disabled in settings
- **Icon layout memory** — icon positions are saved per folder and per monitor configuration; switching folders restores exactly where you left your icons
- **First-visit auto-grid** — icons in a folder you have never visited are placed in a tidy column-major grid across all monitors
- **Public Desktop isolation** — items from `C:\Users\Public\Desktop` are hidden while browsing other folders and restored when you return to your original desktop
- **Crash-safe** — the original desktop path is recorded before any redirect; if the process crashes, the next launch automatically restores it
- **Zero dependencies** — the Release build is a single self-contained `.exe`; no .NET runtime installation required

## System requirements

- Windows 10 or Windows 11 (x64)
- No additional software required for pre-built binaries

## Installation

### Option 1 — winget (recommended)

```
winget install MarkX.DesktopLS
```

> Note: winget availability depends on the package being published to the winget repository. Check the [Releases](../../releases) page if winget reports the package as not found.

### Option 2 — Download pre-built binary

1. Go to [Releases](../../releases) and download `DesktopLS.exe` from the latest release
2. Place it anywhere (e.g. `%LocalAppData%\DesktopLS\DesktopLS.exe`)
3. Double-click to run — no installer needed
4. To run on startup, open the app and enable "Run on startup" in Settings (⋮ button)

### Option 3 — Build from source

**Prerequisites:**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 x64

**Clone and run (debug build):**
```
git clone https://github.com/markmysler/desktop-ls.git
cd desktop-ls
dotnet run --project src/DesktopLS/DesktopLS.csproj
```

**Publish self-contained single executable:**
```
dotnet publish src/DesktopLS/DesktopLS.csproj -c Release
```

The output is at:
```
src/DesktopLS/bin/Release/net8.0-windows/win-x64/publish/DesktopLS.exe
```

## Usage

| Action | How |
|---|---|
| Navigate to a folder | Type a path in the bar and press Enter |
| Autocomplete | Start typing — press Tab to cycle through suggestions, Enter to select and navigate |
| Back / Forward | Buttons or Alt+Left / Alt+Right |
| Go up one level | Button or Alt+Up |
| Refresh | Click ↻ or press F5 |
| Open settings | Click ⋮ button |
| Toggle run on startup | Settings → Run on startup |
| Toggle auto-hide | Settings → Hide when window maximized |
| Close (restore original desktop) | Click ✕ |

**Keyboard shortcuts in autocomplete:**
- **Tab** — cycle through suggestions (fills path bar with each suggestion)
- **Enter** — navigate to the currently selected/shown suggestion
- **Escape** — close autocomplete dropdown

## How it works

DesktopLS calls `SHSetKnownFolderPath` to redirect the `FOLDERID_Desktop` known folder to the target path. Windows Explorer immediately shows the new folder's contents. Icon positions are read and written via cross-process `LVM_GETITEMPOSITION` / `LVM_SETITEMPOSITION32` messages sent to Explorer's `SysListView32` window. Layouts are stored as JSON in `%LocalAppData%\DesktopLS\layouts.json`, keyed by path and monitor configuration.

## License

MIT
