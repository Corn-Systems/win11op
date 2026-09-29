using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Win11Optimizer
{
    public static class TweakEngine
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]   private static extern uint TimeEndPeriod(uint uPeriod);

        // ── RESULTS ───────────────────────────────────────────────────────
        public class TweakResult
        {
            public string Name    { get; set; } = "";
            public bool   Success { get; set; }
            public string Error   { get; set; }
            public string Line    => (Success ? "✔ " : "✘ ") + Name;
        }

        private static readonly List<TweakResult> _results = new();
        public static void ClearResults() => _results.Clear();
        private static void Report(string name, bool ok = true, string error = null) =>
            _results.Add(new TweakResult { Name = name, Success = ok, Error = error });

        // ── REGISTRY PATHS (shared with TweakDetector so apply and detect can't drift) ──
        private  const string LM = "HKEY_LOCAL_MACHINE", CU = "HKEY_CURRENT_USER";
        internal const string Ctl              = LM + @"\SYSTEM\CurrentControlSet\Control";
        internal const string Svc              = LM + @"\SYSTEM\CurrentControlSet\Services";
        internal const string LmPol            = LM + @"\SOFTWARE\Policies\Microsoft";
        internal const string LmCv             = LM + @"\SOFTWARE\Microsoft\Windows\CurrentVersion";
        internal const string CuPol            = CU + @"\Software\Policies\Microsoft\Windows";
        internal const string CuSw             = CU + @"\Software\Microsoft";
        internal const string CuCv             = CuSw + @"\Windows\CurrentVersion";
        internal const string CuPanel          = CU + @"\Control Panel";
        internal const string Desktop          = CuPanel + @"\Desktop";
        internal const string ExplorerAdvanced = CuCv + @"\Explorer\Advanced";
        internal const string ContentDelivery  = CuCv + @"\ContentDeliveryManager";
        internal const string MmProfile        = LM + @"\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        internal const string DnsParams        = Svc + @"\Dnscache\Parameters";
        private  const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        internal const string GpuClassKey      = Ctl + @"\Class\" + DisplayClassGuid + @"\0000";
        internal const string BoostModeKey     = Ctl + @"\Power\PowerSettings\" + SubProcessor + @"\" + PerfBoostMode;
        private  const string ClassicMenuClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        internal const string ClassicMenuKey   = CU + @"\" + ClassicMenuClsid + @"\InprocServer32";
        internal const string NaglePath        = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        internal const string MsiSub           = @"\MessageSignaledInterruptProperties", AffinitySub = @"\Affinity Policy";
        internal const string AppPrivacy       = LmPol + @"\Windows\AppPrivacy";
        internal const string MaintenanceKey   = LM + @"\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
        internal const string GpuPrefKey       = CuSw + @"\DirectX\UserGpuPreferences";

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

        private const string PowerCfgKind = "PowerCfg";
        private static readonly List<BackupEntry> _backups = new();
        private static readonly HashSet<string>   _appliedCategories = new(StringComparer.OrdinalIgnoreCase);
        public static bool HasBackup(string category) => _appliedCategories.Contains(category);

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static (RegistryKey root, string sub) SplitPath(string keyPath)
        {
            var parts = keyPath.Split('\\', 2);
            return (parts[0] switch
            {
                "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
                "HKEY_CURRENT_USER"  => Registry.CurrentUser,
                "HKEY_CLASSES_ROOT"  => Registry.ClassesRoot,
                "HKEY_USERS"         => Registry.Users,
                _                    => null
            }, parts[1]);
        }

        // Only the FIRST backup of a value is kept — re-applying a tweak must not
        // overwrite the original Windows value with our own tweaked value.
        private static bool AlreadyBackedUp(string category, string keyPath, string valueName) =>
            _backups.Exists(b => Same(b.Category, category) && Same(b.KeyPath, keyPath) && Same(b.ValueName, valueName));

        private static void BackupRegistry(string category, string keyPath, string valueName)
        {
            if (AlreadyBackedUp(category, keyPath, valueName)) return;
            // Unreadable or absent → recorded as "didn't exist", so Undo deletes the value
            var entry = new BackupEntry
            {
                Category = category, KeyPath = keyPath, ValueName = valueName,
                ValueData = "", ValueKind = nameof(RegistryValueKind.Unknown)
            };
            try
            {
                var (root, sub) = SplitPath(keyPath);
                using var key = root?.OpenSubKey(sub);
                // DoNotExpand keeps REG_EXPAND_SZ values as written (e.g. %SystemRoot%)
                object cur = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (cur != null)
                {
                    string kind = key.GetValueKind(valueName).ToString();
                    string data = cur switch { byte[] b => Convert.ToBase64String(b), string[] l => string.Join("\n", l), _ => cur.ToString() };
                    (entry.Existed, entry.ValueKind, entry.ValueData) = (true, kind, data);
                }
            }
            catch (Exception ex) { SessionLog.Write("BACKUP READ", ex); }
            _backups.Add(entry);
        }

        private static void SaveBackups() => AppPaths.SaveJson(AppPaths.TweaksBackupFile, _backups, "BACKUP");

        public static void LoadBackups()
        {
            var loaded = AppPaths.LoadJson<List<BackupEntry>>(AppPaths.TweaksBackupFile, "BACKUP");
            if (loaded == null) return;
            _backups.Clear();
            _backups.AddRange(loaded);
            _appliedCategories.UnionWith(loaded.Select(b => b.Category));
        }

        private static List<TweakResult> RestoreCategory(string category)
        {
            var res = new List<TweakResult>();
            bool powerTouched = false;
            foreach (var b in _backups.Where(b => Same(b.Category, category)).ToList())
            {
                try
                {
                    if (b.ValueKind == PowerCfgKind)
                    {
                        // KeyPath = POWERCFG\<scheme>\<subgroup>\<setting>, ValueName = AC | DC
                        var p = b.KeyPath.Split('\\');
                        int code = Exec($"powercfg /{(b.ValueName == "AC" ? "setacvalueindex" : "setdcvalueindex")} {p[1]} {p[2]} {p[3]} {b.ValueData}");
                        powerTouched = true;
                        res.Add(new TweakResult { Name = $"Restored power setting {p[3]} ({b.ValueName})", Success = code == 0 });
                    }
                    else if (b.ValueKind == NicPowerKind)
                    {
                        string adapter = b.KeyPath[(b.KeyPath.IndexOf('\\') + 1)..].Replace("'", "''");
                        int code = Proc.PowerShell($"Set-NetAdapterPowerManagement -Name '{adapter}' -{b.ValueName} {b.ValueData} -ErrorAction Stop").Code;
                        res.Add(new TweakResult { Name = $"Restored {b.ValueName} on {adapter}", Success = code == 0 });
                    }
                    else if (!b.Existed)
                    {
                        var (root, sub) = SplitPath(b.KeyPath);
                        using var k = root?.OpenSubKey(sub, writable: true);
                        k?.DeleteValue(b.ValueName, false);
                        res.Add(new TweakResult { Name = $"Removed {DisplayName(b.ValueName)}", Success = true });
                    }
                    else
                    {
                        var kind = Enum.Parse<RegistryValueKind>(b.ValueKind);
                        Registry.SetValue(b.KeyPath, b.ValueName, kind switch
                        {
                            RegistryValueKind.DWord       => int.Parse(b.ValueData),
                            RegistryValueKind.QWord       => long.Parse(b.ValueData),
                            RegistryValueKind.Binary      => Convert.FromBase64String(b.ValueData),
                            RegistryValueKind.MultiString => b.ValueData.Split('\n'),
                            _                             => (object)b.ValueData
                        }, kind);
                        res.Add(new TweakResult { Name = $"Restored {DisplayName(b.ValueName)}", Success = true });
                    }
                    _backups.Remove(b);
                }
                catch (Exception ex) { res.Add(new TweakResult { Name = $"Restore {b.ValueName}", Error = ex.Message }); }
            }

            if (powerTouched) { Exec("powercfg /setactive SCHEME_CURRENT"); InvalidatePower(); }
            if (!_backups.Exists(b => Same(b.Category, category))) _appliedCategories.Remove(category);
            SaveBackups();
            return res;
        }

        private static string DisplayName(string valueName) => string.IsNullOrEmpty(valueName) ? "(Default)" : valueName;

        // ── HELPERS ───────────────────────────────────────────────────────
        private static string _currentCategory = "";

        private static void SetRegistry(string keyPath, string valueName, object value, RegistryValueKind kind, string friendlyName)
        {
            if (_currentCategory.Length > 0) BackupRegistry(_currentCategory, keyPath, valueName);
            // Registry.SetValue creates missing keys itself
            try { Registry.SetValue(keyPath, valueName, value, kind); Report(friendlyName); }
            catch (Exception ex) { Report(friendlyName, false, ex.Message); }
        }

        private static void Dw(string key, string name, int value, string friendly)    => SetRegistry(key, name, value, RegistryValueKind.DWord, friendly);
        private static void Sz(string key, string name, string value, string friendly) => SetRegistry(key, name, value, RegistryValueKind.String, friendly);

        // Exit code of a cmd.exe command line, or -1 if it couldn't run
        private static int Exec(string command)
        {
            try { return Proc.Cmd(command).Code; }
            catch (Exception ex) { SessionLog.Write("EXEC", ex); return -1; }
        }

        private static void RunCommand(string command, string friendlyName)
        {
            try
            {
                int code = Proc.Cmd(command).Code;
                Report(friendlyName, code == 0, code == 0 ? null : $"exit code {code}");
            }
            catch (Exception ex) { Report(friendlyName, false, ex.Message); }
        }

        private static void RunPowerShell(string script, string friendlyName)
        {
            try
            {
                var (code, _, err) = Proc.PowerShell(script);
                err = err.Trim();
                Report(friendlyName, code == 0, code == 0 ? null : err.Length > 0 ? err.Split('\n')[0].Trim() : $"exit code {code}");
            }
            catch (Exception ex) { Report(friendlyName, false, ex.Message); }
        }

        // Services missing on this edition/OEM image are skipped (exit 0) instead of failing, and
        // net stop/start no longer decides the result — it returns 2 when the service is already
        // in the target state, which used to report perfectly good runs as failures.
        private static void SetService(string s, string startType, string netVerb, string label) =>
            RunCommand($"(sc query {s} >nul 2>&1 || exit /b 0) && sc config {s} start= {startType} >nul && (net {netVerb} {s} >nul 2>&1 & exit /b 0)",
                       $"{label}: {s}");
        private static void DisableService(string s) => SetService(s, "disabled", "stop", "Disable");
        // startType: auto | delayed-auto | demand — must match the Windows default for that service
        private static void EnableService(string s, string startType = "auto") => SetService(s, startType, "start", "Re-enable");

        // Tasks that don't exist on this build are skipped instead of reported as failures
        private static void SetTask(string t, bool enable) =>
            RunCommand($"(schtasks /Query /TN \"{t}\" >nul 2>&1 || exit /b 0) && schtasks /Change /TN \"{t}\" /{(enable ? "Enable" : "Disable")} >nul",
                       $"{(enable ? "Re-enable" : "Disable")} task: {t}");

        private static string CoreParking(int minCores) =>
            $"powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES {minCores} & " +
            $"powercfg -setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES {minCores} & " +
            "powercfg -setactive SCHEME_CURRENT";

        // Large Send Offload v2 (IPv4/IPv6) on every physical adapter; drivers name it either way
        private static string Lso(string value) =>
            "Get-NetAdapter -Physical | ForEach-Object { " +
            string.Join("; ", new[] { "V2 (IPv4)", "V2 (IPv6)", "Version 2 (IPv4)", "Version 2 (IPv6)" }.Select(n =>
                $"  try {{ Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload {n}' -DisplayValue '{value}' -ErrorAction SilentlyContinue }} catch {{}}")) +
            " }";

        // ── POWERCFG (per-setting, backed up like registry values) ─────────
        private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}");

        private static string ActiveSchemeGuid()
        {
            try
            {
                var m = GuidRx.Match(Proc.Cmd("powercfg /getactivescheme").Out ?? "");
                return m.Success ? m.Value : null;
            }
            catch (Exception ex) { SessionLog.Write("POWERCFG", ex); return null; }
        }

        // ── POWER SETTINGS SNAPSHOT ───────────────────────────────────────
        // One `powercfg /qh` dump of the active scheme, parsed once and reused instead of one process
        // per setting (the detection scan reads ~30). Parsed by structure (indentation, GUIDs, hex
        // values) rather than by label, so it doesn't depend on Windows' display language.
        internal sealed class PowerInfo
        {
            public int? Ac, Dc;
            public readonly List<(int index, string name)> Options = new();
        }

        private static Dictionary<string, PowerInfo> _powerSnap;
        private static DateTime _powerSnapAt;
        private static readonly object _powerLock = new();
        private static readonly Regex HexRx = new(@":\s*0x([0-9a-fA-F]+)\s*$"), IndexRx = new(@":\s*(\d+)\s*$");

        private static string PowerKey(string sub, string setting) => (sub + "|" + setting).ToLowerInvariant();

        private static Dictionary<string, PowerInfo> ParsePowerDump(string dump)
        {
            var map = new Dictionary<string, PowerInfo>();
            string sub = null;
            PowerInfo cur = null;
            int? pendingIndex = null;
            var hex = new List<int>();

            // The last two hex values in a block are always Current AC / Current DC — range settings
            // list Minimum / Maximum / Increment (also hex) before them.
            void Flush()
            {
                if (cur != null && hex.Count >= 2) (cur.Ac, cur.Dc) = (hex[^2], hex[^1]);
                hex.Clear();
            }

            foreach (var raw in dump.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int indent = line.Length - line.TrimStart().Length;
                var g = GuidRx.Match(line);
                if (g.Success && indent == 2) { Flush(); cur = null; sub = g.Value; }
                else if (g.Success && indent == 4)
                {
                    Flush();
                    cur = new PowerInfo();
                    if (sub != null) map[PowerKey(sub, g.Value)] = cur;
                }
                else if (cur != null)
                {
                    Match m;
                    if ((m = HexRx.Match(line)).Success) hex.Add(Convert.ToInt32(m.Groups[1].Value, 16));
                    else if ((m = IndexRx.Match(line)).Success) pendingIndex = int.Parse(m.Groups[1].Value);
                    else if (pendingIndex != null && line.IndexOf(':') is var c and >= 0)
                    {
                        cur.Options.Add((pendingIndex.Value, line[(c + 1)..].Trim()));
                        pendingIndex = null;
                    }
                }
            }
            Flush();
            return map;
        }

        // Current AC/DC values (and option names) of one power setting; null when this device
        // doesn't expose it. Cached briefly — writes update the cache in place.
        internal static PowerInfo QueryPower(string subgroup, string setting)
        {
            lock (_powerLock)
            {
                if (_powerSnap == null || DateTime.UtcNow - _powerSnapAt > TimeSpan.FromSeconds(10))
                {
                    try
                    {
                        _powerSnap   = ParsePowerDump(Proc.Cmd("powercfg /qh SCHEME_CURRENT").Out ?? "");
                        _powerSnapAt = DateTime.UtcNow;
                    }
                    catch (Exception ex) { SessionLog.Write("POWERCFG", ex); return null; }
                }
                return _powerSnap.TryGetValue(PowerKey(subgroup, setting), out var info) ? info : null;
            }
        }

        internal static void InvalidatePower() { lock (_powerLock) _powerSnap = null; }

        // ── LAPTOP POWER-PLAN TWEAKS ──────────────────────────────────────
        // One battery-side (DC) value. Ok decides whether the current value already satisfies the
        // tweak (so nothing is written or backed up, and a stricter user setting is never loosened);
        // Fmt/Short render it for the tooltip. ByName picks the option whose driver-supplied name
        // contains the text (Intel/AMD slider indexes differ by vendor) instead of a fixed Dc.
        internal sealed record PowerPart(string Sub, string Setting, int Dc, string Label, string Short,
            Func<int, string> Fmt = null, Func<int, int, bool> Ok = null, string ByName = null)
        {
            public bool Satisfied(int cur, int target) => Ok != null ? Ok(cur, target) : cur == target;
            public string Show(PowerInfo info, int v) =>
                Fmt?.Invoke(v) ?? info?.Options.FirstOrDefault(o => o.index == v).name ?? v.ToString();
        }

        internal static int? PowerTarget(PowerPart p, PowerInfo info) =>
            p.ByName == null ? p.Dc
            : info.Options.Where(o => o.name.Contains(p.ByName, StringComparison.OrdinalIgnoreCase))
                          .Select(o => (int?)o.index).FirstOrDefault();

        private static string Pct(int v)  => v + "%";
        private static string Secs(int v) => v == 0 ? "never" : v < 60 ? v + " s" : $"{v / 60.0:0.#} min";
        private static Func<int, string> Opts(params string[] names) =>
            v => v >= 0 && v < names.Length ? names[v] : v.ToString();

        // "Already good enough" tests: (current, target) → satisfied?
        private static bool Within(int cur, int max) => cur > 0 && cur <= max;   // timeouts — 0 means never
        private static bool AtMost(int cur, int max) => cur <= max;
        private static bool AtLeast(int cur, int min) => cur >= min;
        private static bool NotNothing(int cur, int _) => cur != 0;               // lid: anything but "Do nothing"

        // Sets a power setting's battery (DC) value on the active scheme, recording the previous
        // value so Undo can put it back. Plugged-in (AC) behaviour is never touched.
        private enum PowerResult { Set, Already, Unsupported, Failed }

        private static PowerResult ApplyPowerPart(PowerPart p, string scheme)
        {
            var info = QueryPower(p.Sub, p.Setting);
            if (info?.Dc is not int cur || PowerTarget(p, info) is not int target) return PowerResult.Unsupported;

            if (p.Satisfied(cur, target)) { Report($"{p.Label} (already set)"); return PowerResult.Already; }

            string keyPath = $@"POWERCFG\{scheme}\{p.Sub}\{p.Setting}";
            if (_currentCategory.Length > 0 && !AlreadyBackedUp(_currentCategory, keyPath, "DC"))
                _backups.Add(new BackupEntry
                {
                    Category = _currentCategory, KeyPath = keyPath, ValueName = "DC",
                    ValueData = cur.ToString(), ValueKind = PowerCfgKind, Existed = true
                });
            int code = Exec($"powercfg /setdcvalueindex {scheme} {p.Sub} {p.Setting} {target} && powercfg /setactive SCHEME_CURRENT");
            Report(p.Label, code == 0, code == 0 ? null : $"exit code {code}");
            if (code != 0) return PowerResult.Failed;
            info.Dc = target;
            return PowerResult.Set;
        }

        // Power setting GUIDs (subgroup, setting) — verified with powercfg /qh on Windows 11
        private const string SubEnergySaver   = "de830923-a562-41af-a086-e3a2c6bad2da";
        private const string EsBattThreshold  = "e69653ca-cf7f-4f05-aa73-cb833fa90ad4";
        private const string EsBrightness     = "13d09884-f74e-474a-a852-b6bde8ad03a8";
        private const string SubProcessor     = "54533251-82be-4824-96c1-47b60b740d00";
        private const string PerfBoostMode    = "be337238-0d82-4146-a960-4f3749d470c7";
        private const string PerfEpp          = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
        private const string PerfEppClass1    = "36687f9e-e3a5-4dbf-b1dc-15eb381c6864";
        private const string SubVideo         = "7516b95f-f776-4464-8c53-06167f40cc99";
        private const string VideoIdle        = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
        private const string AdaptBright      = "fbd9aa66-9553-4097-ba44-ed6e9d65eab8";
        private const string SubSleep         = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
        private const string StandbyIdle      = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
        private const string RtcWake          = "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d";
        private const string SubPciExpress    = "501a4d13-42af-4429-9fd1-a8218c268e20";
        private const string Aspm             = "ee12f906-d277-404b-b6da-e5fa1a576df5";
        private const string SubWireless      = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
        private const string WifiPowerMode    = "12bbebe6-58d6-4636-95bb-3217ef867c1a";
        private const string SubUsb           = "2a737441-1930-4402-8d77-b2bebba308a3";
        private const string UsbSuspend       = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
        private const string SubNone          = "fea3413e-7e05-4911-9a71-700331f1c294";
        private const string StandbyNetwork   = "f15576e8-98b7-4186-b944-eafa664402d9";
        private const string SubMultimedia    = "9596fb26-9850-41fd-ac3e-f7c3c00afd4b";
        private const string VideoPlayback    = "34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4";
        private const string VideoQualityBias = "10778347-1370-4ee0-8bbd-33bdacaade49";
        private const string SubBattery       = "e73a048d-bf27-4f12-9731-8b2076e8891f";
        private const string BatActionCrit    = "637ea02f-bbcb-4015-8e2c-a1c7b9c0b546";
        private const string BatLevelLow      = "8183ba9a-e910-48da-8769-14ae6dc1170a";
        private const string BatLevelCrit     = "9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469";
        private const string HibernateIdle    = "9d7815a6-7ee4-497e-8888-515a05f02364";
        private const string UnattendSleep    = "7bc4a2f9-d8fc-4469-b07b-33eb785aaca0";
        private const string VideoDim         = "17aaa29b-8b43-4b94-aafe-35f64daaf1ee";
        private const string VideoConLock     = "8ec4b3a5-6868-48c2-be75-4f3044be88a7";
        private const string VideoNormalLevel = "aded5e82-b909-4619-9949-f5d71dac0bcb";
        private const string ProcThrottleMin  = "893dee8e-2bef-41e0-89c6-b55d0929964c";
        private const string SysCoolPol       = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";
        private const string SubButtons       = "4f971e89-eebd-4455-a8de-9e59040e7347";
        private const string LidAction        = "5ca83367-6e45-459f-a27b-476b1d01c936";
        private const string SubDisk          = "0012ee47-9041-4b5d-9b77-535fba8b1442";
        private const string DiskIdle         = "6738e2c4-e8a5-4a42-b16a-e040e769756e";
        private const string SubSlideshow     = "0d7dbae2-4294-402a-ba8e-26777e8488cd";
        private const string Slideshow        = "309dce9b-bef4-4119-9921-a851fb12f0f4";
        private const string MediaSharing     = "03680956-93bc-4294-bba6-4e0f09bb717f";
        private const string SubGraphics      = "5fb4938d-1ee8-4b0f-9a3c-5036b0ab995c";
        private const string GpuPrefPolicy    = "dd848b2a-8a5d-4451-9ae2-39cd41658f6c";
        private const string SubIntelGfx      = "44f3beca-a7c0-460e-9df2-bb8b99e0cba6";
        private const string IntelGfxPower    = "3619c3f2-afb2-4afc-b0e9-e7fef372de36";
        private const string SubAmdSlider     = "c763b4ec-0e50-4b6b-9bed-2b92a6ee884e";
        private const string AmdOverlay       = "7ec1751b-60ed-4588-afb5-9819d3d77d90";

        // Laptop power-plan tweaks, one entry per tweak: the first part is the headline setting,
        // the rest are companions that only exist on some hardware (a tweak fails only if NONE of
        // its parts are supported here). TweakDetector and the tooltip's "now" line read the same table.
        private static PowerPart P(string sub, string setting, int dc, string label, string shortName,
            Func<int, string> fmt = null, Func<int, int, bool> ok = null, string byName = null) =>
            new(sub, setting, dc, label, shortName, fmt, ok, byName);

        internal static readonly Dictionary<string, PowerPart[]> LaptopPower = new()
        {
            ["Lap_EnergySaver"] =
            [
                P(SubEnergySaver, EsBattThreshold, 30, "Energy Saver turns on at 30%", "Energy Saver at", Pct, AtLeast),
                P(SubEnergySaver, EsBrightness,    50, "Energy Saver dims screen to 50%", "Energy Saver brightness", Pct, AtMost),
            ],
            ["Lap_NoTurbo"] =
            [
                P(SubProcessor, PerfBoostMode, 0, "Disable CPU turbo boost on battery", "Boost mode",
                  Opts("Disabled", "Enabled", "Aggressive", "Efficient enabled", "Efficient aggressive", "Aggressive at guaranteed", "Efficient aggressive at guaranteed")),
            ],
            ["Lap_CpuEfficiency"] =
            [
                P(SubProcessor, PerfEpp,       80, "CPU energy preference → efficiency (battery)", "Energy preference", Pct, AtLeast),
                P(SubProcessor, PerfEppClass1, 80, "E-core energy preference → efficiency (battery)", "E-core preference", Pct, AtLeast),
            ],
            ["Lap_ScreenSleep"] =
            [
                P(SubVideo, VideoIdle,   180, "Screen off after 3 min on battery", "Screen off after", Secs, Within),
                P(SubSleep, StandbyIdle, 600, "Sleep after 10 min on battery",     "Sleep after",      Secs, Within),
            ],
            ["Lap_AdaptiveBright"] =
            [
                P(SubVideo, AdaptBright, 1, "Adaptive brightness on battery", "Adaptive brightness", Opts("Off", "On")),
            ],
            ["Lap_PcieAspm"] =
            [
                P(SubPciExpress, Aspm, 2, "PCIe link state: maximum power savings (battery)", "Link state power", Opts("Off", "Moderate", "Maximum savings")),
            ],
            ["Lap_WifiPowerSave"] =
            [
                P(SubWireless, WifiPowerMode, 3, "Wi-Fi: maximum power saving (battery)", "Wi-Fi power saving",
                  Opts("Maximum performance", "Low", "Medium", "Maximum power saving")),
            ],
            ["Lap_UsbSuspend"] =
            [
                P(SubUsb, UsbSuspend, 1, "USB selective suspend on battery", "USB suspend", Opts("Disabled", "Enabled")),
            ],
            ["Lap_WakeTimers"] =
            [
                P(SubSleep, RtcWake, 0, "Disable wake timers on battery", "Wake timers", Opts("Disabled", "Enabled", "Important only")),
            ],
            ["Lap_StandbyNetwork"] =
            [
                P(SubNone, StandbyNetwork, 0, "Disconnect network in Modern Standby (battery)", "Standby network", Opts("Disconnected", "Connected", "Managed by Windows")),
            ],
            ["Lap_VideoBattery"] =
            [
                P(SubMultimedia, VideoPlayback,    2, "Video playback: optimise for battery", "Video playback", Opts("Video quality", "Balanced", "Power savings")),
                P(SubMultimedia, VideoQualityBias, 0, "Video quality bias: power saving", "Quality bias", Opts("Power-saving", "Performance")),
            ],
            ["Lap_CritHibernate"] =
            [
                P(SubBattery, BatActionCrit, 2, "Critical battery action → Hibernate", "At critical battery", Opts("Do nothing", "Sleep", "Hibernate", "Shut down")),
            ],
            ["Lap_HibernateAfter"] =
            [
                P(SubSleep, HibernateIdle, 3600, "Hibernate after 60 min asleep on battery", "Hibernate after", Secs, Within),
            ],
            ["Lap_UnattendedSleep"] =
            [
                P(SubSleep, UnattendSleep, 60, "Sleep again 1 min after an unattended wake (battery)", "Unattended sleep after", Secs, Within),
            ],
            ["Lap_DimTimeout"] =
            [
                P(SubVideo, VideoDim, 60, "Dim display after 1 min on battery", "Dim after", Secs, Within),
            ],
            ["Lap_BatteryBrightness"] =
            [
                P(SubVideo, VideoNormalLevel, 40, "Display brightness capped at 40% on battery", "Brightness", Pct, AtMost),
            ],
            ["Lap_LockScreenOff"] =
            [
                P(SubVideo, VideoConLock, 30, "Lock-screen display off after 30 s on battery", "Lock screen off after", Secs, Within),
            ],
            ["Lap_MinProcState"] =
            [
                P(SubProcessor, ProcThrottleMin, 5, "Minimum processor state 5% on battery", "Min CPU state", Pct, AtMost),
                P(SubProcessor, SysCoolPol,      0, "Passive cooling on battery", "Cooling", Opts("Passive", "Active")),
            ],
            ["Lap_LidClose"] =
            [
                P(SubButtons, LidAction, 1, "Lid close → Sleep on battery", "Lid close", Opts("Do nothing", "Sleep", "Hibernate", "Shut down"), NotNothing),
            ],
            ["Lap_LowBattery"] =
            [
                P(SubBattery, BatLevelLow,  15, "Low battery warning at 15%", "Low battery at", Pct, AtLeast),
                P(SubBattery, BatLevelCrit, 7,  "Critical battery action at 7%", "Critical at", Pct, AtLeast),
            ],
            ["Lap_Slideshow"] =
            [
                P(SubSlideshow,  Slideshow,    1, "Desktop slide show paused on battery", "Slide show", Opts("Available", "Paused")),
                P(SubMultimedia, MediaSharing, 0, "Sleep allowed while sharing media (battery)", "Media sharing", Opts("Allow sleep", "Prevent sleep", "Away mode")),
            ],
            ["Lap_DiskIdle"] =
            [
                P(SubDisk, DiskIdle, 300, "Hard disk off after 5 min on battery", "Disk off after", Secs, Within),
            ],
            ["Lap_GraphicsSlider"] =
            [
                P(SubIntelGfx,   IntelGfxPower, 0, "Intel graphics power → maximum battery life", "Intel graphics", byName: "battery"),
                P(SubAmdSlider,  AmdOverlay,    0, "AMD power slider → battery saver", "AMD slider", byName: "battery"),
            ],
            // Companion: the powercfg side of "prefer integrated GPU" (the per-app side is in ApplyGpuPreferences)
            ["Lap_GpuPowerSaving"] =
            [
                P(SubGraphics, GpuPrefPolicy, 1, "GPU preference policy → low power (battery)", "GPU policy", Opts("None", "Low power")),
            ],
        };

        // Need hibernation on — refuse rather than silently re-enabling what Performance → Disable Hibernation turned off
        private static readonly HashSet<string> NeedsHibernation = new() { "Lap_CritHibernate", "Lap_HibernateAfter" };

        // True when the driver exposes an Intel or AMD power slider in the active plan
        internal static bool HasGraphicsSlider() =>
            QueryPower(SubIntelGfx, IntelGfxPower) != null || QueryPower(SubAmdSlider, AmdOverlay) != null;

        // ── PER-APP GPU PREFERENCE ────────────────────────────────────────
        // Settings → System → Display → Graphics: "GpuPreference=1;" = power saving (the integrated GPU).
        // Each app's original value is backed up like any registry value, so Undo removes what we added.
        private static void ApplyGpuPreferences()
        {
            var apps = Hardware.GpuPreferenceApps(includeStoreTeams: true);
            if (apps.Count == 0) { Report("Integrated-GPU preference: no browsers or Teams found to configure"); return; }
            foreach (var exe in apps)
                SetRegistry(GpuPrefKey, exe, "GpuPreference=1;", RegistryValueKind.String, "Prefer integrated GPU: " + Path.GetFileName(exe));
        }

        // ── NETWORK ADAPTER POWER MANAGEMENT ──────────────────────────────
        // Undo has no registry value to put back, so each changed property is backed up as its own
        // entry (KeyPath = NICPOWER\<adapter>, ValueName = property, ValueData = original) and
        // restored through Set-NetAdapterPowerManagement in RestoreCategory.
        private const string NicPowerKind = "NicPower";
        private static readonly (string prop, string want, string orig)[] NicProps =
        {
            ("WakeOnMagicPacket",       "Disabled", "Enabled"),
            ("WakeOnPattern",           "Disabled", "Enabled"),
            ("DeviceSleepOnDisconnect", "Enabled",  "Disabled"),
        };

        private static void ApplyNicPower()
        {
            const string name = "Network adapter power management";
            try
            {
                var (code, outp, _) = Proc.PowerShell(
                    "Get-NetAdapter -Physical | ForEach-Object { $p = Get-NetAdapterPowerManagement -Name $_.Name -ErrorAction SilentlyContinue; " +
                    "if ($p) { $_.Name + '|' + $p.WakeOnMagicPacket + '|' + $p.WakeOnPattern + '|' + $p.DeviceSleepOnDisconnect } }");
                var rows = (outp ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(l => l.Split('|')).Where(a => a.Length == 4 && !a[0].Contains('"')).ToList();
                if (code != 0 || rows.Count == 0) { Report(name, false, "No adapter driver exposes power-management settings"); return; }

                foreach (var row in rows)
                {
                    // Only touch a property that's currently in the opposite state — "Unsupported" stays untouched
                    var changes = NicProps.Where((p, i) => row[i + 1].Equals(p.orig, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (changes.Count == 0) { Report($"{row[0]}: already set (or not supported by the driver)"); continue; }

                    string keyPath = @"NICPOWER\" + row[0];
                    foreach (var c in changes.Where(c => _currentCategory.Length > 0 && !AlreadyBackedUp(_currentCategory, keyPath, c.prop)))
                        _backups.Add(new BackupEntry
                        {
                            Category = _currentCategory, KeyPath = keyPath, ValueName = c.prop,
                            ValueData = c.orig, ValueKind = NicPowerKind, Existed = true
                        });
                    RunPowerShell($"Set-NetAdapterPowerManagement -Name '{row[0].Replace("'", "''")}' " +
                                  string.Join(" ", changes.Select(c => $"-{c.prop} {c.want}")) + " -ErrorAction Stop",
                                  $"Power management: {row[0]}");
                }
            }
            catch (Exception ex) { Report(name, false, ex.Message); }
        }

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
            "telemetry.urs.microsoft.com",
            "settings-sandbox.data.microsoft.com", "survey.watson.microsoft.com",
            "watson.live.com",                     "watson.microsoft.com",
            "statsfe2.ws.microsoft.com",           "corpext.msitadfs.glbdns2.microsoft.com",
            "compatexchange.cloudapp.net",         "cs1.wpc.v0cdn.net",
            "a-0001.a-msedge.net",                 "statsfe2.update.microsoft.com.akadns.net",
            "sls.update.microsoft.com.akadns.net", "fe2.update.microsoft.com.akadns.net",
        };

        internal const string HostsMarkerStart = "# WIN11OPTIMIZER_TELEMETRY_BLOCK_START";
        private  const string HostsMarkerEnd   = "# WIN11OPTIMIZER_TELEMETRY_BLOCK_END";
        internal static string HostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        private static void ApplyHostsBlockList()
        {
            try
            {
                if (File.Exists(HostsPath) && File.ReadAllText(HostsPath).Contains(HostsMarkerStart))
                {
                    Report("Block telemetry hosts (already applied)");
                    return;
                }
                var sb = new StringBuilder().AppendLine().AppendLine(HostsMarkerStart);
                foreach (var host in TelemetryHosts) sb.AppendLine($"0.0.0.0 {host}");
                File.AppendAllText(HostsPath, sb.AppendLine(HostsMarkerEnd).ToString());
                Report($"Blocked {TelemetryHosts.Length} telemetry domains");
            }
            catch (Exception ex) { Report("Block telemetry hosts", false, ex.Message); }
        }

        private static void RemoveHostsBlockList()
        {
            try
            {
                if (!File.Exists(HostsPath)) return;
                string content = File.ReadAllText(HostsPath);
                int start = content.IndexOf(HostsMarkerStart, StringComparison.Ordinal);
                int end   = content.IndexOf(HostsMarkerEnd,   StringComparison.Ordinal);
                if (start < 0 || end < start) return;
                end += HostsMarkerEnd.Length;
                // Swallow the line break after the end marker (CRLF, LF, or none at EOF)
                // and the blank line we inserted before the start marker.
                if (end < content.Length && content[end] == '\r') end++;
                if (end < content.Length && content[end] == '\n') end++;
                if (start >= 2 && content.Substring(start - 2, 2) == "\r\n") start -= 2;
                else if (start >= 1 && content[start - 1] == '\n') start -= 1;
                File.WriteAllText(HostsPath, content.Remove(start, end - start));
                Report("Removed telemetry hosts block");
            }
            catch (Exception ex)
            {
                SessionLog.Write("HOSTS RESTORE", ex);
                Report("Remove telemetry hosts block", false, ex.Message);
            }
        }

        // ── NAGLE'S ALGORITHM ─────────────────────────────────────────────
        private static void ModifyNagle(bool disable)
        {
            string name = disable ? "Disable Nagle's Algorithm" : "Restore Nagle's Algorithm";
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(NaglePath, writable: true);
                if (root == null)
                {
                    if (disable) Report(name, false, "Base key not found");
                    return;
                }
                foreach (string sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    if (k == null) continue;
                    foreach (var v in new[] { "TcpAckFrequency", "TCPNoDelay" })
                        if (disable) k.SetValue(v, 1, RegistryValueKind.DWord); else k.DeleteValue(v, false);
                }
                if (disable) Report(name);
            }
            catch (Exception ex) { Report(name, false, ex.Message); }   // restore failures used to vanish silently
        }

        // ── SYSTEM RESTORE POINT ──────────────────────────────────────────
        public static bool CreateRestorePoint(string description)
        {
            try
            {
                var (code, _, err) = Proc.PowerShell(
                    $"Checkpoint-Computer -Description '{description.Replace("'", "")}' -RestorePointType MODIFY_SETTINGS");
                // Windows allows one restore point per 24h — "too soon" means a recent one already exists
                return code == 0 || err.Contains("0x80042306") || err.Contains("too soon") || err.Contains("frequency");
            }
            catch (Exception ex) { SessionLog.Write("RESTORE POINT", ex); return false; }
        }

        // ── APPLY ─────────────────────────────────────────────────────────
        private static void Dispatch(string category, Action action)
        {
            _currentCategory = category;
            try { action(); }
            catch (Exception ex)
            {
                Report($"{category} tweak", false, ex.Message);
                SessionLog.Write("TWEAK", ex);
            }
            finally { _currentCategory = ""; }
            // App removal has no Undo — don't offer one (it used to enable Undo for Bloatware in-session)
            if (category != "Bloatware") _appliedCategories.Add(category);
            SaveBackups();
        }

        // Single entry point used by both the GUI and the headless CLI so the Bloatware / Advanced /
        // regular routing can't drift between the two. Returns just the results this tweak produced.
        public static List<TweakResult> Apply(TweakEntry entry)
        {
            int start = _results.Count;
            if (entry.Category == "Bloatware")
            {
                if (BloatPatterns.TryGetValue(entry.TweakKey, out var patterns)) Dispatch("Bloatware", () => RemoveApps(patterns));
            }
            else if (entry.IsAdvanced && entry.AdvancedKey != null) Dispatch("Advanced", () => ApplyAdvanced(entry.AdvancedKey));
            else Dispatch(entry.Category, () => ApplyTweak(entry.TweakKey));
            return _results.GetRange(start, _results.Count - start);
        }

        // ── UNDO ──────────────────────────────────────────────────────────
        // Restores a category's registry/powercfg backups, then reverts the command-based tweaks
        // that have no registry value to put back. Returns every result from both.
        public static List<TweakResult> Undo(string category)
        {
            ClearResults();
            var r = RestoreCategory(category);
            switch (category)
            {
                case "Performance":
                    RunCommand("powercfg -setactive 381b4222-f694-41f0-9685-ff5bb260df2e", "Restore Balanced power plan");
                    EnableService("SysMain");
                    EnableService("WSearch", "delayed-auto");   // Windows default is Automatic (Delayed Start)
                    RunCommand("fsutil behavior set disablelastaccess 0", "Re-enable NTFS last-access");
                    RunCommand("fsutil behavior set disable8dot3 2", "Restore 8.3 filenames (per-volume default)");
                    RunCommand("powercfg -h on", "Re-enable hibernation");
                    RunPowerShell("Enable-MMAgent -MemoryCompression", "Re-enable memory compression");
                    RunPowerShell("Enable-MMAgent -PageCombining", "Re-enable page combining");
                    try { TimeEndPeriod(1); } catch (Exception ex) { SessionLog.Write("TIMER", ex); }
                    break;
                case "Privacy":
                    EnableService("DiagTrack");
                    // These three default to Manual (demand) — "auto" would leave them running permanently
                    foreach (var s in new[] { "dmwappushservice", "RetailDemo", "WerSvc" }) EnableService(s, "demand");
                    foreach (var t in TelemetryTasks.Append(WerQueueTask)) SetTask(t, true);
                    RemoveHostsBlockList();
                    break;
                case "Responsiveness":
                    RunCommand("bcdedit /deletevalue useplatformtick 2>nul",  "Restore platform tick default");
                    RunCommand("bcdedit /deletevalue useplatformclock 2>nul", "Restore platform clock default");
                    // The classic menu lives in its own CLSID key — remove the whole key to bring back the Win11 menu
                    try { Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuClsid, throwOnMissingSubKey: false); Report("Restore Windows 11 context menu"); }
                    catch (Exception ex) { Report("Restore Windows 11 context menu", false, ex.Message); }
                    break;
                case "Gaming":
                    foreach (var s in NvidiaServices) EnableService(s);
                    foreach (var t in NvidiaTasks) SetTask(t, true);
                    break;
                case "Network":
                    ModifyNagle(false);
                    RunCommand("netsh int tcp set global autotuninglevel=normal", "Restore TCP auto-tuning");
                    RunPowerShell(Lso("Enabled"), "Re-enable Large Send Offload (LSO)");
                    break;
                case "Advanced":
                    RunCommand("bcdedit /deletevalue disabledynamictick 2>nul", "Restore dynamic tick default");
                    RunCommand(CoreParking(0), "Restore CPU core parking");   // 0 = Windows-managed, park freely
                    RunCommand("bcdedit /deletevalue tscsyncpolicy 2>nul", "Restore TSC sync policy default");
                    RunCommand("bcdedit /deletevalue x2apicpolicy 2>nul",  "Restore x2APIC policy default");
                    break;
                case "Security":
                    // 0 = use the NetBIOS setting from the DHCP server (Windows default)
                    RunPowerShell(
                        "Get-CimInstance Win32_NetworkAdapterConfiguration | Where-Object { $_.TcpipNetbiosOptions -ne $null } | " +
                        "ForEach-Object { Invoke-CimMethod -InputObject $_ -MethodName SetTcpipNetbios -Arguments @{TcpipNetbiosOptions=0} | Out-Null }",
                        "Restore NetBIOS over TCP/IP default");
                    break;
                // Laptop: registry, powercfg and adapter-power backups cover every tweak
            }
            r.AddRange(_results);
            return r;
        }

        // ── BLOATWARE ─────────────────────────────────────────────────────
        private static readonly Dictionary<string, string[]> BloatPatterns = new()
        {
            ["Bloat_Bing"]      = new[] { "*BingNews*",  "*BingWeather*", "*BingSearch*" },
            ["Bloat_Zune"]      = new[] { "*ZuneVideo*", "*ZuneMusic*" },
            ["Bloat_Solitaire"] = new[] { "*SolitaireCollection*" },
            ["Bloat_Maps"]      = new[] { "*WindowsMaps*" },
            ["Bloat_PhoneLink"] = new[] { "*YourPhone*",  "*PhoneLink*" },
            ["Bloat_Clipchamp"] = new[] { "*Clipchamp*" },
            ["Bloat_Xbox"]      = new[] { "*Xbox.TCUI*",  "*XboxApp*", "*XboxGameOverlay*", "*XboxGamingOverlay*", "*XboxSpeechToTextOverlay*" },
            ["Bloat_AdTiles"]   = new[] { "*LinkedIn*",   "*Disney*", "*Spotify*", "*TikTok*", "*Instagram*", "*Facebook*" },
            ["Bloat_Office"]    = new[] { "*OfficeHub*",  "*OneNote*" },
            ["Bloat_3D"]        = new[] { "*3DViewer*",   "*Print3D*" },
        };

        private static void RemoveApps(string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                string name = pattern.Replace("*", "").Trim();
                RunPowerShell($"Get-AppxPackage {pattern} | Remove-AppxPackage -ErrorAction SilentlyContinue", $"Remove (user) {name}");
                RunPowerShell($"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.PackageName -like '{pattern}' }}" +
                              " | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue", $"Remove (provisioned) {name}");
            }
        }

        // ── ADVANCED (AdvancedKey) TWEAKS ─────────────────────────────────
        private static void ApplyAdvanced(string advancedKey)
        {
            switch (advancedKey)
            {
                case "ProcessorScheduling":
                    Dw(Ctl + @"\PriorityControl", "Win32PrioritySeparation", 38, "Processor scheduling: programs"); break;
                case "DisableDynamicTick":
                    RunCommand("bcdedit /set disabledynamictick yes", "Disable dynamic tick"); break;
                case "DisableCpuThrottling":
                    Dw(Ctl + @"\Power\PowerSettings\" + SubProcessor + @"\893dee8e-2bef-41e0-89c6-b55d0929964c", "ValueMax", 0, "Disable CPU throttling");
                    RunCommand("powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 0 & powercfg -setactive SCHEME_CURRENT",
                        "Apply CPU throttle policy"); break;
                case "EnableTrim":
                    RunCommand("fsutil behavior set disabledeletenotify 0", "Enable SSD TRIM"); break;
                case "AggressiveAnimations":
                    SetRegistry(Desktop, "UserPreferencesMask", new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 },
                        RegistryValueKind.Binary, "Disable all UI animations");
                    Dw(ExplorerAdvanced, "TaskbarAnimations", 0, "Disable taskbar animations");
                    Sz(Desktop + @"\WindowMetrics", "MinAnimate", "0", "Disable minimize animations");
                    Dw(ExplorerAdvanced, "ListviewShadow", 0, "Disable listview shadows");
                    Sz(Desktop, "FontSmoothing", "2", "Keep ClearType smoothing"); break;
            }
        }

        // Interrupt settings (MSI mode, affinity policy) are read from the PCI device instance key
        // under Enum — NOT the display class key, which Windows ignores for this. One key per
        // display adapter (iGPU + dGPU): "HKEY_LOCAL_MACHINE\...\Enum\PCI\<dev>\<inst>\Device Parameters\Interrupt Management".
        internal static List<string> GpuInterruptKeys()
        {
            var list = new List<string>();
            try
            {
                using var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
                foreach (var dev in pci?.GetSubKeyNames() ?? [])
                {
                    using var devKey = pci.OpenSubKey(dev);
                    foreach (var inst in devKey?.GetSubKeyNames() ?? [])
                    {
                        using var instKey = devKey.OpenSubKey(inst);
                        if (Same(instKey?.GetValue("ClassGUID") as string, DisplayClassGuid))
                            list.Add($@"{LM}\SYSTEM\CurrentControlSet\Enum\PCI\{dev}\{inst}\Device Parameters\Interrupt Management");
                    }
                }
            }
            catch (Exception ex) { SessionLog.Write("GPU ENUM", ex); }
            return list;
        }

        private static void SetGpuInterrupt(string sub, string valueName, int value, string friendlyName, string missingName)
        {
            var gpus = GpuInterruptKeys();
            if (gpus.Count == 0) Report(missingName, false, "No PCI display adapter found");
            foreach (var g in gpus) Dw(g + sub, valueName, value, friendlyName);
        }

        private const string WerQueueTask = @"\Microsoft\Windows\Windows Error Reporting\QueueReporting";

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

        private static readonly string[] NvidiaServices = { "NvTelemetryContainer", "NvDisplayContainerLS" };
        private static readonly string[] NvidiaTasks =
        {
            @"\NvTmRepOnLogon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
            @"\NvTmRep_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
            @"\NvTmMon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
        };

        // ── INDIVIDUAL TWEAKS (by TweakKey) ───────────────────────────────
        private static void ApplyTweak(string key)
        {
            if (LaptopPower.TryGetValue(key, out var parts))
            {
                string headline = parts[0].Label;
                string scheme   = ActiveSchemeGuid();
                if (NeedsHibernation.Contains(key) && TweakDetector.HibernationEnabled() == false)
                    Report(headline, false, "Hibernation is off (Performance → Disable Hibernation). Run 'powercfg -h on' first.");
                else if (scheme == null)
                    Report(headline, false, "Could not read active power scheme");
                else
                {
                    int supported = parts.Count(p => ApplyPowerPart(p, scheme) != PowerResult.Unsupported);
                    // The GPU preference tweak also has a per-app half, so an older Windows without the policy isn't a failure
                    if (supported == 0 && key != "Lap_GpuPowerSaving") Report(headline, false, "Setting not supported on this device");
                }
                if (key == "Lap_GpuPowerSaving") ApplyGpuPreferences();
                return;
            }

            switch (key)
            {
                // ── PERFORMANCE ───────────────────────────────────────
                case "Perf_PowerPlan":      RunCommand("powercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "High Performance power plan"); break;
                case "Perf_PowerThrottle":  Dw(Ctl + @"\Power\PowerThrottling", "PowerThrottlingOff", 1, "Disable Power Throttling"); break;
                case "Perf_SysMain":        DisableService("SysMain"); break;
                case "Perf_WSearch":        DisableService("WSearch"); break;
                case "Perf_StartupDelay":   Dw(CuCv + @"\Explorer\Serialize", "StartupDelayInMSec", 0, "Remove startup delay"); break;
                case "Perf_VisualFX":       Dw(CuCv + @"\Explorer\VisualEffects", "VisualFXSetting", 2, "Visual effects: best performance"); break;
                case "Perf_NtfsLastAccess": RunCommand("fsutil behavior set disablelastaccess 1", "Disable NTFS last-access"); break;
                case "Perf_8Dot3":          RunCommand("fsutil behavior set disable8dot3 1", "Disable 8.3 filenames"); break;
                case "Perf_Hibernate":      RunCommand("powercfg -h off", "Disable hibernation"); break;
                case "Perf_MemCompression":
                    RunPowerShell("Disable-MMAgent -MemoryCompression", "Disable memory compression");
                    // Page combining wastes CPU cycles combining identical pages — low value on 16GB+ systems
                    RunPowerShell("Disable-MMAgent -PageCombining", "Disable page combining"); break;
                case "Perf_TimerRes":
                    try { TimeBeginPeriod(1); Report("Request 1ms timer resolution"); }
                    catch (Exception ex) { Report("Set timer resolution", false, ex.Message); }
                    Dw(Ctl + @"\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, "Persist high-res timer"); break;
                case "Perf_Widgets":        Dw(LmPol + @"\Dsh", "AllowNewsAndInterests", 0, "Disable Widgets board"); break;

                // ── PRIVACY ───────────────────────────────────────────
                case "Priv_Telemetry":
                    Dw(LmPol + @"\Windows\DataCollection", "AllowTelemetry", 0, "Disable telemetry");
                    Dw(LmCv + @"\Policies\DataCollection", "AllowTelemetry", 0, "Disable telemetry (legacy)"); break;
                case "Priv_DiagTrack":
                    foreach (var s in new[] { "DiagTrack", "dmwappushservice", "RetailDemo", "WerSvc" }) DisableService(s); break;
                case "Priv_AdvertisingId":
                    Dw(CuCv + @"\AdvertisingInfo", "Enabled", 0, "Disable Advertising ID");
                    Dw(LmPol + @"\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, "Disable Advertising ID (policy)"); break;
                case "Priv_BingStart":
                    Dw(CuPol + @"\Explorer", "DisableSearchBoxSuggestions", 1, "Disable Bing in Start");
                    Dw(CuCv + @"\Search", "BingSearchEnabled", 0, "Disable Bing Search"); break;
                case "Priv_Cortana":        Dw(CuCv + @"\Search", "CortanaConsent", 0, "Disable Cortana consent"); break;
                case "Priv_ActivityFeed":
                    Dw(LmPol + @"\Windows\System", "EnableActivityFeed",    0, "Disable Activity Feed");
                    Dw(LmPol + @"\Windows\System", "PublishUserActivities", 0, "Disable publishing activities");
                    Dw(LmPol + @"\Windows\System", "UploadUserActivities",  0, "Disable uploading activities"); break;
                case "Priv_Location":       Dw(LmPol + @"\Windows\LocationAndSensors", "DisableLocation", 1, "Disable location tracking"); break;
                case "Priv_Camera":         Dw(LmPol + @"\Windows\AppPrivacy", "LetAppsAccessCamera", 2, "Block app camera access"); break;
                case "Priv_WER":
                    Dw(LmPol + @"\Windows\Windows Error Reporting", "Disabled", 1, "Disable Windows Error Reporting");
                    // Also kill the scheduled upload queue — WerSvc being disabled doesn't prevent this task
                    SetTask(WerQueueTask, false); break;
                case "Priv_SmartScreen":    Dw(LmPol + @"\Windows\System", "EnableSmartScreen", 0, "Disable SmartScreen"); break;
                case "Priv_TelemetryTasks": foreach (var t in TelemetryTasks) SetTask(t, false); break;
                case "Priv_AppTracking":    Dw(ExplorerAdvanced, "Start_TrackProgs", 0, "Disable app launch tracking"); break;
                case "Priv_Feedback":       Dw(CuSw + @"\Siuf\Rules", "NumberOfSIUFInPeriod", 0, "Disable feedback requests"); break;
                case "Priv_ChatIcon":       Dw(ExplorerAdvanced, "TaskbarMn", 0, "Disable Chat/Teams icon"); break;
                case "Priv_Recall":
                    Dw(LmPol + @"\Windows\WindowsAI", "DisableAIDataAnalysis", 1, "Disable Windows Recall (machine)");
                    Dw(CuPol + @"\WindowsAI", "DisableAIDataAnalysis", 1, "Disable Windows Recall (user)"); break;
                case "Priv_CloudContent":
                    // Disables Spotlight suggestions, lock screen ads, "fun facts", and app suggestions
                    Dw(LmPol + @"\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, "Disable Windows consumer features");
                    Dw(LmPol + @"\Windows\CloudContent", "DisableCloudOptimizedContent",   1, "Disable cloud-optimized content");
                    Dw(LmPol + @"\Windows\CloudContent", "DisableSoftLanding",             1, "Disable soft landing tips");
                    // ContentDeliveryManager — controls lock screen spotlight, suggested apps, silent installs
                    foreach (var (v, f) in new[]
                    {
                        ("ContentDeliveryAllowed",          "Disable content delivery"),
                        ("OemPreInstalledAppsEnabled",      "Disable OEM pre-installed apps"),
                        ("PreInstalledAppsEnabled",         "Disable pre-installed apps"),
                        ("SilentInstalledAppsEnabled",      "Disable silent app installs"),
                        ("SystemPaneSuggestionsEnabled",    "Disable Start suggested apps"),
                        ("SubscribedContent-310093Enabled", "Disable Spotlight lock screen"),
                        ("SubscribedContent-338388Enabled", "Disable Start suggestions"),
                        ("SubscribedContent-338389Enabled", "Disable tips/tricks"),
                    })
                        Dw(ContentDelivery, v, 0, f);
                    break;
                case "Priv_Copilot":
                    Dw(LmPol + @"\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, "Disable Windows Copilot (machine)");
                    Dw(CuPol + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1, "Disable Windows Copilot (user)"); break;
                case "Priv_HostsBlock":     ApplyHostsBlockList(); break;
                case "Priv_TailoredExp":
                    Dw(CuCv + @"\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, "Disable tailored experiences");
                    Dw(CuPol + @"\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1, "Disable tailored experiences (policy)"); break;
                case "Priv_InkTyping":
                    Dw(CuSw + @"\InputPersonalization", "RestrictImplicitInkCollection",  1, "Stop inking data collection");
                    Dw(CuSw + @"\InputPersonalization", "RestrictImplicitTextCollection", 1, "Stop typing data collection");
                    Dw(CuSw + @"\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, "Stop contact harvesting");
                    Dw(CuSw + @"\Personalization\Settings", "AcceptedPrivacyPolicy", 0, "Revoke inking & typing consent"); break;

                // ── RESPONSIVENESS ────────────────────────────────────
                case "Resp_MenuDelay":      Sz(Desktop, "MenuShowDelay", "0", "Instant menu show"); break;
                case "Resp_AppKill":
                    Sz(Desktop, "WaitToKillAppTimeout", "2000", "Fast app kill timeout");
                    Sz(Desktop, "HungAppTimeout",       "1000", "Fast hung app timeout"); break;
                case "Resp_ServiceKill":    Sz(Ctl, "WaitToKillServiceTimeout", "2000", "Fast service kill timeout"); break;
                case "Resp_AutoEndTasks":   Sz(Desktop, "AutoEndTasks", "1", "Auto end tasks on shutdown"); break;
                case "Resp_PlatformTick":
                    RunCommand("bcdedit /set useplatformtick yes", "Platform tick");
                    RunCommand("bcdedit /set useplatformclock no", "Disable platform clock (HPET)"); break;
                case "Resp_VerboseStatus":  Dw(LmCv + @"\Policies\System", "verbosestatus", 1, "Verbose boot/shutdown status messages"); break;
                case "Resp_WinTips":        Dw(ContentDelivery, "SoftLandingEnabled", 0, "Disable Windows Tips"); break;
                case "Resp_SuggestedContent":
                    foreach (var id in new[] { "338389", "338393", "353694", "353696" })
                        Dw(ContentDelivery, $"SubscribedContent-{id}Enabled", 0, $"Disable suggested content ({id})");
                    Dw(ExplorerAdvanced, "Start_IrisRecommendations", 0, "Disable Start menu recommendations"); break;
                case "Resp_ClassicContext": Sz(ClassicMenuKey, "", "", "Classic right-click menu"); break;
                case "Resp_EndTask":        Dw(ExplorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, "Add End Task to taskbar menu"); break;

                // ── GAMING ────────────────────────────────────────────
                case "Game_HAGS":           Dw(Ctl + @"\GraphicsDrivers", "HwSchMode", 2, "Enable HAGS"); break;
                case "Game_GameMode":
                    Dw(CuSw + @"\GameBar", "AllowAutoGameMode",   1, "Enable Game Mode");
                    Dw(CuSw + @"\GameBar", "AutoGameModeEnabled", 1, "Enable Auto Game Mode"); break;
                case "Game_MouseAccel":
                    Sz(CuPanel + @"\Mouse", "MouseSpeed",      "0", "Disable mouse acceleration");
                    Sz(CuPanel + @"\Mouse", "MouseThreshold1", "0", "Mouse threshold 1");
                    Sz(CuPanel + @"\Mouse", "MouseThreshold2", "0", "Mouse threshold 2"); break;
                case "Game_CPUPriority":    Dw(Ctl + @"\PriorityControl", "Win32PrioritySeparation", 38, "CPU foreground priority boost"); break;
                case "Game_DVR":
                    Dw(CuCv + @"\GameDVR", "AppCaptureEnabled", 0, "Disable Game DVR capture");
                    Dw(LmPol + @"\Windows\GameDVR", "AllowGameDVR", 0, "Disable Game DVR (policy)"); break;
                case "Game_FSO":
                    Dw(CU + @"\System\GameConfigStore", "GameDVR_FSEBehaviorMode",          2, "Disable FSO globally");
                    Dw(CU + @"\System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, "Honor FSO setting"); break;
                case "Game_GPUPower":
                    Dw(GpuClassKey, "PerfLevelSrc", 0x3322, "GPU: Prefer Maximum Performance");
                    // ⚠️ VIDEOIDLE is "turn off display after" — 0 = never on AC, and it isn't backed up for Undo
                    RunCommand("powercfg -setacvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOIDLE 0 & powercfg -setactive SCHEME_CURRENT",
                        "GPU power: prevent idle"); break;
                case "Game_NvidiaTelemetry":
                    foreach (var s in NvidiaServices) DisableService(s);
                    foreach (var t in NvidiaTasks) SetTask(t, false); break;
                case "Game_StickyKeys":
                    // Keeps the accessibility features usable from Settings — only the 5×Shift / hold-key hotkeys are turned off
                    Sz(CuPanel + @"\Accessibility\StickyKeys",        "Flags", "506", "Disable Sticky Keys hotkey");
                    Sz(CuPanel + @"\Accessibility\ToggleKeys",        "Flags", "58",  "Disable Toggle Keys hotkey");
                    Sz(CuPanel + @"\Accessibility\Keyboard Response", "Flags", "122", "Disable Filter Keys hotkey"); break;

                // ── NETWORK ───────────────────────────────────────────
                case "Net_Nagle":           ModifyNagle(true); break;
                case "Net_RSS":             RunCommand("netsh int tcp set global rss=enabled", "Enable RSS"); break;
                case "Net_TCPAutoTune":     RunCommand("netsh int tcp set global autotuninglevel=normal", "TCP auto-tuning"); break;
                case "Net_Throttle":        Dw(MmProfile, "NetworkThrottlingIndex", unchecked((int)0xffffffff), "Disable network throttling"); break;
                case "Net_MMResponsive":
                    Dw(MmProfile, "SystemResponsiveness", 0, "Max multimedia responsiveness");
                    // MMCSS Games task — scheduling headroom for game threads; Pro Audio — lower DPC
                    // latency for the audio stack (Discord, game audio)
                    foreach (var task in new[] { "Games", "Pro Audio" })
                    {
                        string k = MmProfile + @"\Tasks\" + task, m = $"MMCSS {task}: ";
                        Dw(k, "Affinity",            0,       m + "Affinity");
                        Sz(k, "Background Only",     "False", m + "not background-only");
                        Dw(k, "Clock Rate",          10000,   m + "Clock Rate");
                        Dw(k, "GPU Priority",        8,       m + "GPU Priority");
                        Dw(k, "Priority",            6,       m + "Priority");
                        Sz(k, "Scheduling Category", "High",  m + "Scheduling Category");
                        Sz(k, "SFIO Rate",           "High",  m + "SFIO Rate");
                    }
                    break;
                case "Net_LargeOffload":
                    // Some NIC drivers add inconsistent latency batching large sends
                    RunPowerShell(Lso("Disabled"), "Disable Large Send Offload (LSO) v2 IPv4/IPv6"); break;
                case "Net_TcpTimedWait":
                    // TIME_WAIT hold 240s → 30s
                    Dw(Svc + @"\Tcpip\Parameters", "TcpTimedWaitDelay", 30, "TCP TIME_WAIT delay → 30s"); break;
                case "Net_DoH":
                    Dw(DnsParams, "EnableAutoDoh", 2, "Enable DoH");
                    foreach (var (ip, label) in new[] { ("1.1.1.1", "Cloudflare DoH template"), ("1.0.0.1", "Cloudflare DoH template (secondary)") })
                    {
                        string k = DnsParams + @"\DohWellKnownServers\" + ip;
                        Dw(k, "DohFlags", 3, "Register " + ip);
                        Sz(k, "DohTemplate", "https://cloudflare-dns.com/dns-query", label);
                    }
                    break;
                case "Net_DeliveryOpt":     Dw(LmPol + @"\Windows\DeliveryOptimization", "DODownloadMode", 0, "Delivery Optimization: HTTP only (no P2P)"); break;

                // ── SECURITY ──────────────────────────────────────────
                case "Sec_AutoRun":
                    Sz(LM + @"\SOFTWARE\Microsoft\Windows NT\CurrentVersion\IniFileMapping\Autorun.inf",
                        "", "@SYS:DoesNotExist", "Block Autorun.inf");   // "" = the key's (Default) value
                    Dw(CuCv + @"\Policies\Explorer", "NoDriveTypeAutoRun", 0xFF, "Disable AutoRun (user)");
                    Dw(LmCv + @"\Policies\Explorer", "NoDriveTypeAutoRun", 0xFF, "Disable AutoRun (machine)"); break;
                case "Sec_RDP":
                    Dw(Ctl + @"\Terminal Server", "fDenyTSConnections", 1, "Disable RDP");
                    RunCommand("netsh advfirewall firewall set rule group=\"Remote Desktop\" new enable=no 2>nul", "Block RDP firewall rule"); break;
                case "Sec_SMBv1":
                    RunPowerShell("Set-SmbServerConfiguration -EnableSMB1Protocol $false -Force", "Disable SMBv1 server");
                    RunPowerShell("Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart", "Remove SMBv1 feature"); break;
                case "Sec_NetBIOS":
                    RunPowerShell("Get-WmiObject Win32_NetworkAdapterConfiguration | Where-Object { $_.TcpipNetbiosOptions -ne $null } | ForEach-Object { $_.SetTcpipNetbios(2) }",
                        "Disable NetBIOS over TCP/IP"); break;
                case "Sec_Defender":
                    Dw(LmPol + @"\Windows Defender", "DisableAntiSpyware", 0, "Ensure Defender not disabled");
                    Dw(LmPol + @"\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 0, "Ensure Defender real-time ON");
                    RunPowerShell("Set-MpPreference -DisableRealtimeMonitoring $false", "Enable Defender real-time"); break;

                // ── ADVANCED ──────────────────────────────────────────
                case "Adv_CoreParking":
                    // CPMINCORES 100 keeps every core unparked — parking causes micro-stutter on some desktops
                    RunCommand(CoreParking(100), "Disable CPU core parking"); break;
                case "Adv_MsiMode":
                    // No MessageNumberLimit override — forcing a value the device doesn't support can stop it initialising
                    SetGpuInterrupt(MsiSub, "MSISupported", 1, "Enable MSI mode for GPU", "Enable MSI mode for GPU"); break;
                case "Adv_IrqAffinity":
                    // DevicePolicy 5 = IrqPolicySpreadMessagesAcrossAllProcessors
                    SetGpuInterrupt(AffinitySub, "DevicePolicy", 5, "GPU IRQ: spread across all processors", "GPU IRQ affinity"); break;
                case "Adv_TscSync":         RunCommand("bcdedit /set tscsyncpolicy legacy", "TSC sync policy → legacy"); break;
                case "Adv_X2Apic":          RunCommand("bcdedit /set x2apicpolicy enable", "Enable x2APIC mode"); break;
                case "Adv_BoostMode":
                    // Only unhides the setting (Attributes=2) — doesn't set a boost value; backed up and undoable
                    Dw(BoostModeKey, "Attributes", 2, "Unhide Processor Performance Boost Mode"); break;

                // ── LAPTOP (registry-based; power-plan ones handled above) ─
                case "Lap_IndexOnBattery":  Dw(LmPol + @"\Windows\Windows Search", "PreventIndexOnBattery", 1, "Pause search indexing on battery"); break;
                case "Lap_BackgroundApps":  Dw(LmPol + @"\Windows\AppPrivacy", "LetAppsRunInBackground", 2, "Block Store apps running in background"); break;
                case "Lap_VoiceActivation":
                    Dw(AppPrivacy, "LetAppsActivateWithVoice",          2, "Block apps activating with voice");
                    Dw(AppPrivacy, "LetAppsActivateWithVoiceAboveLock", 2, "Block voice activation above the lock screen"); break;
                case "Lap_CrossDevice":
                    Dw(LmPol + @"\Windows\System", "EnableCdp", 0, "Disable cross-device experiences (policy)");
                    Dw(CuCv + @"\CDP", "CdpSessionUserAuthzPolicy",       0, "Share across devices: off");
                    Dw(CuCv + @"\CDP", "NearShareChannelUserAuthzPolicy", 0, "Nearby sharing: off");
                    Dw(CuCv + @"\CDP", "RomeSdkChannelUserAuthzPolicy",    0, "Cross-device channel: off"); break;
                case "Lap_SearchHighlights":
                    Dw(CuCv + @"\SearchSettings", "IsDynamicSearchBoxEnabled", 0, "Disable Search Highlights");
                    Dw(LmPol + @"\Windows\Windows Search", "EnableDynamicContentInWSB", 0, "Disable Search Highlights (policy)"); break;
                case "Lap_SettingsSync":
                    Dw(LmPol + @"\Windows\SettingSync", "DisableSettingSync",             2, "Disable settings sync");
                    Dw(LmPol + @"\Windows\SettingSync", "DisableSettingSyncUserOverride", 1, "Lock settings sync off"); break;
                case "Lap_MaintenanceWake":
                    Dw(MaintenanceKey, "WakeUp", 0, "Automatic Maintenance: don't wake the PC");
                    Dw(LmPol + @"\Windows\ScheduledMaintenance", "WakeUp", 0, "Automatic Maintenance wake-up (policy)"); break;
                case "Lap_NicPower":        ApplyNicPower(); break;
                case "Lap_EdgeBackground":
                    Dw(LmPol + @"\Edge", "StartupBoostEnabled",   0, "Disable Edge Startup Boost");
                    Dw(LmPol + @"\Edge", "BackgroundModeEnabled", 0, "Stop Edge running after close"); break;
                case "Lap_Transparency":    Dw(CuCv + @"\Themes\Personalize", "EnableTransparency", 0, "Disable transparency effects"); break;

                default: Report($"Unknown key: {key}", false, "No handler"); break;
            }
        }
    }
}
