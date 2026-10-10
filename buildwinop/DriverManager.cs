using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Win11Optimizer
{
    public class driverPackage
    {
        public string    publishedName { get; set; } = "";   // oem12.inf
        public string    originalName  { get; set; } = "";   // nvhda.inf
        public string    providerName  { get; set; } = "";
        public string    className     { get; set; } = "";
        public string    version       { get; set; } = "";
        public DateTime? driverDate    { get; set; }
        public bool      inUse         { get; set; }
        public long      sizeBytes     { get; set; }

        public string sizeLabel => sizeFormat.bytes(sizeBytes);
        public string dateLabel => driverDate?.ToString("yyyy-MM-dd") ?? "Unknown";
    }

    public static class sizeFormat
    {
        public static string bytes(long bytes)
        {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
            return $"{b:0.#} {units[i]}";
        }
    }

    public static class driverManager
    {
        private static string driverStore =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\DriverStore\FileRepository");

        public static List<driverPackage> loadAll()
        {
            var pkgs  = parse(capture(() => proc.run("pnputil.exe", "/enum-drivers")));
            var inUse = inUseInfNames();
            foreach (var p in pkgs)
            {
                // If the in-use lookup failed, lock everything — an empty set used to mark
                // every package (including live device drivers) as safe to remove.
                p.inUse     = inUse == null || inUse.Contains(p.publishedName);
                p.sizeBytes = folderSize(p.originalName);
            }
            return pkgs.OrderByDescending(p => p.sizeBytes).ToList();
        }

        // pnputil /enum-drivers prints blocks like:
        //   Published Name:     oem12.inf
        //   Original Name:      nvhda.inf
        //   Provider Name:      NVIDIA
        //   Class Name:         MEDIA
        //   Class GUID:         {...}
        //   Driver Version:     10/02/2025 32.0.15.7652
        //   Signer Name:        ...
        private static List<driverPackage> parse(string output) =>
            Regex.Split(output, @"(?=Published Name\s*:)")
                .Select(block => (block, pub: fieldValue(block, "Published Name")))
                .Where(x => x.pub.Length > 0)
                .Select(x =>
                {
                    string ver = fieldValue(x.block, "Driver Version");
                    var parts  = ver.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    DateTime? date = parts.Length == 2 && DateTime.TryParse(parts[0], out var d) ? d : null;
                    return new driverPackage
                    {
                        publishedName = x.pub,
                        originalName  = fieldValue(x.block, "Original Name"),
                        providerName  = orUnknown(fieldValue(x.block, "Provider Name")),
                        className     = orUnknown(fieldValue(x.block, "Class Name")),
                        version       = orUnknown(date == null ? ver : parts[1]),
                        driverDate    = date
                    };
                }).ToList();

        private static string fieldValue(string block, string label)
        {
            var m = Regex.Match(block, Regex.Escape(label) + @"\s*:\s*(.+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static string orUnknown(string s) => string.IsNullOrWhiteSpace(s) ? "Unknown" : s;

        // Drivers actually bound to a device right now, so a currently-in-use package never
        // gets flagged as removable. Null when the lookup fails or comes back empty.
        private static HashSet<string> inUseInfNames()
        {
            var set = capture(() => proc.powerShell("Get-CimInstance Win32_PnPSignedDriver | Select-Object -ExpandProperty InfName"))
                .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (set.Count > 0) return set;
            sessionLog.write("DRIVER: in-use driver lookup returned nothing — treating every package as in use");
            return null;
        }

        private static long folderSize(string originalName)
        {
            if (string.IsNullOrWhiteSpace(originalName) || !Directory.Exists(driverStore)) return 0;
            try
            {
                // FileRepository folders look like "nvhda.inf_amd64_8f3a2c1..."
                string match = Directory.GetDirectories(driverStore, Path.GetFileNameWithoutExtension(originalName) + ".inf_*").FirstOrDefault();
                return match == null ? 0 : diskCleanupManager.dirSize(match);
            }
            catch (Exception ex) { sessionLog.write("DRIVER SIZE", ex); return 0; }
        }

        public static bool delete(driverPackage pkg, out string error)
        {
            try
            {
                var (code, stdOut, stdErr) = proc.run("pnputil.exe", $"/delete-driver {pkg.publishedName} /uninstall /force");
                error = code == 0 ? null : (string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr).Trim();
                return code == 0;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string capture(Func<(int code, string output, string error)> run)
        {
            try { return run().output ?? ""; }
            catch (Exception ex) { sessionLog.write("DRIVER", ex); return ""; }
        }
    }
}
