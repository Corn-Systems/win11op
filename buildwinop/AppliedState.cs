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
    public static class AppliedState
    {
        private static readonly string StateFile = AppPaths.AppliedStateFile;

        private static HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);

        public static void Load()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    File.ReadAllText(StateFile));
                if (list != null) _applied = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { SessionLog.Write("APPLIEDSTATE LOAD", ex); }
        }

        public static bool IsApplied(string tweakKey) => _applied.Contains(tweakKey);

        public static void MarkApplied(IEnumerable<string> tweakKeys)
        {
            foreach (var k in tweakKeys) _applied.Add(k);
            Save();
        }

        public static void MarkUndone(IEnumerable<string> tweakKeys)
        {
            foreach (var k in tweakKeys) _applied.Remove(k);
            Save();
        }

        private static void Save()
        {
            try
            {
                AppPaths.EnsureDataDir();
                File.WriteAllText(StateFile,
                    System.Text.Json.JsonSerializer.Serialize(_applied.ToList(),
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { SessionLog.Write("APPLIEDSTATE SAVE", ex); }
        }
    }

}
