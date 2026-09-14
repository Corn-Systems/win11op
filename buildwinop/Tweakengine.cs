using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
#nullable disable warnings

namespace Win11Optimizer
{
    public static class TweakEngine
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);

        // ── RESULTS ───────────────────────────────────────────────────────
        public class TweakResult
        {
            public string Name    { get; set; } = string.Empty;
            public bool   Success { get; set; }
            public string Error   { get; set; }
        }

        private static readonly List<TweakResult> _results = new();
        public static IReadOnlyList<TweakResult> GetResults() => _results.AsReadOnly();
        public static void ClearResults() => _results.Clear();

        // ── BACKUP / RESTORE ──────────────────────────────────────────────
        public class BackupEntry
        {
            public string Category  { get; set; }
            public string KeyPath   { get; set; }
            public string ValueName { get; set; }
            public string ValueData { get; set; }
            public string ValueKind { get; set; }
            public bool   Existed   { get; set; }
        }

        private static readonly List<BackupEntry> _backups = new();
        private static readonly HashSet<string>   _appliedCategories = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string            BackupFile = AppPaths.TweaksBackupFile;

        public static IReadOnlyCollection<string> AppliedCategories => _appliedCategories;
        public static bool HasBackup(string category) => _appliedCategories.Contains(category);

        private static RegistryKey RootKey(string hive) => hive switch
        {
            "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKEY_CURRENT_USER"  => Registry.CurrentUser,
            "HKEY_CLASSES_ROOT"  => Registry.ClassesRoot,
            "HKEY_USERS"         => Registry.Users,
            _                    => null
        };

        private static (RegistryKey root, string sub) SplitPath(string keyPath)
        {
            var parts = keyPath.Split('\\', 2);
            return (RootKey(parts[0]), parts[1]);
        }

        private static void BackupRegistry(string category, string keyPath, string valueName)
        {
            try
            {
                object current = Registry.GetValue(keyPath, valueName, null);
                var kind = RegistryValueKind.Unknown;
                var (root, sub) = SplitPath(keyPath);
                using var key = root?.OpenSubKey(sub);
                if (key != null) kind = key.GetValueKind(valueName);
                _backups.Add(new BackupEntry
                {
                    Category = category, KeyPath = keyPath, ValueName = valueName,
                    ValueData = current?.ToString() ?? "", ValueKind = kind.ToString(), Existed = current != null
                });
            }
            catch
            {
                _backups.Add(new BackupEntry
                {
                    Category = category, KeyPath = keyPath, ValueName = valueName,
                    ValueData = "", ValueKind = RegistryValueKind.Unknown.ToString(), Existed = false
                });
            }
        }

        public static void SaveBackups()
        {
            try { AppPaths.EnsureDataDir(); File.WriteAllText(BackupFile, JsonSerializer.Serialize(_backups,
                new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { SessionLog.Write("BACKUP SAVE", ex); }
        }

        public static void LoadBackups()
        {
            try
            {
                if (!File.Exists(BackupFile)) return;
                var loaded = JsonSerializer.Deserialize<List<BackupEntry>>(File.ReadAllText(BackupFile));
                if (loaded == null) return;
                _backups.Clear(); _backups.AddRange(loaded);
                foreach (var b in _backups) _appliedCategories.Add(b.Category);
            }
            catch (Exception ex) { SessionLog.Write("BACKUP LOAD", ex); }
        }

        public static List<TweakResult> RestoreCategory(string category)
        {
            var res      = new List<TweakResult>();
            var toRemove = new List<BackupEntry>();

            foreach (var entry in _backups)
            {
                if (!entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var (root, sub) = SplitPath(entry.KeyPath);
                    if (!entry.Existed)
                    {
                        root?.OpenSubKey(sub, writable: true)?.DeleteValue(entry.ValueName, false);
                        res.Add(new TweakResult { Name = $"Removed {entry.ValueName}", Success = true });
                    }
                    else
                    {
                        var kind = Enum.Parse<RegistryValueKind>(entry.ValueKind);
                        object val = kind switch
                        {
                            RegistryValueKind.DWord => int.Parse(entry.ValueData),
                            RegistryValueKind.QWord => long.Parse(entry.ValueData),
                            _                       => entry.ValueData
                        };
                        Registry.SetValue(entry.KeyPath, entry.ValueName, val, kind);
                        res.Add(new TweakResult { Name = $"Restored {entry.ValueName}", Success = true });
                    }
                    toRemove.Add(entry);
                }
                catch (Exception ex)
                {
                    res.Add(new TweakResult { Name = $"Restore {entry.ValueName}", Success = false, Error = ex.Message });
                }
            }

            foreach (var e in toRemove) _backups.Remove(e);
            if (!_backups.Exists(b => b.Category.Equals(category, StringComparison.OrdinalIgnoreCase)))
                _appliedCategories.Remove(category);
            SaveBackups();
            return res;
        }

        // ── HELPERS ───────────────────────────────────────────────────────
        private static string _currentCategory = "";

        private static void SetRegistry(string keyPath, string valueName, object value,
                                        RegistryValueKind kind, string friendlyName = null)
        {
            if (!string.IsNullOrEmpty(_currentCategory))
                BackupRegistry(_currentCategory, keyPath, valueName);
            string name = friendlyName ?? valueName;
            try
            {
                Registry.SetValue(keyPath, valueName, value, kind);
                _results.Add(new TweakResult { Name = name, Success = true });
            }
            catch (Exception ex)
            {
                _results.Add(new TweakResult { Name = name, Success = false, Error = ex.Message });
            }
        }

        private static void RunCommand(string command, string friendlyName = null)
        {
            string name = friendlyName ?? command;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("Process.Start returned null (shell declined to launch).");
                p.WaitForExit();
                _results.Add(new TweakResult { Name = name, Success = p.ExitCode == 0 });
            }
            catch (Exception ex)
            {
                _results.Add(new TweakResult { Name = name, Success = false, Error = ex.Message });
            }
        }

        private static void RunPowerShell(string script, string friendlyName = null)
        {
            string name = friendlyName ?? script[..Math.Min(60, script.Length)];
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName  = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("Process.Start returned null (shell declined to launch).");
                p.WaitForExit();
                _results.Add(new TweakResult { Name = name, Success = p.ExitCode == 0 });
            }
            catch (Exception ex)
            {
                _results.Add(new TweakResult { Name = name, Success = false, Error = ex.Message });
            }
        }

        private static void EnsureRegistryKey(string keyPath)
        {
            try { var (root, sub) = SplitPath(keyPath); root?.CreateSubKey(sub, writable: true); }
            catch { }
        }

        private static void DisableService(string s)
            => RunCommand($"sc config {s} start=disabled & net stop {s} 2>nul", $"Disable: {s}");
        private static void EnableService(string s)
            => RunCommand($"sc config {s} start=auto & net start {s} 2>nul", $"Re-enable: {s}");
        private static void DisableTask(string t)
            => RunCommand($"schtasks /Change /TN \"{t}\" /Disable 2>nul", $"Disable task: {t}");
        private static void EnableTask(string t)
            => RunCommand($"schtasks /Change /TN \"{t}\" /Enable 2>nul", $"Re-enable task: {t}");

        private static void Dispatch(string category, Action action)
        {
            _currentCategory = category;
            action();
            _currentCategory = "";
            _appliedCategories.Add(category);
            SaveBackups();
        }

        private static string CategoryForKey(string key)
        {
            if (key.StartsWith("Perf_"))  return "Performance";
            if (key.StartsWith("Priv_"))  return "Privacy";
            if (key.StartsWith("Resp_"))  return "Responsiveness";
            if (key.StartsWith("Game_"))  return "Gaming";
            if (key.StartsWith("Net_"))   return "Network";
            if (key.StartsWith("Bloat_")) return "Bloatware";
            if (key.StartsWith("Sec_"))   return "Security";
            if (key.StartsWith("Adv_"))   return "Advanced";
            return "";
        }

        // ── UNDO (called by MainForm) ─────────────────────────────────────
        public static List<TweakResult> UndoPerformanceTweaks()
        {
            var r = RestoreCategory("Performance");
            RunCommand("powercfg -setactive 381b4222-f694-41f0-9685-ff5bb260df2e", "Restore Balanced power plan");
            EnableService("SysMain"); EnableService("WSearch");
            RunCommand("fsutil behavior set disablelastaccess 0", "Re-enable NTFS last-access");
            RunCommand("powercfg -h on", "Re-enable hibernation");
            RunPowerShell("Enable-MMAgent -MemoryCompression", "Re-enable memory compression");
            RunPowerShell("Enable-MMAgent -PageCombining",    "Re-enable page combining");
            try { TimeEndPeriod(1); } catch { }
            return r;
        }

        public static List<TweakResult> UndoPrivacyTweaks()
        {
            var r = RestoreCategory("Privacy");
            EnableService("DiagTrack"); EnableService("WerSvc");
            EnableTask(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator");
            EnableTask(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip");
            EnableTask(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting");
            EnableTask(@"\Microsoft\Windows\Diagnosis\Scheduled");
            EnableTask(@"\Microsoft\Windows\Push Notifications\LockApplicationComponent");
            RemoveHostsBlockList();
            return r;
        }

        public static List<TweakResult> UndoResponsivenessTweaks()
        {
            var r = RestoreCategory("Responsiveness");
            RunCommand("bcdedit /deletevalue useplatformtick 2>nul",  "Restore platform tick default");
            RunCommand("bcdedit /deletevalue useplatformclock 2>nul", "Restore platform clock default");
            return r;
        }

        public static List<TweakResult> UndoGamingTweaks()
        {
            var r = RestoreCategory("Gaming");
            EnableService("NvTelemetryContainer"); EnableService("NvDisplayContainerLS");
            return r;
        }

        public static List<TweakResult> UndoNetworkTweaks()
        {
            var r = RestoreCategory("Network");
            RestoreNaglesAlgorithm();
            RunCommand("netsh int tcp set global autotuninglevel=normal", "Restore TCP auto-tuning");
            // Re-enable Large Send Offload
            RunPowerShell(
                "Get-NetAdapter -Physical | ForEach-Object { " +
                "  try { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload V2 (IPv4)' -DisplayValue 'Enabled' -ErrorAction SilentlyContinue } catch {}; " +
                "  try { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload V2 (IPv6)' -DisplayValue 'Enabled' -ErrorAction SilentlyContinue } catch {}; " +
                "  try { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload Version 2 (IPv4)' -DisplayValue 'Enabled' -ErrorAction SilentlyContinue } catch {}; " +
                "  try { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload Version 2 (IPv6)' -DisplayValue 'Enabled' -ErrorAction SilentlyContinue } catch {} " +
                "}",
                "Re-enable Large Send Offload (LSO)");
            return r;
        }

        public static List<TweakResult> UndoAdvancedTweaks()
        {
            var r = RestoreCategory("Advanced");
            RunCommand("bcdedit /deletevalue disabledynamictick 2>nul", "Restore dynamic tick default");
            // Restore core parking to Windows-managed default (0 = park freely)
            RunCommand(
                "powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 0 & " +
                "powercfg -setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 0 & " +
                "powercfg -setactive SCHEME_CURRENT",
                "Restore CPU core parking");
            RunCommand("bcdedit /deletevalue tscsyncpolicy 2>nul", "Restore TSC sync policy default");
            RunCommand("bcdedit /deletevalue x2apicpolicy 2>nul",  "Restore x2APIC policy default");
            return r;
        }

        public static List<TweakResult> UndoSecurityTweaks() => RestoreCategory("Security");

        // ── HOSTS BLOCK LIST ──────────────────────────────────────────────
        private static readonly string[] TelemetryHosts =
        {
            "vortex.data.microsoft.com",           "vortex-win.data.microsoft.com",
            "telecommand.telemetry.microsoft.com", "telecommand.telemetry.microsoft.com.nsatc.net",
            "oca.telemetry.microsoft.com",         "oca.telemetry.microsoft.com.nsatc.net",
            "sqm.telemetry.microsoft.com",         "sqm.telemetry.microsoft.com.nsatc.net",
            "watson.telemetry.microsoft.com",      "watson.telemetry.microsoft.com.nsatc.net",
            "redir.metaservices.microsoft.com",    "choice.microsoft.com",
            "choice.microsoft.com.nsatc.net",      "df.telemetry.microsoft.com",
            "reports.wes.df.telemetry.microsoft.com","wes.df.telemetry.microsoft.com",
            "services.wes.df.telemetry.microsoft.com","sqm.df.telemetry.microsoft.com",
            "telemetry.microsoft.com",             "watson.ppe.telemetry.microsoft.com",
            "settings-win.data.microsoft.com",     "telemetry.appex.bing.net",
            "telemetry.urs.microsoft.com",         "telemetry.appex.bing.net:443",
            "settings-sandbox.data.microsoft.com", "survey.watson.microsoft.com",
            "watson.live.com",                     "watson.microsoft.com",
            "statsfe2.ws.microsoft.com",           "corpext.msitadfs.glbdns2.microsoft.com",
            "compatexchange.cloudapp.net",         "cs1.wpc.v0cdn.net",
            "a-0001.a-msedge.net",                 "statsfe2.update.microsoft.com.akadns.net",
            "sls.update.microsoft.com.akadns.net", "fe2.update.microsoft.com.akadns.net",
        };

        private const string HostsMarkerStart = "# WIN11OPTIMIZER_TELEMETRY_BLOCK_START";
        private const string HostsMarkerEnd   = "# WIN11OPTIMIZER_TELEMETRY_BLOCK_END";
        private static string HostsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        private static void ApplyHostsBlockList()
        {
            try
            {
                string current = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
                if (current.Contains(HostsMarkerStart))
                {
                    _results.Add(new TweakResult { Name = "Block telemetry hosts (already applied)", Success = true });
                    return;
                }
                var sb = new StringBuilder();
                sb.AppendLine().AppendLine(HostsMarkerStart);
                foreach (var host in TelemetryHosts) sb.AppendLine($"0.0.0.0 {host}");
                sb.AppendLine(HostsMarkerEnd);
                File.AppendAllText(HostsPath, sb.ToString());
                _results.Add(new TweakResult { Name = $"Blocked {TelemetryHosts.Length} telemetry domains", Success = true });
            }
            catch (Exception ex)
            {
                _results.Add(new TweakResult { Name = "Block telemetry hosts", Success = false, Error = ex.Message });
            }
        }

        private static void RemoveHostsBlockList()
        {
            try
            {
                if (!File.Exists(HostsPath)) return;
                string content = File.ReadAllText(HostsPath);
                int start = content.IndexOf(HostsMarkerStart);
                int end   = content.IndexOf(HostsMarkerEnd);
                if (start < 0 || end < 0) return;
                File.WriteAllText(HostsPath, content.Remove(start, (end - start) + HostsMarkerEnd.Length + 2));
            }
            catch (Exception ex) { SessionLog.Write("HOSTS RESTORE", ex); }
        }

        // ── NAGLE'S ALGORITHM ─────────────────────────────────────────────
        private const string NaglePath =
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

        private static void ModifyNagle(bool disable)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(NaglePath, writable: true);
                if (baseKey == null)
                {
                    if (disable) _results.Add(new TweakResult
                        { Name = "Disable Nagle's Algorithm", Success = false, Error = "Base key not found" });
                    return;
                }
                foreach (string sub in baseKey.GetSubKeyNames())
                {
                    using var subKey = baseKey.OpenSubKey(sub, writable: true);
                    if (disable) { subKey?.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord); subKey?.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord); }
                    else         { subKey?.DeleteValue("TcpAckFrequency", false); subKey?.DeleteValue("TCPNoDelay", false); }
                }
                if (disable) _results.Add(new TweakResult { Name = "Disable Nagle's Algorithm", Success = true });
            }
            catch (Exception ex)
            {
                if (disable) _results.Add(new TweakResult
                    { Name = "Disable Nagle's Algorithm", Success = false, Error = ex.Message });
            }
        }

        private static void DisableNaglesAlgorithm() => ModifyNagle(true);
        private static void RestoreNaglesAlgorithm()  => ModifyNagle(false);

        // ── SYSTEM RESTORE POINT ──────────────────────────────────────────
        public static bool CreateRestorePoint(string description)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName  = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                                $"\"Checkpoint-Computer -Description '{description.Replace("'", "")}'" +
                                $" -RestorePointType MODIFY_SETTINGS\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("Process.Start returned null (shell declined to launch).");
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    string err = p.StandardError.ReadToEnd().Trim();
                    return err.Contains("0x80042306") || err.Contains("too soon") || err.Contains("frequency");
                }
                return true;
            }
            catch (Exception ex) { SessionLog.Write("RESTORE POINT", ex); return false; }
        }

        // ── BLOATWARE ─────────────────────────────────────────────────────
        public static void ApplyBloatwareTweak(string tweakKey)
        {
            var patternMap = new Dictionary<string, string[]>
            {
                ["Bloat_Bing"]      = new[] { "*BingNews*",    "*BingWeather*", "*BingSearch*" },
                ["Bloat_Zune"]      = new[] { "*ZuneVideo*",   "*ZuneMusic*" },
                ["Bloat_Solitaire"] = new[] { "*SolitaireCollection*" },
                ["Bloat_Maps"]      = new[] { "*WindowsMaps*" },
                ["Bloat_PhoneLink"] = new[] { "*YourPhone*",   "*PhoneLink*" },
                ["Bloat_Clipchamp"] = new[] { "*Clipchamp*" },
                ["Bloat_Xbox"]      = new[] { "*Xbox.TCUI*",   "*XboxApp*", "*XboxGameOverlay*", "*XboxGamingOverlay*", "*XboxSpeechToTextOverlay*" },
                ["Bloat_AdTiles"]   = new[] { "*LinkedIn*",    "*Disney*", "*Spotify*", "*TikTok*", "*Instagram*", "*Facebook*" },
                ["Bloat_Office"]    = new[] { "*OfficeHub*",   "*OneNote*" },
                ["Bloat_3D"]        = new[] { "*3DViewer*",    "*Print3D*" },
            };

            if (!patternMap.TryGetValue(tweakKey, out var patterns)) return;

            Dispatch("Bloatware", () =>
            {
                foreach (var pattern in patterns)
                {
                    string name = pattern.Replace("*", "").Trim();
                    RunPowerShell($"Get-AppxPackage {pattern} | Remove-AppxPackage -ErrorAction SilentlyContinue",
                        $"Remove (user) {name}");
                    RunPowerShell(
                        $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.PackageName -like '{pattern}' }}" +
                        $" | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue",
                        $"Remove (provisioned) {name}");
                }
            });
        }

        // ── ADVANCED TWEAKS ───────────────────────────────────────────────
        public static void ApplyAdvancedTweak(string advancedKey)
        {
            Dispatch("Advanced", () =>
            {
                switch (advancedKey)
                {
                    case "DisableDynamicTick":
                        RunCommand("bcdedit /set disabledynamictick yes", "Disable dynamic tick"); break;
                    case "DisableCpuThrottling":
                        SetRegistry(
                            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\" +
                            @"54533251-82be-4824-96c1-47b60b740d00\893dee8e-2bef-41e0-89c6-b55d0929964c",
                            "ValueMax", 0, RegistryValueKind.DWord, "Disable CPU throttling");
                        RunCommand("powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 0 & " +
                            "powercfg -setactive SCHEME_CURRENT", "Apply CPU throttle policy"); break;
                    case "EnableTrim":
                        RunCommand("fsutil behavior set disabledeletenotify 0", "Enable SSD TRIM"); break;
                    case "AggressiveAnimations":
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Desktop", "UserPreferencesMask",
                            new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 },
                            RegistryValueKind.Binary, "Disable all UI animations");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                            "TaskbarAnimations", 0, RegistryValueKind.DWord, "Disable taskbar animations");
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics",
                            "MinAnimate", "0", RegistryValueKind.String, "Disable minimize animations");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                            "ListviewShadow", 0, RegistryValueKind.DWord, "Disable listview shadows");
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Desktop",
                            "FontSmoothing", "2", RegistryValueKind.String, "Keep ClearType smoothing"); break;
                }
            });
        }

        // ── INDIVIDUAL TWEAK DISPATCH ─────────────────────────────────────
        private const string MmProfile     = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string DnsCacheParams = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";
        private const string GpuClassKey   = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000";
        // Processor power management subgroup (54533251-...) → Processor performance boost mode (be337238-...)
        private const string BoostModeSettingKey =
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7";

        private static readonly string[] TelemetryTasks =
        {
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Autochk\Proxy",
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
            @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
            // Diagnostics invocation and push notification telemetry — separate from DiagTrack service
            @"\Microsoft\Windows\Diagnosis\Scheduled",
            @"\Microsoft\Windows\WindowsUpdate\Automatic App Update",
            @"\Microsoft\Windows\Push Notifications\LockApplicationComponent",
        };

        private static readonly string[] NvidiaTasks =
        {
            @"\NvTmRepOnLogon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
            @"\NvTmRep_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
            @"\NvTmMon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
        };

        public static void ApplyTweak(string key)
        {
            Dispatch(CategoryForKey(key), () =>
            {
                switch (key)
                {
                    // ── PERFORMANCE ───────────────────────────────────────
                    case "Perf_PowerPlan":
                        RunCommand("powercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "High Performance power plan"); break;
                    case "Perf_PowerThrottle":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                            "PowerThrottlingOff", 1, RegistryValueKind.DWord, "Disable Power Throttling"); break;
                    case "Perf_SysMain":   DisableService("SysMain"); break;
                    case "Perf_WSearch":   DisableService("WSearch"); break;
                    case "Perf_StartupDelay":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
                            "StartupDelayInMSec", 0, RegistryValueKind.DWord, "Remove startup delay"); break;
                    case "Perf_VisualFX":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
                            "VisualFXSetting", 2, RegistryValueKind.DWord, "Visual effects: best performance"); break;
                    case "Perf_NtfsLastAccess":
                        RunCommand("fsutil behavior set disablelastaccess 1", "Disable NTFS last-access"); break;
                    case "Perf_8Dot3":
                        RunCommand("fsutil behavior set disable8dot3 1", "Disable 8.3 filenames"); break;
                    case "Perf_Hibernate":
                        RunCommand("powercfg -h off", "Disable hibernation"); break;
                    case "Perf_MemCompression":
                        RunPowerShell("Disable-MMAgent -MemoryCompression", "Disable memory compression");
                        // Page combining wastes CPU cycles combining identical pages — low value on 16GB+ systems
                        RunPowerShell("Disable-MMAgent -PageCombining", "Disable page combining"); break;
                    case "Perf_TimerRes":
                        try { TimeBeginPeriod(1); _results.Add(new TweakResult { Name = "Set timer resolution to 0.5ms", Success = true }); }
                        catch (Exception ex) { _results.Add(new TweakResult { Name = "Set timer resolution", Success = false, Error = ex.Message }); }
                        EnsureRegistryKey(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
                            "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord, "Persist high-res timer"); break;

                    // ── PRIVACY ───────────────────────────────────────────
                    case "Priv_Telemetry":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                            "AllowTelemetry", 0, RegistryValueKind.DWord, "Disable telemetry");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection",
                            "AllowTelemetry", 0, RegistryValueKind.DWord, "Disable telemetry (legacy)"); break;
                    case "Priv_DiagTrack":
                        foreach (var s in new[] { "DiagTrack", "dmwappushservice", "RetailDemo", "WerSvc" }) DisableService(s); break;
                    case "Priv_AdvertisingId":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                            "Enabled", 0, RegistryValueKind.DWord, "Disable Advertising ID");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo",
                            "DisabledByGroupPolicy", 1, RegistryValueKind.DWord, "Disable Advertising ID (policy)"); break;
                    case "Priv_BingStart":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer",
                            "DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord, "Disable Bing in Start");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search",
                            "BingSearchEnabled", 0, RegistryValueKind.DWord, "Disable Bing Search"); break;
                    case "Priv_Cortana":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search",
                            "CortanaConsent", 0, RegistryValueKind.DWord, "Disable Cortana consent"); break;
                    case "Priv_ActivityFeed":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed",    0, RegistryValueKind.DWord, "Disable Activity Feed");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, RegistryValueKind.DWord, "Disable publishing activities");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities",  0, RegistryValueKind.DWord, "Disable uploading activities"); break;
                    case "Priv_Location":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                            "DisableLocation", 1, RegistryValueKind.DWord, "Disable location tracking"); break;
                    case "Priv_Camera":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                            "LetAppsAccessCamera", 2, RegistryValueKind.DWord, "Block app camera access"); break;
                    case "Priv_WER":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                            "Disabled", 1, RegistryValueKind.DWord, "Disable Windows Error Reporting");
                        // Also kill the scheduled upload queue — WerSvc being disabled doesn't prevent this task
                        DisableTask(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting"); break;
                    case "Priv_SmartScreen":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                            "EnableSmartScreen", 0, RegistryValueKind.DWord, "Disable SmartScreen"); break;
                    case "Priv_TelemetryTasks":
                        foreach (var t in TelemetryTasks) DisableTask(t); break;
                    case "Priv_AppTracking":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                            "Start_TrackProgs", 0, RegistryValueKind.DWord, "Disable app launch tracking"); break;
                    case "Priv_Feedback":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules",
                            "NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord, "Disable feedback requests"); break;
                    case "Priv_ChatIcon":
                        EnsureRegistryKey(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                            "TaskbarMn", 0, RegistryValueKind.DWord, "Disable Chat/Teams icon"); break;
                    case "Priv_Recall":
                        EnsureRegistryKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                            "DisableAIDataAnalysis", 1, RegistryValueKind.DWord, "Disable Windows Recall (machine)");
                        EnsureRegistryKey(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI",
                            "DisableAIDataAnalysis", 1, RegistryValueKind.DWord, "Disable Windows Recall (user)"); break;
                    case "Priv_CloudContent":
                        // Disables Spotlight suggestions, lock screen ads, "fun facts", and app suggestions
                        EnsureRegistryKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                            "DisableWindowsConsumerFeatures", 1, RegistryValueKind.DWord, "Disable Windows consumer features");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                            "DisableCloudOptimizedContent", 1, RegistryValueKind.DWord, "Disable cloud-optimized content");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                            "DisableSoftLanding", 1, RegistryValueKind.DWord, "Disable soft landing tips");
                        // ContentDeliveryManager — controls lock screen spotlight, suggested apps, silent installs
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "ContentDeliveryAllowed", 0, RegistryValueKind.DWord, "Disable content delivery");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "OemPreInstalledAppsEnabled", 0, RegistryValueKind.DWord, "Disable OEM pre-installed apps");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "PreInstalledAppsEnabled", 0, RegistryValueKind.DWord, "Disable pre-installed apps");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "SilentInstalledAppsEnabled", 0, RegistryValueKind.DWord, "Disable silent app installs");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "SystemPaneSuggestionsEnabled", 0, RegistryValueKind.DWord, "Disable Start suggested apps");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "SubscribedContent-310093Enabled", 0, RegistryValueKind.DWord, "Disable Spotlight lock screen");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "SubscribedContent-338388Enabled", 0, RegistryValueKind.DWord, "Disable Start suggestions");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "SubscribedContent-338389Enabled", 0, RegistryValueKind.DWord, "Disable tips/tricks"); break;
                    case "Priv_Copilot":
                        EnsureRegistryKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot",
                            "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord, "Disable Windows Copilot (machine)");
                        EnsureRegistryKey(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot",
                            "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord, "Disable Windows Copilot (user)"); break;
                    case "Priv_HostsBlock":
                        ApplyHostsBlockList(); break;

                    // ── RESPONSIVENESS ────────────────────────────────────
                    case "Resp_MenuDelay":
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MenuShowDelay", "0", RegistryValueKind.String, "Instant menu show"); break;
                    case "Resp_AppKill":
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WaitToKillAppTimeout", "2000", RegistryValueKind.String, "Fast app kill timeout");
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Desktop", "HungAppTimeout",       "1000", RegistryValueKind.String, "Fast hung app timeout"); break;
                    case "Resp_ServiceKill":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control",
                            "WaitToKillServiceTimeout", "2000", RegistryValueKind.String, "Fast service kill timeout"); break;
                    case "Resp_AutoEndTasks":
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks", "1", RegistryValueKind.String, "Auto end tasks on shutdown"); break;
                    case "Resp_PlatformTick":
                        RunCommand("bcdedit /set useplatformtick yes",   "Platform tick");
                        RunCommand("bcdedit /set useplatformclock no",   "Disable platform clock (HPET)"); break;
                    case "Resp_VerboseStatus":
                        EnsureRegistryKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                            "verbosestatus", 1, RegistryValueKind.DWord, "Verbose boot/shutdown status messages"); break;
                    case "Resp_WinTips":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "SoftLandingEnabled", 0, RegistryValueKind.DWord, "Disable Windows Tips"); break;
                    // ── GAMING ────────────────────────────────────────────
                    case "Game_HAGS":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                            "HwSchMode", 2, RegistryValueKind.DWord, "Enable HAGS"); break;
                    case "Game_GameMode":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AllowAutoGameMode",   1, RegistryValueKind.DWord, "Enable Game Mode");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord, "Enable Auto Game Mode"); break;
                    case "Game_MouseAccel":
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseSpeed",      "0", RegistryValueKind.String, "Disable mouse acceleration");
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String, "Mouse threshold 1");
                        SetRegistry(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String, "Mouse threshold 2"); break;
                    case "Game_CPUPriority":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl",
                            "Win32PrioritySeparation", 38, RegistryValueKind.DWord, "CPU foreground priority boost"); break;
                    case "Game_DVR":
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR",
                            "AppCaptureEnabled", 0, RegistryValueKind.DWord, "Disable Game DVR capture");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\GameDVR",
                            "AllowGameDVR", 0, RegistryValueKind.DWord, "Disable Game DVR (policy)"); break;
                    case "Game_FSO":
                        SetRegistry(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_FSEBehaviorMode",          2, RegistryValueKind.DWord, "Disable FSO globally");
                        SetRegistry(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, RegistryValueKind.DWord, "Honor FSO setting"); break;
                    case "Game_GPUPower":
                        EnsureRegistryKey(GpuClassKey);
                        SetRegistry(GpuClassKey, "PerfLevelSrc", 0x3322, RegistryValueKind.DWord, "GPU: Prefer Maximum Performance");
                        RunCommand("powercfg -setacvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOIDLE 0 & powercfg -setactive SCHEME_CURRENT",
                            "GPU power: prevent idle"); break;
                    case "Game_NvidiaTelemetry":
                        foreach (var s in new[] { "NvTelemetryContainer", "NvDisplayContainerLS" }) DisableService(s);
                        foreach (var t in NvidiaTasks) DisableTask(t); break;

                    // ── NETWORK ───────────────────────────────────────────
                    case "Net_Nagle":       DisableNaglesAlgorithm(); break;
                    case "Net_RSS":         RunCommand("netsh int tcp set global rss=enabled",            "Enable RSS"); break;
                    case "Net_TCPAutoTune": RunCommand("netsh int tcp set global autotuninglevel=normal", "TCP auto-tuning"); break;
                    case "Net_Throttle":
                        SetRegistry(MmProfile, "NetworkThrottlingIndex", unchecked((int)0xffffffff),
                            RegistryValueKind.DWord, "Disable network throttling"); break;
                    case "Net_MMResponsive":
                        SetRegistry(MmProfile, "SystemResponsiveness", 0,
                            RegistryValueKind.DWord, "Max multimedia responsiveness");
                        // MMCSS Games task — extra scheduling headroom for game threads
                        EnsureRegistryKey(MmProfile + @"\Tasks\Games");
                        SetRegistry(MmProfile + @"\Tasks\Games", "Affinity",           0, RegistryValueKind.DWord, "MMCSS Games: Affinity");
                        SetRegistry(MmProfile + @"\Tasks\Games", "Background Only",    "False", RegistryValueKind.String, "MMCSS Games: not background-only");
                        SetRegistry(MmProfile + @"\Tasks\Games", "Clock Rate",         10000, RegistryValueKind.DWord, "MMCSS Games: Clock Rate");
                        SetRegistry(MmProfile + @"\Tasks\Games", "GPU Priority",       8, RegistryValueKind.DWord, "MMCSS Games: GPU Priority");
                        SetRegistry(MmProfile + @"\Tasks\Games", "Priority",           6, RegistryValueKind.DWord, "MMCSS Games: Priority");
                        SetRegistry(MmProfile + @"\Tasks\Games", "Scheduling Category","High", RegistryValueKind.String, "MMCSS Games: Scheduling Category");
                        SetRegistry(MmProfile + @"\Tasks\Games", "SFIO Rate",          "High", RegistryValueKind.String, "MMCSS Games: SFIO Rate");
                        // MMCSS Pro Audio task — reduces DPC latency for audio stack (Discord, game audio)
                        EnsureRegistryKey(MmProfile + @"\Tasks\Pro Audio");
                        SetRegistry(MmProfile + @"\Tasks\Pro Audio", "Affinity",           0,    RegistryValueKind.DWord,  "MMCSS Pro Audio: Affinity");
                        SetRegistry(MmProfile + @"\Tasks\Pro Audio", "Background Only",    "False", RegistryValueKind.String, "MMCSS Pro Audio: not background-only");
                        SetRegistry(MmProfile + @"\Tasks\Pro Audio", "Clock Rate",         10000,RegistryValueKind.DWord,  "MMCSS Pro Audio: Clock Rate");
                        SetRegistry(MmProfile + @"\Tasks\Pro Audio", "GPU Priority",       8,    RegistryValueKind.DWord,  "MMCSS Pro Audio: GPU Priority");
                        SetRegistry(MmProfile + @"\Tasks\Pro Audio", "Priority",           6,    RegistryValueKind.DWord,  "MMCSS Pro Audio: Priority");
                        SetRegistry(MmProfile + @"\Tasks\Pro Audio", "Scheduling Category","High", RegistryValueKind.String, "MMCSS Pro Audio: Scheduling Category");
                        SetRegistry(MmProfile + @"\Tasks\Pro Audio", "SFIO Rate",          "High", RegistryValueKind.String, "MMCSS Pro Audio: SFIO Rate"); break;
                    case "Net_LargeOffload":
                        // Disable Large Send Offload v1 + v2 — reduces jitter caused by some NIC drivers
                        RunPowerShell(
                            "Get-NetAdapter -Physical | ForEach-Object { " +
                            "  try { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload V2 (IPv4)' -DisplayValue 'Disabled' -ErrorAction SilentlyContinue } catch {}; " +
                            "  try { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload V2 (IPv6)' -DisplayValue 'Disabled' -ErrorAction SilentlyContinue } catch {}; " +
                            "  try { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload Version 2 (IPv4)' -DisplayValue 'Disabled' -ErrorAction SilentlyContinue } catch {}; " +
                            "  try { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload Version 2 (IPv6)' -DisplayValue 'Disabled' -ErrorAction SilentlyContinue } catch {} " +
                            "}",
                            "Disable Large Send Offload (LSO) v2 IPv4/IPv6"); break;
                    case "Net_TcpTimedWait":
                        // Reduces TIME_WAIT connection hold from 240s default to 30s
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                            "TcpTimedWaitDelay", 30, RegistryValueKind.DWord, "TCP TIME_WAIT delay → 30s"); break;
                    case "Net_DoH":
                    {
                        const string doh11  = DnsCacheParams + @"\DohWellKnownServers\1.1.1.1";
                        const string doh10  = DnsCacheParams + @"\DohWellKnownServers\1.0.0.1";
                        const string dohUrl = "https://cloudflare-dns.com/dns-query";
                        EnsureRegistryKey(DnsCacheParams);
                        SetRegistry(DnsCacheParams, "EnableAutoDoh", 2, RegistryValueKind.DWord, "Enable DoH");
                        EnsureRegistryKey(doh11);
                        SetRegistry(doh11, "DohFlags",    3,      RegistryValueKind.DWord,  "Register 1.1.1.1");
                        SetRegistry(doh11, "DohTemplate", dohUrl, RegistryValueKind.String, "Cloudflare DoH template");
                        EnsureRegistryKey(doh10);
                        SetRegistry(doh10, "DohFlags",    3,      RegistryValueKind.DWord,  "Register 1.0.0.1");
                        SetRegistry(doh10, "DohTemplate", dohUrl, RegistryValueKind.String, "Cloudflare DoH template (secondary)");
                        break;
                    }

                    // ── SECURITY ──────────────────────────────────────────
                    case "Sec_AutoRun":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\IniFileMapping\Autorun.inf",
                            "(Default)", "@SYS:DoesNotExist", RegistryValueKind.String, "Block Autorun.inf");
                        SetRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                            "NoDriveTypeAutoRun", 0xFF, RegistryValueKind.DWord, "Disable AutoRun (user)");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                            "NoDriveTypeAutoRun", 0xFF, RegistryValueKind.DWord, "Disable AutoRun (machine)"); break;
                    case "Sec_RDP":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server",
                            "fDenyTSConnections", 1, RegistryValueKind.DWord, "Disable RDP");
                        RunCommand("netsh advfirewall firewall set rule group=\"Remote Desktop\" new enable=no 2>nul",
                            "Block RDP firewall rule"); break;
                    case "Sec_SMBv1":
                        RunPowerShell("Set-SmbServerConfiguration -EnableSMB1Protocol $false -Force", "Disable SMBv1 server");
                        RunPowerShell("Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart", "Remove SMBv1 feature"); break;
                    case "Sec_NetBIOS":
                        RunPowerShell(
                            "Get-WmiObject Win32_NetworkAdapterConfiguration | Where-Object { $_.TcpipNetbiosOptions -ne $null } | ForEach-Object { $_.SetTcpipNetbios(2) }",
                            "Disable NetBIOS over TCP/IP"); break;
                    case "Sec_Defender":
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender",
                            "DisableAntiSpyware", 0, RegistryValueKind.DWord, "Ensure Defender not disabled");
                        SetRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection",
                            "DisableRealtimeMonitoring", 0, RegistryValueKind.DWord, "Ensure Defender real-time ON");
                        RunPowerShell("Set-MpPreference -DisableRealtimeMonitoring $false", "Enable Defender real-time"); break;

                    case "Adv_CoreParking":
                        // Disables CPU core parking — prevents Windows from idling cores mid-workload
                        // Done via the power plan's CPMINCORES subgroup (0=park freely, 100=never park)
                        RunCommand(
                            "powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 100 & " +
                            "powercfg -setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 100 & " +
                            "powercfg -setactive SCHEME_CURRENT",
                            "Disable CPU core parking"); break;
                    case "Adv_MsiMode":
                        // Enables Message Signaled Interrupts for GPU — reduces DPC latency vs legacy line-based interrupts
                        // Targets the display adapter class key (0000 = primary GPU slot)
                        EnsureRegistryKey(GpuClassKey + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties");
                        SetRegistry(GpuClassKey + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
                            "MSISupported", 1, RegistryValueKind.DWord, "Enable MSI mode for GPU");
                        // Also set MessageNumberLimit to 0x10 (16 messages) for maximum MSI-X utilisation
                        SetRegistry(GpuClassKey + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
                            "MessageNumberLimit", 0x10, RegistryValueKind.DWord, "GPU MSI message limit"); break;
                    case "Adv_IrqAffinity":
                        // For MSI-X capable devices: spread interrupts across all P-cores for better throughput
                        // GPU class — DevicePolicy 4 = IrqPolicySpreadMessagesAcrossAllProcessors
                        EnsureRegistryKey(GpuClassKey + @"\Device Parameters\Interrupt Management\Affinity Policy");
                        SetRegistry(GpuClassKey + @"\Device Parameters\Interrupt Management\Affinity Policy",
                            "DevicePolicy", 4, RegistryValueKind.DWord, "GPU IRQ: spread across all processors"); break;
                    case "Adv_TscSync":
                        RunCommand("bcdedit /set tscsyncpolicy legacy", "TSC sync policy → legacy"); break;
                    case "Adv_X2Apic":
                        RunCommand("bcdedit /set x2apicpolicy enable", "Enable x2APIC mode"); break;
                    case "Adv_BoostMode":
                        // Only unhides the setting (Attributes=2) — does not set a boost value.
                        // Goes through SetRegistry so it's backed up and undoable like any other registry tweak.
                        SetRegistry(BoostModeSettingKey, "Attributes", 2, RegistryValueKind.DWord,
                            "Unhide Processor Performance Boost Mode"); break;

                    default:
                        _results.Add(new TweakResult { Name = $"Unknown key: {key}", Success = false, Error = "No handler" });
                        break;
                }
            });
        }
    }

    // ── LIVE SYSTEM STATE DETECTOR ────────────────────────────────────────────
    // Checks whether each tweak is already applied on the current system,
    // independent of whether win11op has ever been run before.
    // Returns: true  = already applied
    //          false = not applied (Windows default)
    //          null  = cannot determine (no readable state)

    public static class TweakDetector
    {
        // Read a registry DWORD; returns null if key/value absent or unreadable
        private static int? RegDWord(string keyPath, string valueName)
        {
            try
            {
                object val = Registry.GetValue(keyPath, valueName, null);
                if (val is int i) return i;
                if (val != null && int.TryParse(val.ToString(), out int parsed)) return parsed;
                return null;
            }
            catch { return null; }
        }

        // Read a registry String; returns null if absent
        private static string RegString(string keyPath, string valueName)
        {
            try { return Registry.GetValue(keyPath, valueName, null)?.ToString(); }
            catch { return null; }
        }

        // Check Windows service start type via SC query
        // Returns true if service is disabled
        private static bool? ServiceDisabled(string serviceName)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", $"qc {serviceName}")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (output.Contains("DISABLED")) return true;
                if (output.Contains("AUTO_START") || output.Contains("DEMAND_START") ||
                    output.Contains("BOOT_START")  || output.Contains("SYSTEM_START"))
                    return false;
                return null;
            }
            catch { return null; }
        }

        // Check Nagle — look at first adapter subkey for TcpAckFrequency
        private static bool? NagleDisabled()
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
                if (baseKey == null) return null;
                foreach (string sub in baseKey.GetSubKeyNames())
                {
                    using var subKey = baseKey.OpenSubKey(sub);
                    var val = subKey?.GetValue("TcpAckFrequency");
                    if (val != null) return (int)val == 1;
                }
                return false; // no adapters with the key = not applied
            }
            catch { return null; }
        }

        // Check hosts file for our block list marker
        private static bool HostsBlockApplied()
        {
            try
            {
                string hostsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"drivers\etc\hosts");
                return File.Exists(hostsPath) &&
                       File.ReadAllText(hostsPath).Contains("# WIN11OPTIMIZER_TELEMETRY_BLOCK_START");
            }
            catch { return false; }
        }

        public static bool? Check(string tweakKey)
        {
            try
            {
                return tweakKey switch
                {
                    // ── PERFORMANCE ───────────────────────────────────────────
                    "Perf_PowerPlan" =>
                        // Active power plan GUID readable from registry
                        RegString(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes",
                            "ActivePowerScheme")
                            ?.Equals("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase),

                    "Perf_PowerThrottle" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                            "PowerThrottlingOff") == 1,

                    "Perf_SysMain"  => ServiceDisabled("SysMain"),
                    "Perf_WSearch"  => ServiceDisabled("WSearch"),

                    "Perf_StartupDelay" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
                            "StartupDelayInMSec") == 0,

                    "Perf_VisualFX" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
                            "VisualFXSetting") == 2,

                    // fsutil disablelastaccess stores its state in registry
                    "Perf_NtfsLastAccess" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem",
                            "NtfsDisableLastAccessUpdate") is int v && (v & 1) == 1 ? true : false,

                    // fsutil disable8dot3 → NtfsDisable8dot3NameCreation
                    "Perf_8Dot3" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem",
                            "NtfsDisable8dot3NameCreation") == 1,

                    // Hibernation: HibernateEnabled = 0 means off
                    "Perf_Hibernate" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power",
                            "HibernateEnabled") == 0,

                    "Perf_MemCompression" => null, // No reliable registry flag; MMAgent state not persisted

                    "Perf_TimerRes" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
                            "GlobalTimerResolutionRequests") == 1,

                    // ── PRIVACY ───────────────────────────────────────────────
                    "Priv_Telemetry" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                            "AllowTelemetry") == 0,

                    "Priv_DiagTrack" => ServiceDisabled("DiagTrack"),

                    "Priv_AdvertisingId" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                            "Enabled") == 0,

                    "Priv_BingStart" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer",
                            "DisableSearchBoxSuggestions") == 1,

                    "Priv_Cortana" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search",
                            "CortanaConsent") == 0,

                    "Priv_ActivityFeed" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                            "EnableActivityFeed") == 0,

                    "Priv_Location" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                            "DisableLocation") == 1,

                    "Priv_Camera" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                            "LetAppsAccessCamera") == 2,

                    "Priv_WER" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                            "Disabled") == 1,

                    "Priv_SmartScreen" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                            "EnableSmartScreen") == 0,

                    "Priv_TelemetryTasks" => null, // Scheduled task state not cleanly readable via registry

                    "Priv_AppTracking" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                            "Start_TrackProgs") == 0,

                    "Priv_Feedback" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules",
                            "NumberOfSIUFInPeriod") == 0,

                    "Priv_ChatIcon" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                            "TaskbarMn") == 0,

                    "Priv_Recall" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                            "DisableAIDataAnalysis") == 1,

                    "Priv_Copilot" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot",
                            "TurnOffWindowsCopilot") == 1,

                    "Priv_HostsBlock" => HostsBlockApplied(),

                    "Priv_CloudContent" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                            "DisableWindowsConsumerFeatures") == 1,

                    // ── RESPONSIVENESS ────────────────────────────────────────
                    "Resp_MenuDelay" =>
                        RegString(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MenuShowDelay") == "0",

                    "Resp_AppKill" =>
                        RegString(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WaitToKillAppTimeout") == "2000",

                    "Resp_ServiceKill" =>
                        RegString(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control",
                            "WaitToKillServiceTimeout") == "2000",

                    "Resp_AutoEndTasks" =>
                        RegString(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks") == "1",

                    "Resp_PlatformTick" => null, // BCD store not readable via managed registry API

                    "Resp_VerboseStatus" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                            "verbosestatus") == 1,

                    "Resp_WinTips" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                            "SoftLandingEnabled") == 0,

                    // ── GAMING ────────────────────────────────────────────────
                    "Game_HAGS" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                            "HwSchMode") == 2,

                    "Game_GameMode" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AllowAutoGameMode") == 1,

                    "Game_MouseAccel" =>
                        RegString(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseSpeed") == "0" &&
                        RegString(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold1") == "0" &&
                        RegString(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold2") == "0",

                    "Game_CPUPriority" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl",
                            "Win32PrioritySeparation") == 38,

                    "Game_DVR" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR",
                            "AppCaptureEnabled") == 0,

                    "Game_FSO" =>
                        RegDWord(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_FSEBehaviorMode") == 2,

                    "Game_GPUPower" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000",
                            "PerfLevelSrc") == 0x3322,

                    "Game_NvidiaTelemetry" => ServiceDisabled("NvTelemetryContainer"),

                    // ── NETWORK ───────────────────────────────────────────────
                    "Net_Nagle"       => NagleDisabled(),
                    "Net_RSS"         => null, // netsh global state not in registry
                    "Net_TCPAutoTune" => null, // netsh global state not in registry

                    "Net_Throttle" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                            "NetworkThrottlingIndex") == unchecked((int)0xffffffff),

                    "Net_MMResponsive" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                            "SystemResponsiveness") == 0,

                    "Net_DoH" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters",
                            "EnableAutoDoh") == 2,

                    // ── BLOATWARE ─────────────────────────────────────────────
                    // AppX removal state is not reliable to check via registry — skip
                    "Bloat_Bing" or "Bloat_Zune" or "Bloat_Solitaire" or "Bloat_Maps" or
                    "Bloat_PhoneLink" or "Bloat_Clipchamp" or "Bloat_Xbox" or
                    "Bloat_AdTiles" or "Bloat_Office" or "Bloat_3D" => null,

                    // ── SECURITY ──────────────────────────────────────────────
                    "Sec_AutoRun" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                            "NoDriveTypeAutoRun") == 0xFF,

                    "Sec_RDP" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server",
                            "fDenyTSConnections") == 1,

                    "Sec_SMBv1"   => null, // Windows Optional Feature state — not trivially readable
                    "Sec_NetBIOS" => null, // WMI adapter config — not suitable for startup scan
                    "Sec_Defender" =>
                        RegDWord(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender",
                            "DisableAntiSpyware") == 0,

                    // ── ADVANCED ──────────────────────────────────────────────
                    "Adv_DynamicTick"  => null, // BCD store
                    "Adv_CPUThrottle"  => null, // powercfg scheme — no clean registry check
                    "Adv_TRIM"         => null, // fsutil disabledeletenotify — no clean registry equivalent

                    "Adv_Animations" =>
                        RegDWord(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                            "TaskbarAnimations") == 0,

                    _ => null
                };
            }
            catch { return null; }
        }
    }
}