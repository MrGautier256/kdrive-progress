# kDrive Progress

A small Windows tray app that shows **live, per-file progress** for everything the [kDrive](https://www.infomaniak.com/kdrive) desktop client (Infomaniak) uploads or downloads: percentage, bytes, speed, time left, plus a history of finished transfers. kDrive 3.8 only says a file is "syncing"; kDrive 4 has an activity view, but it is less detailed and tucked away in its settings window.

The interface follows the Windows display language: French on a French Windows, English otherwise (see [Languages](#languages)).

## Compatibility

| kDrive Progress | kDrive desktop for Windows |
| --- | --- |
| 2.0.0 and later | 4.0.x and 3.8.x (tested with 4.0.0.16 and 3.8.7.1) |
| 1.0.0 | 3.8.x (tested with 3.8.7.1); works with 4.0 but flags it as untested |

Windows 10 or 11. Each [release](https://github.com/MrGautier256/kdrive-progress/releases) states the kDrive versions it supports; the full history is in [CHANGELOG.md](CHANGELOG.md).

## Features

- **Tray icon** with a global progress ring and a tooltip (`kDrive · ↑2 ↓1 · 45,0 % · 38,2 Mo/s`).
- **Compact window**: transfers in progress at the top, recently finished ones at the bottom. Double-click a row to reveal the file in Explorer.
- **kDrive status at a glance**: up to date, scanning for changes, syncing (with the pass progress, for example "3 / 8 · 0:22 left"), paused, stopped, network unavailable.
- **kDrive storage quota** (kDrive 4): "kDrive storage: 669.89 GB of 3.00 TB (21.8%)".
- **Sync errors**: per-file failures in the history, with a plain-language cause ("an item with the same name already exists"), and with kDrive 4 an **Errors (n)** list that mirrors kDrive's own, updated as kDrive resolves them.
- **Daily totals** (uploaded and downloaded volume and file count).
- **Stalled transfers** (no progress for over a minute) are highlighted in orange.
- **Windows notifications** when transfers finish (minimum size configurable) or fail, and when a kDrive update is available (kDrive 4).
- **Extended log assistant** (kDrive 3.8): if kDrive's extended log is off, the app offers to turn it on for you or explains how to do it yourself. If you decline, the choice is remembered and a reminder stays in the window.
- **Log format check**: if a kDrive update stops writing the information the app relies on, the app says so (window banner and notification) instead of silently showing nothing.
- Optional: start with Windows, always on top, hide files under 1 MB.
- Light or dark theme, following Windows.

## Installation

1. Download `KDriveProgress.exe` from the [latest release](https://github.com/MrGautier256/kdrive-progress/releases/latest) and copy it anywhere (for example `%LOCALAPPDATA%\Programs\kDriveProgress\`). The release also contains its SHA-256 checksum.
2. Run it. No admin rights and nothing to install: it targets .NET Framework 4.8, which ships with Windows 10 and 11.
3. **kDrive 3.8 only**: enable kDrive's extended log. The app offers to do it on first launch, or you can do it yourself in kDrive: **Settings › Debugging settings › "Extended Full Log"** (takes effect immediately). Without it the app still works in a reduced mode: downloads stay accurate, but large uploads progress in steps and some file names only appear at the end. With kDrive 4 there is nothing to do.

Keep kDrive's **Debug level** set to **Debug** (the default).

Left-click the tray icon to show or hide the window, right-click for the menu. Closing the window hides it; use **Quit** in the menu to exit.

### Options

Available from the tray menu or the **Options** link at the bottom of the window:

- **Notifications**: on or off; for all files or only files of 10 MB, 100 MB (default) or 1 GB and more; failures notified separately. Simultaneous completions are grouped into one notification. Nothing is notified while the window is in the foreground. Clicking a notification opens the window.
- **Hide files under 1 MB**: lightens the lists during large syncs. Hidden transfers are still counted ("+ 12 small files") and their failures are always shown.
- **Window always on top**.
- **kDrive extended log…**: reopens the assistant (enable, or remove the environment variable the app set).
- **Start with Windows**.
- **Open the kDrive log folder**.

The app version is shown at the bottom of the menu.

## Languages

The language is picked from the Windows display language: French (`fr`) or English (`en`, also the fallback for any other language). Numbers and units follow the chosen language (`1,50 Go` or `1.50 GB`).

To force a language, set the user environment variable `KDRIVE_PROGRESS_LANG` to `en` or `fr` and restart the app.

Adding a language:

1. Copy `src/KDriveProgress/Localization/Lang.Fr.cs` to `Lang.Xx.cs` (for example `Lang.De.cs`), rename the method (`German()`) and translate the values. Keys that are left out fall back to English.
2. Register it in `Localization/L.cs`, in `Languages`: `["de"] = new KeyValuePair<…>(German, "de-DE")`.

Transfer statuses are stored in the history as language-neutral codes, so switching languages also translates past entries.

## How it works

The app is **strictly read-only towards kDrive**: it never writes to kDrive's files or databases, never connects to any of its ports, and never stops its processes.

| Source | What it provides |
| --- | --- |
| Engine log `%TEMP%\kDrive-logdir\YYYYMMDD_HHMM_kDrive.log`, opened in shared mode | Start and end of transfers, path, size, sync steps and errors |
| kDrive 4: the JSON messages the engine sends to its own interface, which it writes to that same log (`guicommserver.cpp - Sending message: {…}`) | Per-file progress with direction and status, pass progress and kDrive's time estimate, exact sync state, storage quota, kDrive's error list, update availability |
| kDrive 3.8 with the extended log: `Send progress … progress=N%` lines | Per-file progress, about once per second |
| Temporary files `<kDrive root>\.kDrive-cache\kdrive_*` | Bytes received during a download |
| Cloud Files API (`CfGetPlaceholderRangeInfo`) on the placeholder | Bytes already hydrated for on-demand ("online-only") files |

The app uses the kDrive 4 messages whenever they are present in the log and falls back to the kDrive 3.8 sources otherwise, so one build serves both versions.

File sizes are read through attributes-only handles (`FILE_READ_ATTRIBUTES`), which never conflict with kDrive's sharing mode and never trigger the download of a placeholder.

The app copes with kDrive restarts (new log file), log rotation at 500 MiB, a second kDrive launched while one is already running, sleep and network outages.

### Enabling the extended log for you (kDrive 3.8)

kDrive reads the environment variable `KDRIVE_FORCE_EXTENDED_LOG=1` when it starts (present in `kDrive.exe` 3.8.7.1 and 4.0.0.16). "Turn on automatically" sets this variable for your Windows account (`HKCU\Environment`); nothing is changed inside kDrive. kDrive must then be restarted: quit it from its tray menu, and kDrive Progress relaunches it within the next 15 minutes. The variable can be removed from the same assistant.

### Log format check

On every kDrive start the app checks that the information it needs is still there: line format, log mode, sync root, network request lines, `Send progress` lines (kDrive 3.8) or interface messages (kDrive 4). Any missing piece is listed under **Details** at the bottom of the window (copyable with Ctrl+C), together with the kDrive version and log level, so the parser can be fixed. kDrive versions other than 3.8.x and 4.0.x are flagged as untested.

### Why not kDrive's local "comm server"?

The kDrive engine runs a local server that broadcasts per-file progress. **Do not connect to it**: since 3.8.6, when the last client of that server disconnects, `kDrive.exe` exits. This was confirmed on 2026-10-11 with kDrive 3.8.7: a 4-second connection shut down both the engine and the UI. kDrive 4 no longer publishes the port (`%LOCALAPPDATA%\kDrive\.comm`) and is closing that channel to third parties. Reading the messages from the log gives the same information without any of that risk.

### App data

Stored in `%LOCALAPPDATA%\kDriveProgress\`: `history.tsv` (last 100 transfers), `settings.ini`, `today.ini` (daily totals). Files are written through a flushed temporary file with a `.bak` copy, so a power cut cannot wipe them. If "Start with Windows" is on, the app also adds a `kDriveProgress` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Known limitations

- A kDrive update may change the log; the format check is there to make that visible. With kDrive 4, the main source exists because kDrive logs every message sent to its interface; Infomaniak could drop that logging in a future version, in which case the app falls back to the kDrive 3.8 sources.
- kDrive truncates logged messages at 2048 characters; this only affects large lists, never per-file progress.
- The extended log is large while kDrive scans the whole drive (more than 1 MB/s right after a PC restart). kDrive compresses it and caps its log folder at 2 GB.
- Chunked uploads progress in 10 to 100 MiB steps.
- With kDrive 3.8, the direction of a transfer seen only through a `Send progress` line is inferred from the local file state, and "paused" is inferred from kDrive's sync steps.

## Building

Requires the .NET SDK 6 or later.

```
build.cmd
```

The executable is copied to `dist\KDriveProgress.exe`. If the app is running from `dist\`, quit it first: Windows locks the file.

To rebuild and restart in one step, use:

```
deploy.cmd
```

It asks the running app to quit cleanly (`KDriveProgress.exe --quit`, which saves the history), forces it only if it does not respond, runs `build.cmd`, then relaunches `dist\KDriveProgress.exe`. If the build fails, the previous version is relaunched.

Diagnostic tool: `KDriveProgress.exe --replay <file.log> <output.txt>` replays a (decompressed) kDrive log and writes the reconstructed transfers, kDrive state, kDrive 4 data and log format check.

## Releasing

1. Add a section for the new version at the top of [CHANGELOG.md](CHANGELOG.md), starting with its compatibility line:

   ```
   ## [2.1.0] - 2026-11-02

   **Compatible with kDrive:** 4.0.x and 3.8.x for Windows (tested with …).
   ```

2. Commit, then push a version tag:

   ```
   git tag v2.1.0
   git push origin v2.1.0
   ```

GitHub Actions (`.github/workflows/release.yml`) builds the app, takes that CHANGELOG section as the release description, and publishes `KDriveProgress.exe` with its SHA-256 checksum. The release fails if the section or its "Compatible with kDrive" line is missing. The tag sets the version shown in the app and in the executable's properties. Tags must look like `v1.2.3`; a suffix such as `v1.2.3-beta.1` publishes a pre-release. Progress is visible in the repository's **Actions** tab.

## Uninstalling

Untick "Start with Windows", quit the app, then delete the executable and the `%LOCALAPPDATA%\kDriveProgress` folder. If the app set `KDRIVE_FORCE_EXTENDED_LOG`, remove it first from **Options › kDrive extended log…**; the extended log can also be turned off in kDrive.

## Repository contents

- `src/KDriveProgress/`: the app (C# WinForms, .NET Framework 4.8); texts are in `Localization/`.
- `build.cmd`: build script; `deploy.cmd`: quit, rebuild and relaunch.
- `CHANGELOG.md`: versions and kDrive compatibility.
- `.github/workflows/`: release pipeline (`release.yml`) and build check (`build.yml`).

## License

[MIT](LICENSE) © 2026 Kronys.

The kDrive desktop client itself is licensed under GPL-3.0. This app reuses no kDrive code; it only reads the log the client writes.
