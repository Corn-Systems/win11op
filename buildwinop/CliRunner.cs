using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Win11Optimizer
{
    // ── HEADLESS CLI MODE ────────────────────────────────────────────────────
    //   Win11Optimizer.exe --apply <profile.w11profile> [--silent] [--no-restore-point]
    //   Win11Optimizer.exe --list-tweaks
    //   Win11Optimizer.exe --help
    //
    // Designed for fresh-install scripting (pairs with CornDownloader):
    // export a profile once, then apply it on any machine with one command.
    //
    // Exit codes: 0 = all tweaks succeeded, 1 = some failed, 2 = bad usage/input.
    public static class CliRunner
    {
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();
        private const int ATTACH_PARENT_PROCESS = -1;

        private static StreamWriter _logFile;
        private static bool _silent;

        // Returns true when the process ran in CLI mode (caller should exit),
        // false when no CLI flags were present (caller should launch the GUI).
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;

            bool wantsCli = args.Any(a =>
                a is "--apply" or "--list-tweaks" or "--help" or "-h" or "/?" );
            if (!wantsCli) return false;

            // WinExe has no console — attach to the parent cmd/powershell window
            // so output lands where the user typed the command.
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();

            _silent = args.Contains("--silent");
            try
            {
                AppPaths.EnsureDataDir();
                _logFile = new StreamWriter(AppPaths.CliRunLog,
                    append: true) { AutoFlush = true };
            }
            catch { /* log file is best-effort */ }

            try
            {
                if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
                {
                    PrintHelp();
                    return true;
                }

                if (args.Contains("--list-tweaks"))
                {
                    ListTweaks();
                    return true;
                }

                int idx = Array.IndexOf(args, "--apply");
                string profilePath = idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
                if (string.IsNullOrWhiteSpace(profilePath) || profilePath.StartsWith("--"))
                {
                    Log("ERROR: --apply requires a profile path.");
                    PrintHelp();
                    exitCode = 2;
                    return true;
                }

                exitCode = ApplyProfile(profilePath, skipRestorePoint: args.Contains("--no-restore-point"));
                return true;
            }
            finally
            {
                try { _logFile?.Dispose(); } catch { }
            }
        }

        private static int ApplyProfile(string profilePath, bool skipRestorePoint)
        {
            if (!File.Exists(profilePath))
            {
                Log($"ERROR: profile not found: {profilePath}");
                return 2;
            }

            TweakProfile.Profile profile;
            try
            {
                profile = JsonSerializer.Deserialize<TweakProfile.Profile>(
                    File.ReadAllText(profilePath));
            }
            catch (Exception ex)
            {
                Log($"ERROR: could not parse profile: {ex.Message}");
                return 2;
            }

            if (profile?.TweakKeys == null || profile.TweakKeys.Count == 0)
            {
                Log("ERROR: profile is empty or invalid.");
                return 2;
            }

            var keySet  = new HashSet<string>(profile.TweakKeys, StringComparer.OrdinalIgnoreCase);
            var matched = TweakCatalog.All.Where(t => keySet.Contains(t.TweakKey)).ToList();

            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (!new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                    Log("WARNING: not running as Administrator — most tweaks will fail. " +
                        "Re-run from an elevated prompt.");
            }
            catch (Exception ex) { Log($"WARNING: could not determine admin status: {ex.Message}"); }

            Log($"══ Win11 Optimizer v{AppVersion.Current} — CLI apply ══");
            Log($"Profile:  \"{profile.Name}\" ({profilePath})");
            Log($"Matched:  {matched.Count} of {profile.TweakKeys.Count} tweaks in this version");
            if (matched.Count == 0) { Log("Nothing to do."); return 2; }

            bool rpCreated = false;
            if (!skipRestorePoint)
            {
                Log("Creating System Restore Point…");
                rpCreated = TweakEngine.CreateRestorePoint("Win11Optimizer — CLI apply");
                Log(rpCreated ? "Restore Point created." : "Restore Point failed or skipped.");
            }

            TweakEngine.ClearResults();

            var ordered = matched
                .OrderBy(t => Array.IndexOf(new[] {
                    "Performance","Privacy","Responsiveness",
                    "Gaming","Network","Bloatware","Security","Advanced"
                }, t.Category))
                .ToList();

            int prevCount = 0;
            var catNames  = new List<string>();
            var details   = new List<string>();

            foreach (var entry in ordered)
            {
                if (!catNames.Contains(entry.Category)) catNames.Add(entry.Category);
                Log($"→ [{entry.Category}] {entry.Name}");

                if (entry.Category == "Bloatware")
                    TweakEngine.ApplyBloatwareTweak(entry.TweakKey);
                else if (entry.IsAdvanced && entry.AdvancedKey != null)
                    TweakEngine.ApplyAdvancedTweak(entry.AdvancedKey);
                else
                    TweakEngine.ApplyTweak(entry.TweakKey);

                var results = TweakEngine.GetResults();
                foreach (var r in results.Skip(prevCount))
                {
                    Log(r.Success ? $"   ✔ {r.Name}" : $"   ✘ {r.Name}: {r.Error}");
                    details.Add((r.Success ? "✔ " : "✘ ") + r.Name);
                }
                prevCount = results.Count;
            }

            var all  = TweakEngine.GetResults();
            int pass = all.Count(r => r.Success);
            int fail = all.Count(r => !r.Success);

            AppliedState.MarkApplied(ordered.Select(t => t.TweakKey));
            ChangeLog.AddEntry(new ChangeLog.RunEntry
            {
                Categories   = string.Join(", ", catNames) + " (CLI)",
                Passed       = pass,
                Failed       = fail,
                RestorePoint = rpCreated,
                Details      = details
            });

            var (rebootList, explorerList) = RebootInfo.Split(ordered);
            Log($"══ COMPLETE: {pass} succeeded, {fail} failed ══");
            if (rebootList.Count > 0)
                Log($"Reboot required for: {string.Join(", ", rebootList)}");
            else if (explorerList.Count > 0)
                Log($"Explorer restart recommended for: {string.Join(", ", explorerList)}");

            return fail == 0 ? 0 : 1;
        }

        private static void ListTweaks()
        {
            Log($"Win11 Optimizer v{AppVersion.Current} — available tweak keys:\n");
            foreach (var group in TweakCatalog.All.GroupBy(t => t.Category))
            {
                Log($"[{group.Key}]");
                foreach (var t in group)
                    Log($"  {t.TweakKey,-28} {t.Name}");
                Log("");
            }
        }

        private static void PrintHelp()
        {
            Log($@"
Win11 Optimizer v{AppVersion.Current} — Corn Systems
Headless usage:

  Win11Optimizer.exe --apply <profile.w11profile> [options]
      Applies every tweak in the profile without opening the GUI.
      Options:
        --silent             suppress console output (still logs to cli_run.log)
        --no-restore-point   skip creating a System Restore Point first

  Win11Optimizer.exe --list-tweaks
      Prints every tweak key, grouped by category (for building profiles by hand).

  Win11Optimizer.exe --help
      Shows this text.

Profiles are created in the GUI via 'Export Profile', or written by hand:
  {{ ""Name"": ""My Setup"", ""TweakKeys"": [ ""Perf_PowerPlan"", ""Priv_Telemetry"" ] }}

Exit codes: 0 = all succeeded, 1 = some tweaks failed, 2 = bad usage or input.
Run from an elevated prompt — tweaks need Administrator.");
        }

        private static void Log(string msg)
        {
            if (!_silent)
            {
                try { Console.WriteLine(msg); } catch { }
            }
            try { _logFile?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}"); } catch { }
        }
    }
}