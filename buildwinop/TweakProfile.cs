using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public static class tweakProfile
    {
        private const string filter = "Tweak Profile (*.w11profile)|*.w11profile|JSON (*.json)|*.json";

        public class profile
        {
            [JsonPropertyName("Name")]
            public string       name      { get; set; } = "My Profile";
            [JsonPropertyName("Version")]
            public string       version   { get; set; } = appVersion.current;
            [JsonPropertyName("CreatedAt")]
            public string       createdAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            [JsonPropertyName("TweakKeys")]
            public List<string> tweakKeys { get; set; } = new();
        }

        public static void export(IEnumerable<string> checkedKeys)
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Export Tweak Profile", Filter = filter, DefaultExt = "w11profile",
                FileName = $"win11op_profile_{DateTime.Now:yyyyMMdd}"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(new profile { tweakKeys = checkedKeys.ToList() }, appPaths.indented));
                MessageBox.Show($"Profile exported to:\n{dlg.FileName}", "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Returns loaded tweakKeys on success, null on cancel/error
        public static List<string> import()
        {
            using var dlg = new OpenFileDialog { Title = "Import Tweak Profile", Filter = filter };
            if (dlg.ShowDialog() != DialogResult.OK) return null;
            try
            {
                var p = JsonSerializer.Deserialize<profile>(File.ReadAllText(dlg.FileName));
                if (p?.tweakKeys == null || p.tweakKeys.Count == 0)
                {
                    MessageBox.Show("Profile is empty or invalid.", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }

                // Same case-insensitive match the GUI and CLI use when applying the keys
                var known = tweakCatalog.all.Select(t => t.tweakKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string ver = string.IsNullOrEmpty(p.version) ? "unknown version"
                           : p.version == appVersion.current ? $"v{p.version}"
                           : $"v{p.version} (this app is v{appVersion.current})";
                MessageBox.Show(
                    $"Profile loaded: \"{p.name}\"\nCreated: {p.createdAt}  ·  {ver}\n\n" +
                    $"{p.tweakKeys.Count(known.Contains)} of {p.tweakKeys.Count} tweaks matched this version.",
                    "Import Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return p.tweakKeys;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }
    }
}
