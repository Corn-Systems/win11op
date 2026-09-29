using System;
using System.Collections.Generic;
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
}
