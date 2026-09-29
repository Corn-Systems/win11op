using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using static Win11Optimizer.TweakEngine;

namespace Win11Optimizer
{
    // ── LIVE SYSTEM STATE DETECTOR ────────────────────────────────────────────
    // Checks whether each tweak is already applied on the current system, independent of
    // whether win11op has ever run. true = applied, false = not applied (Windows default),
    // null = can't tell. Tweaks with no readable state (BCD, netsh globals, AppX removal,
    // MMAgent, WMI adapter config, scheduled tasks, the SMBv1 feature) fall through to null.
    public static class TweakDetector
    {
        // Registry DWORD (or a numeric string); null if absent or unreadable
        private static int? Dw(string key, string name)
        {
            try
            {
                object v = Registry.GetValue(key, name, null);
                return v is int i ? i : int.TryParse(v?.ToString(), out int p) ? p : null;
            }
            catch (Exception ex) { SessionLog.Write("DETECT " + name, ex); return null; }
        }

        private static string Sz(string key, string name)
        {
            try { return Registry.GetValue(key, name, null)?.ToString(); }
            catch (Exception ex) { SessionLog.Write("DETECT " + name, ex); return null; }
        }

        // `sc qc` → true when the service's start type is DISABLED
        private static bool? ServiceDisabled(string name)
        {
            try
            {
                string o = Proc.Run("sc.exe", $"qc {name}").Out;
                if (o.Contains("DISABLED")) return true;
                return new[] { "AUTO_START", "DEMAND_START", "BOOT_START", "SYSTEM_START" }.Any(o.Contains) ? false : null;
            }
            catch (Exception ex) { SessionLog.Write("DETECT " + name, ex); return null; }
        }

        // Nagle — the first adapter that has TcpAckFrequency decides
        private static bool? NagleDisabled()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(NaglePath);
                if (root == null) return null;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("TcpAckFrequency") is { } v) return (int)v == 1;
                }
                return false;   // no adapter has it = not applied
            }
            catch (Exception ex) { SessionLog.Write("DETECT NAGLE", ex); return null; }
        }

        private static bool HostsBlockApplied()
        {
            try { return File.Exists(HostsPath) && File.ReadAllText(HostsPath).Contains(HostsMarkerStart); }
            catch (Exception ex) { SessionLog.Write("DETECT HOSTS", ex); return false; }
        }

        internal static bool? HibernationEnabled() => Dw(Ctl + @"\Power", "HibernateEnabled") is int v ? v != 0 : null;

        // True when every display adapter already has the given interrupt value set
        private static bool? AllGpus(string sub, string name, int expected)
        {
            var gpus = GpuInterruptKeys();
            return gpus.Count == 0 ? null : gpus.All(g => Dw(g + sub, name) == expected);
        }

        public static bool? Check(string key)
        {
            if (key == null) return null;
            // Laptop power-plan tweaks: compare the active plan's battery (DC) value
            if (LaptopPower.TryGetValue(key, out var lp))
            {
                var (_, dc) = QueryPowerSetting(lp.sub, lp.setting);
                return dc == null ? null : dc == lp.dc;
            }
            try
            {
                return key switch
                {
                    // ── PERFORMANCE ───────────────────────────────────────────
                    "Perf_PowerPlan"        => Sz(Ctl + @"\Power\User\PowerSchemes", "ActivePowerScheme")
                                                   ?.Equals("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase),
                    "Perf_PowerThrottle"    => Dw(Ctl + @"\Power\PowerThrottling", "PowerThrottlingOff") == 1,
                    "Perf_SysMain"          => ServiceDisabled("SysMain"),
                    "Perf_WSearch"          => ServiceDisabled("WSearch"),
                    "Perf_StartupDelay"     => Dw(CuCv + @"\Explorer\Serialize", "StartupDelayInMSec") == 0,
                    "Perf_VisualFX"         => Dw(CuCv + @"\Explorer\VisualEffects", "VisualFXSetting") == 2,
                    // fsutil keeps these in the FileSystem key
                    "Perf_NtfsLastAccess"   => Dw(Ctl + @"\FileSystem", "NtfsDisableLastAccessUpdate") is int v && (v & 1) == 1,
                    "Perf_8Dot3"            => Dw(Ctl + @"\FileSystem", "NtfsDisable8dot3NameCreation") == 1,
                    "Perf_Hibernate"        => HibernationEnabled() == false,
                    "Perf_TimerRes"         => Dw(Ctl + @"\Session Manager\kernel", "GlobalTimerResolutionRequests") == 1,
                    "Perf_Widgets"          => Dw(LmPol + @"\Dsh", "AllowNewsAndInterests") == 0,

                    // ── PRIVACY ───────────────────────────────────────────────
                    "Priv_Telemetry"        => Dw(LmPol + @"\Windows\DataCollection", "AllowTelemetry") == 0,
                    "Priv_DiagTrack"        => ServiceDisabled("DiagTrack"),
                    "Priv_AdvertisingId"    => Dw(CuCv + @"\AdvertisingInfo", "Enabled") == 0,
                    "Priv_BingStart"        => Dw(CuPol + @"\Explorer", "DisableSearchBoxSuggestions") == 1,
                    "Priv_Cortana"          => Dw(CuCv + @"\Search", "CortanaConsent") == 0,
                    "Priv_ActivityFeed"     => Dw(LmPol + @"\Windows\System", "EnableActivityFeed") == 0,
                    "Priv_Location"         => Dw(LmPol + @"\Windows\LocationAndSensors", "DisableLocation") == 1,
                    "Priv_Camera"           => Dw(LmPol + @"\Windows\AppPrivacy", "LetAppsAccessCamera") == 2,
                    "Priv_WER"              => Dw(LmPol + @"\Windows\Windows Error Reporting", "Disabled") == 1,
                    "Priv_SmartScreen"      => Dw(LmPol + @"\Windows\System", "EnableSmartScreen") == 0,
                    "Priv_AppTracking"      => Dw(ExplorerAdvanced, "Start_TrackProgs") == 0,
                    "Priv_Feedback"         => Dw(CuSw + @"\Siuf\Rules", "NumberOfSIUFInPeriod") == 0,
                    "Priv_ChatIcon"         => Dw(ExplorerAdvanced, "TaskbarMn") == 0,
                    "Priv_Recall"           => Dw(LmPol + @"\Windows\WindowsAI", "DisableAIDataAnalysis") == 1,
                    "Priv_Copilot"          => Dw(LmPol + @"\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1,
                    "Priv_HostsBlock"       => HostsBlockApplied(),
                    "Priv_CloudContent"     => Dw(LmPol + @"\Windows\CloudContent", "DisableWindowsConsumerFeatures") == 1,
                    "Priv_TailoredExp"      => Dw(CuCv + @"\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled") == 0,
                    "Priv_InkTyping"        => Dw(CuSw + @"\InputPersonalization", "RestrictImplicitTextCollection") == 1,

                    // ── RESPONSIVENESS ────────────────────────────────────────
                    "Resp_MenuDelay"        => Sz(Desktop, "MenuShowDelay") == "0",
                    "Resp_AppKill"          => Sz(Desktop, "WaitToKillAppTimeout") == "2000",
                    "Resp_ServiceKill"      => Sz(Ctl, "WaitToKillServiceTimeout") == "2000",
                    "Resp_AutoEndTasks"     => Sz(Desktop, "AutoEndTasks") == "1",
                    "Resp_VerboseStatus"    => Dw(LmCv + @"\Policies\System", "verbosestatus") == 1,
                    "Resp_WinTips"          => Dw(ContentDelivery, "SoftLandingEnabled") == 0,
                    "Resp_SuggestedContent" => Dw(ContentDelivery, "SubscribedContent-338393Enabled") == 0,
                    "Resp_ClassicContext"   => Sz(ClassicMenuKey, "") == "",
                    "Resp_EndTask"          => Dw(ExplorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask") == 1,

                    // ── GAMING ────────────────────────────────────────────────
                    "Game_HAGS"             => Dw(Ctl + @"\GraphicsDrivers", "HwSchMode") == 2,
                    "Game_GameMode"         => Dw(CuSw + @"\GameBar", "AllowAutoGameMode") == 1,
                    "Game_MouseAccel"       => new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" }.All(n => Sz(CuPanel + @"\Mouse", n) == "0"),
                    "Game_CPUPriority"      => Dw(Ctl + @"\PriorityControl", "Win32PrioritySeparation") == 38,
                    "Game_DVR"              => Dw(CuCv + @"\GameDVR", "AppCaptureEnabled") == 0,
                    "Game_FSO"              => Dw(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_FSEBehaviorMode") == 2,
                    "Game_GPUPower"         => Dw(GpuClassKey, "PerfLevelSrc") == 0x3322,
                    "Game_NvidiaTelemetry"  => ServiceDisabled("NvTelemetryContainer"),
                    "Game_StickyKeys"       => Sz(CuPanel + @"\Accessibility\StickyKeys", "Flags") == "506",

                    // ── NETWORK ───────────────────────────────────────────────
                    "Net_Nagle"             => NagleDisabled(),
                    "Net_Throttle"          => Dw(MmProfile, "NetworkThrottlingIndex") == unchecked((int)0xffffffff),
                    "Net_MMResponsive"      => Dw(MmProfile, "SystemResponsiveness") == 0,
                    "Net_DoH"               => Dw(DnsParams, "EnableAutoDoh") == 2,
                    "Net_DeliveryOpt"       => Dw(LmPol + @"\Windows\DeliveryOptimization", "DODownloadMode") == 0,

                    // ── SECURITY ──────────────────────────────────────────────
                    "Sec_AutoRun"           => Dw(LmCv + @"\Policies\Explorer", "NoDriveTypeAutoRun") == 0xFF,
                    "Sec_RDP"               => Dw(Ctl + @"\Terminal Server", "fDenyTSConnections") == 1,
                    "Sec_Defender"          => Dw(LmPol + @"\Windows Defender", "DisableAntiSpyware") == 0,

                    // ── ADVANCED ──────────────────────────────────────────────
                    "Adv_ProcessorScheduling" => Dw(Ctl + @"\PriorityControl", "Win32PrioritySeparation") == 38,
                    "Adv_MsiMode"           => AllGpus(MsiSub, "MSISupported", 1),
                    "Adv_IrqAffinity"       => AllGpus(AffinitySub, "DevicePolicy", 5),
                    "Adv_BoostMode"         => Dw(BoostModeKey, "Attributes") == 2,
                    "Adv_Animations"        => Dw(ExplorerAdvanced, "TaskbarAnimations") == 0,

                    // ── LAPTOP (registry-based; power-plan ones handled above) ─
                    "Lap_IndexOnBattery"    => Dw(LmPol + @"\Windows\Windows Search", "PreventIndexOnBattery") == 1,
                    "Lap_BackgroundApps"    => Dw(LmPol + @"\Windows\AppPrivacy", "LetAppsRunInBackground") == 2,
                    "Lap_EdgeBackground"    => Dw(LmPol + @"\Edge", "StartupBoostEnabled") == 0,
                    "Lap_Transparency"      => Dw(CuCv + @"\Themes\Personalize", "EnableTransparency") == 0,

                    _ => null
                };
            }
            catch (Exception ex) { SessionLog.Write("DETECT " + key, ex); return null; }
        }
    }
}
