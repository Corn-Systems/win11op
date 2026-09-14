using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Win11Optimizer
{
    public enum ServiceStartType { Boot = 0, System = 1, Automatic = 2, Manual = 3, Disabled = 4, Unknown = -1 }

    public class ManagedService
    {
        public string Key         { get; set; } = "";   // service name (sc name)
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string RiskLevel   { get; set; } = "Safe";   // "Safe" or "Caution"

        // Live state (filled by scan)
        public bool             Exists       { get; set; }
        public bool             IsRunning    { get; set; }
        public ServiceStartType StartType    { get; set; } = ServiceStartType.Unknown;

        public bool IsDisabled => StartType == ServiceStartType.Disabled;

        public string StartTypeLabel => StartType switch
        {
            ServiceStartType.Boot      => "Boot",
            ServiceStartType.System    => "System",
            ServiceStartType.Automatic => "Automatic",
            ServiceStartType.Manual    => "Manual",
            ServiceStartType.Disabled  => "Disabled",
            _                          => "Unknown"
        };
    }

    public static class ServicesManager
    {
        private static readonly string BackupFile = AppPaths.ServicesBackupFile;

        // Original start types recorded the first time each service is disabled
        // through this tab, so Restore puts back exactly what the machine had.
        private static Dictionary<string, int> _originals = new(StringComparer.OrdinalIgnoreCase);

        // ── CURATED LIST ─────────────────────────────────────────────────
        // Only services that are genuinely optional on a typical desktop.
        // Caution = disabling breaks a real feature some people use.
        public static List<ManagedService> GetCatalog() => new()
        {
            new ManagedService { Key = "DiagTrack", DisplayName = "Connected User Experiences (DiagTrack)",
                Description = "Primary Windows telemetry collection service. Safe to disable; also covered by the Privacy tweaks.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "dmwappushservice", DisplayName = "WAP Push Message Routing",
                Description = "Device management push messages — telemetry-adjacent, unused on desktops.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "SysMain", DisplayName = "SysMain (Superfetch)",
                Description = "Preloads predicted apps into RAM. Negligible benefit on SSDs; also covered by the Performance tweaks.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "WSearch", DisplayName = "Windows Search Indexer",
                Description = "Background file indexing. Start menu search still works without it, just slower on first query.",
                RiskLevel = "Caution" },

            new ManagedService { Key = "WerSvc", DisplayName = "Windows Error Reporting",
                Description = "Collects and uploads crash reports to Microsoft.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "MapsBroker", DisplayName = "Downloaded Maps Manager",
                Description = "Updates offline maps for the (removed on most systems) Maps app.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "lfsvc", DisplayName = "Geolocation Service",
                Description = "System-wide location access. Disabling breaks 'Find my device' and app location requests.",
                RiskLevel = "Caution" },

            new ManagedService { Key = "Fax", DisplayName = "Fax",
                Description = "It's a fax service. In this economy.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "Spooler", DisplayName = "Print Spooler",
                Description = "Required for ALL printing (and a recurring security-hole factory — PrintNightmare). Disable only if you never print.",
                RiskLevel = "Caution" },

            new ManagedService { Key = "RemoteRegistry", DisplayName = "Remote Registry",
                Description = "Lets remote users modify this PC's registry. Should be disabled on any home machine.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "PhoneSvc", DisplayName = "Phone Service",
                Description = "Backs Phone Link calling features. Unused if you don't link a phone.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "WMPNetworkSvc", DisplayName = "WMP Network Sharing",
                Description = "Shares Windows Media Player libraries over the network. Legacy DLNA leftover.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "RetailDemo", DisplayName = "Retail Demo Service",
                Description = "Runs the store-shelf demo mode. Zero reason to exist on your PC.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "WpcMonSvc", DisplayName = "Parental Controls",
                Description = "Microsoft Family parental controls monitor. Safe to disable if unused.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "SCardSvr", DisplayName = "Smart Card",
                Description = "Smart-card reader support. Caution: some corporate/VPN logins and ID cards need it.",
                RiskLevel = "Caution" },

            new ManagedService { Key = "SEMgrSvc", DisplayName = "Payments & NFC/SE Manager",
                Description = "NFC secure-element payments. Unused on desktops without NFC hardware.",
                RiskLevel = "Safe" },

            new ManagedService { Key = "XblAuthManager", DisplayName = "Xbox Live Auth Manager",
                Description = "Xbox Live sign-in. Needed for Game Pass / Xbox app; useless otherwise.",
                RiskLevel = "Caution" },

            new ManagedService { Key = "XblGameSave", DisplayName = "Xbox Live Game Save",
                Description = "Cloud sync for Xbox Live game saves.",
                RiskLevel = "Caution" },

            new ManagedService { Key = "XboxNetApiSvc", DisplayName = "Xbox Live Networking",
                Description = "Networking layer for Xbox Live titles.",
                RiskLevel = "Caution" },

            new ManagedService { Key = "XboxGipSvc", DisplayName = "Xbox Accessory Management",
                Description = "Manages Xbox controllers/accessories. Keep enabled if you use an Xbox controller.",
                RiskLevel = "Caution" },
        };

        // ── SCAN ─────────────────────────────────────────────────────────
        public static List<ManagedService> LoadAll()
        {
            LoadOriginals();
            var list = GetCatalog();
            foreach (var svc in list) RefreshState(svc);
            // Missing services (e.g. Fax not installed) sink to the bottom
            return list.OrderBy(s => !s.Exists).ThenBy(s => s.DisplayName).ToList();
        }

        public static void RefreshState(ManagedService svc)
        {
            svc.StartType = ReadStartType(svc.Key, out bool exists);
            svc.Exists    = exists;
            svc.IsRunning = exists && QueryRunning(svc.Key);
        }

        private static ServiceStartType ReadStartType(string serviceName, out bool exists)
        {
            exists = false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                if (key == null) return ServiceStartType.Unknown;
                exists = true;
                object val = key.GetValue("Start");
                if (val is int i && i >= 0 && i <= 4) return (ServiceStartType)i;
                return ServiceStartType.Unknown;
            }
            catch { return ServiceStartType.Unknown; }
        }

        private static bool QueryRunning(string serviceName)
        {
            string output = RunCapture("sc.exe", $"query \"{serviceName}\"");
            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }

        // ── TOGGLE ───────────────────────────────────────────────────────
        public static bool Disable(ManagedService svc, out string error)
        {
            error = null;
            try
            {
                // Record the original start type once, before we ever touch it
                if (!_originals.ContainsKey(svc.Key) &&
                    svc.StartType != ServiceStartType.Unknown &&
                    svc.StartType != ServiceStartType.Disabled)
                {
                    _originals[svc.Key] = (int)svc.StartType;
                    SaveOriginals();
                }

                RunCapture("sc.exe", $"config \"{svc.Key}\" start= disabled");
                RunCapture("sc.exe", $"stop \"{svc.Key}\"");
                RefreshState(svc);

                if (!svc.IsDisabled)
                {
                    error = "Service did not accept the change (protected or access denied).";
                    return false;
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Restore(ManagedService svc, out string error)
        {
            error = null;
            try
            {
                // Fall back to Manual if we never recorded an original — the
                // safest neutral state for every service in the catalog.
                int original = _originals.TryGetValue(svc.Key, out int o) ? o
                    : (int)ServiceStartType.Manual;

                string mode = (ServiceStartType)original switch
                {
                    ServiceStartType.Automatic => "auto",
                    ServiceStartType.Boot      => "boot",
                    ServiceStartType.System    => "system",
                    _                          => "demand"
                };

                RunCapture("sc.exe", $"config \"{svc.Key}\" start= {mode}");
                if ((ServiceStartType)original == ServiceStartType.Automatic)
                    RunCapture("sc.exe", $"start \"{svc.Key}\"");

                _originals.Remove(svc.Key);
                SaveOriginals();
                RefreshState(svc);

                if (svc.IsDisabled)
                {
                    error = "Service is still disabled (protected or access denied).";
                    return false;
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // ── ORIGINAL-STATE PERSISTENCE ───────────────────────────────────
        private static void LoadOriginals()
        {
            try
            {
                if (!File.Exists(BackupFile)) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(
                    File.ReadAllText(BackupFile));
                if (loaded != null)
                    _originals = new Dictionary<string, int>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { SessionLog.Write("SERVICES LOAD ORIGINALS", ex); }
        }

        private static void SaveOriginals()
        {
            try
            {
                AppPaths.EnsureDataDir();
                File.WriteAllText(BackupFile, JsonSerializer.Serialize(_originals,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { SessionLog.Write("SERVICES SAVE ORIGINALS", ex); }
        }

        // ── PROCESS HELPER (deadlock-safe pattern) ───────────────────────
        private static string RunCapture(string fileName, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName, args)
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                return outTask.Result + errTask.Result;
            }
            catch (Exception ex)
            {
                SessionLog.Write("SERVICES", ex);
                return "";
            }
        }
    }
}
