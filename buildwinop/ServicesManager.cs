using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Win11Optimizer
{
    public enum serviceStartType { boot = 0, system = 1, automatic = 2, manual = 3, disabled = 4, unknown = -1 }

    public class managedService
    {
        public string key         { get; set; } = "";   // service name (sc name)
        public string displayName { get; set; } = "";
        public string description { get; set; } = "";
        public bool   caution     { get; set; }         // disabling breaks a real feature some people use

        // Live state (filled by scan)
        public bool             exists    { get; set; }
        public bool             isRunning { get; set; }
        public serviceStartType startType { get; set; } = serviceStartType.unknown;

        public bool   isDisabled     => startType == serviceStartType.disabled;
        public string startTypeLabel => startType.ToString();
    }

    public static class servicesManager
    {
        // Original start types recorded the first time each service is disabled through this
        // tab, so Restore puts back exactly what the machine had. Rows toggle on worker threads
        // concurrently, so every access (and the file write) goes through the lock.
        private static readonly object _lock = new();
        private static Dictionary<string, int> _originals = new(StringComparer.OrdinalIgnoreCase);

        // ── CURATED LIST ─────────────────────────────────────────────────
        // Only services that are genuinely optional on a typical desktop.
        private static managedService s(string key, string name, string desc, bool caution = false) =>
            new() { key = key, displayName = name, description = desc, caution = caution };

        public static List<managedService> getCatalog() =>
        [
            s("DiagTrack", "Connected User Experiences (DiagTrack)",
                "Primary Windows telemetry collection service. Safe to disable; also covered by the Privacy tweaks."),
            s("dmwappushservice", "WAP Push Message Routing",
                "Device management push messages — telemetry-adjacent, unused on desktops."),
            s("SysMain", "SysMain (Superfetch)",
                "Preloads predicted apps into RAM. Negligible benefit on SSDs; also covered by the Performance tweaks."),
            s("WSearch", "Windows Search Indexer",
                "Background file indexing. Start menu search still works without it, just slower on first query.", true),
            s("WerSvc", "Windows Error Reporting",
                "Collects and uploads crash reports to Microsoft."),
            s("MapsBroker", "Downloaded Maps Manager",
                "Updates offline maps for the (removed on most systems) Maps app."),
            s("lfsvc", "Geolocation Service",
                "System-wide location access. Disabling breaks 'Find my device' and app location requests.", true),
            s("Fax", "Fax",
                "It's a fax service. In this economy."),
            s("Spooler", "Print Spooler",
                "Required for ALL printing (and a recurring security-hole factory — PrintNightmare). Disable only if you never print.", true),
            s("RemoteRegistry", "Remote Registry",
                "Lets remote users modify this PC's registry. Should be disabled on any home machine."),
            s("PhoneSvc", "Phone Service",
                "Backs Phone Link calling features. Unused if you don't link a phone."),
            s("WMPNetworkSvc", "WMP Network Sharing",
                "Shares Windows Media Player libraries over the network. Legacy DLNA leftover."),
            s("RetailDemo", "Retail Demo Service",
                "Runs the store-shelf demo mode. Zero reason to exist on your PC."),
            s("WpcMonSvc", "Parental Controls",
                "Microsoft Family parental controls monitor. Safe to disable if unused."),
            s("SCardSvr", "Smart Card",
                "Smart-card reader support. Caution: some corporate/VPN logins and ID cards need it.", true),
            s("SEMgrSvc", "Payments & NFC/SE Manager",
                "NFC secure-element payments. Unused on desktops without NFC hardware."),
            s("XblAuthManager", "Xbox Live Auth Manager",
                "Xbox Live sign-in. Needed for Game Pass / Xbox app; useless otherwise.", true),
            s("XblGameSave", "Xbox Live Game Save",
                "Cloud sync for Xbox Live game saves.", true),
            s("XboxNetApiSvc", "Xbox Live Networking",
                "Networking layer for Xbox Live titles.", true),
            s("XboxGipSvc", "Xbox Accessory Management",
                "Manages Xbox controllers/accessories. Keep enabled if you use an Xbox controller.", true),
        ];

        // ── SCAN ─────────────────────────────────────────────────────────
        public static List<managedService> loadAll()
        {
            var loaded = appPaths.loadJson<Dictionary<string, int>>(appPaths.servicesBackupFile, "SERVICES ORIGINALS");
            if (loaded != null) lock (_lock) _originals = new(loaded, StringComparer.OrdinalIgnoreCase);
            var list = getCatalog();
            foreach (var svc in list) refreshState(svc);
            // Missing services (e.g. Fax not installed) sink to the bottom
            return list.OrderBy(s => !s.exists).ThenBy(s => s.displayName).ToList();
        }

        public static void refreshState(managedService svc)
        {
            svc.startType = readStartType(svc.key, out bool exists);
            svc.exists    = exists;
            svc.isRunning = exists && sc($"query \"{svc.key}\"").Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }

        private static serviceStartType readStartType(string name, out bool exists)
        {
            exists = false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                exists = key != null;
                return key?.GetValue("Start") is int i && i is >= 0 and <= 4 ? (serviceStartType)i : serviceStartType.unknown;
            }
            catch (Exception ex) { sessionLog.write("SERVICES READ " + name, ex); return serviceStartType.unknown; }
        }

        // ── TOGGLE ───────────────────────────────────────────────────────
        public static bool disable(managedService svc, out string error)
        {
            try
            {
                // Record the original start type once, before we ever touch it
                if (svc.startType is not (serviceStartType.unknown or serviceStartType.disabled))
                    lock (_lock) if (_originals.TryAdd(svc.key, (int)svc.startType)) saveOriginals();

                sc($"config \"{svc.key}\" start= disabled");
                sc($"stop \"{svc.key}\"");
                refreshState(svc);
                error = svc.isDisabled ? null : "Service did not accept the change (protected or access denied).";
                return svc.isDisabled;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool restore(managedService svc, out string error)
        {
            try
            {
                // Fall back to Manual if we never recorded an original — the safest
                // neutral state for every service in the catalog.
                serviceStartType original;
                lock (_lock) original = _originals.TryGetValue(svc.key, out int o) ? (serviceStartType)o : serviceStartType.manual;
                string mode = original switch
                {
                    serviceStartType.automatic => "auto",
                    serviceStartType.boot      => "boot",
                    serviceStartType.system    => "system",
                    _                          => "demand"
                };

                sc($"config \"{svc.key}\" start= {mode}");
                if (original == serviceStartType.automatic) sc($"start \"{svc.key}\"");
                lock (_lock) { _originals.Remove(svc.key); saveOriginals(); }
                refreshState(svc);

                error = svc.isDisabled ? "Service is still disabled (protected or access denied)." : null;
                return !svc.isDisabled;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Caller holds _lock
        private static void saveOriginals() => appPaths.saveJson(appPaths.servicesBackupFile, _originals, "SERVICES ORIGINALS");

        // sc.exe output (stdout + stderr); "" if it couldn't run
        private static string sc(string args)
        {
            try { var (_, o, e) = proc.run("sc.exe", args); return o + e; }
            catch (Exception ex) { sessionLog.write("SERVICES", ex); return ""; }
        }
    }
}
