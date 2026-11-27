using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    // ── APP VERSION ──────────────────────────────────────────────────────────
    // Read from the assembly, so <Version> in the .csproj is the only thing to bump (the
    // .iss reads it from the published exe). The SDK may append "+<commit>" — strip it.
    public static class appVersion
    {
        public static readonly string current =
            typeof(appVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

        private const string repo = "Corn-Systems/win11op";
        public const string repoUrl        = "https://github.com/" + repo;
        public const string releasesApiUrl = "https://api.github.com/repos/" + repo + "/releases/latest";
    }

    // ── PROCESS RUNNER ───────────────────────────────────────────────────────
    // The one hidden-process helper every shell-out goes through. Both pipes are drained
    // concurrently — reading them one after the other (or leaving one unread) deadlocks as
    // soon as a chatty child such as DISM fills the unread pipe's buffer.
    public static class proc
    {
        public static (int code, string output, string error) run(string file, string args, Encoding encoding = null)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (encoding != null) psi.StandardOutputEncoding = psi.StandardErrorEncoding = encoding;
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null (shell declined to launch).");
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, o.Result, e.Result);
        }

        public static (int code, string output, string error) cmd(string command) => run("cmd.exe", "/c " + command);

        // -EncodedCommand (Base64 UTF-16LE) instead of -Command "<script>": a script containing a
        // double quote can't break out of the command line, and output is forced to UTF-8 so
        // adapter / profile names with non-ASCII characters (localized Windows) survive the round
        // trip back into Set-NetAdapter* / Set-DnsClient* calls instead of turning into '?'.
        public static (int code, string output, string error) powerShell(string script)
        {
            string full = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + script;
            string b64  = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
            var (code, output, error) = run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, new UTF8Encoding(false));
            return (code, output, clixmlToText(error));
        }

        // With a redirected stderr PowerShell may serialise its error stream as CLIXML
        // ("#< CLIXML <Objs …><S S="Error">…</S>"). Turn that back into the plain message.
        private static string clixmlToText(string err)
        {
            if (string.IsNullOrEmpty(err) || !err.TrimStart().StartsWith("#< CLIXML", StringComparison.Ordinal)) return err;
            var text = string.Concat(Regex.Matches(err, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline).Select(m => m.Groups[1].Value));
            text = Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            return WebUtility.HtmlDecode(text).Trim();
        }

        // Opens a URL or file with its default handler.
        public static void open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { sessionLog.write("OPEN " + target, ex); }
        }
    }

    // ── UPDATE CHECKER ───────────────────────────────────────────────────────
    // Silent GitHub Releases check at launch. Never blocks, never throws to the
    // caller, never nags — the caller decides how to surface a newer version.
    public static class updateChecker
    {
        // Returns the newer tag (e.g. "1.5.0") if one exists, otherwise null.
        public static async Task<string> checkAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                // GitHub's API rejects requests without a User-Agent
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Win11Optimizer/{appVersion.current}");
                using var doc = JsonDocument.Parse(await http.GetStringAsync(appVersion.releasesApiUrl).ConfigureAwait(false));
                string tag = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
                return Version.TryParse(tag, out var remote) && Version.TryParse(appVersion.current, out var local) && remote > local
                    ? tag : null;
            }
            catch (Exception ex) { sessionLog.write("UPDATE CHECK", ex); return null; }   // offline, rate-limited, blocked by our own hosts tweak…
        }
    }

    // ── EXPLORER RESTART ─────────────────────────────────────────────────────
    // Many shell/UI tweaks (visual effects, menu delay, taskbar icons, animation
    // masks) take effect after an Explorer restart — no full reboot needed.
    // Blocking (it waits for the shell to come back) — call it from a background thread.
    public static class explorerHelper
    {
        public static bool restart(out string error)
        {
            error = null;
            try
            {
                var (code, _, err) = proc.run("taskkill.exe", "/f /im explorer.exe");
                // 128 = no explorer.exe was running; anything else non-zero means it couldn't be killed
                if (code != 0 && code != 128) { error = string.IsNullOrWhiteSpace(err) ? $"taskkill exit code {code}" : err.Trim(); return false; }

                // Winlogon restarts the shell itself (AutoRestartShell=1, the default) and starts it
                // as the signed-in user. Launching explorer.exe ourselves straight away used to race
                // that: either a second Explorer window popped open, or — when ours won — the shell
                // came back running ELEVATED, because this process is elevated.
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(250);
                    if (Process.GetProcessesByName("explorer").Length > 0) return true;
                }

                // AutoRestartShell is off, so start it ourselves — de-elevated via
                // `runas /trustlevel` (basic-user token), falling back to a plain launch.
                var (rc, _, rerr) = proc.run("runas.exe", "/trustlevel:0x20000 explorer.exe");
                if (rc != 0)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
                    sessionLog.write($"EXPLORER: runas /trustlevel failed ({rerr?.Trim()}), started explorer.exe directly");
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }

    // ── REBOOT IMPACT CLASSIFICATION ─────────────────────────────────────────
    // Tags each tweak with what it needs to take full effect, so the app can
    // report exactly what's pending instead of a blanket "reboot recommended".
    public static class rebootInfo
    {
        // Tweaks that need a full reboot (bcdedit, interrupt routing, kernel
        // timer, protocol/feature removal, memory manager changes).
        private static readonly HashSet<string> needsReboot = new(StringComparer.OrdinalIgnoreCase)
        {
            "Game_HAGS",            // GPU scheduler flips at boot
            "Perf_TimerRes",        // global timer resolution policy
            "Perf_MemCompression",  // MMAgent changes apply at boot
            "Sec_SMBv1",            // Windows feature removal
            "Sec_NetBIOS",          // adapter binding re-init
            "Net_Nagle",            // per-interface TCP params read at boot
            "Net_TcpTimedWait",     // Tcpip\Parameters read at boot
            "Resp_PlatformTick",    // bcdedit platform tick
            "Adv_DynamicTick",      // bcdedit
            "Adv_TscSync",          // bcdedit
            "Adv_X2Apic",           // bcdedit
            "Adv_MsiMode",          // interrupt mode re-read at device init
            "Adv_IrqAffinity",      // interrupt affinity re-read at device init
            "Game_StickyKeys",      // accessibility flags read at sign-in
            "Lap_IndexOnBattery",   // Windows Search reads policy at service start
            "Lap_BackgroundApps",   // AppPrivacy policy applied at sign-in
            "Net_DeliveryOpt",      // DoSvc reads policy at service start
            "Lap_VoiceActivation",  // AppPrivacy policy applied at sign-in
            "Lap_CrossDevice",      // CDPUserSvc reads policy at service start
            "Lap_SettingsSync",     // SettingSyncHost reads policy at sign-in
            "Perf_ReservedStorage", // servicing stack applies the change at boot
            "Priv_AppDiagInfo",     // AppPrivacy policy applied at sign-in
            "Priv_OneDrive",        // OneDrive reads the policy at sign-in
            "Priv_ClipboardSync",   // clipboard user service reads policy at start
            "Sec_LLMNR",            // DNS client reads policy at service start
            "Sec_Teredo",           // IPv6 transition policy applied at boot
            "Sec_PS2",              // optional-feature removal finishes at boot
            "Net_NicPower",         // adapter driver re-reads its properties on restart
            "Adv_VbsOff",           // VBS / HVCI toggles at boot
            "Adv_Mpo",              // Desktop Window Manager reads it at start
        };

        // Tweaks that only need the shell restarted (disjoint from needsReboot).
        private static readonly HashSet<string> needsExplorer = new(StringComparer.OrdinalIgnoreCase)
        {
            "Perf_VisualFX", "Adv_Animations", "Resp_MenuDelay", "Resp_WinTips",
            "Resp_SuggestedContent", "Priv_ChatIcon", "Priv_BingStart", "Priv_CloudContent",
            "Perf_Widgets", "Resp_ClassicContext", "Resp_EndTask", "Lap_Transparency", "Lap_SearchHighlights",
            "Priv_RecentFiles", "Priv_SearchCloud", "Resp_NagScreens", "Resp_ThisPC", "Resp_TaskbarClean", "Sec_ShowExt",
        };

        public static (List<string> reboot, List<string> explorer) split(IEnumerable<tweakEntry> entries)
        {
            var list = entries.Where(e => e.tweakKey != null).ToList();
            List<string> of(HashSet<string> set) => list.Where(e => set.Contains(e.tweakKey)).Select(e => e.name).ToList();
            return (of(needsReboot), of(needsExplorer));
        }
    }
}
