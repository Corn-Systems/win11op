using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    // ── APP VERSION ──────────────────────────────────────────────────────────
    // Read from the assembly, so <Version> in the .csproj (plus MyAppVersion in the
    // .iss) is the only thing to bump. The SDK may append "+<commit>" — strip it.
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
        public static (int code, string output, string error) run(string file, string args)
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Process.Start returned null (shell declined to launch).");
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, o.Result, e.Result);
        }

        public static (int code, string output, string error) cmd(string command) => run("cmd.exe", "/c " + command);

        public static (int code, string output, string error) powerShell(string script) =>
            run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"");

        // Opens a URL or file with its default handler.
        public static void open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { sessionLog.write("OPEN " + target, ex); }
        }
    }

    // ── DARK TITLE BAR ───────────────────────────────────────────────────────
    // The form body is full Corn Systems dark, but Windows paints the title bar
    // white by default. DWMWA_USE_IMMERSIVE_DARK_MODE flips it to dark. The
    // attribute id is 20 on Win10 20H1+ / Win11, and 19 on older 1809 builds.
    public static class darkTitleBar
    {
        [DllImport("dwmapi.dll", PreserveSig = true, EntryPoint = "DwmSetWindowAttribute")]
        private static extern int dwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int dwmwaUseImmersiveDarkMode        = 20;
        private const int dwmwaUseImmersiveDarkModeLegacy = 19;

        public static void apply(Form form)
        {
            try
            {
                int on = 1;
                if (dwmSetWindowAttribute(form.Handle, dwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                    dwmSetWindowAttribute(form.Handle, dwmwaUseImmersiveDarkModeLegacy, ref on, sizeof(int));
            }
            catch (Exception ex) { sessionLog.write("DARK TITLE BAR", ex); }   // older builds without the attribute — cosmetic only
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
    public static class explorerHelper
    {
        public static bool restart(out string error)
        {
            error = null;
            try
            {
                proc.run("taskkill.exe", "/f /im explorer.exe");
                Thread.Sleep(500);   // give the shell a beat to fully exit before relaunching
                // UseShellExecute so Explorer starts as the shell, not a child window
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
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
        };

        // Tweaks that only need the shell restarted (disjoint from needsReboot).
        private static readonly HashSet<string> needsExplorer = new(StringComparer.OrdinalIgnoreCase)
        {
            "Perf_VisualFX", "Adv_Animations", "Resp_MenuDelay", "Resp_WinTips",
            "Resp_SuggestedContent", "Priv_ChatIcon", "Priv_BingStart", "Priv_CloudContent",
            "Perf_Widgets", "Resp_ClassicContext", "Resp_EndTask", "Lap_Transparency", "Lap_SearchHighlights",
        };

        public static (List<string> reboot, List<string> explorer) split(IEnumerable<tweakEntry> entries)
        {
            var list = entries.Where(e => e.tweakKey != null).ToList();
            List<string> of(HashSet<string> set) => list.Where(e => set.Contains(e.tweakKey)).Select(e => e.name).ToList();
            return (of(needsReboot), of(needsExplorer));
        }
    }
}
