using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Win11Optimizer
{
    public class DriverPackage
    {
        public string    PublishedName { get; set; } = "";   // oem12.inf
        public string    OriginalName  { get; set; } = "";   // nvhda.inf
        public string    ProviderName  { get; set; } = "";
        public string    ClassName     { get; set; } = "";
        public string    Version       { get; set; } = "";
        public DateTime? DriverDate    { get; set; }
        public bool      InUse         { get; set; }
        public long      SizeBytes     { get; set; }

        public string SizeLabel => SizeFormat.Bytes(SizeBytes);
        public string DateLabel => DriverDate?.ToString("yyyy-MM-dd") ?? "Unknown";
    }

    public static class SizeFormat
    {
        public static string Bytes(long bytes)
        {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
            return $"{b:0.#} {units[i]}";
        }
    }

    public static class DriverManager
    {
        private static string DriverStore =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\DriverStore\FileRepository");

        public static List<DriverPackage> LoadAll()
        {
            var pkgs  = Parse(Capture(() => Proc.Run("pnputil.exe", "/enum-drivers")));
            var inUse = InUseInfNames();
            foreach (var p in pkgs)
            {
                // If the in-use lookup failed, lock everything — an empty set used to mark
                // every package (including live device drivers) as safe to remove.
                p.InUse     = inUse == null || inUse.Contains(p.PublishedName);
                p.SizeBytes = FolderSize(p.OriginalName);
            }
            return pkgs.OrderByDescending(p => p.SizeBytes).ToList();
        }

        // pnputil /enum-drivers prints blocks like:
        //   Published Name:     oem12.inf
        //   Original Name:      nvhda.inf
        //   Provider Name:      NVIDIA
        //   Class Name:         MEDIA
        //   Class GUID:         {...}
        //   Driver Version:     10/02/2025 32.0.15.7652
        //   Signer Name:        ...
        private static List<DriverPackage> Parse(string output) =>
            Regex.Split(output, @"(?=Published Name\s*:)")
                .Select(block => (block, pub: Field(block, "Published Name")))
                .Where(x => x.pub.Length > 0)
                .Select(x =>
                {
                    string ver = Field(x.block, "Driver Version");
                    var parts  = ver.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    DateTime? date = parts.Length == 2 && DateTime.TryParse(parts[0], out var d) ? d : null;
                    return new DriverPackage
                    {
                        PublishedName = x.pub,
                        OriginalName  = Field(x.block, "Original Name"),
                        ProviderName  = OrUnknown(Field(x.block, "Provider Name")),
                        ClassName     = OrUnknown(Field(x.block, "Class Name")),
                        Version       = OrUnknown(date == null ? ver : parts[1]),
                        DriverDate    = date
                    };
                }).ToList();

        private static string Field(string block, string label)
        {
            var m = Regex.Match(block, Regex.Escape(label) + @"\s*:\s*(.+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static string OrUnknown(string s) => string.IsNullOrWhiteSpace(s) ? "Unknown" : s;

        // Drivers actually bound to a device right now, so a currently-in-use package never
        // gets flagged as removable. Null when the lookup fails or comes back empty.
        private static HashSet<string> InUseInfNames()
        {
            var set = Capture(() => Proc.PowerShell("Get-CimInstance Win32_PnPSignedDriver | Select-Object -ExpandProperty InfName"))
                .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (set.Count > 0) return set;
            SessionLog.Write("DRIVER: in-use driver lookup returned nothing — treating every package as in use");
            return null;
        }

        private static long FolderSize(string originalName)
        {
            if (string.IsNullOrWhiteSpace(originalName) || !Directory.Exists(DriverStore)) return 0;
            try
            {
                // FileRepository folders look like "nvhda.inf_amd64_8f3a2c1..."
                string match = Directory.GetDirectories(DriverStore, Path.GetFileNameWithoutExtension(originalName) + ".inf_*").FirstOrDefault();
                return match == null ? 0 : DiskCleanupManager.DirSize(match);
            }
            catch (Exception ex) { SessionLog.Write("DRIVER SIZE", ex); return 0; }
        }

        public static bool Delete(DriverPackage pkg, out string error)
        {
            try
            {
                var (code, stdOut, stdErr) = Proc.Run("pnputil.exe", $"/delete-driver {pkg.PublishedName} /uninstall /force");
                error = code == 0 ? null : (string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr).Trim();
                return code == 0;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string Capture(Func<(int Code, string Out, string Err)> run)
        {
            try { return run().Out ?? ""; }
            catch (Exception ex) { SessionLog.Write("DRIVER", ex); return ""; }
        }
    }
}
