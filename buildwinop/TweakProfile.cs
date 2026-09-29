using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public static class TweakProfile
    {
        private const string Filter = "Tweak Profile (*.w11profile)|*.w11profile|JSON (*.json)|*.json";

        public class Profile
        {
            public string       Name      { get; set; } = "My Profile";
            public string       Version   { get; set; } = AppVersion.Current;
            public string       CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            public List<string> TweakKeys { get; set; } = new();
        }

        public static void Export(IEnumerable<string> checkedKeys)
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Export Tweak Profile", Filter = Filter, DefaultExt = "w11profile",
                FileName = $"win11op_profile_{DateTime.Now:yyyyMMdd}"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(new Profile { TweakKeys = checkedKeys.ToList() }, AppPaths.Indented));
                MessageBox.Show($"Profile exported to:\n{dlg.FileName}", "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Returns loaded TweakKeys on success, null on cancel/error
        public static List<string> Import()
        {
            using var dlg = new OpenFileDialog { Title = "Import Tweak Profile", Filter = Filter };
            if (dlg.ShowDialog() != DialogResult.OK) return null;
            try
            {
                var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(dlg.FileName));
                if (p?.TweakKeys == null || p.TweakKeys.Count == 0)
                {
                    MessageBox.Show("Profile is empty or invalid.", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }

                // Same case-insensitive match the GUI and CLI use when applying the keys
                var known = TweakCatalog.All.Select(t => t.TweakKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string ver = string.IsNullOrEmpty(p.Version) ? "unknown version"
                           : p.Version == AppVersion.Current ? $"v{p.Version}"
                           : $"v{p.Version} (this app is v{AppVersion.Current})";
                MessageBox.Show(
                    $"Profile loaded: \"{p.Name}\"\nCreated: {p.CreatedAt}  ·  {ver}\n\n" +
                    $"{p.TweakKeys.Count(known.Contains)} of {p.TweakKeys.Count} tweaks matched this version.",
                    "Import Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return p.TweakKeys;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }
    }
}
