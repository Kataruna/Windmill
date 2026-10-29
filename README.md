<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="Windmill-white.svg">
    <img src="Windmill.svg" alt="Windmill logo" width="128">
  </picture>
</p>

# Windmill

A gesture-based window snapper for Windows. Hold a key, flick the mouse toward where you want the window, let go.

## Why

Windmill is inspired by [Loop](https://github.com/MrKai77/Loop), a window manager for macOS. I love Loop's radial gesture, where you hold a key and push the mouse in a direction to snap a window, but I couldn't find anything like it on Windows. So I made one.

Windmill is an independent project. It isn't affiliated with or endorsed by Loop or its authors, and it shares no code with it.

## Features

- **Radial snap menu.** Hold **Alt** over a window and a ring appears under the cursor. Move toward a direction to pick a half or quarter, push a short way up for full screen, and release to snap.
- **More layouts.** Press **Tab** or scroll to switch between the ring and miniature zone maps: halves, thirds, priority columns, rows, quarters, a 3×2 grid and more.
- **PowerToys FancyZones layouts.** Custom layouts you made in FancyZones appear in the list automatically.
- **Auto-arrange.** Hold **Alt + Shift** to see your open windows. Click up to six in order, then release to tile them; the first one you click gets the most space.
- **Native Windows snap.** Halves and quarters use real Windows snap, so you get snap groups and Windows' own restore-on-drag. Other zones are moved and resized, and still return to their old size when you drag them away.
- **Hold or Toggle.** Each menu can open while the shortcut is held, or open with a tap and close with a click.
- **Looks.** An optional frosted blur behind the menu, an adjustable ring size, and an optional gap between snapped windows.
- **Lives in the tray**, with an option to start with Windows.

## Controls

| | Snap menu (default **Alt**) | Auto-arrange (default **Alt + Shift**) |
|---|---|---|
| Open | Hold the shortcut | Hold the shortcut |
| Choose | Move the mouse toward a zone | Click windows in order |
| Change layout | **Tab** / **Shift + Tab** or scroll | — |
| Confirm | Release the shortcut (Toggle: click, Enter or the shortcut again) | Release the shortcut (Toggle: Enter or the shortcut again) |
| Cancel | **Esc** | **Esc** |

Pressing another key before the menu appears cancels it, so normal shortcuts like Alt+Tab or Alt+F4 keep working. If quick shortcuts still open the menu, raise **Hold delay** in Settings.

## Settings

Right-click the tray icon and choose **Settings…**, or double-click the tray icon.

- **Activation:** turn the snap menu on or off, record its shortcut, choose Hold or Toggle, set the hold delay and the ring size.
- **Auto-arrange:** turn it on or off, record its shortcut, choose Hold or Toggle.
- **Appearance:** blur behind the menu. It's a snapshot taken when the menu opens, so a video playing behind the menu pauses while the menu is open.
- **Windows:**
  - **Snap style:** **Native** uses real Windows snap where possible; **Classic** always just moves and resizes.
  - **Snap Assist:** keeps Windows' "pick a window for the other side" panel after a snap. Off by default.
  - **Gap between windows.**
  - **Include PowerToys FancyZones layouts.**
- **General:** start with Windows.

Settings are stored in `%APPDATA%\Windmill\settings.json`.

## Install

Run `WindmillSetup-<version>.exe`. It installs for your user only, with no admin prompt, into `%LOCALAPPDATA%\Programs\Windmill`. It adds a Start menu shortcut and can start Windmill when you sign in. To install for all users, choose that option in the first dialog.

Uninstall from **Settings → Apps**. Your settings in `%APPDATA%\Windmill` are kept.

## Build from source

### Requirements

- Windows 10 or 11 (x64)
- [.NET 9 SDK](https://dotnet.microsoft.com/download) or newer
- [Inno Setup 6](https://jrsoftware.org/isinfo.php), only to build the installer

### Build

```
git clone https://github.com/Kataruna/Windmill.git
cd Windmill
dotnet publish -p:PublishProfile=dist
```

This produces `dist\Windmill.exe`, a single self-contained file that doesn't need .NET installed. Start it and look for the pinwheel icon in the notification area.

To also build the installer:

```
ISCC installer\Windmill.iss
```

This produces `dist\WindmillSetup-<version>.exe`. The version comes from `<Version>` in `Windmill.csproj`.

For development, `dotnet build` and `dotnet run` also work.

## Limitations

- Native snap only applies to halves and quarters with the gap set to 0. Everything else, and all of auto-arrange, is moved and resized by Windmill.
- Windmill can't move windows of apps running as administrator unless Windmill also runs as administrator. This is a Windows restriction.
- The snap shortcut uses a global keyboard hook. While a menu is open, Windmill also briefly installs a mouse hook for scrolling and clicks.

## AI usage disclosure

This project was built with substantial help from AI. Much of the code, along with this README, was written using [Claude](https://www.anthropic.com/claude) (by Anthropic) through Claude Code. I directed the design, made the decisions, and tested the app on my own machine.

AI-written code can contain mistakes. If you find a bug, please open an issue.
