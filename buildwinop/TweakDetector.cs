using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using static Win11Optimizer.tweakEngine;

namespace Win11Optimizer
{
    // ── LIVE SYSTEM STATE DETECTOR ────────────────────────────────────────────
    // Checks whether each tweak is already applied on the current system, independent of
    // whether win11op has ever run. true = applied, false = not applied (Windows default),
    // null = can't tell. Tweaks with no readable state (BCD, netsh globals, AppX removal,
    // MMAgent, WMI adapter config, scheduled tasks, the SMBv1 feature) fall through to null.
    public static class tweakDetector
    {
        // Registry DWORD (or a numeric string); null if absent or unreadable
        private static int? dw(string key, string name)
        {
            try
            {
                object v = Registry.GetValue(key, name, null);
                return v is int i ? i : int.TryParse(v?.ToString(), out int p) ? p : null;
            }
            catch (Exception ex) { sessionLog.write("DETECT " + name, ex); return null; }
        }

        private static string sz(string key, string name)
        {
            try { return Registry.GetValue(key, name, null)?.ToString(); }
            catch (Exception ex) { sessionLog.write("DETECT " + name, ex); return null; }
        }

        // `sc qc` → true when the service's start type is DISABLED
        private static bool? serviceDisabled(string name)
        {
            try
            {
                string o = proc.run("sc.exe", $"qc {name}").output;
                if (o.Contains("DISABLED")) return true;
                return new[] { "AUTO_START", "DEMAND_START", "BOOT_START", "SYSTEM_START" }.Any(o.Contains) ? false : null;
            }
            catch (Exception ex) { sessionLog.write("DETECT " + name, ex); return null; }
        }

        // Nagle — the first adapter that has TcpAckFrequency decides
        private static bool? nagleDisabled()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(naglePath);
                if (root == null) return null;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("TcpAckFrequency") is { } v) return (int)v == 1;
                }
                return false;   // no adapter has it = not applied
            }
            catch (Exception ex) { sessionLog.write("DETECT NAGLE", ex); return null; }
        }

        private static bool hostsBlockApplied()
        {
            try { return File.Exists(hostsPath) && File.ReadAllText(hostsPath).Contains(hostsMarkerStart); }
            catch (Exception ex) { sessionLog.write("DETECT HOSTS", ex); return false; }
        }

        internal static bool? hibernationEnabled() => dw(ctl + @"\Power", "HibernateEnabled") is int v ? v != 0 : null;

        // true when every part this device exposes already satisfies the tweak; null when none are readable
        private static bool? powerApplied(powerPart[] parts)
        {
            bool any = false;
            foreach (var p in parts)
            {
                var info = queryPower(p.sub, p.setting);
                if (info?.dc is not int dc || powerTarget(p, info) is not int target) continue;
                any = true;
                if (!p.satisfied(dc, target)) return false;
            }
            return any ? true : null;
        }

        // Every installed browser / Teams already prefers the integrated GPU; null when there are none
        private static bool? gpuPreferencesApplied()
        {
            var apps = hardware.gpuPreferenceApps();
            return apps.Count == 0 ? null : apps.All(a => sz(gpuPrefKey, a) == "GpuPreference=1;");
        }

        // One line for the tooltip: what the battery-side settings are right now, and what the tweak would change them to
        public static string currentSummary(string key)
        {
            if (!laptopPower.TryGetValue(key, out var parts)) return null;
            var bits = new System.Collections.Generic.List<string>();
            foreach (var p in parts)
            {
                var info = queryPower(p.sub, p.setting);
                if (info?.dc is not int dc || powerTarget(p, info) is not int target) continue;
                bits.Add(p.satisfied(dc, target)
                    ? $"{p.shortName} {p.show(info, dc)} ✔"
                    : $"{p.shortName} {p.show(info, dc)} → {p.show(info, target)}");
            }
            return bits.Count == 0 ? null : string.Join("  ·  ", bits);
        }

        // True when every display adapter already has the given interrupt value set
        private static bool? allGpus(string sub, string name, int expected)
        {
            var gpus = gpuInterruptKeys();
            return gpus.Count == 0 ? null : gpus.All(g => dw(g + sub, name) == expected);
        }

        public static bool? check(string key)
        {
            if (key == null) return null;
            // Laptop power-plan tweaks: every readable part of the active plan's battery (DC) values must be satisfied
            if (laptopPower.TryGetValue(key, out var parts))
            {
                bool? power = powerApplied(parts);
                if (key != "Lap_GpuPowerSaving") return power;
                bool? apps = gpuPreferencesApplied();
                return power == null ? apps : apps == null ? power : power == true && apps == true;
            }
            try
            {
                return key switch
                {
                    // ── PERFORMANCE ───────────────────────────────────────────
                    "Perf_PowerPlan"        => sz(ctl + @"\Power\User\PowerSchemes", "ActivePowerScheme")
                                                   ?.Equals("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase),
                    "Perf_PowerThrottle"    => dw(ctl + @"\Power\PowerThrottling", "PowerThrottlingOff") == 1,
                    "Perf_SysMain"          => serviceDisabled("SysMain"),
                    "Perf_WSearch"          => serviceDisabled("WSearch"),
                    "Perf_StartupDelay"     => dw(cuCv + @"\Explorer\Serialize", "StartupDelayInMSec") == 0,
                    "Perf_VisualFX"         => dw(cuCv + @"\Explorer\VisualEffects", "VisualFXSetting") == 2,
                    // fsutil keeps these in the FileSystem key
                    "Perf_NtfsLastAccess"   => dw(ctl + @"\FileSystem", "NtfsDisableLastAccessUpdate") is int v && (v & 1) == 1,
                    "Perf_8Dot3"            => dw(ctl + @"\FileSystem", "NtfsDisable8dot3NameCreation") == 1,
                    "Perf_Hibernate"        => hibernationEnabled() == false,
                    "Perf_TimerRes"         => dw(ctl + @"\Session Manager\kernel", "GlobalTimerResolutionRequests") == 1,
                    "Perf_Widgets"          => dw(lmPol + @"\Dsh", "AllowNewsAndInterests") == 0,

                    // ── PRIVACY ───────────────────────────────────────────────
                    "Priv_Telemetry"        => dw(lmPol + @"\Windows\DataCollection", "AllowTelemetry") == 0,
                    "Priv_DiagTrack"        => serviceDisabled("DiagTrack"),
                    "Priv_AdvertisingId"    => dw(cuCv + @"\AdvertisingInfo", "Enabled") == 0,
                    "Priv_BingStart"        => dw(cuPol + @"\Explorer", "DisableSearchBoxSuggestions") == 1,
                    "Priv_Cortana"          => dw(cuCv + @"\Search", "CortanaConsent") == 0,
                    "Priv_ActivityFeed"     => dw(lmPol + @"\Windows\System", "EnableActivityFeed") == 0,
                    "Priv_Location"         => dw(lmPol + @"\Windows\LocationAndSensors", "DisableLocation") == 1,
                    "Priv_Camera"           => dw(lmPol + @"\Windows\AppPrivacy", "LetAppsAccessCamera") == 2,
                    "Priv_WER"              => dw(lmPol + @"\Windows\Windows Error Reporting", "Disabled") == 1,
                    "Priv_SmartScreen"      => dw(lmPol + @"\Windows\System", "EnableSmartScreen") == 0,
                    "Priv_AppTracking"      => dw(explorerAdvanced, "Start_TrackProgs") == 0,
                    "Priv_Feedback"         => dw(cuSw + @"\Siuf\Rules", "NumberOfSIUFInPeriod") == 0,
                    "Priv_ChatIcon"         => dw(explorerAdvanced, "TaskbarMn") == 0,
                    "Priv_Recall"           => dw(lmPol + @"\Windows\WindowsAI", "DisableAIDataAnalysis") == 1,
                    "Priv_Copilot"          => dw(lmPol + @"\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1,
                    "Priv_HostsBlock"       => hostsBlockApplied(),
                    "Priv_CloudContent"     => dw(lmPol + @"\Windows\CloudContent", "DisableWindowsConsumerFeatures") == 1,
                    "Priv_TailoredExp"      => dw(cuCv + @"\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled") == 0,
                    "Priv_InkTyping"        => dw(cuSw + @"\InputPersonalization", "RestrictImplicitTextCollection") == 1,

                    // ── RESPONSIVENESS ────────────────────────────────────────
                    "Resp_MenuDelay"        => sz(desktop, "MenuShowDelay") == "0",
                    "Resp_AppKill"          => sz(desktop, "WaitToKillAppTimeout") == "2000",
                    "Resp_ServiceKill"      => sz(ctl, "WaitToKillServiceTimeout") == "2000",
                    "Resp_AutoEndTasks"     => sz(desktop, "AutoEndTasks") == "1",
                    "Resp_VerboseStatus"    => dw(lmCv + @"\Policies\System", "verbosestatus") == 1,
                    "Resp_WinTips"          => dw(contentDelivery, "SoftLandingEnabled") == 0,
                    "Resp_SuggestedContent" => dw(contentDelivery, "SubscribedContent-338393Enabled") == 0,
                    "Resp_ClassicContext"   => sz(classicMenuKey, "") == "",
                    "Resp_EndTask"          => dw(explorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask") == 1,

                    // ── GAMING ────────────────────────────────────────────────
                    "Game_HAGS"             => dw(ctl + @"\GraphicsDrivers", "HwSchMode") == 2,
                    "Game_GameMode"         => dw(cuSw + @"\GameBar", "AllowAutoGameMode") == 1,
                    "Game_MouseAccel"       => new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" }.All(n => sz(cuPanel + @"\Mouse", n) == "0"),
                    "Game_CPUPriority"      => dw(ctl + @"\PriorityControl", "Win32PrioritySeparation") == 38,
                    "Game_DVR"              => dw(cuCv + @"\GameDVR", "AppCaptureEnabled") == 0,
                    "Game_FSO"              => dw(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_FSEBehaviorMode") == 2,
                    "Game_GPUPower"         => dw(gpuClassKey, "PerfLevelSrc") == 0x3322,
                    "Game_NvidiaTelemetry"  => serviceDisabled("NvTelemetryContainer"),
                    "Game_StickyKeys"       => sz(cuPanel + @"\Accessibility\StickyKeys", "Flags") == "506",

                    // ── NETWORK ───────────────────────────────────────────────
                    "Net_Nagle"             => nagleDisabled(),
                    "Net_Throttle"          => dw(mmProfile, "NetworkThrottlingIndex") == unchecked((int)0xffffffff),
                    "Net_MMResponsive"      => dw(mmProfile, "SystemResponsiveness") == 0,
                    "Net_DoH"               => dw(dnsParams, "EnableAutoDoh") == 2,
                    "Net_DeliveryOpt"       => dw(lmPol + @"\Windows\DeliveryOptimization", "DODownloadMode") == 0,

                    // ── SECURITY ──────────────────────────────────────────────
                    "Sec_AutoRun"           => dw(lmCv + @"\Policies\Explorer", "NoDriveTypeAutoRun") == 0xFF,
                    "Sec_RDP"               => dw(ctl + @"\Terminal Server", "fDenyTSConnections") == 1,
                    "Sec_Defender"          => dw(lmPol + @"\Windows Defender", "DisableAntiSpyware") == 0,

                    // ── ADVANCED ──────────────────────────────────────────────
                    "Adv_ProcessorScheduling" => dw(ctl + @"\PriorityControl", "Win32PrioritySeparation") == 38,
                    "Adv_MsiMode"           => allGpus(msiSub, "MSISupported", 1),
                    "Adv_IrqAffinity"       => allGpus(affinitySub, "DevicePolicy", 5),
                    "Adv_BoostMode"         => dw(boostModeKey, "Attributes") == 2,
                    "Adv_Animations"        => dw(explorerAdvanced, "TaskbarAnimations") == 0,

                    // ── LAPTOP (registry-based; power-plan ones handled above) ─
                    "Lap_IndexOnBattery"    => dw(lmPol + @"\Windows\Windows Search", "PreventIndexOnBattery") == 1,
                    "Lap_BackgroundApps"    => dw(lmPol + @"\Windows\AppPrivacy", "LetAppsRunInBackground") == 2,
                    "Lap_EdgeBackground"    => dw(lmPol + @"\Edge", "StartupBoostEnabled") == 0,
                    "Lap_Transparency"      => dw(cuCv + @"\Themes\Personalize", "EnableTransparency") == 0,
                    "Lap_VoiceActivation"   => dw(appPrivacy, "LetAppsActivateWithVoice") == 2,
                    "Lap_CrossDevice"       => dw(lmPol + @"\Windows\System", "EnableCdp") == 0,
                    "Lap_SearchHighlights"  => dw(cuCv + @"\SearchSettings", "IsDynamicSearchBoxEnabled") == 0,
                    "Lap_SettingsSync"      => dw(lmPol + @"\Windows\SettingSync", "DisableSettingSync") == 2,
                    "Lap_MaintenanceWake"   => dw(maintenanceKey, "WakeUp") == 0 || dw(lmPol + @"\Windows\ScheduledMaintenance", "WakeUp") == 0,

                    _ => null
                };
            }
            catch (Exception ex) { sessionLog.write("DETECT " + key, ex); return null; }
        }
    }
}
