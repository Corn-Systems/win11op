using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Win11Optimizer
{
    public enum startupSource { registryCurrentUser, registryLocalMachine, startupFolder }

    public class startupEntry
    {
        private static readonly string[] highImpact   = { "onedrive", "teams", "discord", "steam", "spotify", "zoom", "slack", "skype" };
        private static readonly string[] mediumImpact = { "update", "helper", "agent", "daemon", "launcher", "tray" };

        public string        name          { get; set; } = "";
        public string        command       { get; set; } = "";
        public string        publisher     { get; set; } = "";
        public bool          isEnabled     { get; set; }
        public startupSource source        { get; set; }
        public string        registryValue { get; set; }   // registry: the value name inside the Run key
        public string        filePath      { get; set; }   // folder: full path to the .lnk / .bat

        public string sourceLabel => source switch
        {
            startupSource.registryCurrentUser  => "Registry (User)",
            startupSource.registryLocalMachine => "Registry (System)",
            _                                  => "Startup Folder"
        };

        public string impactLabel
        {
            get
            {
                string cmd = command.ToLowerInvariant();
                return highImpact.Any(cmd.Contains) ? "High" : mediumImpact.Any(cmd.Contains) ? "Medium" : "Low";
            }
        }

        public Color impactColor => impactLabel switch
        {
            "High"   => theme.danger,
            "Medium" => theme.warning,
            _        => theme.success
        };
    }

    public static class startupManager
    {
        private const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        // Windows stores disabled startup entries here (same technique as Task Manager)
        private const string approvedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        private static RegistryKey hive(startupSource s) =>
            s == startupSource.registryCurrentUser ? Registry.CurrentUser : Registry.LocalMachine;

        public static List<startupEntry> loadAll()
        {
            var entries = new List<startupEntry>();
            readRegistryRun(startupSource.registryCurrentUser,  entries);
            readRegistryRun(startupSource.registryLocalMachine, entries);
            readStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),       entries);
            readStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), entries);
            return entries;
        }

        private static void readRegistryRun(startupSource source, List<startupEntry> entries)
        {
            try
            {
                using var run      = hive(source).OpenSubKey(runKey);
                using var approved = hive(source).OpenSubKey(approvedKey);
                if (run == null) return;
                foreach (string name in run.GetValueNames())
                {
                    string cmd = run.GetValue(name)?.ToString() ?? "";
                    entries.Add(new startupEntry
                    {
                        name = name, command = cmd, publisher = getPublisher(cmd),
                        isEnabled = isApproved(approved, name), source = source, registryValue = name
                    });
                }
            }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); }
        }

        // Windows uses an 8-byte binary value in StartupApproved\Run.
        // Byte[0] = 2 → enabled, 3 → disabled.  Absent = enabled.
        private static bool isApproved(RegistryKey approved, string name)
        {
            try { return approved?.GetValue(name) is not byte[] { Length: > 0 } data || data[0] == 2; }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); return true; }
        }

        private static void readStartupFolder(string folder, List<startupEntry> entries)
        {
            if (!Directory.Exists(folder)) return;
            try
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    entries.Add(new startupEntry
                    {
                        name = name, command = file, publisher = getPublisher(file),
                        isEnabled = true,   // folder items have no disable mechanism — only delete
                        source = startupSource.startupFolder, filePath = file
                    });
                }
            }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); }
        }

        // Folder items have no standard disable, so this reports false for them (the caller explains).
        public static bool setEnabled(startupEntry entry, bool enable)
        {
            if (entry.source == startupSource.startupFolder) return false;
            try
            {
                using var approved = hive(entry.source).CreateSubKey(approvedKey, writable: true);
                if (approved == null) return false;
                // 8-byte value: byte[0] = 2 (enabled) or 3 (disabled), rest zeroed
                approved.SetValue(entry.registryValue, new byte[] { enable ? (byte)2 : (byte)3, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                entry.isEnabled = enable;
                return true;
            }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); return false; }
        }

        public static bool delete(startupEntry entry)
        {
            try
            {
                if (entry.source == startupSource.startupFolder)
                {
                    if (File.Exists(entry.filePath)) File.Delete(entry.filePath);
                }
                else
                {
                    using var run = hive(entry.source).OpenSubKey(runKey, writable: true);
                    run?.DeleteValue(entry.registryValue, throwOnMissingValue: false);
                }
                return true;
            }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); return false; }
        }

        private static string getPublisher(string command)
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
            catch (Exception ex) { sessionLog.write("STARTUP PUBLISHER", ex); return ""; }
        }
    }
}
