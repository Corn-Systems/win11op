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
    public static class TweakProfile
    {
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
                Title      = "Export Tweak Profile",
                Filter     = "Tweak Profile (*.w11profile)|*.w11profile|JSON (*.json)|*.json",
                DefaultExt = "w11profile",
                FileName   = $"win11op_profile_{DateTime.Now:yyyyMMdd}"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            var profile = new Profile { TweakKeys = checkedKeys.ToList(), Version = AppVersion.Current };
            try
            {
                File.WriteAllText(dlg.FileName,
                    System.Text.Json.JsonSerializer.Serialize(profile,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show($"Profile exported to:\n{dlg.FileName}",
                    "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Returns loaded TweakKeys on success, null on cancel/error
        public static List<string> Import()
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Import Tweak Profile",
                Filter = "Tweak Profile (*.w11profile)|*.w11profile|JSON (*.json)|*.json"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return null;

            try
            {
                var profile = System.Text.Json.JsonSerializer.Deserialize<Profile>(
                    File.ReadAllText(dlg.FileName));
                if (profile?.TweakKeys == null || profile.TweakKeys.Count == 0)
                {
                    MessageBox.Show("Profile is empty or invalid.",
                        "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }

                int matched = profile.TweakKeys.Count(k =>
                    TweakCatalog.All.Any(t => t.TweakKey == k));
                string versionNote = string.IsNullOrEmpty(profile.Version)
                    ? "unknown version"
                    : profile.Version == AppVersion.Current
                        ? $"v{profile.Version}"
                        : $"v{profile.Version} (this app is v{AppVersion.Current})";
                MessageBox.Show(
                    $"Profile loaded: \"{profile.Name}\"\n" +
                    $"Created: {profile.CreatedAt}  ·  {versionNote}\n\n" +
                    $"{matched} of {profile.TweakKeys.Count} tweaks matched this version.",
                    "Import Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return profile.TweakKeys;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}",
                    "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }
    }

}
