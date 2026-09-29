using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Win11Optimizer
{
    public class CleanupCategory
    {
        public string Key         { get; set; } = "";
        public string Name        { get; set; } = "";
        public string Description { get; set; } = "";
        public bool   Caution     { get; set; }   // safe but not easily reversible
        public bool   DefaultOn   { get; set; }
        public long   SizeBytes   { get; set; }
        public bool   SizeKnown   { get; set; } = true;

        public string SizeLabel => SizeKnown ? SizeFormat.Bytes(SizeBytes) : "—";
    }

    public class CleanupResult
    {
        public string Name       { get; set; } = "";
        public bool   Success    { get; set; }
        public string Error      { get; set; }
        public long   BytesFreed { get; set; }
    }

    public static class DiskCleanupManager
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHQUERYRBINFO
        {
            public int  cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI   = 0x00000002;
        private const uint SHERB_NOSOUND        = 0x00000004;

        // The SearchOption overloads never skipped hidden/system files; EnumerationOptions does by
        // default, so reset AttributesToSkip. IgnoreInaccessible: one protected subfolder no longer
        // zeroes a whole folder's size or aborts its cleanup.
        private static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        private static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true, AttributesToSkip = 0, MatchType = MatchType.Win32 };

        // ── PATHS ─────────────────────────────────────────────────────────
        private static string WinDir         => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static string Local          => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static string WerDir         => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER");
        private static string WinUpdateCache => Path.Combine(WinDir, @"SoftwareDistribution\Download");
        private static string DeliveryOpt    => Path.Combine(WinDir, @"SoftwareDistribution\DeliveryOptimization");
        private static string WindowsOld     => Path.Combine(Path.GetPathRoot(WinDir) ?? @"C:\", "Windows.old");

        private static string[] Subdirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
            catch (Exception ex) { SessionLog.Write("CLEANUP ENUM", ex); return []; }
        }

        // Chromium browsers keep caches per profile: User Data\Default\Cache,
        // User Data\Profile 1\Cache, etc. plus Code Cache and GPUCache siblings.
        private static IEnumerable<string> ChromiumCacheDirs(string userData) =>
            Subdirs(userData)
                .Where(p => Path.GetFileName(p) is var n && (n == "Default" || n.StartsWith("Profile ")))
                .SelectMany(p => new[] { "Cache", "Code Cache", "GPUCache" }.Select(s => Path.Combine(p, s)));

        private static IEnumerable<string> BrowserCacheDirs() =>
            ChromiumCacheDirs(Path.Combine(Local, @"Google\Chrome\User Data"))
                .Concat(ChromiumCacheDirs(Path.Combine(Local, @"Microsoft\Edge\User Data")))
                .Concat(Subdirs(Path.Combine(Local, @"Mozilla\Firefox\Profiles")).Select(p => Path.Combine(p, "cache2")))
                .Where(Directory.Exists);

        // Folders each category measures and empties…
        private static string[] Dirs(string key) => key switch
        {
            "WinUpdate"    => [WinUpdateCache],
            "DeliveryOpt"  => [DeliveryOpt],
            "Temp"         => [Path.GetTempPath(), Path.Combine(WinDir, "Temp")],
            "ShaderCache"  => [Path.Combine(Local, "D3DSCache")],
            "WER"          => [Path.Combine(WerDir, "ReportArchive"), Path.Combine(WerDir, "ReportQueue"), Path.Combine(Local, "CrashDumps")],
            "BrowserCache" => [.. BrowserCacheDirs()],
            "WinOld"       => [WindowsOld],
            _              => []
        };

        // …or, for these, the top-level files matching a pattern.
        private static (string Dir, string Pattern)? Files(string key) => key switch
        {
            "Thumbnails" => (Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db"),
            "Prefetch"   => (Path.Combine(WinDir, "Prefetch"), "*.pf"),
            _            => null
        };

        // ── CATEGORY LIST ────────────────────────────────────────────────
        private static CleanupCategory C(string key, string name, string desc, bool on, bool caution = false, bool sized = true) =>
            new() { Key = key, Name = name, Description = desc, DefaultOn = on, Caution = caution, SizeKnown = sized };

        public static List<CleanupCategory> GetCategories() =>
        [
            C("WinUpdate", "Windows Update Cache",
                "Old downloaded update files in SoftwareDistribution\\Download. Windows re-downloads what it needs.", true),
            C("DeliveryOpt", "Delivery Optimization Cache",
                "Peer-to-peer cache used to share Windows Update files with other PCs on your network.", true),
            C("Temp", "Temp Files",
                "User and system TEMP folders. Locked/in-use files are skipped automatically.", true),
            C("ShaderCache", "DirectX Shader Cache",
                "Compiled GPU shader cache. Regenerates automatically the next time you play.", true),
            C("WER", "Error Reports & Crash Dumps",
                "Windows Error Reporting archive/queue plus local app crash dumps.", true),
            C("Thumbnails", "Thumbnail Cache",
                "Explorer's thumbnail cache database. Regenerates as you browse folders.", true),
            C("BrowserCache", "Browser Caches (Chrome / Edge / Firefox)",
                "Web caches only — cookies, passwords, and history are untouched. Close browsers first; in-use files are skipped.", false),
            C("ComponentStore", "Component Store (WinSxS)",
                "Runs DISM StartComponentCleanup — removes superseded update components. Can free multiple GB but takes several minutes.", false, sized: false),
            C("Prefetch", "Prefetch Files",
                "App-launch prefetch hints. Windows rebuilds these over the next few launches.", false, caution: true),
            C("RecycleBin", "Recycle Bin",
                "Permanently empties the Recycle Bin for all drives. Cannot be undone.", false, caution: true),
            C("WinOld", "Windows.old Folder",
                "Leftover previous Windows installation from an upgrade. You lose the ability to roll back.", false, caution: true),
            C("EventLogs", "Application/System Event Logs",
                "Clears the Application and System event logs. Useful for troubleshooting, otherwise low-impact.", false, caution: true, sized: false),
        ];

        // ── SIZE MEASUREMENT (shared by scan and clean) ──────────────────
        // EventLogs / ComponentStore report 0 — DISM analyze is too slow for the startup scan.
        private static long Measure(string key) =>
            key == "RecycleBin"   ? RecycleBinSize()
            : Files(key) is { } f ? DirSize(f.Dir, f.Pattern, deep: false)
            : Dirs(key).Sum(d => DirSize(d));

        // ── SCAN (read-only size calculation) ───────────────────────────
        public static void ScanSizes(List<CleanupCategory> categories)
        {
            foreach (var c in categories) c.SizeBytes = Measure(c.Key);
        }

        // ── CLEAN (destructive) ─────────────────────────────────────────
        public static List<CleanupResult> Clean(IEnumerable<CleanupCategory> selected) =>
            selected.Select(c =>
            {
                try
                {
                    // Measure right before and right after so BytesFreed is the real delta,
                    // not the pre-scan estimate — locked/skipped files don't inflate it.
                    long before = c.SizeKnown ? Measure(c.Key) : 0;
                    CleanOne(c.Key);
                    long after = c.SizeKnown ? Measure(c.Key) : 0;
                    return new CleanupResult { Name = c.Name, Success = true, BytesFreed = Math.Max(0, before - after) };
                }
                catch (Exception ex) { return new CleanupResult { Name = c.Name, Error = ex.Message }; }
            }).ToList();

        private static void CleanOne(string key)
        {
            switch (key)
            {
                case "WinUpdate":
                    Cmd("net stop wuauserv & net stop bits");
                    Wipe(WinUpdateCache);
                    Cmd("net start bits & net start wuauserv");
                    return;
                case "DeliveryOpt":
                    try { Proc.PowerShell("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue"); }
                    catch (Exception ex) { SessionLog.Write("CLEANUP", ex); }
                    Wipe(DeliveryOpt);
                    return;
                case "WinOld":
                    if (!Directory.Exists(WindowsOld)) return;
                    // Windows.old contains TrustedInstaller-owned files — take ownership first
                    Cmd($"takeown /F \"{WindowsOld}\" /R /D Y >nul 2>&1");
                    Cmd($"icacls \"{WindowsOld}\" /grant administrators:F /T /C >nul 2>&1");
                    Cmd($"rd /s /q \"{WindowsOld}\"");
                    return;
                case "EventLogs":
                    Cmd("wevtutil cl Application");
                    Cmd("wevtutil cl System");
                    return;
                case "ComponentStore":
                    // Removes superseded component versions from WinSxS. Deliberately NOT /ResetBase —
                    // that would make installed updates permanent and remove the ability to uninstall them.
                    Cmd("Dism.exe /Online /Cleanup-Image /StartComponentCleanup");
                    return;
                case "RecycleBin":
                    SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                    return;
            }
            if (Files(key) is { } f) Wipe(f.Dir, f.Pattern);
            foreach (var d in Dirs(key)) Wipe(d);
        }

        // ── HELPERS ──────────────────────────────────────────────────────
        internal static long DirSize(string dir, string pattern = "*", bool deep = true)
        {
            try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles(pattern, deep ? Deep : Flat).Sum(f => f.Length) : 0; }
            catch (Exception ex) { SessionLog.Write("CLEANUP SIZE", ex); return 0; }
        }

        // Deletes a folder's contents — or, with a pattern, just its matching top-level files.
        // Locked/in-use items are skipped and tallied once in the session log.
        private static void Wipe(string dir, string pattern = null)
        {
            if (!Directory.Exists(dir)) return;
            int skipped = 0;
            foreach (var f in pattern == null ? Directory.EnumerateFiles(dir, "*", Deep) : Directory.EnumerateFiles(dir, pattern, Flat))
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch (Exception) { skipped++; }
            if (pattern == null)
                foreach (var d in Directory.EnumerateDirectories(dir))
                    try { Directory.Delete(d, true); } catch (Exception) { skipped++; }
            if (skipped > 0) SessionLog.Write($"CLEANUP: skipped {skipped} locked item(s) in {dir}");
        }

        private static long RecycleBinSize()
        {
            try
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
            }
            catch (Exception ex) { SessionLog.Write("RECYCLE BIN", ex); return 0; }
        }

        private static void Cmd(string command)
        {
            try { Proc.Cmd(command); }
            catch (Exception ex) { SessionLog.Write("CLEANUP", ex); }
        }
    }
}
