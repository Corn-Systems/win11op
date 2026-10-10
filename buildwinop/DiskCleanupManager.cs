using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Win11Optimizer
{
    public class cleanupCategory
    {
        public string key         { get; set; } = "";
        public string name        { get; set; } = "";
        public string description { get; set; } = "";
        public bool   caution     { get; set; }   // safe but not easily reversible
        public bool   defaultOn   { get; set; }
        public long   sizeBytes   { get; set; }
        public bool   sizeKnown   { get; set; } = true;

        public string sizeLabel => sizeKnown ? sizeFormat.bytes(sizeBytes) : "—";
    }

    public class cleanupResult
    {
        public string name       { get; set; } = "";
        public bool   success    { get; set; }
        public string error      { get; set; }
        public long   bytesFreed { get; set; }
    }

    public static class diskCleanupManager
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBin")]
        private static extern int shQueryRecycleBin(string pszRootPath, ref shqueryrbinfo pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBin")]
        private static extern int shEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct shqueryrbinfo
        {
            public int  cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        private const uint sherbNoconfirmation = 0x00000001;
        private const uint sherbNoprogressui   = 0x00000002;
        private const uint sherbNosound        = 0x00000004;

        // The SearchOption overloads never skipped hidden/system files; EnumerationOptions does by
        // default, so reset AttributesToSkip. IgnoreInaccessible: one protected subfolder no longer
        // zeroes a whole folder's size or aborts its cleanup.
        private static readonly EnumerationOptions deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        private static readonly EnumerationOptions flat = new() { IgnoreInaccessible = true, AttributesToSkip = 0, MatchType = MatchType.Win32 };

        // ── PATHS ─────────────────────────────────────────────────────────
        private static string winDir         => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static string local          => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static string werDir         => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER");
        private static string winUpdateCache => Path.Combine(winDir, @"SoftwareDistribution\Download");
        private static string deliveryOpt    => Path.Combine(winDir, @"SoftwareDistribution\DeliveryOptimization");
        private static string windowsOld     => Path.Combine(Path.GetPathRoot(winDir) ?? @"C:\", "Windows.old");

        private static string[] subdirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
            catch (Exception ex) { sessionLog.write("CLEANUP ENUM", ex); return []; }
        }

        // Chromium browsers keep caches per profile: User Data\Default\Cache,
        // User Data\Profile 1\Cache, etc. plus Code Cache and GPUCache siblings.
        private static IEnumerable<string> chromiumCacheDirs(string userData) =>
            subdirs(userData)
                .Where(p => Path.GetFileName(p) is var n && (n == "Default" || n.StartsWith("Profile ")))
                .SelectMany(p => new[] { "Cache", "Code Cache", "GPUCache" }.Select(s => Path.Combine(p, s)));

        private static IEnumerable<string> browserCacheDirs() =>
            chromiumCacheDirs(Path.Combine(local, @"Google\Chrome\User Data"))
                .Concat(chromiumCacheDirs(Path.Combine(local, @"Microsoft\Edge\User Data")))
                .Concat(subdirs(Path.Combine(local, @"Mozilla\Firefox\Profiles")).Select(p => Path.Combine(p, "cache2")))
                .Where(Directory.Exists);

        // Folders each category measures and empties…
        private static string[] dirs(string key) => key switch
        {
            "WinUpdate"    => [winUpdateCache],
            "DeliveryOpt"  => [deliveryOpt],
            "Temp"         => [Path.GetTempPath(), Path.Combine(winDir, "Temp")],
            "ShaderCache"  => [Path.Combine(local, "D3DSCache")],
            "WER"          => [Path.Combine(werDir, "ReportArchive"), Path.Combine(werDir, "ReportQueue"), Path.Combine(local, "CrashDumps")],
            "BrowserCache" => [.. browserCacheDirs()],
            "WinOld"       => [windowsOld],
            _              => []
        };

        // …or, for these, the top-level files matching a pattern.
        private static (string dir, string pattern)? files(string key) => key switch
        {
            "Thumbnails" => (Path.Combine(local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db"),
            "Prefetch"   => (Path.Combine(winDir, "Prefetch"), "*.pf"),
            _            => null
        };

        // ── CATEGORY LIST ────────────────────────────────────────────────
        private static cleanupCategory c(string key, string name, string desc, bool on, bool caution = false, bool sized = true) =>
            new() { key = key, name = name, description = desc, defaultOn = on, caution = caution, sizeKnown = sized };

        public static List<cleanupCategory> getCategories() =>
        [
            c("WinUpdate", "Windows Update Cache",
                "Old downloaded update files in SoftwareDistribution\\Download. Windows re-downloads what it needs.", true),
            c("DeliveryOpt", "Delivery Optimization Cache",
                "Peer-to-peer cache used to share Windows Update files with other PCs on your network.", true),
            c("Temp", "Temp Files",
                "User and system TEMP folders. Locked/in-use files are skipped automatically.", true),
            c("ShaderCache", "DirectX Shader Cache",
                "Compiled GPU shader cache. Regenerates automatically the next time you play.", true),
            c("WER", "Error Reports & Crash Dumps",
                "Windows Error Reporting archive/queue plus local app crash dumps.", true),
            c("Thumbnails", "Thumbnail Cache",
                "Explorer's thumbnail cache database. Regenerates as you browse folders.", true),
            c("BrowserCache", "Browser Caches (Chrome / Edge / Firefox)",
                "Web caches only — cookies, passwords, and history are untouched. Close browsers first; in-use files are skipped.", false),
            c("ComponentStore", "Component Store (WinSxS)",
                "Runs DISM StartComponentCleanup — removes superseded update components. Can free multiple GB but takes several minutes.", false, sized: false),
            c("Prefetch", "Prefetch Files",
                "App-launch prefetch hints. Windows rebuilds these over the next few launches.", false, caution: true),
            c("RecycleBin", "Recycle Bin",
                "Permanently empties the Recycle Bin for all drives. Cannot be undone.", false, caution: true),
            c("WinOld", "Windows.old Folder",
                "Leftover previous Windows installation from an upgrade. You lose the ability to roll back.", false, caution: true),
            c("EventLogs", "Application/System Event Logs",
                "Clears the Application and System event logs. Useful for troubleshooting, otherwise low-impact.", false, caution: true, sized: false),
        ];

        // ── SIZE MEASUREMENT (shared by scan and clean) ──────────────────
        // EventLogs / ComponentStore report 0 — DISM analyze is too slow for the startup scan.
        private static long measure(string key) =>
            key == "RecycleBin"   ? recycleBinSize()
            : files(key) is { } f ? dirSize(f.dir, f.pattern, recursive: false)
            : dirs(key).Sum(d => dirSize(d));

        // ── SCAN (read-only size calculation) ───────────────────────────
        public static void scanSizes(List<cleanupCategory> categories)
        {
            foreach (var c in categories) c.sizeBytes = measure(c.key);
        }

        // ── CLEAN (destructive) ─────────────────────────────────────────
        public static List<cleanupResult> clean(IEnumerable<cleanupCategory> selected) =>
            selected.Select(c =>
            {
                try
                {
                    // Measure right before and right after so bytesFreed is the real delta,
                    // not the pre-scan estimate — locked/skipped files don't inflate it.
                    long before = c.sizeKnown ? measure(c.key) : 0;
                    cleanOne(c.key);
                    long after = c.sizeKnown ? measure(c.key) : 0;
                    return new cleanupResult { name = c.name, success = true, bytesFreed = Math.Max(0, before - after) };
                }
                catch (Exception ex) { return new cleanupResult { name = c.name, error = ex.Message }; }
            }).ToList();

        private static void cleanOne(string key)
        {
            switch (key)
            {
                case "WinUpdate":
                    cmd("net stop wuauserv & net stop bits");
                    wipe(winUpdateCache);
                    cmd("net start bits & net start wuauserv");
                    return;
                case "DeliveryOpt":
                    try { proc.powerShell("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue"); }
                    catch (Exception ex) { sessionLog.write("CLEANUP", ex); }
                    wipe(deliveryOpt);
                    return;
                case "WinOld":
                    if (!Directory.Exists(windowsOld)) return;
                    // Windows.old contains TrustedInstaller-owned files — take ownership first
                    cmd($"takeown /F \"{windowsOld}\" /R /D Y >nul 2>&1");
                    cmd($"icacls \"{windowsOld}\" /grant administrators:F /T /C >nul 2>&1");
                    cmd($"rd /s /q \"{windowsOld}\"");
                    return;
                case "EventLogs":
                    cmd("wevtutil cl Application");
                    cmd("wevtutil cl System");
                    return;
                case "ComponentStore":
                    // Removes superseded component versions from WinSxS. Deliberately NOT /ResetBase —
                    // that would make installed updates permanent and remove the ability to uninstall them.
                    cmd("Dism.exe /Online /Cleanup-Image /StartComponentCleanup");
                    return;
                case "RecycleBin":
                    shEmptyRecycleBin(IntPtr.Zero, null, sherbNoconfirmation | sherbNoprogressui | sherbNosound);
                    return;
            }
            if (files(key) is { } f) wipe(f.dir, f.pattern);
            foreach (var d in dirs(key)) wipe(d);
        }

        // ── HELPERS ──────────────────────────────────────────────────────
        internal static long dirSize(string dir, string pattern = "*", bool recursive = true)
        {
            try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles(pattern, recursive ? deep : flat).Sum(f => f.Length) : 0; }
            catch (Exception ex) { sessionLog.write("CLEANUP SIZE", ex); return 0; }
        }

        // Deletes a folder's contents — or, with a pattern, just its matching top-level files.
        // Locked/in-use items are skipped and tallied once in the session log.
        private static void wipe(string dir, string pattern = null)
        {
            if (!Directory.Exists(dir)) return;
            int skipped = 0;
            foreach (var f in pattern == null ? Directory.EnumerateFiles(dir, "*", deep) : Directory.EnumerateFiles(dir, pattern, flat))
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch (Exception) { skipped++; }
            if (pattern == null)
                foreach (var d in Directory.EnumerateDirectories(dir))
                    try { Directory.Delete(d, true); } catch (Exception) { skipped++; }
            if (skipped > 0) sessionLog.write($"CLEANUP: skipped {skipped} locked item(s) in {dir}");
        }

        private static long recycleBinSize()
        {
            try
            {
                var info = new shqueryrbinfo { cbSize = Marshal.SizeOf<shqueryrbinfo>() };
                return shQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
            }
            catch (Exception ex) { sessionLog.write("RECYCLE BIN", ex); return 0; }
        }

        private static void cmd(string command)
        {
            try { proc.cmd(command); }
            catch (Exception ex) { sessionLog.write("CLEANUP", ex); }
        }
    }
}
