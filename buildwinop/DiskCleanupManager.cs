using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public string RiskLevel   { get; set; } = "Safe";   // "Safe" or "Caution"
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

        // ── PATHS ─────────────────────────────────────────────────────────
        private static string WinDir        => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static string LocalAppData  => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static string SystemDrive   => Path.GetPathRoot(WinDir) ?? @"C:\";

        private static string WinUpdateCachePath => Path.Combine(WinDir, @"SoftwareDistribution\Download");
        private static string WindowsOldPath     => Path.Combine(SystemDrive, "Windows.old");
        private static string DeliveryOptPath    => Path.Combine(WinDir, @"SoftwareDistribution\DeliveryOptimization");
        private static string UserTempPath       => Path.GetTempPath();
        private static string WinTempPath        => Path.Combine(WinDir, "Temp");
        private static string ShaderCachePath    => Path.Combine(LocalAppData, "D3DSCache");
        private static string PrefetchPath       => Path.Combine(WinDir, "Prefetch");
        private static string WerReportArchive   => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportArchive");
        private static string WerReportQueue     => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportQueue");
        private static string LocalCrashDumps    => Path.Combine(LocalAppData, "CrashDumps");
        private static string ThumbCacheDir      => Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer");
        private static string ChromeUserData     => Path.Combine(LocalAppData, @"Google\Chrome\User Data");
        private static string EdgeUserData       => Path.Combine(LocalAppData, @"Microsoft\Edge\User Data");
        private static string FirefoxProfiles    => Path.Combine(LocalAppData, @"Mozilla\Firefox\Profiles");

        // Chromium browsers keep caches per profile: User Data\Default\Cache,
        // User Data\Profile 1\Cache, etc. plus Code Cache and GPUCache siblings.
        private static IEnumerable<string> ChromiumCacheDirs(string userData)
        {
            if (!Directory.Exists(userData)) yield break;
            IEnumerable<string> profiles;
            try { profiles = Directory.EnumerateDirectories(userData); }
            catch { yield break; }

            foreach (var profile in profiles)
            {
                string name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ")) continue;
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    string dir = Path.Combine(profile, sub);
                    if (Directory.Exists(dir)) yield return dir;
                }
            }
        }

        private static IEnumerable<string> FirefoxCacheDirs()
        {
            if (!Directory.Exists(FirefoxProfiles)) yield break;
            IEnumerable<string> profiles;
            try { profiles = Directory.EnumerateDirectories(FirefoxProfiles); }
            catch { yield break; }

            foreach (var profile in profiles)
            {
                string dir = Path.Combine(profile, "cache2");
                if (Directory.Exists(dir)) yield return dir;
            }
        }

        private static IEnumerable<string> AllBrowserCacheDirs() =>
            ChromiumCacheDirs(ChromeUserData)
                .Concat(ChromiumCacheDirs(EdgeUserData))
                .Concat(FirefoxCacheDirs());

        // ── CATEGORY LIST ────────────────────────────────────────────────
        public static List<CleanupCategory> GetCategories() => new()
        {
            new CleanupCategory { Key = "WinUpdate",   Name = "Windows Update Cache",
                Description = "Old downloaded update files in SoftwareDistribution\\Download. Windows re-downloads what it needs.",
                RiskLevel = "Safe", DefaultOn = true },

            new CleanupCategory { Key = "DeliveryOpt", Name = "Delivery Optimization Cache",
                Description = "Peer-to-peer cache used to share Windows Update files with other PCs on your network.",
                RiskLevel = "Safe", DefaultOn = true },

            new CleanupCategory { Key = "Temp",        Name = "Temp Files",
                Description = "User and system TEMP folders. Locked/in-use files are skipped automatically.",
                RiskLevel = "Safe", DefaultOn = true },

            new CleanupCategory { Key = "ShaderCache", Name = "DirectX Shader Cache",
                Description = "Compiled GPU shader cache. Regenerates automatically the next time you play.",
                RiskLevel = "Safe", DefaultOn = true },

            new CleanupCategory { Key = "WER",         Name = "Error Reports & Crash Dumps",
                Description = "Windows Error Reporting archive/queue plus local app crash dumps.",
                RiskLevel = "Safe", DefaultOn = true },

            new CleanupCategory { Key = "Thumbnails",  Name = "Thumbnail Cache",
                Description = "Explorer's thumbnail cache database. Regenerates as you browse folders.",
                RiskLevel = "Safe", DefaultOn = true },

            new CleanupCategory { Key = "BrowserCache", Name = "Browser Caches (Chrome / Edge / Firefox)",
                Description = "Web caches only — cookies, passwords, and history are untouched. Close browsers first; in-use files are skipped.",
                RiskLevel = "Safe", DefaultOn = false },

            new CleanupCategory { Key = "ComponentStore", Name = "Component Store (WinSxS)",
                Description = "Runs DISM StartComponentCleanup — removes superseded update components. Can free multiple GB but takes several minutes.",
                RiskLevel = "Safe", DefaultOn = false, SizeKnown = false },

            new CleanupCategory { Key = "Prefetch",    Name = "Prefetch Files",
                Description = "App-launch prefetch hints. Windows rebuilds these over the next few launches.",
                RiskLevel = "Caution", DefaultOn = false },

            new CleanupCategory { Key = "RecycleBin",  Name = "Recycle Bin",
                Description = "Permanently empties the Recycle Bin for all drives. Cannot be undone.",
                RiskLevel = "Caution", DefaultOn = false },

            new CleanupCategory { Key = "WinOld",      Name = "Windows.old Folder",
                Description = "Leftover previous Windows installation from an upgrade. You lose the ability to roll back.",
                RiskLevel = "Caution", DefaultOn = false },

            new CleanupCategory { Key = "EventLogs",   Name = "Application/System Event Logs",
                Description = "Clears the Application and System event logs. Useful for troubleshooting, otherwise low-impact.",
                RiskLevel = "Caution", DefaultOn = false, SizeKnown = false },
        };

        // ── SIZE MEASUREMENT (shared by scan and clean) ──────────────────
        private static long MeasureCategory(string key) => key switch
        {
            "WinUpdate"      => DirSize(WinUpdateCachePath),
            "DeliveryOpt"    => DirSize(DeliveryOptPath),
            "Temp"           => DirSize(UserTempPath) + DirSize(WinTempPath),
            "ShaderCache"    => DirSize(ShaderCachePath),
            "WER"            => DirSize(WerReportArchive) + DirSize(WerReportQueue) + DirSize(LocalCrashDumps),
            "Thumbnails"     => FilesSize(ThumbCacheDir, "thumbcache_*.db"),
            "BrowserCache"   => AllBrowserCacheDirs().Sum(DirSize),
            "Prefetch"       => FilesSize(PrefetchPath, "*.pf"),
            "RecycleBin"     => RecycleBinSize(),
            "WinOld"         => DirSize(WindowsOldPath),
            "EventLogs"      => 0,
            "ComponentStore" => 0,   // DISM analyze is too slow for the startup scan
            _                => 0
        };

        // ── SCAN (read-only size calculation) ───────────────────────────
        public static void ScanSizes(List<CleanupCategory> categories)
        {
            foreach (var c in categories)
                c.SizeBytes = MeasureCategory(c.Key);
        }

        // ── CLEAN (destructive) ─────────────────────────────────────────
        public static List<CleanupResult> Clean(IEnumerable<CleanupCategory> selected)
        {
            var results = new List<CleanupResult>();
            foreach (var c in selected)
            {
                try
                {
                    // Measure right before and right after so BytesFreed is the
                    // real delta, not the pre-scan estimate — locked/skipped
                    // files no longer inflate the "freed" number.
                    long before = c.SizeKnown ? MeasureCategory(c.Key) : 0;

                    switch (c.Key)
                    {
                        case "WinUpdate":      CleanWindowsUpdateCache(); break;
                        case "DeliveryOpt":    CleanDeliveryOptimization(); break;
                        case "Temp":           DeleteContents(UserTempPath); DeleteContents(WinTempPath); break;
                        case "ShaderCache":    DeleteContents(ShaderCachePath); break;
                        case "WER":            DeleteContents(WerReportArchive); DeleteContents(WerReportQueue); DeleteContents(LocalCrashDumps); break;
                        case "Thumbnails":     DeleteFiles(ThumbCacheDir, "thumbcache_*.db"); break;
                        case "BrowserCache":   foreach (var dir in AllBrowserCacheDirs()) DeleteContents(dir); break;
                        case "Prefetch":       DeleteFiles(PrefetchPath, "*.pf"); break;
                        case "RecycleBin":     EmptyRecycleBin(); break;
                        case "WinOld":         CleanWindowsOld(); break;
                        case "EventLogs":      ClearEventLogs(); break;
                        case "ComponentStore": CleanComponentStore(); break;
                    }

                    long after = c.SizeKnown ? MeasureCategory(c.Key) : 0;
                    results.Add(new CleanupResult
                    {
                        Name       = c.Name,
                        Success    = true,
                        BytesFreed = Math.Max(0, before - after)
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new CleanupResult { Name = c.Name, Success = false, Error = ex.Message });
                }
            }
            return results;
        }

        // ── CATEGORY-SPECIFIC CLEANERS ──────────────────────────────────
        private static void CleanWindowsUpdateCache()
        {
            RunCommand("net stop wuauserv & net stop bits");
            DeleteContents(WinUpdateCachePath);
            RunCommand("net start bits & net start wuauserv");
        }

        private static void CleanDeliveryOptimization()
        {
            RunPowerShell("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue");
            DeleteContents(DeliveryOptPath);
        }

        private static void CleanWindowsOld()
        {
            if (!Directory.Exists(WindowsOldPath)) return;
            // Windows.old contains TrustedInstaller-owned files — take ownership first
            RunCommand($"takeown /F \"{WindowsOldPath}\" /R /D Y >nul 2>&1");
            RunCommand($"icacls \"{WindowsOldPath}\" /grant administrators:F /T /C >nul 2>&1");
            RunCommand($"rd /s /q \"{WindowsOldPath}\"");
        }

        private static void ClearEventLogs()
        {
            RunCommand("wevtutil cl Application");
            RunCommand("wevtutil cl System");
        }

        private static void CleanComponentStore()
        {
            // Removes superseded component versions from WinSxS. Deliberately
            // NOT using /ResetBase — that would make installed updates permanent
            // and remove the ability to uninstall them.
            RunCommand("Dism.exe /Online /Cleanup-Image /StartComponentCleanup");
        }

        // ── HELPERS ──────────────────────────────────────────────────────
        private static long DirSize(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return 0;
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => { try { return f.Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        private static long FilesSize(string dir, string pattern)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                return Directory.GetFiles(dir, pattern)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        private static void DeleteContents(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { /* skip locked files */ }
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                try { Directory.Delete(sub, true); } catch { /* skip locked folders */ }
            }
        }

        private static void DeleteFiles(string dir, string pattern)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, pattern))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { /* skip locked files */ }
            }
        }

        private static long RecycleBinSize()
        {
            try
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                int hr = SHQueryRecycleBin(null, ref info);
                return hr == 0 ? info.i64Size : 0;
            }
            catch { return 0; }
        }

        private static void EmptyRecycleBin()
        {
            SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        }

        private static void RunCommand(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("Process.Start returned null (shell declined to launch).");
                p.WaitForExit();
            }
            catch (Exception ex) { SessionLog.Write("CLEANUP", ex); }
        }

        private static void RunPowerShell(string script)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName  = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("Process.Start returned null (shell declined to launch).");
                p.WaitForExit();
            }
            catch (Exception ex) { SessionLog.Write("CLEANUP", ex); }
        }
    }
}
