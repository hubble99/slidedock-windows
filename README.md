# SlideDock

A modern dock that replaces the Windows 10/11 taskbar. Single C# file, built with the compiler that already ships with Windows. No installer, no Visual Studio.

## Features

- **Dock** with pinned and running apps, live window previews (hover), magnify on hover
- **Notch mode**: shrinks to a small pill when idle, expands when the mouse comes near
- **Start**: your chosen apps (drag to reorder), clock, Wi‑Fi/battery/disk info, background (tray) apps, Settings, Task Manager, power buttons (update-aware Restart/Shut down)
- **Search** for apps, files and the web
- **Quick Settings**: brightness, volume, Wi‑Fi list and connect
- **Calendar + notifications** (dismiss one or clear all)
- **Themes**: Glass (frosted blur) or Dark
- **Position**: bottom, left or right; one dock per monitor, per‑monitor DPI
- **Windows key** opens Start (disabled automatically in fullscreen games and presentations)

## Requirements

- Windows 10 or 11
- .NET Framework 4.8 (built into Windows 10 1903+ and Windows 11)
- The Windows SDK is optional: if it isn't installed, the build uses the metadata files that come with Windows

## Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This creates `SlideDock.exe` next to the source.

## Run

Double-click `SlideDock.exe`. The Windows taskbar is hidden while SlideDock runs.

**Options**: right-click the Start button on the dock (or an empty area of the dock):
- Theme: Glass / Dark
- Position: Bottom / Left / Right
- Windows key opens dock
- Start with Windows
- Open settings folder
- **Exit SlideDock (restore taskbar)**: closes the dock and brings the normal taskbar back

## Settings

Stored per user in `%APPDATA%\SlideDock` (pinned apps, Start apps, theme, position). Delete that folder to reset.

## Notes

- Notifications in the calendar need permission the first time (Windows asks automatically).
- If SlideDock ever crashes, the taskbar is restored on exit; errors are written to `%APPDATA%\SlideDock\error.log`.
