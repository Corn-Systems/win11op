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
    public static class AppVersion
    {
        public static readonly string Current =
            typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

        private const string Repo = "Corn-Systems/win11op";
        public const string RepoUrl        = "https://github.com/" + Repo;
        public const string ReleasesApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    }

    // ── PROCESS RUNNER ───────────────────────────────────────────────────────
    // The one hidden-process helper every shell-out goes through. Both pipes are drained
    // concurrently — reading them one after the other (or leaving one unread) deadlocks as
    // soon as a chatty child such as DISM fills the unread pipe's buffer.
    public static class Proc
    {
        public static (int Code, string Out, string Err) Run(string file, string args)
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

        public static (int Code, string Out, string Err) Cmd(string command) => Run("cmd.exe", "/c " + command);

        public static (int Code, string Out, string Err) PowerShell(string script) =>
            Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"");

        // Opens a URL or file with its default handler.
        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { SessionLog.Write("OPEN " + target, ex); }
        }
    }

    // ── DARK TITLE BAR ───────────────────────────────────────────────────────
    // The form body is full Corn Systems dark, but Windows paints the title bar
    // white by default. DWMWA_USE_IMMERSIVE_DARK_MODE flips it to dark. The
    // attribute id is 20 on Win10 20H1+ / Win11, and 19 on older 1809 builds.
    public static class DarkTitleBar
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE        = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

        public static void Apply(Form form)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref on, sizeof(int));
            }
            catch (Exception ex) { SessionLog.Write("DARK TITLE BAR", ex); }   // older builds without the attribute — cosmetic only
        }
    }

    // ── UPDATE CHECKER ───────────────────────────────────────────────────────
    // Silent GitHub Releases check at launch. Never blocks, never throws to the
    // caller, never nags — the caller decides how to surface a newer version.
    public static class UpdateChecker
    {
        // Returns the newer tag (e.g. "1.5.0") if one exists, otherwise null.
        public static async Task<string> CheckAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                // GitHub's API rejects requests without a User-Agent
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Win11Optimizer/{AppVersion.Current}");
                using var doc = JsonDocument.Parse(await http.GetStringAsync(AppVersion.ReleasesApiUrl).ConfigureAwait(false));
                string tag = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
                return Version.TryParse(tag, out var remote) && Version.TryParse(AppVersion.Current, out var local) && remote > local
                    ? tag : null;
            }
            catch (Exception ex) { SessionLog.Write("UPDATE CHECK", ex); return null; }   // offline, rate-limited, blocked by our own hosts tweak…
        }
    }

    // ── EXPLORER RESTART ─────────────────────────────────────────────────────
    // Many shell/UI tweaks (visual effects, menu delay, taskbar icons, animation
    // masks) take effect after an Explorer restart — no full reboot needed.
    public static class ExplorerHelper
    {
        public static bool Restart(out string error)
        {
            error = null;
            try
            {
                Proc.Run("taskkill.exe", "/f /im explorer.exe");
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
    public static class RebootInfo
    {
        // Tweaks that need a full reboot (bcdedit, interrupt routing, kernel
        // timer, protocol/feature removal, memory manager changes).
        private static readonly HashSet<string> NeedsReboot = new(StringComparer.OrdinalIgnoreCase)
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
        };

        // Tweaks that only need the shell restarted (disjoint from NeedsReboot).
        private static readonly HashSet<string> NeedsExplorer = new(StringComparer.OrdinalIgnoreCase)
        {
            "Perf_VisualFX", "Adv_Animations", "Resp_MenuDelay", "Resp_WinTips",
            "Resp_SuggestedContent", "Priv_ChatIcon", "Priv_BingStart", "Priv_CloudContent",
            "Perf_Widgets", "Resp_ClassicContext", "Resp_EndTask", "Lap_Transparency",
        };

        public static (List<string> reboot, List<string> explorer) Split(IEnumerable<TweakEntry> entries)
        {
            var list = entries.Where(e => e.TweakKey != null).ToList();
            List<string> Of(HashSet<string> set) => list.Where(e => set.Contains(e.TweakKey)).Select(e => e.Name).ToList();
            return (Of(NeedsReboot), Of(NeedsExplorer));
        }
    }
}
