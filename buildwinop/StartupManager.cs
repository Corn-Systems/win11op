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

        // Where the entry lives and where Windows keeps its enabled/disabled flag. Run32 / WOW6432Node
        // holds 32-bit apps' machine-wide entries; startup-folder items are flagged under StartupFolder.
        internal bool   machineWide    { get; set; }
        internal string runSubKey      { get; set; }
        internal string approvedSubKey { get; set; }
        internal string approvedValue  { get; set; }
        internal bool   is32Bit        { get; set; }

        public string sourceLabel => source switch
        {
            startupSource.registryCurrentUser  => "Registry (User)",
            startupSource.registryLocalMachine => is32Bit ? "Registry (System, 32-bit)" : "Registry (System)",
            _                                  => machineWide ? "Startup Folder (All Users)" : "Startup Folder"
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
        private const string runKey      = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string runKey32    = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        // Windows stores each entry's enabled/disabled flag here (same technique as Task Manager)
        private const string approvedRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        private static RegistryKey hive(bool machineWide) => machineWide ? Registry.LocalMachine : Registry.CurrentUser;

        public static List<startupEntry> loadAll()
        {
            var entries = new List<startupEntry>();
            readRegistryRun(startupSource.registryCurrentUser,  false, runKey,   "Run",   false, entries);
            readRegistryRun(startupSource.registryLocalMachine, true,  runKey,   "Run",   false, entries);
            readRegistryRun(startupSource.registryLocalMachine, true,  runKey32, "Run32", true,  entries);
            readStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),       false, entries);
            readStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true,  entries);
            return entries;
        }

        private static void readRegistryRun(startupSource source, bool machine, string runSub, string approvedName, bool is32, List<startupEntry> entries)
        {
            try
            {
                using var run      = hive(machine).OpenSubKey(runSub);
                using var approved = hive(machine).OpenSubKey(approvedRoot + approvedName);
                if (run == null) return;
                foreach (string name in run.GetValueNames())
                {
                    string cmd = run.GetValue(name)?.ToString() ?? "";
                    entries.Add(new startupEntry
                    {
                        name = name, command = cmd, publisher = getPublisher(cmd),
                        isEnabled = isApproved(approved, name), source = source, registryValue = name,
                        machineWide = machine, runSubKey = runSub, approvedSubKey = approvedRoot + approvedName,
                        approvedValue = name, is32Bit = is32
                    });
                }
            }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); }
        }

        // StartupApproved values are 12 bytes: a flags DWORD then the FILETIME it was disabled.
        // An odd first byte means disabled (Task Manager writes 03 / 07), even means enabled
        // (02 / 06). Absent = enabled. This used to treat anything but 02 as disabled, so
        // entries Windows itself had re-enabled with 06 showed up as off.
        private static bool isApproved(RegistryKey approved, string name)
        {
            try { return approved?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0; }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); return true; }
        }

        private static void readStartupFolder(string folder, bool machine, List<startupEntry> entries)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try
            {
                using var approved = hive(machine).OpenSubKey(approvedRoot + "StartupFolder");
                foreach (string file in Directory.GetFiles(folder))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    // desktop.ini (hidden + system) was being listed as a startup item called "desktop"
                    if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                        || (File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    entries.Add(new startupEntry
                    {
                        name = name, command = file, publisher = getPublisher(resolveShortcut(file) ?? file),
                        isEnabled = isApproved(approved, Path.GetFileName(file)),
                        source = startupSource.startupFolder, filePath = file,
                        machineWide = machine, approvedSubKey = approvedRoot + "StartupFolder", approvedValue = Path.GetFileName(file)
                    });
                }
            }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); }
        }

        // Same flag Task Manager writes — works for Run keys and startup-folder shortcuts alike.
        public static bool setEnabled(startupEntry entry, bool enable)
        {
            if (string.IsNullOrEmpty(entry.approvedSubKey)) return false;
            try
            {
                using var approved = hive(entry.machineWide).CreateSubKey(entry.approvedSubKey, writable: true);
                if (approved == null) return false;
                var data = new byte[12];
                data[0] = enable ? (byte)2 : (byte)3;
                if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
                approved.SetValue(entry.approvedValue, data, RegistryValueKind.Binary);
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
                    using var run = hive(entry.machineWide).OpenSubKey(entry.runSubKey ?? runKey, writable: true);
                    run?.DeleteValue(entry.registryValue, throwOnMissingValue: false);
                }
                // Drop the orphaned enabled/disabled flag too
                if (!string.IsNullOrEmpty(entry.approvedSubKey))
                {
                    using var approved = hive(entry.machineWide).OpenSubKey(entry.approvedSubKey, writable: true);
                    approved?.DeleteValue(entry.approvedValue, throwOnMissingValue: false);
                }
                return true;
            }
            catch (Exception ex) { sessionLog.write("STARTUP", ex); return false; }
        }

        // The executable a Run command launches. Unquoted paths with spaces
        // (C:\Program Files\App\app.exe -min) used to be cut at the first space and lose the publisher.
        private static string getPublisher(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            try
            {
                string path = exePath(Environment.ExpandEnvironmentVariables(command.Trim()));
                return path != null ? FileVersionInfo.GetVersionInfo(path).CompanyName ?? "" : "";
            }
            catch (Exception ex) { sessionLog.write("STARTUP PUBLISHER", ex); return ""; }
        }

        private static string exePath(string cmd)
        {
            if (cmd.StartsWith('"'))
            {
                int close = cmd.IndexOf('"', 1);
                string q = close > 1 ? cmd[1..close] : cmd.Trim('"');
                return File.Exists(q) ? q : null;
            }
            if (File.Exists(cmd)) return cmd;
            // Grow the candidate one space-separated chunk at a time until it names a file
            for (int i = cmd.IndexOf(' '); i > 0; i = cmd.IndexOf(' ', i + 1))
                if (File.Exists(cmd[..i])) return cmd[..i];
            return null;
        }

        // Target of a .lnk in a startup folder (for its publisher). Null if it isn't a readable shortcut.
        private static string resolveShortcut(string file)
        {
            if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return null;
                dynamic shell = Activator.CreateInstance(type);
                try { return (string)shell.CreateShortcut(file).TargetPath; }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            }
            catch (Exception ex) { sessionLog.write("STARTUP LNK", ex); return null; }
        }
    }
}
