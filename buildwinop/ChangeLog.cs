using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper — byte-identical across Corn Systems repos

namespace Win11Optimizer
{
public static class ChangeLog
    {
        static readonly string LogFile = AppPaths.ChangeLogFile;

        public class RunEntry
        {
            public string       Timestamp    { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            public string       WindowsVer   { get; set; } = WinVersion.DisplayName;
            public string       Categories   { get; set; } = "";
            public int          Passed       { get; set; }
            public int          Failed       { get; set; }
            public bool         RestorePoint { get; set; }
            public List<string> Details      { get; set; } = new();
        }

        static List<RunEntry> _entries = new();
        public static IReadOnlyList<RunEntry> Entries => _entries.AsReadOnly();

        public static void Load()
        {
            try
            {
                if (!File.Exists(LogFile)) return;
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<RunEntry>>(
                    File.ReadAllText(LogFile));
                if (loaded != null) _entries = loaded;
            }
            catch (Exception ex) { SessionLog.Write("CHANGELOG LOAD", ex); }
        }

        public static void AddEntry(RunEntry entry)
        {
            _entries.Insert(0, entry);
            Save();
        }

        static void Save()
        {
            try
            {
                AppPaths.EnsureDataDir();
                File.WriteAllText(LogFile,
                    System.Text.Json.JsonSerializer.Serialize(_entries,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { SessionLog.Write("CHANGELOG SAVE", ex); }
        }

        public static void Clear()
        {
            _entries.Clear();
            try { if (File.Exists(LogFile)) File.Delete(LogFile); } catch (Exception ex) { SessionLog.Write("CHANGELOG CLEAR", ex); }
        }
    }

}
