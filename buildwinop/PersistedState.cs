using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Win11Optimizer
{
    // Tweak keys applied by this app or detected on the system. The detection scan marks
    // keys from a worker thread while the UI reads and writes them, so every access locks.
    public static class AppliedState
    {
        private static readonly object _lock = new();
        private static HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);

        public static void Load()
        {
            var list = AppPaths.LoadJson<List<string>>(AppPaths.AppliedStateFile, "APPLIEDSTATE");
            if (list != null) lock (_lock) _applied = new(list, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsApplied(string tweakKey) { lock (_lock) return _applied.Contains(tweakKey); }
        public static void MarkApplied(IEnumerable<string> tweakKeys) => Update(tweakKeys, true);
        public static void MarkUndone(IEnumerable<string> tweakKeys)  => Update(tweakKeys, false);

        private static void Update(IEnumerable<string> keys, bool add)
        {
            lock (_lock)
            {
                foreach (var k in keys) if (add) _applied.Add(k); else _applied.Remove(k);
                AppPaths.SaveJson(AppPaths.AppliedStateFile, _applied.ToList(), "APPLIEDSTATE");
            }
        }
    }

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
