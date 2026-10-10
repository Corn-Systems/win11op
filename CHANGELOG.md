# 📋 Changelog

All notable changes to **Win11 Optimizer** are listed here, newest first.
Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [1.5.1] - 2026-10-10

A big release: 31 new tweaks, a new Laptop section, a preset overhaul, and a large internal refactor (≈34% less code) with a batch of crash, hang, and safety fixes found along the way. Every tweak that existed before the refactor writes the same registry values and runs the same commands as before — this was verified by running the old and new tweak engines side by side against a sandboxed registry. The tweaks added or changed afterwards are listed below.

### ✨ Added — 31 new tweaks

**Privacy (8)**
- **Disable App-Compat Telemetry & Steps Recorder** — the policy-level switches behind the Compatibility Appraiser and CEIP (`AITEnable`, `DisableInventory`, `DisableUAR`, `CEIPEnable`), which no existing tweak covered. *On by default.*
- **Disable Cloud Search & Search History** — Microsoft-account / work-or-school cloud search and on-device search history. *On by default.*
- **Disable Online Speech Recognition**, **Disable Clipboard Sync**, **Disable Edge Telemetry & Recommendations**, **Block App Diagnostics & Call History Access**, **Hide Recent Files & Frequent Folders**, **Disable OneDrive File Sync**.

**Responsiveness (4)**
- **Disable Nag Screens & Explorer Ads** ("finish setting up your device", sync-provider ads, Start account notifications) and **No Automatic Restart While Signed In** — *both on by default.*
- **File Explorer Opens to This PC**, **Clean Taskbar (No Search Box / Task View)**.

**Performance (2)** — **Disable Fast Startup** (real cold boot on shutdown) and **Disable Reserved Storage** (≈7 GB back).

**Gaming (2)** — **Optimizations for Windowed Games** (merges into the existing `DirectXUserGlobalSettings` list) and **Desktop Latency Power Settings (Plugged In)** — USB selective suspend and PCIe link power management off on the AC side only, the desktop counterpart of the Laptop tweaks.

**Network (1)** — **Network Adapter: No Power Saving**: stops Windows powering the NIC down and disables Energy-Efficient / Green Ethernet where the driver exposes it. Only the "allow the computer to turn off this device" bit is changed; Wake-on-LAN settings are left as they were.

**Security (7)**
- **Disable LLMNR** — the NetBIOS tweak's own description mentions LLMNR poisoning, but nothing actually disabled LLMNR.
- **Ensure Windows Firewall Is On**, **Defender: Block Unwanted Apps & Network Protection**, **Remove PowerShell 2.0**, **Disable Remote Assistance**, **Always Show File Extensions**.
- **Disable Teredo, 6to4 & ISATAP Tunnelling** — tooltip warns that some Xbox Live / peer-to-peer games use Teredo for NAT traversal.

**Advanced (2)** — **Disable VBS & Memory Integrity** and **Disable Multiplane Overlay**. Both are deliberately in *no* preset.

**Bloatware (5)** — **Copilot app**, **Cortana**, **Get Help / Tips / Feedback Hub**, **New Outlook & Mail and Calendar**, **Mixed Reality Portal / Dev Home / Power Automate**. *Remove Third-Party Ad Tiles* also now catches Candy Crush, Netflix, Prime Video, Duolingo, Hulu, Pandora, Twitter and Adobe Express.

All of them have live "already applied" detection (except where Windows exposes no readable state), reboot / Explorer-restart tagging, and Undo.

### ✨ Added — Laptop section

- **18 new Laptop tweaks**: Hibernate After 60 Min Asleep, Re-Sleep Fast After Auto-Wakes, Dim Screen After 1 Min, Cap Battery Brightness at 40%, Lock Screen Off After 30 Sec, Passive Cooling & 5% Min CPU, Lid Close = Sleep on Battery, Earlier Low-Battery Warnings, Pause Slideshow / Allow Media Sleep, Spin Down Hard Disk After 5 Min, Graphics Power Slider: Battery, Browsers & Teams on Integrated GPU, Block Voice Activation, Disable Cross-Device Sync, Disable Search Highlights, Disable Settings Sync, No Maintenance Wake-Ups, Network Adapter: No Wake-on-LAN. All are backed up and undoable.
- **Tiered Laptop preset.** The 💻 Laptop button now opens a menu: **Light** (invisible tweaks only), **Balanced** (what the old preset did, plus the new everyday tweaks) and **Max battery** (adds the brightness cap, passive cooling and background Store-app block).
- **Hardware-aware Laptop tiles.** Tweaks for hardware you don't have (ambient light sensor, hard disk, Wi-Fi, second GPU, Intel/AMD power slider) are hidden. Tweaks that can't work on this PC are greyed out with the reason and can't be selected — battery-only tweaks on a desktop, and *No Network in Modern Standby* on S3-sleep machines — so presets, Select All and profile imports skip them.
- **Current value in the tooltip.** Hovering a power-plan tweak shows what the setting is right now and what the tweak will change it to.

### 🔄 Changed — presets and defaults

- **The Security preset no longer weakens security.** It was built from "Recommended + Security", so it picked up *Disable SmartScreen* and the telemetry hosts block (which Defender flags as a hosts-file hijack). It now excludes both.
- **SmartScreen off, Block App Camera Access and Disable Location Tracking are no longer on by default.** They were part of "Recommended" and therefore of the Gaming, Security and Laptop presets, even though they trade security or app functionality (Store camera/location apps) for privacy. They remain in the Privacy preset; SmartScreen-off is also excluded from Clean Install and the Laptop presets.
- **Gaming preset** no longer includes DNS-over-HTTPS (it swaps your DNS provider — nothing to do with gaming) and adds Fast Startup off.
- **Privacy preset** leaves out the new OneDrive sync-off tweak (a workflow decision, not a privacy default); it's in Clean Install and Nuclear only.
- **Dev Machine preset** leaves out DNS-over-HTTPS (breaks VPN / split-DNS), Reserved Storage and the taskbar cleanup, and adds app-compat telemetry and file extensions.
- **Clean Install preset** also applies the new security baseline (LLMNR, firewall, PUA protection, PowerShell 2.0, Remote Assistance, file extensions) and tidy-ups (nag screens, no forced restart, Explorer to This PC, clean taskbar, Reserved Storage).
- **Laptop presets** take whole Privacy / Responsiveness / Security categories, so they now explicitly exclude the cosmetic and security-trading tweaks that have nothing to do with battery life (SmartScreen off, OneDrive off, This PC, taskbar cleanup, file extensions).
- **Every preset was re-audited** against the new tweaks; the README describes what each one contains.

### 🔄 Changed — existing tweaks

- **DNS over HTTPS actually encrypts your DNS now.** It used to register the Cloudflare DoH templates but never changed the adapters' DNS servers, so unless your DNS was already 1.1.1.1 nothing was encrypted. It now points every *connected physical* adapter at 1.1.1.1 / 1.0.0.1 (saving each adapter's previous servers, or "automatic", for Undo) and flushes the DNS cache. VPN / virtual adapters and adapters already using a local DNS proxy (`127.x`, e.g. Cloudflare WARP) are skipped.
- **High Performance Power Plan → Maximum Performance Power Plan.** Activates Ultimate Performance, and falls back to High Performance where Windows doesn't offer it (some Modern Standby machines hide it). Detection accepts either plan.
- **Disable Game DVR / Capture** also sets `GameDVR_Enabled = 0` and turns off the Game Bar controller shortcut and startup panel, so the `ms-gamingoverlay` pop-up no longer appears on the controller's Guide button after the Xbox apps are removed.

### 🔄 Changed (Laptop)

- Battery timeout, level and threshold tweaks now only tighten: they no longer overwrite a value you already set stricter (for example Energy Saver at 40%, or a screen timeout under 3 min), and report "already set" instead of rewriting it.
- Power settings are read from a single `powercfg /qh` snapshot instead of one process per setting, so the startup detection scan is much faster and doesn't depend on Windows' display language.

### 🐛 Fixed

- **GPU Power: Prefer Maximum Performance** set *Turn off display after* to **Never** when plugged in and Undo never restored it. The old timeout is now backed up and put back, and the tweak's tooltip says what it does. (This was the first entry under Known issues.)
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

- **Naming:** every type, method, property, field, constant and enum member is now camelCase; only file names are PascalCase. Exceptions forced by .NET or shared code: the two namespaces, the shared `Dpi.cs`, `Main`, and the WinForms overrides. Private fields keep their `_` prefix. Saved data is unaffected — `changelog.json`, the Undo backup and `.w11profile` files keep their existing PascalCase keys (pinned with `[JsonPropertyName]`), and P/Invoke declarations now name their native entry point explicitly.

- **Power-plan tweaks can now edit the plugged-in (AC) side** as well as the battery side — `laptopPower` became `powerTweaks`, and each part says which side it edits. Detection, the "Now (battery / plugged in)" tooltip and Undo all follow it.
- **New backup kinds** for changes that aren't registry values (adapter DNS, firewall profile state, Reserved Storage, PowerShell 2.0). Undo only reverts what the tweak really changed, and every value read back from the backup file is validated before it reaches a command line.
- Four list tabs (Startup, Services, Driver Cleanup, Disk Cleanup) now share one base layout instead of four copies.
- Registry paths are shared between applying and detecting a tweak, so the two can't drift apart.
- Laptop power-plan tweaks and their detection come from a single table.
- `TweakDetector` moved to its own file; the unused `AdminWarning` class was removed.
- Previously silent `catch { }` blocks now write to the session log in `Data\logs\`.

### ⚠ Known issues

- The Startup, Services, Driver Cleanup and Disk Cleanup tabs don't scale with display DPI yet — they look small at 150%+ scaling.

---

## Earlier versions

Release notes for 1.5.0 and earlier are on the [GitHub Releases](https://github.com/Corn-Systems/win11op/releases) page.
