using System;
using System.Collections.Generic;
using System.IO;

namespace Win11Optimizer
{
    public static class ChangeLog
    {
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

        private static List<RunEntry> _entries = new();
        public static IReadOnlyList<RunEntry> Entries => _entries.AsReadOnly();

        public static void Load() =>
            _entries = AppPaths.LoadJson<List<RunEntry>>(AppPaths.ChangeLogFile, "CHANGELOG") ?? _entries;

        public static void AddEntry(RunEntry entry)
        {
            _entries.Insert(0, entry);
            AppPaths.SaveJson(AppPaths.ChangeLogFile, _entries, "CHANGELOG");
        }

        public static void Clear()
        {
            _entries.Clear();
            try { if (File.Exists(AppPaths.ChangeLogFile)) File.Delete(AppPaths.ChangeLogFile); }
            catch (Exception ex) { SessionLog.Write("CHANGELOG CLEAR", ex); }
        }
    }
}
