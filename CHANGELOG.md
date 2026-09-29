# 📋 Changelog

All notable changes to **Win11 Optimizer** are listed here, newest first.
Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

A cleanup release: a large internal refactor (≈34% less code), plus a batch of crash, hang, and safety fixes found along the way. Every tweak writes the same registry values and runs the same commands as before — this was verified by running the old and new tweak engines side by side against a sandboxed registry.

### ✨ Added — Laptop section

- **18 new Laptop tweaks**: Hibernate After 60 Min Asleep, Re-Sleep Fast After Auto-Wakes, Dim Screen After 1 Min, Cap Battery Brightness at 40%, Lock Screen Off After 30 Sec, Passive Cooling & 5% Min CPU, Lid Close = Sleep on Battery, Earlier Low-Battery Warnings, Pause Slideshow / Allow Media Sleep, Spin Down Hard Disk After 5 Min, Graphics Power Slider: Battery, Browsers & Teams on Integrated GPU, Block Voice Activation, Disable Cross-Device Sync, Disable Search Highlights, Disable Settings Sync, No Maintenance Wake-Ups, Network Adapter: No Wake-on-LAN. All are backed up and undoable.
- **Tiered Laptop preset.** The 💻 Laptop button now opens a menu: **Light** (invisible tweaks only), **Balanced** (what the old preset did, plus the new everyday tweaks) and **Max battery** (adds the brightness cap, passive cooling and background Store-app block).
- **Hardware-aware Laptop tiles.** Tweaks for hardware you don't have (ambient light sensor, hard disk, Wi-Fi, second GPU, Intel/AMD power slider) are hidden. Tweaks that can't work on this PC are greyed out with the reason and can't be selected — battery-only tweaks on a desktop, and *No Network in Modern Standby* on S3-sleep machines — so presets, Select All and profile imports skip them.
- **Current value in the tooltip.** Hovering a power-plan tweak shows what the setting is right now and what the tweak will change it to.

### 🔄 Changed (Laptop)

- Battery timeout, level and threshold tweaks now only tighten: they no longer overwrite a value you already set stricter (for example Energy Saver at 40%, or a screen timeout under 3 min), and report "already set" instead of rewriting it.
- Power settings are read from a single `powercfg /qh` snapshot instead of one process per setting, so the startup detection scan is much faster and doesn't depend on Windows' display language.

### 🐛 Fixed

- **Startup Manager — Enable All / Disable All crashed the app.** Toggling every entry re-triggered each row's own toggle handler, which flipped the entry back and recursed until the app ran out of stack. A failed toggle also showed its error message twice.
- **Launching a second copy crashed on exit.** A second instance tried to release a single-instance lock it never owned. It now exits cleanly with code `1` (CLI) or a "already running" message (GUI).
- **Relaunch as Administrator could race itself.** The unelevated copy now releases its lock *before* starting the elevated one.
- **Disk Cleanup could hang forever.** Long-running commands like DISM `StartComponentCleanup` were started with their output redirected but never read — once the output buffer filled, the command stalled and "Cleaning..." never finished. Every external command now goes through one runner that always drains its output.
- **Driver Cleanup safety:** if the check for in-use drivers failed or came back empty, *every* driver package was marked "Unused" and selectable — including drivers for devices currently plugged in. Now a failed check locks everything instead.
- **Disk Cleanup accuracy:** one protected subfolder no longer zeroes out an entire category's size or aborts its cleanup; hidden/system files are counted consistently.
- **Background detection race:** the startup detection scan and the UI could write the applied-tweaks list at the same time. Same for toggling two services at once in the Services tab. Both are now thread-safe.
- **Memory / handle leaks:** switching categories, rescanning, or reopening History created new tiles, rows and fonts without releasing the old ones. Long sessions no longer grow.
- **Buttons stuck disabled:** if a scan, run, or undo hit an unexpected error, its buttons stayed greyed out until restart. Errors are now logged and shown, and the buttons come back.
- **Driver Cleanup warning banner** was clipped at 200px wide.
- **Output log panel** didn't follow the window when resized.
- **"&" in tweak names** (e.g. *Inking & Typing*) disappeared in tooltips and history.
- **Network undo:** a failure restoring Nagle's algorithm is now reported instead of silently ignored.
- **Profile import** now matches tweak keys case-insensitively in its "X of Y matched" message, the same way it actually applies them, and reports how many tweaks were really selected.
- **History** no longer crashes on a hand-edited `changelog.json` with a missing `Details` field.

### 🔄 Changed

- **Gold buttons now use dark text** (Select All, Clean Selected, Run). A leftover colour check from the old lime theme was giving them white text and a border.
- **Undo is no longer offered for Bloatware.** App removal can't be undone, but the Undo button used to light up for it anyway.
- **Importing a profile switches to the "All" view first**, like presets already did, so tweaks in other categories get selected too.
- **Declining the UAC prompt** after choosing "relaunch as Administrator" now continues unelevated (with the warning badge) instead of silently closing.
- **Rebooting from the prompt** now reports it if `shutdown.exe` fails.
- **App version** is now read from the build itself, so `<Version>` in the `.csproj` and `MyAppVersion` in the `.iss` are the only two places to bump.
- **Output log** uses a dark scrollbar to match the rest of the UI.
- **Startup Manager** rows for disabled entries use the same dimmed text colour as other tabs.

### 🧰 Internal

- Four list tabs (Startup, Services, Driver Cleanup, Disk Cleanup) now share one base layout instead of four copies.
- Registry paths are shared between applying and detecting a tweak, so the two can't drift apart.
- Laptop power-plan tweaks and their detection come from a single table.
- `TweakDetector` moved to its own file; the unused `AdminWarning` class was removed.
- Previously silent `catch { }` blocks now write to the session log in `Data\logs\`.

### ⚠ Known issues

- **GPU Power: Prefer Maximum Performance** also sets *Turn off display after* to **Never** when plugged in, and Undo doesn't restore it. Flagged in code; fix pending.
- The Startup, Services, Driver Cleanup and Disk Cleanup tabs don't scale with display DPI yet — they look small at 150%+ scaling.

---

## Earlier versions

Release notes for 1.5.0 and earlier are on the [GitHub Releases](https://github.com/Corn-Systems/win11op/releases) page.
