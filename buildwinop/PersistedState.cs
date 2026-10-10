using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace Win11Optimizer
{
    // Tweak keys applied by this app or detected on the system. The detection scan marks
    // keys from a worker thread while the UI reads and writes them, so every access locks.
    public static class appliedState
    {
        private static readonly object _lock = new();
        private static HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);

        public static void load()
        {
            var list = appPaths.loadJson<List<string>>(appPaths.appliedStateFile, "APPLIEDSTATE");
            if (list != null) lock (_lock) _applied = new(list, StringComparer.OrdinalIgnoreCase);
        }

        public static bool isApplied(string tweakKey) { lock (_lock) return _applied.Contains(tweakKey); }
        public static void markApplied(IEnumerable<string> tweakKeys) => update(tweakKeys, true);
        public static void markUndone(IEnumerable<string> tweakKeys)  => update(tweakKeys, false);

        private static void update(IEnumerable<string> keys, bool add)
        {
            lock (_lock)
            {
                foreach (var k in keys) if (add) _applied.Add(k); else _applied.Remove(k);
                appPaths.saveJson(appPaths.appliedStateFile, _applied.ToList(), "APPLIEDSTATE");
            }
        }
    }

    public static class changeLog
    {
        public class runEntry
        {
            [JsonPropertyName("Timestamp")]
            public string       timestamp    { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            [JsonPropertyName("WindowsVer")]
            public string       windowsVer   { get; set; } = winVersion.displayName;
            [JsonPropertyName("Categories")]
            public string       categories   { get; set; } = "";
            [JsonPropertyName("Passed")]
            public int          passed       { get; set; }
            [JsonPropertyName("Failed")]
            public int          failed       { get; set; }
            [JsonPropertyName("RestorePoint")]
            public bool         restorePoint { get; set; }
            [JsonPropertyName("Details")]
            public List<string> details      { get; set; } = new();
        }

        private static List<runEntry> _entries = new();
        public static IReadOnlyList<runEntry> entries => _entries.AsReadOnly();

        public static void load() =>
            _entries = appPaths.loadJson<List<runEntry>>(appPaths.changeLogFile, "CHANGELOG") ?? _entries;

        public static void addEntry(runEntry entry)
        {
            _entries.Insert(0, entry);
            appPaths.saveJson(appPaths.changeLogFile, _entries, "CHANGELOG");
        }

        public static void clear()
        {
            _entries.Clear();
            try { if (File.Exists(appPaths.changeLogFile)) File.Delete(appPaths.changeLogFile); }
            catch (Exception ex) { sessionLog.write("CHANGELOG CLEAR", ex); }
        }
    }
}
