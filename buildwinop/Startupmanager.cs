using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Win11Optimizer
{
    public enum StartupSource { RegistryCurrentUser, RegistryLocalMachine, StartupFolder }

    public class StartupEntry
    {
        private static readonly string[] HighImpact   = { "onedrive", "teams", "discord", "steam", "spotify", "zoom", "slack", "skype" };
        private static readonly string[] MediumImpact = { "update", "helper", "agent", "daemon", "launcher", "tray" };

        public string        Name          { get; set; } = "";
        public string        Command       { get; set; } = "";
        public string        Publisher     { get; set; } = "";
        public bool          IsEnabled     { get; set; }
        public StartupSource Source        { get; set; }
        public string        RegistryValue { get; set; }   // registry: the value name inside the Run key
        public string        FilePath      { get; set; }   // folder: full path to the .lnk / .bat

        public string SourceLabel => Source switch
        {
            StartupSource.RegistryCurrentUser  => "Registry (User)",
            StartupSource.RegistryLocalMachine => "Registry (System)",
            _                                  => "Startup Folder"
        };

        public string ImpactLabel
        {
            get
            {
                string cmd = Command.ToLowerInvariant();
                return HighImpact.Any(cmd.Contains) ? "High" : MediumImpact.Any(cmd.Contains) ? "Medium" : "Low";
            }
        }

        public Color ImpactColor => ImpactLabel switch
        {
            "High"   => Theme.DANGER,
            "Medium" => Theme.WARNING,
            _        => Theme.SUCCESS
        };
    }

    public static class StartupManager
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        // Windows stores disabled startup entries here (same technique as Task Manager)
        private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        private static RegistryKey Hive(StartupSource s) =>
            s == StartupSource.RegistryCurrentUser ? Registry.CurrentUser : Registry.LocalMachine;

        public static List<StartupEntry> LoadAll()
        {
            var entries = new List<StartupEntry>();
            ReadRegistryRun(StartupSource.RegistryCurrentUser,  entries);
            ReadRegistryRun(StartupSource.RegistryLocalMachine, entries);
            ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),       entries);
            ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), entries);
            return entries;
        }

        private static void ReadRegistryRun(StartupSource source, List<StartupEntry> entries)
        {
            try
            {
                using var run      = Hive(source).OpenSubKey(RunKey);
                using var approved = Hive(source).OpenSubKey(ApprovedKey);
                if (run == null) return;
                foreach (string name in run.GetValueNames())
                {
                    string cmd = run.GetValue(name)?.ToString() ?? "";
                    entries.Add(new StartupEntry
                    {
                        Name = name, Command = cmd, Publisher = GetPublisher(cmd),
                        IsEnabled = IsApproved(approved, name), Source = source, RegistryValue = name
                    });
                }
            }
            catch (Exception ex) { SessionLog.Write("STARTUP", ex); }
        }

        // Windows uses an 8-byte binary value in StartupApproved\Run.
        // Byte[0] = 2 → enabled, 3 → disabled.  Absent = enabled.
        private static bool IsApproved(RegistryKey approved, string name)
        {
            try { return approved?.GetValue(name) is not byte[] { Length: > 0 } data || data[0] == 2; }
            catch (Exception ex) { SessionLog.Write("STARTUP", ex); return true; }
        }

        private static void ReadStartupFolder(string folder, List<StartupEntry> entries)
        {
            if (!Directory.Exists(folder)) return;
            try
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    entries.Add(new StartupEntry
                    {
                        Name = name, Command = file, Publisher = GetPublisher(file),
                        IsEnabled = true,   // folder items have no disable mechanism — only delete
                        Source = StartupSource.StartupFolder, FilePath = file
                    });
                }
            }
            catch (Exception ex) { SessionLog.Write("STARTUP", ex); }
        }

        // Folder items have no standard disable, so this reports false for them (the caller explains).
        public static bool SetEnabled(StartupEntry entry, bool enable)
        {
            if (entry.Source == StartupSource.StartupFolder) return false;
            try
            {
                using var approved = Hive(entry.Source).CreateSubKey(ApprovedKey, writable: true);
                if (approved == null) return false;
                // 8-byte value: byte[0] = 2 (enabled) or 3 (disabled), rest zeroed
                approved.SetValue(entry.RegistryValue, new byte[] { enable ? (byte)2 : (byte)3, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                entry.IsEnabled = enable;
                return true;
            }
            catch (Exception ex) { SessionLog.Write("STARTUP", ex); return false; }
        }

        public static bool Delete(StartupEntry entry)
        {
            try
            {
                if (entry.Source == StartupSource.StartupFolder)
                {
                    if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
                }
                else
                {
                    using var run = Hive(entry.Source).OpenSubKey(RunKey, writable: true);
                    run?.DeleteValue(entry.RegistryValue, throwOnMissingValue: false);
                }
                return true;
            }
            catch (Exception ex) { SessionLog.Write("STARTUP", ex); return false; }
        }

        private static string GetPublisher(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            try
            {
                // Strip quotes and args to get the exe path
                string path = command.TrimStart('"');
                int end = path.IndexOf('"');
                if (end < 0) end = path.IndexOf(' ');
                if (end > 0) path = path[..end];
                return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName ?? "" : "";
            }
            catch (Exception ex) { SessionLog.Write("STARTUP PUBLISHER", ex); return ""; }
        }
    }
}
