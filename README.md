# Kinetik Clock: world clock widget for the Windows desktop

[![Latest release](https://img.shields.io/github/v/release/WastedDesigner/KinetikClock?label=download)](../../releases/latest)
[![License: GPL v3](https://img.shields.io/github/license/WastedDesigner/KinetikClock)](LICENSE)
![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)

**Kinetik Clock** is a free, open-source **world clock desktop widget for Windows 11 and Windows 10**. It shows the time in as many places as you like, each with a small analog dial that turns dark at night, so you can see at a glance who's awake.

It's a small portable app with no installer and no account. It's a companion to [Kinetik](https://github.com/WastedDesigner/Kinetik), the taskbar system monitor, and [Kinetik Calendar](https://github.com/WastedDesigner/KinetikCalendar), and shares their look.

> **About this project:** I made this for my own desktop and decided to share it in case anyone else finds it useful. If you run into a bug or have an idea for a feature, please [open an issue](../../issues).

## Features

- **Any number of places**, each with its own name and Windows time zone (daylight saving handled by Windows)
- **Analog dial per place** that turns dark at night
- **Themes** in Kinetik's style, a choice of fonts, size and background opacity
- **Placement:** drag anywhere, snap to edges, lock position
- **Settings backup** next to the exe, offered again on a fresh Windows

## Download and use

1. Download `KinetikClock.zip` from [Releases](../../releases/latest) and extract it anywhere.
2. Run `KinetikClock.exe`. Right-click the widget for places, style, size, placement and backup.

Settings are kept in `HKCU\Software\KinetikClock`. The app makes no network connections.

## Requirements

Windows 10 or 11 (64-bit) with .NET Framework 4.8, which ships with both.

## Building from source

No SDK needed: `build.bat` compiles `KinetikClock.cs` with the .NET Framework compiler that ships with Windows. `build.bat package` also makes `KinetikClock.zip` for a release.

## License

[GPL-3.0-or-later](LICENSE). Copyright © 2026 Akila Sella Hennedige.
