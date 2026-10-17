# Changelog

Each release states which kDrive desktop versions it works with. The release pipeline publishes the section of the pushed version as the release description, and refuses to publish if that section or its compatibility line is missing.

## [2.1.0] - 2026-10-17

**Compatible with kDrive:** 4.0.x and 3.8.x for Windows (tested with 4.0.0.16 and 3.8.7.1), on Windows 10 or 11.

### Changed

- The window now closes when you click another window, like other tray windows. A click on the tray icon while the window is open still closes it.

### Removed

- The **Window always on top** option, which no longer has a purpose now that the window closes as soon as another window is in front.

### Fixed

- With several screens, the window could open on a secondary screen and stay there: its position was chosen once, from wherever the mouse was the first time it opened. It now opens next to the notification area every time, on the screen where the tray icon was clicked (a screen with a copy of the tray icons, for example), or else on the screen of the main taskbar.

## [2.0.0] - 2026-10-15

**Compatible with kDrive:** 4.0.x and 3.8.x for Windows (tested with 4.0.0.16 and 3.8.7.1), on Windows 10 or 11.

### Added (kDrive 4)

- Per-file progress read from the messages kDrive 4 sends to its own interface: direction and status come straight from kDrive, and the extended log is no longer needed.
- Overall progress of the current sync pass and kDrive's own time estimate in the header ("Syncing · 3 / 8 · 0:22 left"), and the exact kDrive state, including a manual pause, a stopped sync or an error.
- kDrive storage quota in the header.
- Notification when a kDrive update is available or ready to install.
- kDrive's own list of sync errors ("Errors (n)" link above the history), updated as kDrive resolves them, with causes and conflict types in plain language.

### Added (all kDrive versions)

- Automatic updates: the app checks GitHub for a newer release at startup and every 12 hours, downloads it, verifies its SHA-256 checksum and installs it as soon as no transfer is in progress (at the latest after 30 minutes), then restarts itself. Both checking and automatic installation can be turned off in Options; with automatic installation off, an "Install" button appears at the bottom of the window. "Check for updates now" is in the menu too.

### Changed

- kDrive 4.0 is a tested version. The extended log assistant only appears with kDrive 3.8.
- Daily totals only show the directions that had transfers.

### Fixed

- With kDrive 4, relaunching kDrive could start the interface launcher (`client\kDrive.exe`) instead of the sync engine.
- With kDrive 4, a download started by opening an online-only file is no longer at risk of being taken as finished (kDrive announces it with "100 %").
- A failed upload reported only by kDrive's interface messages now appears in the history.

## [1.0.0] - 2026-10-12

**Compatible with kDrive:** 3.8.x for Windows (tested with 3.8.7.1), on Windows 10 or 11. It also works with kDrive 4.0, but reports it as an untested version; use 2.0.0 or later with kDrive 4.

First release:

- Live per-file progress of uploads and downloads (percentage, bytes, speed, time left) in a tray icon and a compact window, with a history of finished transfers.
- kDrive state, per-file sync errors, daily totals, stalled transfers, notifications.
- Extended log assistant, log format check, English and French interface.
