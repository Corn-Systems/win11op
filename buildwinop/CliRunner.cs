using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        private static readonly string[] HelpFlags = { "--help", "-h", "/?" };

        private static StreamWriter _log;
        private static bool _silent;

        // Returns true when the process ran in CLI mode (caller should exit),
        // false when no CLI flags were present (caller should launch the GUI).
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || !args.Any(a => a is "--apply" or "--list-tweaks" || HelpFlags.Contains(a))) return false;

            // WinExe has no console — attach to the parent cmd/powershell window
            // so output lands where the user typed the command.
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();

            _silent = args.Contains("--silent");
            try
            {
                AppPaths.EnsureDataDir();
                _log = new StreamWriter(AppPaths.CliRunLog, append: true) { AutoFlush = true };
            }
            catch (Exception ex) { SessionLog.Write("CLI LOG", ex); }   // the log file is best-effort

            try
            {
                if (args.Any(HelpFlags.Contains)) PrintHelp();
                else if (args.Contains("--list-tweaks")) ListTweaks();
                else
                {
                    int i = Array.IndexOf(args, "--apply");
                    string path = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                    if (string.IsNullOrWhiteSpace(path) || path.StartsWith("--"))
                    {
                        Log("ERROR: --apply requires a profile path.");
                        PrintHelp();
                        exitCode = 2;
                    }
                    else exitCode = ApplyProfile(path, skipRestorePoint: args.Contains("--no-restore-point"));
                }
                return true;
            }
            finally
            {
                try { _log?.Dispose(); } catch (Exception ex) { SessionLog.Write("CLI LOG", ex); }
                _log = null;
            }
        }

        private static int ApplyProfile(string path, bool skipRestorePoint)
        {
            if (!File.Exists(path)) { Log($"ERROR: profile not found: {path}"); return 2; }

            TweakProfile.Profile profile;
            try { profile = JsonSerializer.Deserialize<TweakProfile.Profile>(File.ReadAllText(path)); }
            catch (Exception ex) { Log($"ERROR: could not parse profile: {ex.Message}"); return 2; }
            if (profile?.TweakKeys == null || profile.TweakKeys.Count == 0) { Log("ERROR: profile is empty or invalid."); return 2; }

            var keys = new HashSet<string>(profile.TweakKeys, StringComparer.OrdinalIgnoreCase);
            var todo = TweakCatalog.All.Where(t => keys.Contains(t.TweakKey))
                                       .OrderBy(t => TweakCatalog.OrderOf(t.Category)).ToList();

            if (!Program.IsAdmin())
                Log("WARNING: not running as Administrator — most tweaks will fail. Re-run from an elevated prompt.");

            Log($"══ Win11 Optimizer v{AppVersion.Current} — CLI apply ══");
            Log($"Profile:  \"{profile.Name}\" ({path})");
            Log($"Matched:  {todo.Count} of {profile.TweakKeys.Count} tweaks in this version");
            if (todo.Count == 0) { Log("Nothing to do."); return 2; }

            bool rp = false;
            if (!skipRestorePoint)
            {
                Log("Creating System Restore Point…");
                rp = TweakEngine.CreateRestorePoint("Win11Optimizer — CLI apply");
                Log(rp ? "Restore Point created." : "Restore Point failed or skipped.");
            }

            TweakEngine.ClearResults();
            var all = new List<TweakEngine.TweakResult>();
            foreach (var entry in todo)
            {
                Log($"→ [{entry.Category}] {entry.Name}");
                foreach (var r in TweakEngine.Apply(entry))
                {
                    Log(r.Success ? $"   ✔ {r.Name}" : $"   ✘ {r.Name}: {r.Error}");
                    all.Add(r);
                }
            }

            int fail = all.Count(r => !r.Success), pass = all.Count - fail;
            AppliedState.MarkApplied(todo.Select(t => t.TweakKey));
            ChangeLog.AddEntry(new ChangeLog.RunEntry
            {
                Categories   = string.Join(", ", todo.Select(t => t.Category).Distinct()) + " (CLI)",
                Passed       = pass,
                Failed       = fail,
                RestorePoint = rp,
                Details      = all.Select(r => r.Line).ToList()
            });

            var (reboot, explorer) = RebootInfo.Split(todo);
            Log($"══ COMPLETE: {pass} succeeded, {fail} failed ══");
            if (reboot.Count > 0)        Log($"Reboot required for: {string.Join(", ", reboot)}");
            else if (explorer.Count > 0) Log($"Explorer restart recommended for: {string.Join(", ", explorer)}");
            return fail == 0 ? 0 : 1;
        }

        private static void ListTweaks()
        {
            Log($"Win11 Optimizer v{AppVersion.Current} — available tweak keys:\n");
            foreach (var g in TweakCatalog.All.GroupBy(t => t.Category).OrderBy(g => TweakCatalog.OrderOf(g.Key)))
            {
                Log($"[{g.Key}]");
                foreach (var t in g) Log($"  {t.TweakKey,-28} {t.Name}");
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

        // The logger itself — a failed console/file write has nowhere else to go.
        private static void Log(string msg)
        {
            if (!_silent) try { Console.WriteLine(msg); } catch { }
            try { _log?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}"); } catch { }
        }
    }
}
