using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Win11Optimizer
{
    public enum ServiceStartType { Boot = 0, System = 1, Automatic = 2, Manual = 3, Disabled = 4, Unknown = -1 }

    public class ManagedService
    {
        public string Key         { get; set; } = "";   // service name (sc name)
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public bool   Caution     { get; set; }         // disabling breaks a real feature some people use

        // Live state (filled by scan)
        public bool             Exists    { get; set; }
        public bool             IsRunning { get; set; }
        public ServiceStartType StartType { get; set; } = ServiceStartType.Unknown;

        public bool   IsDisabled     => StartType == ServiceStartType.Disabled;
        public string StartTypeLabel => StartType.ToString();
    }

    public static class ServicesManager
    {
        // Original start types recorded the first time each service is disabled through this
        // tab, so Restore puts back exactly what the machine had. Rows toggle on worker threads
        // concurrently, so every access (and the file write) goes through the lock.
        private static readonly object _lock = new();
        private static Dictionary<string, int> _originals = new(StringComparer.OrdinalIgnoreCase);

        // ── CURATED LIST ─────────────────────────────────────────────────
        // Only services that are genuinely optional on a typical desktop.
        private static ManagedService S(string key, string name, string desc, bool caution = false) =>
            new() { Key = key, DisplayName = name, Description = desc, Caution = caution };

        public static List<ManagedService> GetCatalog() =>
        [
            S("DiagTrack", "Connected User Experiences (DiagTrack)",
                "Primary Windows telemetry collection service. Safe to disable; also covered by the Privacy tweaks."),
            S("dmwappushservice", "WAP Push Message Routing",
                "Device management push messages — telemetry-adjacent, unused on desktops."),
            S("SysMain", "SysMain (Superfetch)",
                "Preloads predicted apps into RAM. Negligible benefit on SSDs; also covered by the Performance tweaks."),
            S("WSearch", "Windows Search Indexer",
                "Background file indexing. Start menu search still works without it, just slower on first query.", true),
            S("WerSvc", "Windows Error Reporting",
                "Collects and uploads crash reports to Microsoft."),
            S("MapsBroker", "Downloaded Maps Manager",
                "Updates offline maps for the (removed on most systems) Maps app."),
            S("lfsvc", "Geolocation Service",
                "System-wide location access. Disabling breaks 'Find my device' and app location requests.", true),
            S("Fax", "Fax",
                "It's a fax service. In this economy."),
            S("Spooler", "Print Spooler",
                "Required for ALL printing (and a recurring security-hole factory — PrintNightmare). Disable only if you never print.", true),
            S("RemoteRegistry", "Remote Registry",
                "Lets remote users modify this PC's registry. Should be disabled on any home machine."),
            S("PhoneSvc", "Phone Service",
                "Backs Phone Link calling features. Unused if you don't link a phone."),
            S("WMPNetworkSvc", "WMP Network Sharing",
                "Shares Windows Media Player libraries over the network. Legacy DLNA leftover."),
            S("RetailDemo", "Retail Demo Service",
                "Runs the store-shelf demo mode. Zero reason to exist on your PC."),
            S("WpcMonSvc", "Parental Controls",
                "Microsoft Family parental controls monitor. Safe to disable if unused."),
            S("SCardSvr", "Smart Card",
                "Smart-card reader support. Caution: some corporate/VPN logins and ID cards need it.", true),
            S("SEMgrSvc", "Payments & NFC/SE Manager",
                "NFC secure-element payments. Unused on desktops without NFC hardware."),
            S("XblAuthManager", "Xbox Live Auth Manager",
                "Xbox Live sign-in. Needed for Game Pass / Xbox app; useless otherwise.", true),
            S("XblGameSave", "Xbox Live Game Save",
                "Cloud sync for Xbox Live game saves.", true),
            S("XboxNetApiSvc", "Xbox Live Networking",
                "Networking layer for Xbox Live titles.", true),
            S("XboxGipSvc", "Xbox Accessory Management",
                "Manages Xbox controllers/accessories. Keep enabled if you use an Xbox controller.", true),
        ];

        // ── SCAN ─────────────────────────────────────────────────────────
        public static List<ManagedService> LoadAll()
        {
            var loaded = AppPaths.LoadJson<Dictionary<string, int>>(AppPaths.ServicesBackupFile, "SERVICES ORIGINALS");
            if (loaded != null) lock (_lock) _originals = new(loaded, StringComparer.OrdinalIgnoreCase);
            var list = GetCatalog();
            foreach (var svc in list) RefreshState(svc);
            // Missing services (e.g. Fax not installed) sink to the bottom
            return list.OrderBy(s => !s.Exists).ThenBy(s => s.DisplayName).ToList();
        }

        public static void RefreshState(ManagedService svc)
        {
            svc.StartType = ReadStartType(svc.Key, out bool exists);
            svc.Exists    = exists;
            svc.IsRunning = exists && Sc($"query \"{svc.Key}\"").Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }

        private static ServiceStartType ReadStartType(string name, out bool exists)
        {
            exists = false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                exists = key != null;
                return key?.GetValue("Start") is int i && i is >= 0 and <= 4 ? (ServiceStartType)i : ServiceStartType.Unknown;
            }
            catch (Exception ex) { SessionLog.Write("SERVICES READ " + name, ex); return ServiceStartType.Unknown; }
        }

        // ── TOGGLE ───────────────────────────────────────────────────────
        public static bool Disable(ManagedService svc, out string error)
        {
            try
            {
                // Record the original start type once, before we ever touch it
                if (svc.StartType is not (ServiceStartType.Unknown or ServiceStartType.Disabled))
                    lock (_lock) if (_originals.TryAdd(svc.Key, (int)svc.StartType)) SaveOriginals();

                Sc($"config \"{svc.Key}\" start= disabled");
                Sc($"stop \"{svc.Key}\"");
                RefreshState(svc);
                error = svc.IsDisabled ? null : "Service did not accept the change (protected or access denied).";
                return svc.IsDisabled;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Restore(ManagedService svc, out string error)
        {
            try
            {
                // Fall back to Manual if we never recorded an original — the safest
                // neutral state for every service in the catalog.
                ServiceStartType original;
                lock (_lock) original = _originals.TryGetValue(svc.Key, out int o) ? (ServiceStartType)o : ServiceStartType.Manual;
                string mode = original switch
                {
                    ServiceStartType.Automatic => "auto",
                    ServiceStartType.Boot      => "boot",
                    ServiceStartType.System    => "system",
                    _                          => "demand"
                };

                Sc($"config \"{svc.Key}\" start= {mode}");
                if (original == ServiceStartType.Automatic) Sc($"start \"{svc.Key}\"");
                lock (_lock) { _originals.Remove(svc.Key); SaveOriginals(); }
                RefreshState(svc);

                error = svc.IsDisabled ? "Service is still disabled (protected or access denied)." : null;
                return !svc.IsDisabled;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Caller holds _lock
        private static void SaveOriginals() => AppPaths.SaveJson(AppPaths.ServicesBackupFile, _originals, "SERVICES ORIGINALS");

        // sc.exe output (stdout + stderr); "" if it couldn't run
        private static string Sc(string args)
        {
            try { var (_, o, e) = Proc.Run("sc.exe", args); return o + e; }
            catch (Exception ex) { SessionLog.Write("SERVICES", ex); return ""; }
        }
    }
}
