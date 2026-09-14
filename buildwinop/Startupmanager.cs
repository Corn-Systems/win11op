using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Win11Optimizer
{
    public enum StartupSource
    {
        RegistryCurrentUser,
        RegistryLocalMachine,
        StartupFolder
    }

    public class StartupEntry
    {
        public string        Name           { get; set; } = "";
        public string        Command        { get; set; } = "";
        public string        Publisher      { get; set; } = "";
        public bool          IsEnabled      { get; set; }
        public StartupSource Source         { get; set; }

        // Registry: the value name inside the Run key (used to toggle)
        public string        RegistryValue  { get; set; }

        // Folder: full path to the .lnk / .bat
        public string        FilePath       { get; set; }

        public string SourceLabel => Source switch
        {
            StartupSource.RegistryCurrentUser  => "Registry (User)",
            StartupSource.RegistryLocalMachine => "Registry (System)",
            StartupSource.StartupFolder        => "Startup Folder",
            _                                  => "Unknown"
        };

        public string ImpactLabel
        {
            get
            {
                string cmd = Command.ToLowerInvariant();
                if (cmd.Contains("onedrive") || cmd.Contains("teams")   ||
                    cmd.Contains("discord")  || cmd.Contains("steam")   ||
                    cmd.Contains("spotify")  || cmd.Contains("zoom")    ||
                    cmd.Contains("slack")    || cmd.Contains("skype"))
                    return "High";
                if (cmd.Contains("update")  || cmd.Contains("helper")   ||
                    cmd.Contains("agent")   || cmd.Contains("daemon")   ||
                    cmd.Contains("launcher")|| cmd.Contains("tray"))
                    return "Medium";
                return "Low";
            }
        }

        public System.Drawing.Color ImpactColor => ImpactLabel switch
        {
            "High"   => Theme.DANGER,
            "Medium" => Theme.WARNING,
            _        => Theme.SUCCESS
        };
    }

    public static class StartupManager
    {
        private const string RunKeyUser    = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunKeyMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        // Windows stores disabled startup entries here (same technique as Task Manager)
        private const string ApprovedKeyUser    = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ApprovedKeyMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        // Startup folder paths
        private static string UserStartupFolder =>
            Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        private static string CommonStartupFolder =>
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

        public static List<StartupEntry> LoadAll()
        {
            var entries = new List<StartupEntry>();

            ReadRegistryRun(Registry.CurrentUser,  RunKeyUser,    ApprovedKeyUser,    StartupSource.RegistryCurrentUser,  entries);
            ReadRegistryRun(Registry.LocalMachine, RunKeyMachine, ApprovedKeyMachine, StartupSource.RegistryLocalMachine, entries);
            ReadStartupFolder(UserStartupFolder,   entries);
            ReadStartupFolder(CommonStartupFolder, entries);

            return entries;
        }

        private static void ReadRegistryRun(RegistryKey hive, string runPath,
            string approvedPath, StartupSource source, List<StartupEntry> entries)
        {
            try
            {
                using var runKey      = hive.OpenSubKey(runPath,      writable: false);
                using var approvedKey = hive.OpenSubKey(approvedPath, writable: false);
                if (runKey == null) return;

                foreach (string valueName in runKey.GetValueNames())
                {
                    string command = runKey.GetValue(valueName)?.ToString() ?? "";
                    bool   enabled = IsApproved(approvedKey, valueName);

                    entries.Add(new StartupEntry
                    {
                        Name          = valueName,
                        Command       = command,
                        Publisher     = GetPublisher(command),
                        IsEnabled     = enabled,
                        Source        = source,
                        RegistryValue = valueName
                    });
                }
            }
            catch (Exception ex)
            {
                SessionLog.Write("STARTUP", ex);
            }
        }

        // Windows uses an 8-byte binary value in StartupApproved\Run.
        // Byte[0] = 2 → enabled, 3 → disabled.  Absent = enabled.
        private static bool IsApproved(RegistryKey approvedKey, string valueName)
        {
            if (approvedKey == null) return true;
            try
            {
                var data = approvedKey.GetValue(valueName) as byte[];
                if (data == null || data.Length == 0) return true;
                return data[0] == 2;
            }
            catch { return true; }
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
                        Name      = name,
                        Command   = file,
                        Publisher = GetPublisher(file),
                        IsEnabled = true,   // folder items have no disable mechanism — only delete
                        Source    = StartupSource.StartupFolder,
                        FilePath  = file
                    });
                }
            }
            catch (Exception ex)
            {
                SessionLog.Write("STARTUP", ex);
            }
        }

        public static bool SetEnabled(StartupEntry entry, bool enable)
        {
            try
            {
                if (entry.Source == StartupSource.StartupFolder)
                {
                    // Folder items: no standard disable — we can't reliably toggle
                    // so we just report it as unsupported (caller shows message)
                    return false;
                }

                bool   isUser       = entry.Source == StartupSource.RegistryCurrentUser;
                var    hive         = isUser ? Registry.CurrentUser : Registry.LocalMachine;
                string approvedPath = isUser ? ApprovedKeyUser : ApprovedKeyMachine;

                using var approvedKey = hive.CreateSubKey(approvedPath, writable: true);
                if (approvedKey == null) return false;

                // 8-byte value: byte[0] = 2 (enabled) or 3 (disabled), rest zeroed
                var data = new byte[8];
                data[0] = enable ? (byte)2 : (byte)3;
                approvedKey.SetValue(entry.RegistryValue, data, RegistryValueKind.Binary);
                entry.IsEnabled = enable;
                return true;
            }
            catch (Exception ex)
            {
                SessionLog.Write("STARTUP", ex);
                return false;
            }
        }

        public static bool Delete(StartupEntry entry)
        {
            try
            {
                if (entry.Source == StartupSource.StartupFolder)
                {
                    if (File.Exists(entry.FilePath))
                        File.Delete(entry.FilePath);
                    return true;
                }

                bool   isUser   = entry.Source == StartupSource.RegistryCurrentUser;
                var    hive     = isUser ? Registry.CurrentUser : Registry.LocalMachine;
                string runPath  = isUser ? RunKeyUser : RunKeyMachine;

                using var runKey = hive.OpenSubKey(runPath, writable: true);
                runKey?.DeleteValue(entry.RegistryValue, throwOnMissingValue: false);
                return true;
            }
            catch (Exception ex)
            {
                SessionLog.Write("STARTUP", ex);
                return false;
            }
        }

        private static string GetPublisher(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            try
            {
                // Strip quotes and args to get the exe path
                string path = command.TrimStart('"');
                int    end  = path.IndexOf('"');
                if (end > 0) path = path.Substring(0, end);
                else
                {
                    int space = path.IndexOf(' ');
                    if (space > 0) path = path.Substring(0, space);
                }

                if (!File.Exists(path)) return "";
                var vi = FileVersionInfo.GetVersionInfo(path);
                return vi.CompanyName ?? "";
            }
            catch { return ""; }
        }
    }
}