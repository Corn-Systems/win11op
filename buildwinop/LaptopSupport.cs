using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public enum Avail { Ok, Disabled, Hidden }

    // ── HARDWARE FACTS ────────────────────────────────────────────────────────
    // What this PC actually has, so the Laptop section only offers tweaks that can do something.
    // Facts are nullable: null = couldn't tell, and an unknown never hides or disables a tweak.
    //   Hidden   — the hardware isn't there at all (no ambient light sensor, no discrete GPU, no HDD…)
    //   Disabled — the tweak exists but can't work here (no battery, S3 sleep instead of Modern Standby)
    public static class Hardware
    {
        public static bool  HasBattery    => !SystemInformation.PowerStatus.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery);
        public static bool? ModernStandby { get; private set; }
        public static bool? AmbientLight  { get; private set; }
        public static bool? HybridGpu     { get; private set; }
        public static bool? Hdd           { get; private set; }
        public static bool? WiFi          { get; private set; }
        public static bool? GraphicsSlider { get; private set; }
        public static volatile bool Ready;

        private static string _teamsPath;

        // Runs the slow probes (one PowerShell call + powercfg) — call from a background thread.
        public static void Detect()
        {
            try
            {
                ModernStandby  = DetectModernStandby();
                GraphicsSlider = TweakEngine.HasGraphicsSlider();

                var (code, outp, _) = Proc.PowerShell(
                    "$v = @(Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID -like 'PCI*' }); " +
                    "'GPU=' + $v.Count; " +
                    "'LIGHT=' + $(if (Get-PnpDevice -Class Sensor -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'Light|Illuminance' }) {1} else {0}); " +
                    "'HDD=' + $(if (Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.MediaType -eq 'HDD' }) {1} else {0}); " +
                    "'WIFI=' + $(if (Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.PhysicalMediaType -match '802.11' }) {1} else {0}); " +
                    "'TEAMS=' + (Get-AppxPackage MSTeams -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty InstallLocation)");
                if (code == 0)
                {
                    var facts = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Select(l => l.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]);
                    bool? Flag(string k) => facts.TryGetValue(k, out var v) ? v == "1" : null;
                    HybridGpu    = facts.TryGetValue("GPU", out var g) && int.TryParse(g, out int n) ? n >= 2 : null;
                    AmbientLight = Flag("LIGHT");
                    Hdd          = Flag("HDD");
                    WiFi         = Flag("WIFI");
                    _teamsPath   = facts.TryGetValue("TEAMS", out var t) && t.Length > 0 ? t : null;
                }
            }
            catch (Exception ex) { SessionLog.Write("HARDWARE", ex); }
            Ready = true;
        }

        // `powercfg /a` lists available sleep states first, then unavailable ones with an indented
        // reason under each. An S0 line with no indented reason after it is an available one.
        private static bool? DetectModernStandby()
        {
            try
            {
                var lines = (Proc.Cmd("powercfg /a").Out ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                if (lines.All(l => l.Trim().Length == 0)) return null;
                int Indent(string l) => l.Length - l.TrimStart().Length;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!lines[i].Contains("S0 Low Power Idle", StringComparison.OrdinalIgnoreCase)) continue;
                    var next = lines.Skip(i + 1).FirstOrDefault(l => l.Trim().Length > 0);
                    return next == null || Indent(next) <= Indent(lines[i]);
                }
                return false;
            }
            catch (Exception ex) { SessionLog.Write("MODERN STANDBY", ex); return null; }
        }

        // Tweaks that only act on the battery side of the power plan (or on battery-only behaviour)
        private static bool BatteryOnly(string key) =>
            key is "Lap_IndexOnBattery" or "Lap_NicPower" ||
            (TweakEngine.LaptopPower.ContainsKey(key) && key != "Lap_GpuPowerSaving");   // that one also has a per-app half that works on AC

        // Hardware check for one tweak key. Non-Laptop keys are always available.
        public static (Avail state, string reason) Check(string key)
        {
            if (key == null || !key.StartsWith("Lap_", StringComparison.Ordinal)) return (Avail.Ok, null);

            // Hardware that isn't there → hide
            switch (key)
            {
                case "Lap_AdaptiveBright" when AmbientLight   == false: return (Avail.Hidden, "no ambient light sensor");
                case "Lap_GpuPowerSaving" when HybridGpu      == false: return (Avail.Hidden, "no second GPU");
                case "Lap_DiskIdle"       when Hdd            == false: return (Avail.Hidden, "no hard disk drive");
                case "Lap_WifiPowerSave"  when WiFi           == false: return (Avail.Hidden, "no Wi-Fi adapter");
                case "Lap_GraphicsSlider" when GraphicsSlider == false: return (Avail.Hidden, "no Intel/AMD graphics power slider");
            }

            // Present but pointless here → grey out with the reason
            if (BatteryOnly(key) && !HasBattery) return (Avail.Disabled, "no battery detected");
            if (key == "Lap_StandbyNetwork" && ModernStandby == false) return (Avail.Disabled, "needs Modern Standby (S0)");
            return (Avail.Ok, null);
        }

        // Browsers and Teams that are installed, for the per-app GPU preference. Store Teams needs a
        // PowerShell lookup, so it only comes from the cached probe result.
        public static List<string> GpuPreferenceApps(bool includeStoreTeams = false)
        {
            string pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                   pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                   loc  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string>
            {
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf,   @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf,   @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(loc,  @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf,   @"Mozilla Firefox\firefox.exe"),
                Path.Combine(pf86, @"Mozilla Firefox\firefox.exe"),
                Path.Combine(pf,   @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Path.Combine(loc,  @"Microsoft\Teams\current\Teams.exe"),   // classic Teams
            };
            if (includeStoreTeams && _teamsPath != null) candidates.Add(Path.Combine(_teamsPath, "ms-teams.exe"));
            return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // ── LAPTOP PRESET TIERS ───────────────────────────────────────────────────
    // 1 = Light: invisible or near-invisible changes, nothing you'd notice day to day
    // 2 = Balanced: the everyday battery set (what the old single Laptop preset did, plus the new tweaks)
    // 3 = Max battery: noticeable trade-offs — dimmer screen, no background Store apps, no boost clocks…
    public static class LaptopTiers
    {
        private static readonly Dictionary<string, int> Map = new()
        {
            // Light
            ["Lap_WakeTimers"] = 1, ["Lap_UsbSuspend"] = 1, ["Lap_EnergySaver"] = 1, ["Lap_AdaptiveBright"] = 1,
            ["Lap_VideoBattery"] = 1, ["Lap_CritHibernate"] = 1, ["Lap_LowBattery"] = 1, ["Lap_LidClose"] = 1,
            ["Lap_Slideshow"] = 1, ["Lap_UnattendedSleep"] = 1, ["Lap_MaintenanceWake"] = 1, ["Lap_SearchHighlights"] = 1,
            ["Lap_PcieAspm"] = 1,

            // Balanced
            ["Lap_CpuEfficiency"] = 2, ["Lap_ScreenSleep"] = 2, ["Lap_WifiPowerSave"] = 2, ["Lap_StandbyNetwork"] = 2,
            ["Lap_HibernateAfter"] = 2, ["Lap_DimTimeout"] = 2, ["Lap_LockScreenOff"] = 2, ["Lap_IndexOnBattery"] = 2,
            ["Lap_EdgeBackground"] = 2, ["Lap_VoiceActivation"] = 2, ["Lap_CrossDevice"] = 2, ["Lap_SettingsSync"] = 2,
            ["Lap_NicPower"] = 2, ["Lap_DiskIdle"] = 2, ["Lap_GraphicsSlider"] = 2, ["Lap_GpuPowerSaving"] = 2,
            ["Lap_NoTurbo"] = 2, ["Lap_Transparency"] = 2,

            // Max battery
            ["Lap_MinProcState"] = 3, ["Lap_BatteryBrightness"] = 3, ["Lap_BackgroundApps"] = 3,
        };

        // 0 for anything that isn't a tiered Laptop tweak
        public static int Of(string key) => key != null && Map.TryGetValue(key, out int t) ? t : 0;
    }
}
