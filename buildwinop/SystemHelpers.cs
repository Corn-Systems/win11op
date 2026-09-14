using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    // ── SINGLE SOURCE OF TRUTH FOR THE APP VERSION ───────────────────────────
    // Bump this alongside <Version> in the .csproj and MyAppVersion in the .iss.
    public static class AppVersion
    {
        public const string Current = "1.4.2";
        public const string RepoUrl = "https://github.com/Corn-Systems/win11op";
        public const string ReleasesApiUrl =
            "https://api.github.com/repos/Corn-Systems/win11op/releases/latest";
    }

    // ── DARK TITLE BAR ───────────────────────────────────────────────────────
    // The form body is full Corn Systems dark, but Windows paints the title bar
    // white by default. DWMWA_USE_IMMERSIVE_DARK_MODE flips it to dark. The
    // attribute id is 20 on Win10 20H1+ / Win11, and 19 on older 1809 builds.
    public static class DarkTitleBar
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
            ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE        = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

        public static void Apply(Form form)
        {
            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE,
                        ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY,
                        ref enabled, sizeof(int));
            }
            catch { /* older Windows builds without the attribute — cosmetic only */ }
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
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(8);
                // GitHub's API rejects requests without a User-Agent
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    $"Win11Optimizer/{AppVersion.Current}");

                string json = await http.GetStringAsync(AppVersion.ReleasesApiUrl)
                                        .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                tag = tag.TrimStart('v', 'V');

                if (Version.TryParse(tag, out var remote) &&
                    Version.TryParse(AppVersion.Current, out var local) &&
                    remote > local)
                    return tag;
            }
            catch { /* offline, rate-limited, DNS blocked by our own hosts tweak, etc. */ }
            return null;
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
                var psi = new ProcessStartInfo("taskkill.exe", "/f /im explorer.exe")
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi)) p?.WaitForExit(10000);

                // Give the shell a beat to fully exit before relaunching
                Thread.Sleep(500);

                // UseShellExecute so Explorer starts as the shell, not a child window
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    // ── REBOOT IMPACT CLASSIFICATION ─────────────────────────────────────────
    // Tags each tweak with what it needs to take full effect, so the app can
    // report exactly what's pending instead of a blanket "reboot recommended".
    public enum RebootImpact { None, ExplorerRestart, Reboot }

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
            "Adv_CoreParking",      // power scheme processor policy
            "Adv_MsiMode",          // interrupt mode re-read at device init
            "Adv_IrqAffinity",      // interrupt affinity re-read at device init
        };

        // Tweaks that only need the shell restarted.
        private static readonly HashSet<string> NeedsExplorer = new(StringComparer.OrdinalIgnoreCase)
        {
            "Perf_VisualFX",
            "Adv_Animations",
            "Resp_MenuDelay",
            "Resp_WinTips",
            "Resp_SuggestedContent",
            "Priv_ChatIcon",
            "Priv_BingStart",
            "Priv_CloudContent",
        };

        public static RebootImpact Classify(string tweakKey)
        {
            if (tweakKey == null)                 return RebootImpact.None;
            if (NeedsReboot.Contains(tweakKey))   return RebootImpact.Reboot;
            if (NeedsExplorer.Contains(tweakKey)) return RebootImpact.ExplorerRestart;
            return RebootImpact.None;
        }

        public static (List<string> reboot, List<string> explorer) Split(
            IEnumerable<TweakEntry> entries)
        {
            var reboot   = new List<string>();
            var explorer = new List<string>();
            foreach (var e in entries)
            {
                switch (Classify(e.TweakKey))
                {
                    case RebootImpact.Reboot:          reboot.Add(e.Name);   break;
                    case RebootImpact.ExplorerRestart: explorer.Add(e.Name); break;
                }
            }
            return (reboot, explorer);
        }
    }
}