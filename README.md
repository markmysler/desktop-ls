# DesktopLS

[![CI](https://github.com/markmysler/desktop-ls/actions/workflows/ci.yml/badge.svg)](https://github.com/markmysler/desktop-ls/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/markmysler/desktop-ls)](https://github.com/markmysler/desktop-ls/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A lightweight navigation bar for the Windows desktop. Point your desktop at any folder on disk, then browse, go back and forward, and switch between folders like a file manager, while each folder keeps its own icon arrangement.

![DesktopLS bar with path autocomplete](docs/demo.gif)

## Features

- **Show any folder on the desktop**: type a path (with autocomplete) and press Enter; the desktop shows that folder's contents
- **Back / Forward / Up** with history (Alt+Left, Alt+Right, Alt+Up) and **Refresh** (F5)
- **Path autocomplete**: subfolders are suggested as you type; Tab moves through the suggestions, Enter or double-click opens one
- **Icon layout memory**: icon positions are saved per folder and per monitor setup, and restored when you come back
- **First-visit grid**: a folder you've never opened gets its icons laid out in a tidy grid across all monitors
- **Auto-hide** (on by default): the bar hides while another window is maximized
- **Crash recovery**: your real desktop is recorded before any change and restored on exit, on errors, at log-off, and on the next start after a crash
- **Single `.exe`**: the release build is self-contained; no .NET install needed

## What DesktopLS changes on your system

DesktopLS changes Windows settings, so here is exactly what it touches and how to undo it.

| What | Where | When it's undone |
|---|---|---|
| Your **Desktop folder location** | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders` → `Desktop` (via `SHSetKnownFolderPath`) | When you close DesktopLS, when it crashes or Windows logs off, or at the latest the next time it starts |
| A recovery file holding your real desktop path | `%LocalAppData%\DesktopLS\desktop_original.txt` | Deleted once the desktop is restored |
| Saved icon layouts and settings | `%LocalAppData%\DesktopLS\layouts.json`, `settings.json` | Kept; delete the folder to reset |
| "Run on startup" (only if you enable it) | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `DesktopLS` | Removed when you turn the setting off |

It never modifies the shared **Public Desktop** (`C:\Users\Public\Desktop`); its shortcuts stay visible on any folder you open. (Version 1.0.0 did move them; starting a newer version puts back anything it left behind.)

**If your desktop is ever stuck on another folder**, start DesktopLS once and close it, or fix it by hand: right-click your Desktop folder in File Explorer → **Properties** → **Location** → **Restore Default** → OK.

## Installation

**Download:** get `DesktopLS.exe` from the [latest release](https://github.com/markmysler/desktop-ls/releases/latest), put it anywhere (for example `%LocalAppData%\Programs\DesktopLS`), and run it. Windows 10 or 11, x64.

To start it with Windows, open the ⋮ menu and enable **Run on startup**.

A winget package is planned but not published yet.

**Build from source** (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```
git clone https://github.com/markmysler/desktop-ls.git
cd desktop-ls
dotnet run --project src/DesktopLS/DesktopLS.csproj
```

Single-file release build: `dotnet publish src/DesktopLS/DesktopLS.csproj -c Release`, output in `src/DesktopLS/bin/Release/net8.0-windows/win-x64/publish/`.

## Usage

| Action | How |
|---|---|
| Open a folder | Type a path and press Enter (or pass it as an argument: `DesktopLS.exe "D:\Projects"`) |
| Autocomplete | Type; **Tab** moves through suggestions, **Enter** opens the selected one, **Esc** closes the list |
| Back / Forward | ← → buttons, or Alt+Left / Alt+Right |
| Up one level | ↑ button or Alt+Up |
| Refresh | ↻ button or F5 |
| Settings | ⋮ button (run on startup, hide when a window is maximized) |
| Close and restore your desktop | ✕ button |

If a folder can't be opened (deleted, no access), the bar says so instead of changing anything.

## How it works

- **Redirecting the desktop:** `SHSetKnownFolderPath(FOLDERID_Desktop, …)`, followed by `SHChangeNotify` so Explorer refreshes. The original path is saved first so every exit path can restore it.
- **Icon positions:** Explorer's desktop is a `SysListView32` control in another process. DesktopLS allocates a buffer inside Explorer (`VirtualAllocEx`), sends `LVM_GETITEMW` / `LVM_GETITEMPOSITION` / `LVM_SETITEMPOSITION32`, and reads the results back with `ReadProcessMemory`. Layouts are stored in `layouts.json`, keyed by folder and monitor configuration.
- **Auto-hide:** checks the foreground windows for a maximized one every 500 ms.

## Development

```
dotnet build DesktopLS.sln
dotnet test DesktopLS.sln
```

CI builds and tests every push. Pushing a `v*` tag publishes the single-file `DesktopLS.exe` to a GitHub release.

## License

[MIT](LICENSE)
