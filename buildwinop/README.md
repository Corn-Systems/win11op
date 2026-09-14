# ⚡ Win11 Optimizer

> A clean, open-source Windows 10/11 optimizer built in C# / WinForms.  
> Drop it on a fresh Windows install, run it once as Administrator, and apply exactly the tweaks you want — with full per-tweak undo support.

**Version:** `1.4.2`  
**Platform:** Windows 10 / 11 (64-bit)  
**Runtime:** Self-contained — no .NET install required  
**License:** MIT

---

## Features

### 🔍 Live System State Detection
Win11 Optimizer scans your system on startup and automatically detects tweaks that are already applied — even ones you configured manually before ever running the app. Detected tweaks show a purple **applied** indicator so you know what's already done before running anything.

### 💾 Per-Tweak Applied State Persistence
Applied state is tracked at the individual tweak level and remembered across app restarts via `applied_tweaks.json`. Returning after a reboot shows exactly which tweaks were already run.

### 📦 Tweak Profiles (Export & Import)
Save your current tweak selection to a `.w11profile` file and load it on any machine. Profiles include a name, creation timestamp, and version — the importer reports how many tweaks matched when loading a profile from an older version.

### 🚀 Startup Manager
A dedicated tab to view, enable, disable, and delete startup entries from both the registry and startup folders.

### 🛠 Services Manager
A curated list of genuinely optional Windows services — telemetry (DiagTrack, WAP Push), legacy leftovers (Fax, Retail Demo, WMP Network Sharing), Xbox services, and more — each with a plain-English description of what breaks if you disable it. Disabling records the service's original startup type to `services_backup.json`, so **Restore** puts back exactly what the machine had, not a guess. Protected or in-use services report the failure instead of silently pretending.

### 💻 Headless CLI Mode
Apply a profile on a fresh install without ever opening the GUI — pairs perfectly with CornDownloader for one-command machine setup:

```
Win11Optimizer.exe --apply gaming.w11profile [--silent] [--no-restore-point]
Win11Optimizer.exe --list-tweaks
Win11Optimizer.exe --help
```

Output goes to the console you launched from and to `cli_run.log`. Exit codes: `0` all succeeded, `1` some tweaks failed, `2` bad usage/input. Run from an elevated prompt.

### ⬆ Update Notifications
On launch the app silently checks GitHub Releases; if a newer version exists, a gold badge appears in the top bar linking straight to the release. Works alongside the existing What's New dialog — no nagging, no auto-download.

### ⚠ Smart Reboot Tracking
Every tweak is tagged with what it actually needs to take effect: nothing, an Explorer restart, or a full reboot. After a run the app tells you exactly which tweaks are waiting on what (amber badge in the bottom bar — click for the list), only prompts for a reboot when one is genuinely required, and offers a one-click **⟳ Restart Explorer** for shell-level tweaks like visual effects and menu delay.


### 🔧 Driver Cleanup
Scans the Windows Driver Store (`pnputil /enum-drivers`) and cross-references every published driver package against the drivers actually bound to a device right now. Packages that are currently in use are locked and can't be selected; unused/orphaned packages (old GPU driver versions left behind by updates, drivers for devices you no longer own, etc.) can be selected and removed in a batch, with an estimated space freed before you confirm.

### 🧹 Disk Cleanup
A thorough cleanup pass across the categories the built-in Disk Cleanup tool misses or hides: Windows Update cache, Delivery Optimization cache, temp files, DirectX shader cache, WER/crash dumps, thumbnail cache, browser caches (Chrome / Edge / Firefox — caches only, cookies and passwords untouched), the Component Store (WinSxS via DISM `StartComponentCleanup` — often frees multiple GB), prefetch files, the Recycle Bin, `Windows.old`, and Application/System event logs. Each category is scanned for reclaimable space before you pick what to clean, and is flagged **Safe** or **Caution** — Caution items (Recycle Bin, `Windows.old`, Prefetch, Event Logs) are off by default. The "freed" number reported after cleaning is measured for real (before/after), not estimated — locked files that got skipped no longer inflate it.

### 🆕 What's New Dialog
Win11 Optimizer detects version changes on launch and offers to open the release notes on GitHub, so you always know what changed.

### ↩ Per-Tweak Undo
Every registry change is backed up before being applied. The **↩ Undo Selected** button in the bottom bar fully restores any category to its pre-tweak state. Backups persist across app restarts via `tweaks_backup.json`.

### ⭐ Presets
One-click presets to quickly select tweaks for common configurations:

| Preset | Description |
|--------|-------------|
| ⭐ Recommended | Safe default-on tweaks only |
| 🎮 Gaming PC | Recommended + Gaming + Network |
| 🔒 Privacy | Recommended + all Privacy tweaks |
| 🛡 Security | Recommended + all Security tweaks |
| 🪶 Minimal | Conservative subset of safe tweaks |
| 💻 Laptop | Privacy & responsiveness tweaks, excludes power/battery changes |
| 🧹 Clean Install | Bloatware + Privacy + security baseline |
| 🔬 Dev Machine | Performance + Network + advanced CPU tweaks |
| ☢ Nuclear | Everything except Bloatware & Advanced |

---

## Tweak Categories

### ⚡ Performance
| Tweak | What It Does |
|-------|-------------|
| High Performance Power Plan | Switches to the maximum performance power plan |
| Disable Power Throttling | Prevents Windows throttling background CPU usage |
| Disable SysMain (Superfetch) | Stops RAM preloading — negligible benefit on SSDs |
| Disable Windows Search Indexer | Removes background disk I/O from search indexing |
| Remove Startup Delay | Eliminates the artificial Explorer startup pause |
| Visual Effects: Best Performance | Turns off animations, shadows, and fancy rendering |
| Disable NTFS Last-Access Timestamps | Reduces filesystem writes on every file read |
| Disable 8.3 Filenames | Removes legacy short filename generation on NTFS |
| Disable Hibernation | Frees several GB of disk space, speeds shutdown |
| Disable Memory Compression | Reduces CPU overhead; also disables page combining |
| Set Timer Resolution to 0.5ms | Calls `timeBeginPeriod(1)` for sub-ms scheduler ticks |

### 🔒 Privacy & Telemetry
| Tweak | What It Does |
|-------|-------------|
| Disable Telemetry | Blocks Microsoft data collection at the registry level |
| Disable DiagTrack Service | Stops Connected User Experiences & related telemetry services |
| Disable Advertising ID | Prevents apps from accessing your ad tracking ID |
| Disable Bing in Start Menu | Removes web search results from the Start search bar |
| Disable Cortana Consent | Turns off Cortana data collection |
| Disable Activity Feed | Stops Windows logging app and file activity history |
| Disable Location Tracking | Blocks apps from accessing your physical location |
| Block App Camera Access | Prevents UWP apps from using the webcam by default |
| Disable Windows Error Reporting | Stops crash dumps and disables the WER upload queue task |
| Disable SmartScreen (Explorer) | Removes SmartScreen cloud checks in File Explorer |
| Disable Scheduled Telemetry Tasks | Kills CEIP, AppraiserV2, DiskDiag, DiagnosticInvoker, push notification, and WindowsUpdate app auto-update tasks |
| Disable App Launch Tracking | Stops Windows logging which apps you open and when |
| Disable Feedback Requests | Prevents Windows asking you to rate features |
| Disable Chat / Teams Taskbar Icon | Removes the Teams/Chat icon from the taskbar |
| Disable Windows Recall | Kills the AI screenshot feature on Copilot+ PCs (no-op otherwise) |
| Disable Windows Copilot | Blocks Copilot from launching system-wide via policy |
| Disable Cloud Content & Delivery Manager | Kills Spotlight lock screen ads, silent app installs, OEM app promotions, and Start suggestions |
| Block Telemetry Hosts | Adds 35 Microsoft telemetry domains to the hosts file (`0.0.0.0`) |

### 🖥 Responsiveness
| Tweak | What It Does |
|-------|-------------|
| Instant Menu Show | Sets menu open delay to 0ms |
| Fast App Kill Timeout | Reduces wait before force-killing frozen apps |
| Fast Service Kill Timeout | Cuts shutdown wait for slow-stopping services |
| Auto End Tasks on Shutdown | Automatically kills hung apps instead of prompting |
| Platform Tick (High-Res Timer) | Forces constant-rate platform tick + disables HPET override |
| Disable Windows Tips | Stops "Did you know..." popups and suggestions |
| Disable Suggested Content | Removes app install suggestions from the Start menu |
| Verbose Boot/Shutdown Status | Shows real service names during boot/shutdown instead of a spinner |

### 🎮 Gaming
| Tweak | What It Does |
|-------|-------------|
| Enable HAGS | Hardware-Accelerated GPU Scheduling — reduces GPU latency |
| Enable Game Mode | Tells Windows to prioritize foreground game processes |
| Disable Mouse Acceleration | Removes pointer precision for 1:1 raw mouse input |
| CPU Foreground Priority Boost | Increases CPU time slice for the active window/game |
| Disable Game DVR / Capture | Turns off Xbox Game Bar background recording |
| Disable Fullscreen Optimisations | Forces exclusive fullscreen for lower input latency |
| GPU Power: Prefer Maximum Performance | Sets D3D power policy to never downclock the GPU |
| Disable NVIDIA Telemetry Services | Stops NvTelemetryContainer & NvDisplayContainerLS |

### 🌐 Network
| Tweak | What It Does |
|-------|-------------|
| Disable Nagle's Algorithm | Reduces TCP packet buffering — lowers game ping |
| Enable Receive-Side Scaling | Spreads network processing across CPU cores |
| TCP Auto-Tuning: Normal | Enables adaptive TCP receive buffer scaling |
| Disable Network Throttling Index | Removes multimedia network rate caps |
| Max Multimedia Responsiveness | `SystemResponsiveness = 0` + tunes MMCSS Games & Pro Audio task priorities |
| Disable Large Send Offload (LSO) | Disables LSO v2 on all physical adapters — reduces NIC-driver jitter |
| Reduce TCP TIME_WAIT Delay | Cuts TIME_WAIT hold from 240s to 30s — frees ports faster |
| DNS over HTTPS (Cloudflare 1.1.1.1) | Enables DoH via Windows DNS Client, routes queries encrypted |

### 🗑 Bloatware Removal
Removes pre-installed Microsoft and third-party UWP packages from both user and provisioned scopes, including: Bing News & Weather, Zune Music/Video, Solitaire Collection, Windows Maps, Phone Link, Clipchamp, Xbox apps & overlays, third-party ad tiles (LinkedIn, Disney, Spotify, TikTok, Instagram), Office Hub & OneNote, 3D Viewer & Print 3D.

> ⚠ Bloatware removal cannot be undone — removed apps must be reinstalled from the Microsoft Store.

### 🔐 Security Hardening
| Tweak | What It Does |
|-------|-------------|
| Disable AutoRun / AutoPlay | Blocks `autorun.inf` and AutoPlay on all drive types |
| Disable Remote Desktop (RDP) | Refuses all inbound RDP connections |
| Disable SMBv1 | Removes the WannaCry/EternalBlue-vulnerable protocol |
| Disable NetBIOS over TCP/IP | Stops NetBIOS broadcasts — prevents NBNS poisoning |
| Ensure Defender Real-Time Protection | Forces Defender real-time monitoring ON via policy + cmdlet |

### ⚠ Advanced Tweaks
Lower-level tweaks for power users. Off by default.

| Tweak | What It Does |
|-------|-------------|
| Processor Scheduling: Programs | `Win32PrioritySeparation = 38` — max foreground CPU boost |
| Disable Dynamic Tick | Forces constant high-res IRQ8 timer, reduces micro-stutter |
| Disable CPU Throttling | Prevents Windows pulling background process CPU clocks |
| Ensure SSD TRIM Enabled | Sets `disabledeletenotify = 0` — keeps SSD write speeds consistent |
| Aggressive Animation Disabling | Kills `UserPreferencesMask`, TaskbarAnim, MinAnimate bits |
| Disable CPU Core Parking | Forces all cores active via `CPMINCORES = 100` — prevents park-induced stutter |
| Enable MSI Mode (GPU) | Switches GPU to Message Signaled Interrupts — reduces DPC latency |
| IRQ Affinity — Spread GPU Interrupts | Spreads MSI-X GPU interrupts across all P-cores |
| TSC Sync Policy: Legacy | Reduces scheduling micro-jitter on multi-core systems |
| Enable x2APIC Mode | Improves interrupt delivery on many-core CPUs (HEDT, Ryzen) |
| Unhide Processor Boost Mode | Reveals the hidden "Processor performance boost mode" dropdown in Advanced Power Settings — doesn't force a value, just lets you choose one yourself |

---

## Requirements

- Windows 10 or 11 (64-bit)
- Administrator privileges (required for registry, service, and hosts file changes)
- No .NET runtime required — the exe is self-contained

---

## Installation

1. Go to [Releases](https://github.com/Corn-Systems/win11op/releases) and download the latest `.exe`
2. Right-click → **Run as Administrator**

## Build from Source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Clone the repo:
   ```
   git clone https://github.com/Corn-Systems/win11op.git
   ```
3. Publish (self-contained single exe):
   ```
   cd win11op/buildwinop
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./publish
   ```
4. Run as Administrator:
   ```
   publish\Win11Optimizer.exe
   ```

### Building the Installer

Win11 Optimizer ships two ways: the portable single-file exe above, and a proper installer built with [Inno Setup](https://jrsoftware.org/isinfo.php) (free, 6.x+).

1. Publish the portable build first (step 3 above) into `.\publish`
2. Install Inno Setup, then compile `Win11Optimizer.iss`:
   ```
   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" Win11Optimizer.iss
   ```
3. The installer is produced at `installer_output\Win11Optimizer-Setup.exe` — it installs to Program Files, creates Start Menu/desktop shortcuts, and registers a proper uninstaller. The app itself still requires Administrator every launch (declared in `app.manifest`), same as the portable build.

A GitHub release should include both `Win11Optimizer-Portable.exe` (the raw publish output, renamed) and `Win11Optimizer-Setup.exe`.

---

## Notes

- The app now reports **per-tweak** reboot impact — a reboot is only prompted when an applied tweak genuinely needs one (HAGS, timer resolution, SMBv1, bcdedit-level tweaks, core parking, MSI mode, etc.); shell-level tweaks just need the built-in **⟳ Restart Explorer** button
- Bloatware removal cannot be undone — reinstall from the Microsoft Store if needed
- All registry changes are backed up to `Data\tweaks_backup.json` before being applied, and are loaded back on startup so per-category Undo works across app restarts
- Applied tweak state is tracked per-tweak in `Data\applied_tweaks.json`
- Service startup types changed in the Services tab are recorded in `Data\services_backup.json` for exact restore
- Component Store cleanup runs DISM and can take several minutes; it deliberately does **not** use `/ResetBase`, so installed updates stay uninstallable
- Browser cache cleanup touches caches only — cookies, passwords, and history are never deleted; close browsers first for a fuller clean (in-use files are skipped)
- Windows Recall tweaks are a no-op on non-Copilot+ PCs — safe to apply on any hardware
- NVIDIA telemetry tweaks are a no-op if NVIDIA drivers are not installed
- The hosts file block list is cleanly removed by the Privacy undo function
- Startup folder shortcuts cannot be disabled (Windows limitation), only deleted
- MSI Mode and IRQ Affinity tweaks target the primary GPU adapter slot (0000) only
- "Unhide Processor Boost Mode" only reveals the setting in Control Panel's Advanced Power Settings — it does not itself change boost behavior. Pick the mode yourself once it's visible; the "aggressive" values mean different things across Intel/AMD/laptop hardware, so this app won't pick one for you
- Disabling Windows Copilot only blocks it from launching — it does not remove the taskbar icon (pair with the existing Chat/Teams icon tweak if you want the icon gone too)
- Driver Cleanup only ever removes packages the app confirms are **not** currently bound to a device — "Select Unused" respects the same lock — if you plug that device back in, Windows will need to reinstall the driver
- Disk Cleanup skips any locked/in-use files automatically rather than failing the whole pass; the freed total shown after cleaning is measured, not estimated

---

## License

MIT — see [LICENSE](LICENSE)

---

## AI Disclosure

> ⚠ This project contains code written with the assistance of **Claude by Anthropic** (claude.ai).  All readmes were made by Claude Sonnet, as I am too lazy to make publishes this good.  
> The **Startup Manager** feature and the **v1.0.0 UI rework** was developed with Claude Sonnet. All code has been reviewed and tested by me.