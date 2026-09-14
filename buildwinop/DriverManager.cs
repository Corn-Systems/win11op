using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Win11Optimizer
{
    public class DriverPackage
    {
        public string    PublishedName { get; set; } = "";   // oem12.inf
        public string    OriginalName  { get; set; } = "";    // nvhda.inf
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
        private static string DriverStorePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\DriverStore\FileRepository");

        public static List<DriverPackage> LoadAll()
        {
            var packages  = ParsePnputilOutput(RunCapture("pnputil.exe", "/enum-drivers"));
            var inUseInfs = GetInUseInfNames();

            foreach (var pkg in packages)
            {
                pkg.InUse     = inUseInfs.Contains(pkg.PublishedName);
                pkg.SizeBytes = EstimateFolderSize(pkg.OriginalName);
            }

            return packages.OrderByDescending(p => p.SizeBytes).ToList();
        }

        // pnputil /enum-drivers prints blocks like:
        //   Published Name:     oem12.inf
        //   Original Name:      nvhda.inf
        //   Provider Name:      NVIDIA
        //   Class Name:         MEDIA
        //   Class GUID:         {...}
        //   Driver Version:     10/02/2025 32.0.15.7652
        //   Signer Name:        ...
        private static List<DriverPackage> ParsePnputilOutput(string output)
        {
            var list = new List<DriverPackage>();
            if (string.IsNullOrWhiteSpace(output)) return list;

            var blocks = Regex.Split(output, @"(?=Published Name\s*:)");
            foreach (var block in blocks)
            {
                if (!block.Contains("Published Name")) continue;

                string published = GetField(block, "Published Name");
                if (string.IsNullOrWhiteSpace(published)) continue;

                string original = GetField(block, "Original Name");
                string provider = GetField(block, "Provider Name");
                string cls      = GetField(block, "Class Name");
                string verLine  = GetField(block, "Driver Version");

                DateTime? date = null;
                string    ver  = verLine;
                var parts = verLine.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && DateTime.TryParse(parts[0], out var d))
                {
                    date = d;
                    ver  = parts[1];
                }

                list.Add(new DriverPackage
                {
                    PublishedName = published,
                    OriginalName  = original,
                    ProviderName  = string.IsNullOrWhiteSpace(provider) ? "Unknown" : provider,
                    ClassName     = string.IsNullOrWhiteSpace(cls) ? "Unknown" : cls,
                    Version       = string.IsNullOrWhiteSpace(ver) ? "Unknown" : ver,
                    DriverDate    = date
                });
            }
            return list;
        }

        private static string GetField(string block, string label)
        {
            var m = Regex.Match(block, Regex.Escape(label) + @"\s*:\s*(.+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        // Cross-reference against drivers actually bound to a device right now,
        // so we never let a currently-in-use package get flagged as removable.
        private static HashSet<string> GetInUseInfNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string output = RunCapture("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                    "\"Get-CimInstance Win32_PnPSignedDriver | Select-Object -ExpandProperty InfName\"");
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (!string.IsNullOrWhiteSpace(t)) set.Add(t);
                }
            }
            catch (Exception ex)
            {
                SessionLog.Write("DRIVER", ex);
            }
            return set;
        }

        private static long EstimateFolderSize(string originalName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(originalName)) return 0;
                if (!Directory.Exists(DriverStorePath)) return 0;

                string baseName = Path.GetFileNameWithoutExtension(originalName);
                // FileRepository folders look like "nvhda.inf_amd64_8f3a2c1..."
                string match = Directory.GetDirectories(DriverStorePath, baseName + ".inf_*")
                    .FirstOrDefault();
                if (match == null) return 0;

                return new DirectoryInfo(match)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => { try { return f.Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        public static bool Delete(DriverPackage pkg, out string error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo("pnputil.exe",
                    $"/delete-driver {pkg.PublishedName} /uninstall /force")
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) { error = "pnputil.exe failed to launch."; return false; }
                // Read both streams concurrently — reading them sequentially can
                // deadlock if the process fills one pipe buffer while we're
                // blocked on the other.
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                p.WaitForExit();
                string stdErr = errTask.Result;
                string stdOut = outTask.Result;

                if (p.ExitCode == 0) return true;
                error = string.IsNullOrWhiteSpace(stdErr) ? stdOut.Trim() : stdErr.Trim();
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

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
                // Drain both streams concurrently — stderr is redirected, so if
                // it's never read and the buffer fills, the child blocks forever.
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                _ = errTask.Result;
                return outTask.Result;
            }
            catch (Exception ex)
            {
                SessionLog.Write("DRIVER", ex);
                return "";
            }
        }
    }
}
