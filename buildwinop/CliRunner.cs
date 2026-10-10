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
    public static class cliRunner
    {
        [DllImport("kernel32.dll", EntryPoint = "AttachConsole")] private static extern bool attachConsole(int pid);
        [DllImport("kernel32.dll", EntryPoint = "AllocConsole")]  private static extern bool allocConsole();
        private const int attachParentProcess = -1;
        private static readonly string[] helpFlags = { "--help", "-h", "/?" };

        private static StreamWriter _log;
        private static bool _silent;

        // Returns true when the process ran in CLI mode (caller should exit),
        // false when no CLI flags were present (caller should launch the GUI).
        public static bool tryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || !args.Any(a => a is "--apply" or "--list-tweaks" || helpFlags.Contains(a))) return false;

            // WinExe has no console — attach to the parent cmd/powershell window
            // so output lands where the user typed the command.
            if (!attachConsole(attachParentProcess)) allocConsole();

            _silent = args.Contains("--silent");
            try
            {
                appPaths.ensureDataDir();
                _log = new StreamWriter(appPaths.cliRunLog, append: true) { AutoFlush = true };
            }
            catch (Exception ex) { sessionLog.write("CLI LOG", ex); }   // the log file is best-effort

            try
            {
                if (args.Any(helpFlags.Contains)) printHelp();
                else if (args.Contains("--list-tweaks")) listTweaks();
                else
                {
                    int i = Array.IndexOf(args, "--apply");
                    string path = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                    if (string.IsNullOrWhiteSpace(path) || path.StartsWith("--"))
                    {
                        log("ERROR: --apply requires a profile path.");
                        printHelp();
                        exitCode = 2;
                    }
                    else exitCode = applyProfile(path, skipRestorePoint: args.Contains("--no-restore-point"));
                }
                return true;
            }
            finally
            {
                try { _log?.Dispose(); } catch (Exception ex) { sessionLog.write("CLI LOG", ex); }
                _log = null;
            }
        }

        private static int applyProfile(string path, bool skipRestorePoint)
        {
            if (!File.Exists(path)) { log($"ERROR: profile not found: {path}"); return 2; }

            tweakProfile.profile profile;
            try { profile = JsonSerializer.Deserialize<tweakProfile.profile>(File.ReadAllText(path)); }
            catch (Exception ex) { log($"ERROR: could not parse profile: {ex.Message}"); return 2; }
            if (profile?.tweakKeys == null || profile.tweakKeys.Count == 0) { log("ERROR: profile is empty or invalid."); return 2; }

            var keys = new HashSet<string>(profile.tweakKeys, StringComparer.OrdinalIgnoreCase);
            var todo = tweakCatalog.all.Where(t => keys.Contains(t.tweakKey))
                                       .OrderBy(t => tweakCatalog.orderOf(t.category)).ToList();

            if (!program.isAdmin())
                log("WARNING: not running as Administrator — most tweaks will fail. Re-run from an elevated prompt.");

            log($"══ Win11 Optimizer v{appVersion.current} — CLI apply ══");
            log($"Profile:  \"{profile.name}\" ({path})");
            log($"Matched:  {todo.Count} of {profile.tweakKeys.Count} tweaks in this version");
            if (todo.Count == 0) { log("Nothing to do."); return 2; }

            bool rp = false;
            if (!skipRestorePoint)
            {
                log("Creating System Restore Point…");
                rp = tweakEngine.createRestorePoint("Win11Optimizer — CLI apply");
                log(rp ? "Restore Point created." : "Restore Point failed or skipped.");
            }

            tweakEngine.clearResults();
            var all = new List<tweakEngine.tweakResult>();
            foreach (var entry in todo)
            {
                log($"→ [{entry.category}] {entry.name}");
                foreach (var r in tweakEngine.apply(entry))
                {
                    log(r.success ? $"   ✔ {r.name}" : $"   ✘ {r.name}: {r.error}");
                    all.Add(r);
                }
            }

            int fail = all.Count(r => !r.success), pass = all.Count - fail;
            appliedState.markApplied(todo.Select(t => t.tweakKey));
            changeLog.addEntry(new changeLog.runEntry
            {
                categories   = string.Join(", ", todo.Select(t => t.category).Distinct()) + " (CLI)",
                passed       = pass,
                failed       = fail,
                restorePoint = rp,
                details      = all.Select(r => r.line).ToList()
            });

            var (reboot, explorer) = rebootInfo.split(todo);
            log($"══ COMPLETE: {pass} succeeded, {fail} failed ══");
            if (reboot.Count > 0)        log($"Reboot required for: {string.Join(", ", reboot)}");
            else if (explorer.Count > 0) log($"Explorer restart recommended for: {string.Join(", ", explorer)}");
            return fail == 0 ? 0 : 1;
        }

        private static void listTweaks()
        {
            log($"Win11 Optimizer v{appVersion.current} — available tweak keys:\n");
            foreach (var g in tweakCatalog.all.GroupBy(t => t.category).OrderBy(g => tweakCatalog.orderOf(g.Key)))
            {
                log($"[{g.Key}]");
                foreach (var t in g) log($"  {t.tweakKey,-28} {t.name}");
                log("");
            }
        }

        private static void printHelp()
        {
            log($@"
Win11 Optimizer v{appVersion.current} — Corn Systems
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
        private static void log(string msg)
        {
            if (!_silent) try { Console.WriteLine(msg); } catch { }
            try { _log?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}"); } catch { }
        }
    }
}
