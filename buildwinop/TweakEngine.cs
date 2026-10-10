using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Win11Optimizer
{
    public static class tweakEngine
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]   private static extern uint timeEndPeriod(uint uPeriod);

        // ── RESULTS ───────────────────────────────────────────────────────
        public class tweakResult
        {
            public string name    { get; set; } = "";
            public bool   success { get; set; }
            public string error   { get; set; }
            public string line    => (success ? "✔ " : "✘ ") + name;
        }

        private static readonly List<tweakResult> _results = new();
        public static void clearResults() => _results.Clear();
        private static void report(string name, bool ok = true, string error = null) =>
            _results.Add(new tweakResult { name = name, success = ok, error = error });

        // ── REGISTRY PATHS (shared with tweakDetector so apply and detect can't drift) ──
        private  const string lm = "HKEY_LOCAL_MACHINE", cu = "HKEY_CURRENT_USER";
        internal const string ctl              = lm + @"\SYSTEM\CurrentControlSet\Control";
        internal const string svc              = lm + @"\SYSTEM\CurrentControlSet\Services";
        internal const string lmPol            = lm + @"\SOFTWARE\Policies\Microsoft";
        internal const string lmCv             = lm + @"\SOFTWARE\Microsoft\Windows\CurrentVersion";
        internal const string cuPol            = cu + @"\Software\Policies\Microsoft\Windows";
        internal const string cuSw             = cu + @"\Software\Microsoft";
        internal const string cuCv             = cuSw + @"\Windows\CurrentVersion";
        internal const string cuPanel          = cu + @"\Control Panel";
        internal const string desktop          = cuPanel + @"\Desktop";
        internal const string explorerAdvanced = cuCv + @"\Explorer\Advanced";
        internal const string contentDelivery  = cuCv + @"\ContentDeliveryManager";
        internal const string mmProfile        = lm + @"\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        internal const string dnsParams        = svc + @"\Dnscache\Parameters";
        private  const string displayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        internal const string gpuClassKey      = ctl + @"\Class\" + displayClassGuid + @"\0000";
        internal const string boostModeKey     = ctl + @"\Power\PowerSettings\" + subProcessor + @"\" + perfBoostMode;
        private  const string classicMenuClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        internal const string classicMenuKey   = cu + @"\" + classicMenuClsid + @"\InprocServer32";
        internal const string naglePath        = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        internal const string msiSub           = @"\MessageSignaledInterruptProperties", affinitySub = @"\Affinity Policy";
        internal const string appPrivacy       = lmPol + @"\Windows\AppPrivacy";
        internal const string maintenanceKey   = lm + @"\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
        internal const string gpuPrefKey       = cuSw + @"\DirectX\UserGpuPreferences";
        internal const string appCompat        = lmPol + @"\Windows\AppCompat";
        internal const string searchSettings   = cuCv + @"\SearchSettings";
        internal const string reserveManager   = lmCv + @"\ReserveManager";
        internal const string transitionPolicy = lmPol + @"\Windows\TCPIP\v6Transition";
        internal const string firewallPolicy   = svc + @"\SharedAccess\Parameters\FirewallPolicy";
        internal const string defenderPolicy   = lmPol + @"\Windows Defender";
        internal const string deviceGuard      = ctl + @"\DeviceGuard";
        internal const string edgePolicy       = lmPol + @"\Edge";
        internal const string dwmKey           = lm + @"\SOFTWARE\Microsoft\Windows\Dwm";
        internal const string netClassKey      = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        internal const string powerPlanUltimate = "e9a42b02-d5df-448d-aa00-03f14749eb61", powerPlanHigh = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        // ── BACKUP / RESTORE ──────────────────────────────────────────────
        public class backupEntry
        {
            [JsonPropertyName("Category")]
            public string category  { get; set; }
            [JsonPropertyName("KeyPath")]
            public string keyPath   { get; set; }
            [JsonPropertyName("ValueName")]
            public string valueName { get; set; }
            [JsonPropertyName("ValueData")]
            public string valueData { get; set; }
            [JsonPropertyName("ValueKind")]
            public string valueKind { get; set; }
            [JsonPropertyName("Existed")]
            public bool   existed   { get; set; }
        }

        private const string powerCfgKind = "PowerCfg";
        // Backups that aren't registry values. keyPath = <PREFIX>\<target>, valueName = what was changed, valueData = the original.
        private const string markerKind = "Marker", dnsKind = "Dns", firewallKind = "Firewall";
        private static readonly List<backupEntry> _backups = new();
        private static readonly HashSet<string>   _appliedCategories = new(StringComparer.OrdinalIgnoreCase);
        public static bool hasBackup(string category) => _appliedCategories.Contains(category);

        private static bool same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static (RegistryKey root, string sub) splitPath(string keyPath)
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
        private static bool alreadyBackedUp(string category, string keyPath, string valueName) =>
            _backups.Exists(b => same(b.category, category) && same(b.keyPath, keyPath) && same(b.valueName, valueName));

        private static void backupRegistry(string category, string keyPath, string valueName)
        {
            if (alreadyBackedUp(category, keyPath, valueName)) return;
            // Unreadable or absent → recorded as "didn't exist", so Undo deletes the value
            var entry = new backupEntry
            {
                category = category, keyPath = keyPath, valueName = valueName,
                valueData = "", valueKind = nameof(RegistryValueKind.Unknown)
            };
            try
            {
                var (root, sub) = splitPath(keyPath);
                using var key = root?.OpenSubKey(sub);
                // DoNotExpand keeps REG_EXPAND_SZ values as written (e.g. %SystemRoot%)
                object cur = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (cur != null)
                {
                    string kind = key.GetValueKind(valueName).ToString();
                    string data = cur switch { byte[] b => Convert.ToBase64String(b), string[] l => string.Join("\n", l), _ => cur.ToString() };
                    (entry.existed, entry.valueKind, entry.valueData) = (true, kind, data);
                }
            }
            catch (Exception ex) { sessionLog.write("BACKUP READ", ex); }
            _backups.Add(entry);
        }

        private static void saveBackups() => appPaths.saveJson(appPaths.tweaksBackupFile, _backups, "BACKUP");

        public static void loadBackups()
        {
            var loaded = appPaths.loadJson<List<backupEntry>>(appPaths.tweaksBackupFile, "BACKUP");
            if (loaded == null) return;
            _backups.Clear();
            _backups.AddRange(loaded);
            _appliedCategories.UnionWith(loaded.Select(b => b.category));
        }

        private static List<tweakResult> restoreCategory(string category)
        {
            var res = new List<tweakResult>();
            bool powerTouched = false;
            foreach (var b in _backups.Where(b => same(b.category, category)).ToList())
            {
                try
                {
                    if (b.valueKind == powerCfgKind)
                    {
                        // keyPath = POWERCFG\<scheme>\<subgroup>\<setting>, valueName = AC | DC
                        var p = b.keyPath.Split('\\');
                        int code = exec($"powercfg /{(b.valueName == "AC" ? "setacvalueindex" : "setdcvalueindex")} {p[1]} {p[2]} {p[3]} {b.valueData}");
                        powerTouched = true;
                        res.Add(new tweakResult { name = $"Restored power setting {p[3]} ({b.valueName})", success = code == 0 });
                    }
                    else if (restoreSpecial(b) is { } special) res.Add(special);
                    else if (b.valueKind == nicPowerKind)
                    {
                        string adapter = b.keyPath[(b.keyPath.IndexOf('\\') + 1)..].Replace("'", "''");
                        int code = proc.powerShell($"Set-NetAdapterPowerManagement -Name '{adapter}' -{b.valueName} {b.valueData} -ErrorAction Stop").code;
                        res.Add(new tweakResult { name = $"Restored {b.valueName} on {adapter}", success = code == 0 });
                    }
                    else if (!b.existed)
                    {
                        var (root, sub) = splitPath(b.keyPath);
                        using var k = root?.OpenSubKey(sub, writable: true);
                        k?.DeleteValue(b.valueName, false);
                        res.Add(new tweakResult { name = $"Removed {displayName(b.valueName)}", success = true });
                    }
                    else
                    {
                        var kind = Enum.Parse<RegistryValueKind>(b.valueKind);
                        Registry.SetValue(b.keyPath, b.valueName, kind switch
                        {
                            RegistryValueKind.DWord       => int.Parse(b.valueData),
                            RegistryValueKind.QWord       => long.Parse(b.valueData),
                            RegistryValueKind.Binary      => Convert.FromBase64String(b.valueData),
                            RegistryValueKind.MultiString => b.valueData.Split('\n'),
                            _                             => (object)b.valueData
                        }, kind);
                        res.Add(new tweakResult { name = $"Restored {displayName(b.valueName)}", success = true });
                    }
                    _backups.Remove(b);
                }
                catch (Exception ex) { res.Add(new tweakResult { name = $"Restore {b.valueName}", error = ex.Message }); }
            }

            if (powerTouched) { exec("powercfg /setactive SCHEME_CURRENT"); invalidatePower(); }
            if (!_backups.Exists(b => same(b.category, category))) _appliedCategories.Remove(category);
            saveBackups();
            return res;
        }

        private static void addBackup(string kind, string prefix, string target, string valueName, string original)
        {
            string keyPath = prefix + @"\" + target;
            if (_currentCategory.Length > 0 && !alreadyBackedUp(_currentCategory, keyPath, valueName))
                _backups.Add(new backupEntry
                {
                    category = _currentCategory, keyPath = keyPath, valueName = valueName,
                    valueData = original, valueKind = kind, existed = true
                });
        }

        // Remembers that a command-based tweak changed something, so Undo only reverts what it really did
        private static void recordMarker(string id) => addBackup(markerKind, "MARKER", id, id, "");

        private static readonly string[] firewallProfiles = { "Domain", "Private", "Public" };
        private static readonly string[] firewallStates   = { "True", "False", "NotConfigured" };

        // Restores the non-registry backups. Everything read back from the backup file is validated before it
        // reaches a command line. Returns null when the entry isn't one of these kinds.
        private static tweakResult restoreSpecial(backupEntry b)
        {
            string target = b.keyPath.Contains('\\') ? b.keyPath[(b.keyPath.IndexOf('\\') + 1)..] : "";
            string quoted = target.Replace("'", "''");
            switch (b.valueKind)
            {
                case markerKind when b.valueName == "ReservedStorage":
                    return new tweakResult { name = "Re-enabled Reserved Storage", success = exec("DISM.exe /Online /Set-ReservedStorageState /State:Enabled") == 0 };
                case markerKind when b.valueName == "PowerShell2":
                    return new tweakResult
                    {
                        name = "Re-enabled PowerShell 2.0",
                        success = proc.powerShell("'MicrosoftWindowsPowerShellV2Root','MicrosoftWindowsPowerShellV2' | ForEach-Object { " +
                                                  "Enable-WindowsOptionalFeature -Online -FeatureName $_ -NoRestart -ErrorAction Stop | Out-Null }").code == 0
                    };
                case markerKind:
                    return new tweakResult { name = "Unknown marker " + b.valueName, success = false, error = "Not recognised" };

                case dnsKind when target.Length > 0 && !target.Contains('"'):
                    var servers = b.valueData.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!servers.All(a => System.Net.IPAddress.TryParse(a, out _)))
                        return new tweakResult { name = $"Restore DNS on {target}", success = false, error = "Saved DNS servers are invalid" };
                    string what = servers.Length == 0 ? "-ResetServerAddresses" : $"-ServerAddresses ({string.Join(",", servers.Select(a => $"'{a}'"))})";
                    return new tweakResult
                    {
                        name = $"Restored DNS servers on {target}",
                        success = proc.powerShell($"Set-DnsClientServerAddress -InterfaceAlias '{quoted}' {what} -ErrorAction Stop").code == 0
                    };

                case firewallKind when firewallProfiles.Contains(target) && firewallStates.Contains(b.valueData):
                    return new tweakResult
                    {
                        name = $"Restored firewall {target} profile",
                        success = proc.powerShell($"Set-NetFirewallProfile -Profile {target} -Enabled {b.valueData} -ErrorAction Stop").code == 0
                    };
                case dnsKind or firewallKind:
                    return new tweakResult { name = $"Restore {b.valueKind} backup", success = false, error = "Saved backup entry is invalid" };
            }
            return null;
        }

        private static string displayName(string valueName) => string.IsNullOrEmpty(valueName) ? "(Default)" : valueName;

        // ── HELPERS ───────────────────────────────────────────────────────
        private static string _currentCategory = "";

        private static void setRegistry(string keyPath, string valueName, object value, RegistryValueKind kind, string friendlyName)
        {
            if (_currentCategory.Length > 0) backupRegistry(_currentCategory, keyPath, valueName);
            // Registry.SetValue creates missing keys itself
            try { Registry.SetValue(keyPath, valueName, value, kind); report(friendlyName); }
            catch (Exception ex) { report(friendlyName, false, ex.Message); }
        }

        private static void dw(string key, string name, int value, string friendly)    => setRegistry(key, name, value, RegistryValueKind.DWord, friendly);
        private static void sz(string key, string name, string value, string friendly) => setRegistry(key, name, value, RegistryValueKind.String, friendly);

        // Exit code of a cmd.exe command line, or -1 if it couldn't run
        private static int exec(string command)
        {
            try { return proc.cmd(command).code; }
            catch (Exception ex) { sessionLog.write("EXEC", ex); return -1; }
        }

        private static void runCommand(string command, string friendlyName)
        {
            try
            {
                int code = proc.cmd(command).code;
                report(friendlyName, code == 0, code == 0 ? null : $"exit code {code}");
            }
            catch (Exception ex) { report(friendlyName, false, ex.Message); }
        }

        private static void runPowerShell(string script, string friendlyName)
        {
            try
            {
                var (code, _, err) = proc.powerShell(script);
                err = err.Trim();
                report(friendlyName, code == 0, code == 0 ? null : err.Length > 0 ? err.Split('\n')[0].Trim() : $"exit code {code}");
            }
            catch (Exception ex) { report(friendlyName, false, ex.Message); }
        }

        // Services missing on this edition/OEM image are skipped (exit 0) instead of failing, and
        // net stop/start no longer decides the result — it returns 2 when the service is already
        // in the target state, which used to report perfectly good runs as failures.
        private static void setService(string s, string startType, string netVerb, string label) =>
            runCommand($"(sc query {s} >nul 2>&1 || exit /b 0) && sc config {s} start= {startType} >nul && (net {netVerb} {s} >nul 2>&1 & exit /b 0)",
                       $"{label}: {s}");
        private static void disableService(string s) => setService(s, "disabled", "stop", "Disable");
        // startType: auto | delayed-auto | demand — must match the Windows default for that service
        private static void enableService(string s, string startType = "auto") => setService(s, startType, "start", "Re-enable");

        // Tasks that don't exist on this build are skipped instead of reported as failures
        private static void setTask(string t, bool enable) =>
            runCommand($"(schtasks /Query /TN \"{t}\" >nul 2>&1 || exit /b 0) && schtasks /Change /TN \"{t}\" /{(enable ? "Enable" : "Disable")} >nul",
                       $"{(enable ? "Re-enable" : "Disable")} task: {t}");

        private static string coreParking(int minCores) =>
            $"powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES {minCores} & " +
            $"powercfg -setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES {minCores} & " +
            "powercfg -setactive SCHEME_CURRENT";

        // Large Send Offload v2 (IPv4/IPv6) on every physical adapter; drivers name it either way
        private static string lso(string value) =>
            "Get-NetAdapter -Physical | ForEach-Object { " +
            string.Join("; ", new[] { "V2 (IPv4)", "V2 (IPv6)", "Version 2 (IPv4)", "Version 2 (IPv6)" }.Select(n =>
                $"  try {{ Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName 'Large Send Offload {n}' -DisplayValue '{value}' -ErrorAction SilentlyContinue }} catch {{}}")) +
            " }";

        // ── POWERCFG (per-setting, backed up like registry values) ─────────
        private static readonly Regex guidRx = new(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}");

        private static string activeSchemeGuid()
        {
            try
            {
                var m = guidRx.Match(proc.cmd("powercfg /getactivescheme").output ?? "");
                return m.Success ? m.Value : null;
            }
            catch (Exception ex) { sessionLog.write("POWERCFG", ex); return null; }
        }

        // ── POWER SETTINGS SNAPSHOT ───────────────────────────────────────
        // One `powercfg /qh` dump of the active scheme, parsed once and reused instead of one process
        // per setting (the detection scan reads ~30). Parsed by structure (indentation, GUIDs, hex
        // values) rather than by label, so it doesn't depend on Windows' display language.
        internal sealed class powerInfo
        {
            public int? ac, dc;
            public readonly List<(int index, string name)> options = new();
        }

        private static Dictionary<string, powerInfo> _powerSnap;
        private static DateTime _powerSnapAt;
        private static readonly object _powerLock = new();
        private static readonly Regex hexRx = new(@":\s*0x([0-9a-fA-F]+)\s*$"), indexRx = new(@":\s*(\d+)\s*$");

        private static string powerKey(string sub, string setting) => (sub + "|" + setting).ToLowerInvariant();

        private static Dictionary<string, powerInfo> parsePowerDump(string dump)
        {
            var map = new Dictionary<string, powerInfo>();
            string sub = null;
            powerInfo cur = null;
            int? pendingIndex = null;
            var hex = new List<int>();

            // The last two hex values in a block are always Current AC / Current DC — range settings
            // list Minimum / Maximum / Increment (also hex) before them.
            void flush()
            {
                if (cur != null && hex.Count >= 2) (cur.ac, cur.dc) = (hex[^2], hex[^1]);
                hex.Clear();
            }

            foreach (var raw in dump.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int indent = line.Length - line.TrimStart().Length;
                var g = guidRx.Match(line);
                if (g.Success && indent == 2) { flush(); cur = null; sub = g.Value; }
                else if (g.Success && indent == 4)
                {
                    flush();
                    cur = new powerInfo();
                    if (sub != null) map[powerKey(sub, g.Value)] = cur;
                }
                else if (cur != null)
                {
                    Match m;
                    if ((m = hexRx.Match(line)).Success) hex.Add(Convert.ToInt32(m.Groups[1].Value, 16));
                    else if ((m = indexRx.Match(line)).Success) pendingIndex = int.Parse(m.Groups[1].Value);
                    else if (pendingIndex != null && line.IndexOf(':') is var c and >= 0)
                    {
                        cur.options.Add((pendingIndex.Value, line[(c + 1)..].Trim()));
                        pendingIndex = null;
                    }
                }
            }
            flush();
            return map;
        }

        // Current AC/DC values (and option names) of one power setting; null when this device
        // doesn't expose it. Cached briefly — writes update the cache in place.
        internal static powerInfo queryPower(string subgroup, string setting)
        {
            lock (_powerLock)
            {
                if (_powerSnap == null || DateTime.UtcNow - _powerSnapAt > TimeSpan.FromSeconds(10))
                {
                    try
                    {
                        _powerSnap   = parsePowerDump(proc.cmd("powercfg /qh SCHEME_CURRENT").output ?? "");
                        _powerSnapAt = DateTime.UtcNow;
                    }
                    catch (Exception ex) { sessionLog.write("POWERCFG", ex); return null; }
                }
                return _powerSnap.TryGetValue(powerKey(subgroup, setting), out var info) ? info : null;
            }
        }

        internal static void invalidatePower() { lock (_powerLock) _powerSnap = null; }

        // ── LAPTOP POWER-PLAN TWEAKS ──────────────────────────────────────
        // One power-plan value (battery side unless ac is set). Ok decides whether the current value already satisfies the
        // tweak (so nothing is written or backed up, and a stricter user setting is never loosened);
        // Fmt/Short render it for the tooltip. byName picks the option whose driver-supplied name
        // contains the text (Intel/AMD slider indexes differ by vendor) instead of a fixed target.
        internal sealed record powerPart(string sub, string setting, int target, string label, string shortName,
            Func<int, string> fmt = null, Func<int, int, bool> ok = null, string byName = null, bool ac = false)
        {
            public bool satisfied(int cur, int want) => ok != null ? ok(cur, want) : cur == want;
            // Which side of the plan this part edits: battery (DC) by default, plugged-in (AC) for the desktop-latency tweaks
            public int?   now(powerInfo info) => ac ? info.ac : info.dc;
            public string side                => ac ? "AC" : "DC";
            public string show(powerInfo info, int v) =>
                fmt?.Invoke(v) ?? info?.options.FirstOrDefault(o => o.index == v).name ?? v.ToString();
        }

        internal static int? powerTarget(powerPart p, powerInfo info) =>
            p.byName == null ? p.target
            : info.options.Where(o => o.name.Contains(p.byName, StringComparison.OrdinalIgnoreCase))
                          .Select(o => (int?)o.index).FirstOrDefault();

        private static string pct(int v)  => v + "%";
        private static string secs(int v) => v == 0 ? "never" : v < 60 ? v + " s" : $"{v / 60.0:0.#} min";
        private static Func<int, string> opts(params string[] names) =>
            v => v >= 0 && v < names.Length ? names[v] : v.ToString();

        // "Already good enough" tests: (current, target) → satisfied?
        private static bool within(int cur, int max) => cur > 0 && cur <= max;   // timeouts — 0 means never
        private static bool atMost(int cur, int max) => cur <= max;
        private static bool atLeast(int cur, int min) => cur >= min;
        private static bool notNothing(int cur, int _) => cur != 0;               // lid: anything but "Do nothing"

        // Sets one power setting on the active scheme (the battery side unless the part says otherwise),
        // recording the previous value so Undo can put it back. The other side is never touched.
        private enum powerResult { changed, already, unsupported, failed }

        private static powerResult applyPowerPart(powerPart p, string scheme)
        {
            var info = queryPower(p.sub, p.setting);
            if (info == null || p.now(info) is not int cur || powerTarget(p, info) is not int target) return powerResult.unsupported;

            if (p.satisfied(cur, target)) { report($"{p.label} (already set)"); return powerResult.already; }

            string keyPath = $@"POWERCFG\{scheme}\{p.sub}\{p.setting}";
            if (_currentCategory.Length > 0 && !alreadyBackedUp(_currentCategory, keyPath, p.side))
                _backups.Add(new backupEntry
                {
                    category = _currentCategory, keyPath = keyPath, valueName = p.side,
                    valueData = cur.ToString(), valueKind = powerCfgKind, existed = true
                });
            int code = exec($"powercfg /set{p.side.ToLowerInvariant()}valueindex {scheme} {p.sub} {p.setting} {target} && powercfg /setactive SCHEME_CURRENT");
            report(p.label, code == 0, code == 0 ? null : $"exit code {code}");
            if (code != 0) return powerResult.failed;
            if (p.ac) info.ac = target; else info.dc = target;
            return powerResult.changed;
        }

        // Power setting GUIDs (subgroup, setting) — verified with powercfg /qh on Windows 11
        private const string subEnergySaver   = "de830923-a562-41af-a086-e3a2c6bad2da";
        private const string esBattThreshold  = "e69653ca-cf7f-4f05-aa73-cb833fa90ad4";
        private const string esBrightness     = "13d09884-f74e-474a-a852-b6bde8ad03a8";
        private const string subProcessor     = "54533251-82be-4824-96c1-47b60b740d00";
        private const string perfBoostMode    = "be337238-0d82-4146-a960-4f3749d470c7";
        private const string perfEpp          = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
        private const string perfEppClass1    = "36687f9e-e3a5-4dbf-b1dc-15eb381c6864";
        private const string subVideo         = "7516b95f-f776-4464-8c53-06167f40cc99";
        private const string videoIdle        = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
        private const string adaptBright      = "fbd9aa66-9553-4097-ba44-ed6e9d65eab8";
        private const string subSleep         = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
        private const string standbyIdle      = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
        private const string rtcWake          = "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d";
        private const string subPciExpress    = "501a4d13-42af-4429-9fd1-a8218c268e20";
        private const string aspm             = "ee12f906-d277-404b-b6da-e5fa1a576df5";
        private const string subWireless      = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
        private const string wifiPowerMode    = "12bbebe6-58d6-4636-95bb-3217ef867c1a";
        private const string subUsb           = "2a737441-1930-4402-8d77-b2bebba308a3";
        private const string usbSuspend       = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
        private const string subNone          = "fea3413e-7e05-4911-9a71-700331f1c294";
        private const string standbyNetwork   = "f15576e8-98b7-4186-b944-eafa664402d9";
        private const string subMultimedia    = "9596fb26-9850-41fd-ac3e-f7c3c00afd4b";
        private const string videoPlayback    = "34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4";
        private const string videoQualityBias = "10778347-1370-4ee0-8bbd-33bdacaade49";
        private const string subBattery       = "e73a048d-bf27-4f12-9731-8b2076e8891f";
        private const string batActionCrit    = "637ea02f-bbcb-4015-8e2c-a1c7b9c0b546";
        private const string batLevelLow      = "8183ba9a-e910-48da-8769-14ae6dc1170a";
        private const string batLevelCrit     = "9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469";
        private const string hibernateIdle    = "9d7815a6-7ee4-497e-8888-515a05f02364";
        private const string unattendSleep    = "7bc4a2f9-d8fc-4469-b07b-33eb785aaca0";
        private const string videoDim         = "17aaa29b-8b43-4b94-aafe-35f64daaf1ee";
        private const string videoConLock     = "8ec4b3a5-6868-48c2-be75-4f3044be88a7";
        private const string videoNormalLevel = "aded5e82-b909-4619-9949-f5d71dac0bcb";
        private const string procThrottleMin  = "893dee8e-2bef-41e0-89c6-b55d0929964c";
        private const string sysCoolPol       = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";
        private const string subButtons       = "4f971e89-eebd-4455-a8de-9e59040e7347";
        private const string lidAction        = "5ca83367-6e45-459f-a27b-476b1d01c936";
        private const string subDisk          = "0012ee47-9041-4b5d-9b77-535fba8b1442";
        private const string diskIdle         = "6738e2c4-e8a5-4a42-b16a-e040e769756e";
        private const string subSlideshow     = "0d7dbae2-4294-402a-ba8e-26777e8488cd";
        private const string slideshow        = "309dce9b-bef4-4119-9921-a851fb12f0f4";
        private const string mediaSharing     = "03680956-93bc-4294-bba6-4e0f09bb717f";
        private const string subGraphics      = "5fb4938d-1ee8-4b0f-9a3c-5036b0ab995c";
        private const string gpuPrefPolicy    = "dd848b2a-8a5d-4451-9ae2-39cd41658f6c";
        private const string subIntelGfx      = "44f3beca-a7c0-460e-9df2-bb8b99e0cba6";
        private const string intelGfxPower    = "3619c3f2-afb2-4afc-b0e9-e7fef372de36";
        private const string subAmdSlider     = "c763b4ec-0e50-4b6b-9bed-2b92a6ee884e";
        private const string amdOverlay       = "7ec1751b-60ed-4588-afb5-9819d3d77d90";

        // Power-plan tweaks (Laptop battery side, plus the plugged-in Game_AcLatency), one entry per tweak: the first part is the headline setting,
        // the rest are companions that only exist on some hardware (a tweak fails only if NONE of
        // its parts are supported here). tweakDetector and the tooltip's "now" line read the same table.
        private static powerPart p(string sub, string setting, int target, string label, string shortName,
            Func<int, string> fmt = null, Func<int, int, bool> ok = null, string byName = null, bool ac = false) =>
            new(sub, setting, target, label, shortName, fmt, ok, byName, ac);

        internal static readonly Dictionary<string, powerPart[]> powerTweaks = new()
        {
            ["Lap_EnergySaver"] =
            [
                p(subEnergySaver, esBattThreshold, 30, "Energy Saver turns on at 30%", "Energy Saver at", pct, atLeast),
                p(subEnergySaver, esBrightness,    50, "Energy Saver dims screen to 50%", "Energy Saver brightness", pct, atMost),
            ],
            ["Lap_NoTurbo"] =
            [
                p(subProcessor, perfBoostMode, 0, "Disable CPU turbo boost on battery", "Boost mode",
                  opts("Disabled", "Enabled", "Aggressive", "Efficient enabled", "Efficient aggressive", "Aggressive at guaranteed", "Efficient aggressive at guaranteed")),
            ],
            ["Lap_CpuEfficiency"] =
            [
                p(subProcessor, perfEpp,       80, "CPU energy preference → efficiency (battery)", "Energy preference", pct, atLeast),
                p(subProcessor, perfEppClass1, 80, "E-core energy preference → efficiency (battery)", "E-core preference", pct, atLeast),
            ],
            ["Lap_ScreenSleep"] =
            [
                p(subVideo, videoIdle,   180, "Screen off after 3 min on battery", "Screen off after", secs, within),
                p(subSleep, standbyIdle, 600, "Sleep after 10 min on battery",     "Sleep after",      secs, within),
            ],
            ["Lap_AdaptiveBright"] =
            [
                p(subVideo, adaptBright, 1, "Adaptive brightness on battery", "Adaptive brightness", opts("Off", "On")),
            ],
            ["Lap_PcieAspm"] =
            [
                p(subPciExpress, aspm, 2, "PCIe link state: maximum power savings (battery)", "Link state power", opts("Off", "Moderate", "Maximum savings")),
            ],
            ["Lap_WifiPowerSave"] =
            [
                p(subWireless, wifiPowerMode, 3, "Wi-Fi: maximum power saving (battery)", "Wi-Fi power saving",
                  opts("Maximum performance", "Low", "Medium", "Maximum power saving")),
            ],
            ["Lap_UsbSuspend"] =
            [
                p(subUsb, usbSuspend, 1, "USB selective suspend on battery", "USB suspend", opts("Disabled", "Enabled")),
            ],
            ["Lap_WakeTimers"] =
            [
                p(subSleep, rtcWake, 0, "Disable wake timers on battery", "Wake timers", opts("Disabled", "Enabled", "Important only")),
            ],
            ["Lap_StandbyNetwork"] =
            [
                p(subNone, standbyNetwork, 0, "Disconnect network in Modern Standby (battery)", "Standby network", opts("Disconnected", "Connected", "Managed by Windows")),
            ],
            ["Lap_VideoBattery"] =
            [
                p(subMultimedia, videoPlayback,    2, "Video playback: optimise for battery", "Video playback", opts("Video quality", "Balanced", "Power savings")),
                p(subMultimedia, videoQualityBias, 0, "Video quality bias: power saving", "Quality bias", opts("Power-saving", "Performance")),
            ],
            ["Lap_CritHibernate"] =
            [
                p(subBattery, batActionCrit, 2, "Critical battery action → Hibernate", "At critical battery", opts("Do nothing", "Sleep", "Hibernate", "Shut down")),
            ],
            ["Lap_HibernateAfter"] =
            [
                p(subSleep, hibernateIdle, 3600, "Hibernate after 60 min asleep on battery", "Hibernate after", secs, within),
            ],
            ["Lap_UnattendedSleep"] =
            [
                p(subSleep, unattendSleep, 60, "Sleep again 1 min after an unattended wake (battery)", "Unattended sleep after", secs, within),
            ],
            ["Lap_DimTimeout"] =
            [
                p(subVideo, videoDim, 60, "Dim display after 1 min on battery", "Dim after", secs, within),
            ],
            ["Lap_BatteryBrightness"] =
            [
                p(subVideo, videoNormalLevel, 40, "Display brightness capped at 40% on battery", "Brightness", pct, atMost),
            ],
            ["Lap_LockScreenOff"] =
            [
                p(subVideo, videoConLock, 30, "Lock-screen display off after 30 s on battery", "Lock screen off after", secs, within),
            ],
            ["Lap_MinProcState"] =
            [
                p(subProcessor, procThrottleMin, 5, "Minimum processor state 5% on battery", "Min CPU state", pct, atMost),
                p(subProcessor, sysCoolPol,      0, "Passive cooling on battery", "Cooling", opts("Passive", "Active")),
            ],
            ["Lap_LidClose"] =
            [
                p(subButtons, lidAction, 1, "Lid close → Sleep on battery", "Lid close", opts("Do nothing", "Sleep", "Hibernate", "Shut down"), notNothing),
            ],
            ["Lap_LowBattery"] =
            [
                p(subBattery, batLevelLow,  15, "Low battery warning at 15%", "Low battery at", pct, atLeast),
                p(subBattery, batLevelCrit, 7,  "Critical battery action at 7%", "Critical at", pct, atLeast),
            ],
            ["Lap_Slideshow"] =
            [
                p(subSlideshow,  slideshow,    1, "Desktop slide show paused on battery", "Slide show", opts("Available", "Paused")),
                p(subMultimedia, mediaSharing, 0, "Sleep allowed while sharing media (battery)", "Media sharing", opts("Allow sleep", "Prevent sleep", "Away mode")),
            ],
            ["Lap_DiskIdle"] =
            [
                p(subDisk, diskIdle, 300, "Hard disk off after 5 min on battery", "Disk off after", secs, within),
            ],
            ["Lap_GraphicsSlider"] =
            [
                p(subIntelGfx,   intelGfxPower, 0, "Intel graphics power → maximum battery life", "Intel graphics", byName: "battery"),
                p(subAmdSlider,  amdOverlay,    0, "AMD power slider → battery saver", "AMD slider", byName: "battery"),
            ],
            // Desktop mirror of the Laptop ASPM / USB tweaks: plugged-in side, trading idle power for latency
            ["Game_AcLatency"] =
            [
                p(subUsb,        usbSuspend, 0, "USB selective suspend off (plugged in)", "USB suspend", opts("Disabled", "Enabled"), ac: true),
                p(subPciExpress, aspm,       0, "PCIe link state power management off (plugged in)", "Link state power", opts("Off", "Moderate", "Maximum savings"), ac: true),
            ],
            // Companion: the powercfg side of "prefer integrated GPU" (the per-app side is in applyGpuPreferences)
            ["Lap_GpuPowerSaving"] =
            [
                p(subGraphics, gpuPrefPolicy, 1, "GPU preference policy → low power (battery)", "GPU policy", opts("None", "Low power")),
            ],
        };

        // Game_GPUPower also keeps the display awake while plugged in — backed up like any power value so Undo restores it
        private static readonly powerPart displayNeverOffAc =
            p(subVideo, videoIdle, 0, "Display never turns off while plugged in", "Screen off after", secs, ac: true);

        // Need hibernation on — refuse rather than silently re-enabling what Performance → Disable Hibernation turned off
        private static readonly HashSet<string> needsHibernation = new() { "Lap_CritHibernate", "Lap_HibernateAfter" };

        // True when the driver exposes an Intel or AMD power slider in the active plan
        internal static bool hasGraphicsSlider() =>
            queryPower(subIntelGfx, intelGfxPower) != null || queryPower(subAmdSlider, amdOverlay) != null;

        // ── PER-APP GPU PREFERENCE ────────────────────────────────────────
        // Settings → System → Display → Graphics: "GpuPreference=1;" = power saving (the integrated GPU).
        // Each app's original value is backed up like any registry value, so Undo removes what we added.
        private static void applyGpuPreferences()
        {
            var apps = hardware.gpuPreferenceApps(includeStoreTeams: true);
            if (apps.Count == 0) { report("Integrated-GPU preference: no browsers or Teams found to configure"); return; }
            foreach (var exe in apps)
                setRegistry(gpuPrefKey, exe, "GpuPreference=1;", RegistryValueKind.String, "Prefer integrated GPU: " + Path.GetFileName(exe));
        }

        // ── NETWORK ADAPTER POWER MANAGEMENT ──────────────────────────────
        // Undo has no registry value to put back, so each changed property is backed up as its own
        // entry (keyPath = NICPOWER\<adapter>, valueName = property, valueData = original) and
        // restored through Set-NetAdapterPowerManagement in restoreCategory.
        private const string nicPowerKind = "NicPower";
        private static readonly (string prop, string want, string orig)[] nicProps =
        {
            ("WakeOnMagicPacket",       "Disabled", "Enabled"),
            ("WakeOnPattern",           "Disabled", "Enabled"),
            ("DeviceSleepOnDisconnect", "Enabled",  "Disabled"),
        };

        private static void applyNicPower()
        {
            const string name = "Network adapter power management";
            try
            {
                var (code, outp, _) = proc.powerShell(
                    "Get-NetAdapter -Physical | ForEach-Object { $p = Get-NetAdapterPowerManagement -Name $_.Name -ErrorAction SilentlyContinue; " +
                    "if ($p) { $_.Name + '|' + $p.WakeOnMagicPacket + '|' + $p.WakeOnPattern + '|' + $p.DeviceSleepOnDisconnect } }");
                var rows = (outp ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(l => l.Split('|')).Where(a => a.Length == 4 && !a[0].Contains('"')).ToList();
                if (code != 0 || rows.Count == 0) { report(name, false, "No adapter driver exposes power-management settings"); return; }

                foreach (var row in rows)
                {
                    // Only touch a property that's currently in the opposite state — "Unsupported" stays untouched
                    var changes = nicProps.Where((p, i) => row[i + 1].Equals(p.orig, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (changes.Count == 0) { report($"{row[0]}: already set (or not supported by the driver)"); continue; }

                    string keyPath = @"NICPOWER\" + row[0];
                    foreach (var c in changes.Where(c => _currentCategory.Length > 0 && !alreadyBackedUp(_currentCategory, keyPath, c.prop)))
                        _backups.Add(new backupEntry
                        {
                            category = _currentCategory, keyPath = keyPath, valueName = c.prop,
                            valueData = c.orig, valueKind = nicPowerKind, existed = true
                        });
                    runPowerShell($"Set-NetAdapterPowerManagement -Name '{row[0].Replace("'", "''")}' " +
                                  string.Join(" ", changes.Select(c => $"-{c.prop} {c.want}")) + " -ErrorAction Stop",
                                  $"Power management: {row[0]}");
                }
            }
            catch (Exception ex) { report(name, false, ex.Message); }
        }

        // ── POWER PLAN ────────────────────────────────────────────────────
        // Ultimate Performance where Windows will provide it, High Performance otherwise. Duplicating a
        // scheme onto its own GUID is idempotent and makes it selectable on builds that ship it hidden.
        private static void applyPerformancePlan()
        {
            foreach (var (guid, label) in new[] { (powerPlanUltimate, "Ultimate Performance"), (powerPlanHigh, "High Performance") })
            {
                exec($"powercfg -duplicatescheme {guid} {guid}");   // fails harmlessly if it already exists or isn't offered
                if (exec($"powercfg -setactive {guid}") != 0) continue;
                invalidatePower();
                report($"{label} power plan");
                return;
            }
            report("Performance power plan", false, "Neither Ultimate nor High Performance is available on this PC");
        }

        // ── RESERVED STORAGE ──────────────────────────────────────────────
        private static void applyReservedStorage()
        {
            const string name = "Disable Reserved Storage";
            if (Registry.GetValue(reserveManager, "ShippedWithReserves", null) is int and 0) { report(name + " (already off)"); return; }
            int code = exec("DISM.exe /Online /Set-ReservedStorageState /State:Disabled");
            report(name, code == 0, code == 0 ? null : $"exit code {code}");
            if (code == 0) recordMarker("ReservedStorage");
        }

        // ── POWERSHELL 2.0 ────────────────────────────────────────────────
        private static void applyPowerShell2()
        {
            const string name = "Remove PowerShell 2.0";
            try
            {
                var (code, outp, _) = proc.powerShell("(Get-WindowsOptionalFeature -Online -FeatureName MicrosoftWindowsPowerShellV2Root -ErrorAction SilentlyContinue).State");
                string state = (outp ?? "").Trim();
                if (code != 0 || state.Length == 0) { report(name + " (not present on this Windows build)"); return; }
                if (state.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) { report(name + " (already off)"); return; }

                runPowerShell("'MicrosoftWindowsPowerShellV2','MicrosoftWindowsPowerShellV2Root' | ForEach-Object { " +
                              "Disable-WindowsOptionalFeature -Online -FeatureName $_ -NoRestart -ErrorAction Stop | Out-Null }", name);
                if (_results[^1].success) recordMarker("PowerShell2");
            }
            catch (Exception ex) { report(name, false, ex.Message); }
        }

        // ── FIREWALL ──────────────────────────────────────────────────────
        // Only profiles that are currently off (or unconfigured) are touched, and each one's old state is kept for Undo.
        private static void applyFirewall()
        {
            const string name = "Windows Firewall";
            try
            {
                var (code, outp, _) = proc.powerShell("Get-NetFirewallProfile | ForEach-Object { $_.Name + '|' + $_.Enabled }");
                var rows = (outp ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(l => l.Split('|')).Where(a => a.Length == 2 && firewallProfiles.Contains(a[0])).ToList();
                if (code != 0 || rows.Count == 0) { report(name, false, "Could not read the firewall profiles"); return; }

                foreach (var (profile, state) in rows.Select(r => (r[0], r[1])))
                {
                    if (state.Equals("True", StringComparison.OrdinalIgnoreCase)) { report($"Firewall {profile} profile (already on)"); continue; }
                    if (firewallStates.Contains(state)) addBackup(firewallKind, "FIREWALL", profile, "Enabled", state);
                    runPowerShell($"Set-NetFirewallProfile -Profile {profile} -Enabled True -ErrorAction Stop", $"Enable firewall: {profile} profile");
                }
            }
            catch (Exception ex) { report(name, false, ex.Message); }
        }

        // ── DNS (Cloudflare, for the DoH tweak) ───────────────────────────
        private const string cloudflareDns = "1.1.1.1,1.0.0.1";

        // Connected physical adapters only (VPN and virtual adapters keep their own DNS). Each adapter's
        // old servers are saved; an empty entry means "was automatic (DHCP)" and Undo resets it to that.
        private static void applyDnsServers()
        {
            const string name = "Set adapter DNS to Cloudflare";
            try
            {
                var (code, outp, _) = proc.powerShell("Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' } | ForEach-Object { $_.Name + '|' + $_.InterfaceGuid }");
                var rows = (outp ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(l => l.Split('|')).Where(a => a.Length == 2 && !a[0].Contains('"')).ToList();
                if (code != 0 || rows.Count == 0) { report(name, false, "No connected network adapter found"); return; }

                foreach (var row in rows)
                {
                    string raw  = Registry.GetValue(svc + @"\Tcpip\Parameters\Interfaces\" + row[1], "NameServer", "") as string ?? "";
                    string orig = string.Join(",", raw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    if (orig == cloudflareDns) { report($"DNS on {row[0]} (already Cloudflare)"); continue; }
                    // 127.x / ::1 = a local resolver (WARP, DNSCrypt, a DoH proxy…) is managing DNS here — replacing it would bypass it
                    if (orig.Split(',').Any(a => a.StartsWith("127.") || a == "::1")) { report($"DNS on {row[0]} (skipped — a local DNS proxy is in use)"); continue; }

                    addBackup(dnsKind, "DNS", row[0], "NameServer", orig);
                    runPowerShell($"Set-DnsClientServerAddress -InterfaceAlias '{row[0].Replace("'", "''")}' -ServerAddresses ('1.1.1.1','1.0.0.1') -ErrorAction Stop",
                                  $"DNS on {row[0]} → Cloudflare");
                }
            }
            catch (Exception ex) { report(name, false, ex.Message); }
        }

        // ── NETWORK ADAPTER LATENCY (desktop) ─────────────────────────────
        // The mirror of Lap_NicPower: stops Windows powering the NIC down and, where the driver has the option,
        // turns off Energy-Efficient / Green Ethernet. Written straight into the adapter's class key (driver
        // properties live there as strings), so the normal registry backup covers Undo.
        private static readonly string[] greenEthernetKeywords = { "*EEE", "EEE", "AdvancedEEE", "EnableGreenEthernet", "GreenEthernet" };

        private static void applyNicLatency()
        {
            const string name = "Network adapter power saving";
            try
            {
                var (code, outp, _) = proc.powerShell("Get-NetAdapter -Physical | ForEach-Object { $_.InterfaceGuid }");
                var guids = (outp ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (code != 0 || guids.Count == 0) { report(name, false, "No physical network adapter found"); return; }

                using var cls = Registry.LocalMachine.OpenSubKey(netClassKey);
                int touched = 0;
                foreach (string sub in cls?.GetSubKeyNames() ?? [])
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("NetCfgInstanceId") is not string id || !guids.Contains(id)) continue;

                    string path = $@"{lm}\{netClassKey}\{sub}", label = (k.GetValue("DriverDesc") as string) ?? sub;
                    // 0x10 = "Allow the computer to turn off this device" unticked; the wake-related bits are left as they were
                    dw(path, "PnPCapabilities", (k.GetValue("PnPCapabilities") is int caps ? caps : 0) | 0x10, $"{label}: don't power the adapter down");
                    foreach (string kw in greenEthernetKeywords.Where(w => k.GetValue(w) != null))
                        sz(path, kw, "0", $"{label}: {kw} off");
                    touched++;
                }
                if (touched == 0) report(name, false, "No adapter driver key matched");
            }
            catch (Exception ex) { report(name, false, ex.Message); }
        }

        // ── OPTIMISATIONS FOR WINDOWED GAMES ──────────────────────────────
        // DirectXUserGlobalSettings is a ';'-separated list shared with VRR / Auto HDR — merge, don't overwrite
        private static void applyWindowedOptimisations()
        {
            string cur = Registry.GetValue(gpuPrefKey, "DirectXUserGlobalSettings", null) as string ?? "";
            const string want = "SwapEffectUpgradeEnable=1;";
            var rx = new Regex(@"SwapEffectUpgradeEnable=\d;?", RegexOptions.IgnoreCase);
            string next = rx.IsMatch(cur) ? rx.Replace(cur, want) : cur + (cur.Length == 0 || cur.EndsWith(';') ? "" : ";") + want;
            sz(gpuPrefKey, "DirectXUserGlobalSettings", next, "Optimizations for windowed games");
        }

        // ── HOSTS BLOCK LIST ──────────────────────────────────────────────
        private static readonly string[] telemetryHosts =
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

        internal const string hostsMarkerStart = "# WIN11OPTIMIZER_TELEMETRY_BLOCK_START";
        private  const string hostsMarkerEnd   = "# WIN11OPTIMIZER_TELEMETRY_BLOCK_END";
        internal static string hostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        private static void applyHostsBlockList()
        {
            try
            {
                if (File.Exists(hostsPath) && File.ReadAllText(hostsPath).Contains(hostsMarkerStart))
                {
                    report("Block telemetry hosts (already applied)");
                    return;
                }
                var sb = new StringBuilder().AppendLine().AppendLine(hostsMarkerStart);
                foreach (var host in telemetryHosts) sb.AppendLine($"0.0.0.0 {host}");
                File.AppendAllText(hostsPath, sb.AppendLine(hostsMarkerEnd).ToString());
                report($"Blocked {telemetryHosts.Length} telemetry domains");
            }
            catch (Exception ex) { report("Block telemetry hosts", false, ex.Message); }
        }

        private static void removeHostsBlockList()
        {
            try
            {
                if (!File.Exists(hostsPath)) return;
                string content = File.ReadAllText(hostsPath);
                int start = content.IndexOf(hostsMarkerStart, StringComparison.Ordinal);
                int end   = content.IndexOf(hostsMarkerEnd,   StringComparison.Ordinal);
                if (start < 0 || end < start) return;
                end += hostsMarkerEnd.Length;
                // Swallow the line break after the end marker (CRLF, LF, or none at EOF)
                // and the blank line we inserted before the start marker.
                if (end < content.Length && content[end] == '\r') end++;
                if (end < content.Length && content[end] == '\n') end++;
                if (start >= 2 && content.Substring(start - 2, 2) == "\r\n") start -= 2;
                else if (start >= 1 && content[start - 1] == '\n') start -= 1;
                File.WriteAllText(hostsPath, content.Remove(start, end - start));
                report("Removed telemetry hosts block");
            }
            catch (Exception ex)
            {
                sessionLog.write("HOSTS RESTORE", ex);
                report("Remove telemetry hosts block", false, ex.Message);
            }
        }

        // ── NAGLE'S ALGORITHM ─────────────────────────────────────────────
        private static void modifyNagle(bool disable)
        {
            string name = disable ? "Disable Nagle's Algorithm" : "Restore Nagle's Algorithm";
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(naglePath, writable: true);
                if (root == null)
                {
                    if (disable) report(name, false, "Base key not found");
                    return;
                }
                foreach (string sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    if (k == null) continue;
                    foreach (var v in new[] { "TcpAckFrequency", "TCPNoDelay" })
                        if (disable) k.SetValue(v, 1, RegistryValueKind.DWord); else k.DeleteValue(v, false);
                }
                if (disable) report(name);
            }
            catch (Exception ex) { report(name, false, ex.Message); }   // restore failures used to vanish silently
        }

        // ── SYSTEM RESTORE POINT ──────────────────────────────────────────
        public static bool createRestorePoint(string description)
        {
            try
            {
                var (code, _, err) = proc.powerShell(
                    $"Checkpoint-Computer -Description '{description.Replace("'", "")}' -RestorePointType MODIFY_SETTINGS");
                // Windows allows one restore point per 24h — "too soon" means a recent one already exists
                return code == 0 || err.Contains("0x80042306") || err.Contains("too soon") || err.Contains("frequency");
            }
            catch (Exception ex) { sessionLog.write("RESTORE POINT", ex); return false; }
        }

        // ── APPLY ─────────────────────────────────────────────────────────
        private static void dispatch(string category, Action action)
        {
            _currentCategory = category;
            try { action(); }
            catch (Exception ex)
            {
                report($"{category} tweak", false, ex.Message);
                sessionLog.write("TWEAK", ex);
            }
            finally { _currentCategory = ""; }
            // App removal has no Undo — don't offer one (it used to enable Undo for Bloatware in-session)
            if (category != "Bloatware") _appliedCategories.Add(category);
            saveBackups();
        }

        // Single entry point used by both the GUI and the headless CLI so the Bloatware / Advanced /
        // regular routing can't drift between the two. Returns just the results this tweak produced.
        public static List<tweakResult> apply(tweakEntry entry)
        {
            int start = _results.Count;
            if (entry.category == "Bloatware")
            {
                if (bloatPatterns.TryGetValue(entry.tweakKey, out var patterns)) dispatch("Bloatware", () => removeApps(patterns));
            }
            else if (entry.isAdvanced && entry.advancedKey != null) dispatch("Advanced", () => applyAdvanced(entry.advancedKey));
            else dispatch(entry.category, () => applyTweak(entry.tweakKey));
            return _results.GetRange(start, _results.Count - start);
        }

        // ── UNDO ──────────────────────────────────────────────────────────
        // Restores a category's registry/powercfg backups, then reverts the command-based tweaks
        // that have no registry value to put back. Returns every result from both.
        public static List<tweakResult> undo(string category)
        {
            clearResults();
            var r = restoreCategory(category);
            switch (category)
            {
                case "Performance":
                    runCommand("powercfg -setactive 381b4222-f694-41f0-9685-ff5bb260df2e", "Restore Balanced power plan");
                    enableService("SysMain");
                    enableService("WSearch", "delayed-auto");   // Windows default is Automatic (Delayed Start)
                    runCommand("fsutil behavior set disablelastaccess 0", "Re-enable NTFS last-access");
                    runCommand("fsutil behavior set disable8dot3 2", "Restore 8.3 filenames (per-volume default)");
                    runCommand("powercfg -h on", "Re-enable hibernation");
                    runPowerShell("Enable-MMAgent -MemoryCompression", "Re-enable memory compression");
                    runPowerShell("Enable-MMAgent -PageCombining", "Re-enable page combining");
                    try { timeEndPeriod(1); } catch (Exception ex) { sessionLog.write("TIMER", ex); }
                    break;
                case "Privacy":
                    enableService("DiagTrack");
                    // These three default to Manual (demand) — "auto" would leave them running permanently
                    foreach (var s in new[] { "dmwappushservice", "RetailDemo", "WerSvc" }) enableService(s, "demand");
                    foreach (var t in telemetryTasks.Append(werQueueTask)) setTask(t, true);
                    removeHostsBlockList();
                    break;
                case "Responsiveness":
                    runCommand("bcdedit /deletevalue useplatformtick 2>nul",  "Restore platform tick default");
                    runCommand("bcdedit /deletevalue useplatformclock 2>nul", "Restore platform clock default");
                    // The classic menu lives in its own CLSID key — remove the whole key to bring back the Win11 menu
                    try { Registry.CurrentUser.DeleteSubKeyTree(classicMenuClsid, throwOnMissingSubKey: false); report("Restore Windows 11 context menu"); }
                    catch (Exception ex) { report("Restore Windows 11 context menu", false, ex.Message); }
                    break;
                case "Gaming":
                    foreach (var s in nvidiaServices) enableService(s);
                    foreach (var t in nvidiaTasks) setTask(t, true);
                    break;
                case "Network":
                    modifyNagle(false);
                    runCommand("netsh int tcp set global autotuninglevel=normal", "Restore TCP auto-tuning");
                    runPowerShell(lso("Enabled"), "Re-enable Large Send Offload (LSO)");
                    break;
                case "Advanced":
                    runCommand("bcdedit /deletevalue disabledynamictick 2>nul", "Restore dynamic tick default");
                    runCommand(coreParking(0), "Restore CPU core parking");   // 0 = Windows-managed, park freely
                    runCommand("bcdedit /deletevalue tscsyncpolicy 2>nul", "Restore TSC sync policy default");
                    runCommand("bcdedit /deletevalue x2apicpolicy 2>nul",  "Restore x2APIC policy default");
                    break;
                case "Security":
                    // 0 = use the NetBIOS setting from the DHCP server (Windows default)
                    runPowerShell(
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
        private static readonly Dictionary<string, string[]> bloatPatterns = new()
        {
            ["Bloat_Bing"]      = new[] { "*BingNews*",  "*BingWeather*", "*BingSearch*" },
            ["Bloat_Zune"]      = new[] { "*ZuneVideo*", "*ZuneMusic*" },
            ["Bloat_Solitaire"] = new[] { "*SolitaireCollection*" },
            ["Bloat_Maps"]      = new[] { "*WindowsMaps*" },
            ["Bloat_PhoneLink"] = new[] { "*YourPhone*",  "*PhoneLink*" },
            ["Bloat_Clipchamp"] = new[] { "*Clipchamp*" },
            ["Bloat_Xbox"]      = new[] { "*Xbox.TCUI*",  "*XboxApp*", "*XboxGameOverlay*", "*XboxGamingOverlay*", "*XboxSpeechToTextOverlay*" },
            ["Bloat_AdTiles"]   = new[] { "*LinkedIn*",   "*Disney*", "*Spotify*", "*TikTok*", "*Instagram*", "*Facebook*",
                                          "*CandyCrush*", "*king.com*", "*Netflix*", "*PrimeVideo*", "*Duolingo*", "*Hulu*",
                                          "*Pandora*",    "*Twitter*", "*AdobePhotoshopExpress*", "*AdobeExpress*" },
            ["Bloat_Office"]    = new[] { "*OfficeHub*",  "*OneNote*" },
            ["Bloat_3D"]        = new[] { "*3DViewer*",   "*Print3D*" },
            ["Bloat_Copilot"]   = new[] { "*Microsoft.Copilot*", "*Windows.Ai.Copilot*" },
            ["Bloat_Cortana"]   = new[] { "*549981C3F5F10*" },
            ["Bloat_HelpApps"]  = new[] { "*GetHelp*", "*Getstarted*", "*WindowsFeedbackHub*" },
            ["Bloat_Outlook"]   = new[] { "*OutlookForWindows*", "*windowscommunicationsapps*" },
            ["Bloat_Misc"]      = new[] { "*MixedReality.Portal*", "*DevHome*", "*PowerAutomateDesktop*" },
        };

        private static void removeApps(string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                string name = pattern.Replace("*", "").Trim();
                runPowerShell($"Get-AppxPackage {pattern} | Remove-AppxPackage -ErrorAction SilentlyContinue", $"Remove (user) {name}");
                runPowerShell($"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.PackageName -like '{pattern}' }}" +
                              " | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue", $"Remove (provisioned) {name}");
            }
        }

        // ── ADVANCED (advancedKey) TWEAKS ─────────────────────────────────
        private static void applyAdvanced(string advancedKey)
        {
            switch (advancedKey)
            {
                case "ProcessorScheduling":
                    dw(ctl + @"\PriorityControl", "Win32PrioritySeparation", 38, "Processor scheduling: programs"); break;
                case "DisableDynamicTick":
                    runCommand("bcdedit /set disabledynamictick yes", "Disable dynamic tick"); break;
                case "DisableCpuThrottling":
                    dw(ctl + @"\Power\PowerSettings\" + subProcessor + @"\893dee8e-2bef-41e0-89c6-b55d0929964c", "ValueMax", 0, "Disable CPU throttling");
                    runCommand("powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 0 & powercfg -setactive SCHEME_CURRENT",
                        "Apply CPU throttle policy"); break;
                case "EnableTrim":
                    runCommand("fsutil behavior set disabledeletenotify 0", "Enable SSD TRIM"); break;
                case "AggressiveAnimations":
                    setRegistry(desktop, "UserPreferencesMask", new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 },
                        RegistryValueKind.Binary, "Disable all UI animations");
                    dw(explorerAdvanced, "TaskbarAnimations", 0, "Disable taskbar animations");
                    sz(desktop + @"\WindowMetrics", "MinAnimate", "0", "Disable minimize animations");
                    dw(explorerAdvanced, "ListviewShadow", 0, "Disable listview shadows");
                    sz(desktop, "FontSmoothing", "2", "Keep ClearType smoothing"); break;
            }
        }

        // Interrupt settings (MSI mode, affinity policy) are read from the PCI device instance key
        // under Enum — NOT the display class key, which Windows ignores for this. One key per
        // display adapter (iGPU + dGPU): "HKEY_LOCAL_MACHINE\...\Enum\PCI\<dev>\<inst>\Device Parameters\Interrupt Management".
        internal static List<string> gpuInterruptKeys()
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
                        if (same(instKey?.GetValue("ClassGUID") as string, displayClassGuid))
                            list.Add($@"{lm}\SYSTEM\CurrentControlSet\Enum\PCI\{dev}\{inst}\Device Parameters\Interrupt Management");
                    }
                }
            }
            catch (Exception ex) { sessionLog.write("GPU ENUM", ex); }
            return list;
        }

        private static void setGpuInterrupt(string sub, string valueName, int value, string friendlyName, string missingName)
        {
            var gpus = gpuInterruptKeys();
            if (gpus.Count == 0) report(missingName, false, "No PCI display adapter found");
            foreach (var g in gpus) dw(g + sub, valueName, value, friendlyName);
        }

        private const string werQueueTask = @"\Microsoft\Windows\Windows Error Reporting\QueueReporting";

        private static readonly string[] telemetryTasks =
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

        private static readonly string[] nvidiaServices = { "NvTelemetryContainer", "NvDisplayContainerLS" };
        private static readonly string[] nvidiaTasks =
        {
            @"\NvTmRepOnLogon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
            @"\NvTmRep_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
            @"\NvTmMon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
        };

        // ── INDIVIDUAL TWEAKS (by tweakKey) ───────────────────────────────
        private static void applyTweak(string key)
        {
            if (powerTweaks.TryGetValue(key, out var parts))
            {
                string headline = parts[0].label;
                string scheme   = activeSchemeGuid();
                if (needsHibernation.Contains(key) && tweakDetector.hibernationEnabled() == false)
                    report(headline, false, "Hibernation is off (Performance → Disable Hibernation). Run 'powercfg -h on' first.");
                else if (scheme == null)
                    report(headline, false, "Could not read active power scheme");
                else
                {
                    int supported = parts.Count(p => applyPowerPart(p, scheme) != powerResult.unsupported);
                    // The GPU preference tweak also has a per-app half, so an older Windows without the policy isn't a failure
                    if (supported == 0 && key != "Lap_GpuPowerSaving") report(headline, false, "Setting not supported on this device");
                }
                if (key == "Lap_GpuPowerSaving") applyGpuPreferences();
                return;
            }

            switch (key)
            {
                // ── PERFORMANCE ───────────────────────────────────────
                case "Perf_PowerPlan":      applyPerformancePlan(); break;
                case "Perf_PowerThrottle":  dw(ctl + @"\Power\PowerThrottling", "PowerThrottlingOff", 1, "Disable Power Throttling"); break;
                case "Perf_SysMain":        disableService("SysMain"); break;
                case "Perf_WSearch":        disableService("WSearch"); break;
                case "Perf_StartupDelay":   dw(cuCv + @"\Explorer\Serialize", "StartupDelayInMSec", 0, "Remove startup delay"); break;
                case "Perf_VisualFX":       dw(cuCv + @"\Explorer\VisualEffects", "VisualFXSetting", 2, "Visual effects: best performance"); break;
                case "Perf_NtfsLastAccess": runCommand("fsutil behavior set disablelastaccess 1", "Disable NTFS last-access"); break;
                case "Perf_8Dot3":          runCommand("fsutil behavior set disable8dot3 1", "Disable 8.3 filenames"); break;
                case "Perf_Hibernate":      runCommand("powercfg -h off", "Disable hibernation"); break;
                case "Perf_MemCompression":
                    runPowerShell("Disable-MMAgent -MemoryCompression", "Disable memory compression");
                    // Page combining wastes CPU cycles combining identical pages — low value on 16GB+ systems
                    runPowerShell("Disable-MMAgent -PageCombining", "Disable page combining"); break;
                case "Perf_TimerRes":
                    try { timeBeginPeriod(1); report("Request 1ms timer resolution"); }
                    catch (Exception ex) { report("Set timer resolution", false, ex.Message); }
                    dw(ctl + @"\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, "Persist high-res timer"); break;
                case "Perf_Widgets":        dw(lmPol + @"\Dsh", "AllowNewsAndInterests", 0, "Disable Widgets board"); break;
                case "Perf_FastStartup":    dw(ctl + @"\Session Manager\Power", "HiberbootEnabled", 0, "Disable Fast Startup"); break;
                case "Perf_ReservedStorage": applyReservedStorage(); break;

                // ── PRIVACY ───────────────────────────────────────────
                case "Priv_Telemetry":
                    dw(lmPol + @"\Windows\DataCollection", "AllowTelemetry", 0, "Disable telemetry");
                    dw(lmCv + @"\Policies\DataCollection", "AllowTelemetry", 0, "Disable telemetry (legacy)"); break;
                case "Priv_DiagTrack":
                    foreach (var s in new[] { "DiagTrack", "dmwappushservice", "RetailDemo", "WerSvc" }) disableService(s); break;
                case "Priv_AdvertisingId":
                    dw(cuCv + @"\AdvertisingInfo", "Enabled", 0, "Disable Advertising ID");
                    dw(lmPol + @"\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, "Disable Advertising ID (policy)"); break;
                case "Priv_BingStart":
                    dw(cuPol + @"\Explorer", "DisableSearchBoxSuggestions", 1, "Disable Bing in Start");
                    dw(cuCv + @"\Search", "BingSearchEnabled", 0, "Disable Bing Search"); break;
                case "Priv_Cortana":        dw(cuCv + @"\Search", "CortanaConsent", 0, "Disable Cortana consent"); break;
                case "Priv_ActivityFeed":
                    dw(lmPol + @"\Windows\System", "EnableActivityFeed",    0, "Disable Activity Feed");
                    dw(lmPol + @"\Windows\System", "PublishUserActivities", 0, "Disable publishing activities");
                    dw(lmPol + @"\Windows\System", "UploadUserActivities",  0, "Disable uploading activities"); break;
                case "Priv_Location":       dw(lmPol + @"\Windows\LocationAndSensors", "DisableLocation", 1, "Disable location tracking"); break;
                case "Priv_Camera":         dw(lmPol + @"\Windows\AppPrivacy", "LetAppsAccessCamera", 2, "Block app camera access"); break;
                case "Priv_WER":
                    dw(lmPol + @"\Windows\Windows Error Reporting", "Disabled", 1, "Disable Windows Error Reporting");
                    // Also kill the scheduled upload queue — WerSvc being disabled doesn't prevent this task
                    setTask(werQueueTask, false); break;
                case "Priv_SmartScreen":    dw(lmPol + @"\Windows\System", "EnableSmartScreen", 0, "Disable SmartScreen"); break;
                case "Priv_TelemetryTasks": foreach (var t in telemetryTasks) setTask(t, false); break;
                case "Priv_AppTracking":    dw(explorerAdvanced, "Start_TrackProgs", 0, "Disable app launch tracking"); break;
                case "Priv_Feedback":       dw(cuSw + @"\Siuf\Rules", "NumberOfSIUFInPeriod", 0, "Disable feedback requests"); break;
                case "Priv_ChatIcon":       dw(explorerAdvanced, "TaskbarMn", 0, "Disable Chat/Teams icon"); break;
                case "Priv_Recall":
                    dw(lmPol + @"\Windows\WindowsAI", "DisableAIDataAnalysis", 1, "Disable Windows Recall (machine)");
                    dw(cuPol + @"\WindowsAI", "DisableAIDataAnalysis", 1, "Disable Windows Recall (user)"); break;
                case "Priv_CloudContent":
                    // Disables Spotlight suggestions, lock screen ads, "fun facts", and app suggestions
                    dw(lmPol + @"\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, "Disable Windows consumer features");
                    dw(lmPol + @"\Windows\CloudContent", "DisableCloudOptimizedContent",   1, "Disable cloud-optimized content");
                    dw(lmPol + @"\Windows\CloudContent", "DisableSoftLanding",             1, "Disable soft landing tips");
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
                        dw(contentDelivery, v, 0, f);
                    break;
                case "Priv_Copilot":
                    dw(lmPol + @"\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, "Disable Windows Copilot (machine)");
                    dw(cuPol + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1, "Disable Windows Copilot (user)"); break;
                case "Priv_HostsBlock":     applyHostsBlockList(); break;
                case "Priv_TailoredExp":
                    dw(cuCv + @"\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, "Disable tailored experiences");
                    dw(cuPol + @"\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1, "Disable tailored experiences (policy)"); break;
                case "Priv_AppCompatTelemetry":
                    dw(appCompat, "AITEnable",        0, "Disable application-impact telemetry");
                    dw(appCompat, "DisableInventory", 1, "Disable application inventory");
                    dw(appCompat, "DisableUAR",       1, "Disable Steps Recorder");
                    dw(lmPol + @"\SQMClient\Windows", "CEIPEnable", 0, "Disable Customer Experience Improvement Program"); break;
                case "Priv_SearchCloud":
                    dw(searchSettings, "IsAADCloudSearchEnabled",      0, "Disable work/school cloud search");
                    dw(searchSettings, "IsMSACloudSearchEnabled",      0, "Disable Microsoft-account cloud search");
                    dw(searchSettings, "IsDeviceSearchHistoryEnabled", 0, "Disable search history on this device"); break;
                case "Priv_OnlineSpeech":
                    dw(cuSw + @"\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0, "Disable online speech recognition");
                    dw(lmPol + @"\InputPersonalization", "AllowInputPersonalization", 0, "Disable online speech recognition (policy)"); break;
                case "Priv_ClipboardSync":  dw(lmPol + @"\Windows\System", "AllowCrossDeviceClipboard", 0, "Disable clipboard sync across devices"); break;
                case "Priv_EdgeTelemetry":
                    foreach (var (v, f) in new[]
                    {
                        ("MetricsReportingEnabled",                       "Edge: stop usage and crash reporting"),
                        ("PersonalizationReportingEnabled",               "Edge: stop personalised ads/news data"),
                        ("ShowRecommendationsEnabled",                    "Edge: no recommendations"),
                        ("SpotlightExperiencesAndRecommendationsEnabled", "Edge: no spotlight experiences"),
                        ("HubsSidebarEnabled",                            "Edge: hide the sidebar"),
                        ("EdgeShoppingAssistantEnabled",                  "Edge: disable shopping assistant"),
                        ("UserFeedbackAllowed",                           "Edge: disable feedback prompts"),
                    })
                        dw(edgePolicy, v, 0, f);
                    break;
                case "Priv_AppDiagInfo":
                    foreach (var (v, f) in new[]
                    {
                        ("LetAppsGetDiagnosticInfo",    "Block app access to other apps' diagnostics"),
                        ("LetAppsAccessCallHistory",    "Block app access to call history"),
                        ("LetAppsAccessTasks",          "Block app access to tasks"),
                        ("LetAppsAccessTrustedDevices", "Block app access to trusted devices"),
                    })
                        dw(appPrivacy, v, 2, f);
                    break;
                case "Priv_RecentFiles":
                    dw(cuCv + @"\Explorer", "ShowRecent",   0, "Hide recent files in Quick Access");
                    dw(cuCv + @"\Explorer", "ShowFrequent", 0, "Hide frequent folders in Quick Access");
                    dw(explorerAdvanced, "Start_TrackDocs", 0, "Stop tracking opened documents"); break;
                case "Priv_OneDrive":       dw(lmPol + @"\Windows\OneDrive", "DisableFileSyncNGSC", 1, "Disable OneDrive file sync"); break;
                case "Priv_InkTyping":
                    dw(cuSw + @"\InputPersonalization", "RestrictImplicitInkCollection",  1, "Stop inking data collection");
                    dw(cuSw + @"\InputPersonalization", "RestrictImplicitTextCollection", 1, "Stop typing data collection");
                    dw(cuSw + @"\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, "Stop contact harvesting");
                    dw(cuSw + @"\Personalization\Settings", "AcceptedPrivacyPolicy", 0, "Revoke inking & typing consent"); break;

                // ── RESPONSIVENESS ────────────────────────────────────
                case "Resp_MenuDelay":      sz(desktop, "MenuShowDelay", "0", "Instant menu show"); break;
                case "Resp_AppKill":
                    sz(desktop, "WaitToKillAppTimeout", "2000", "Fast app kill timeout");
                    sz(desktop, "HungAppTimeout",       "1000", "Fast hung app timeout"); break;
                case "Resp_ServiceKill":    sz(ctl, "WaitToKillServiceTimeout", "2000", "Fast service kill timeout"); break;
                case "Resp_AutoEndTasks":   sz(desktop, "AutoEndTasks", "1", "Auto end tasks on shutdown"); break;
                case "Resp_PlatformTick":
                    runCommand("bcdedit /set useplatformtick yes", "Platform tick");
                    runCommand("bcdedit /set useplatformclock no", "Disable platform clock (HPET)"); break;
                case "Resp_VerboseStatus":  dw(lmCv + @"\Policies\System", "verbosestatus", 1, "Verbose boot/shutdown status messages"); break;
                case "Resp_WinTips":        dw(contentDelivery, "SoftLandingEnabled", 0, "Disable Windows Tips"); break;
                case "Resp_SuggestedContent":
                    foreach (var id in new[] { "338389", "338393", "353694", "353696" })
                        dw(contentDelivery, $"SubscribedContent-{id}Enabled", 0, $"Disable suggested content ({id})");
                    dw(explorerAdvanced, "Start_IrisRecommendations", 0, "Disable Start menu recommendations"); break;
                case "Resp_NagScreens":
                    dw(cuCv + @"\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, "Disable 'finish setting up your device' nag");
                    dw(explorerAdvanced, "ShowSyncProviderNotifications", 0, "Disable File Explorer sync-provider ads");
                    dw(explorerAdvanced, "Start_AccountNotifications",    0, "Disable Start account notifications"); break;
                case "Resp_NoForcedRestart": dw(lmPol + @"\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, "No automatic restart while signed in"); break;
                case "Resp_ThisPC":         dw(explorerAdvanced, "LaunchTo", 1, "File Explorer opens to This PC"); break;
                case "Resp_TaskbarClean":
                    dw(cuCv + @"\Search", "SearchboxTaskbarMode", 0, "Hide taskbar search box");
                    dw(explorerAdvanced, "ShowTaskViewButton", 0, "Hide Task View button"); break;
                case "Resp_ClassicContext": sz(classicMenuKey, "", "", "Classic right-click menu"); break;
                case "Resp_EndTask":        dw(explorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, "Add End Task to taskbar menu"); break;

                // ── GAMING ────────────────────────────────────────────
                case "Game_HAGS":           dw(ctl + @"\GraphicsDrivers", "HwSchMode", 2, "Enable HAGS"); break;
                case "Game_GameMode":
                    dw(cuSw + @"\GameBar", "AllowAutoGameMode",   1, "Enable Game Mode");
                    dw(cuSw + @"\GameBar", "AutoGameModeEnabled", 1, "Enable Auto Game Mode"); break;
                case "Game_MouseAccel":
                    sz(cuPanel + @"\Mouse", "MouseSpeed",      "0", "Disable mouse acceleration");
                    sz(cuPanel + @"\Mouse", "MouseThreshold1", "0", "Mouse threshold 1");
                    sz(cuPanel + @"\Mouse", "MouseThreshold2", "0", "Mouse threshold 2"); break;
                case "Game_CPUPriority":    dw(ctl + @"\PriorityControl", "Win32PrioritySeparation", 38, "CPU foreground priority boost"); break;
                case "Game_DVR":
                    dw(cuCv + @"\GameDVR", "AppCaptureEnabled", 0, "Disable Game DVR capture");
                    dw(lmPol + @"\Windows\GameDVR", "AllowGameDVR", 0, "Disable Game DVR (policy)");
                    // Without these, the controller's Guide button still pops the 'ms-gamingoverlay' prompt once the Xbox apps are gone
                    dw(cu + @"\System\GameConfigStore", "GameDVR_Enabled", 0, "Disable Game DVR (GameConfigStore)");
                    dw(cuSw + @"\GameBar", "UseNexusForGameBarEnabled", 0, "Disable Game Bar controller shortcut");
                    dw(cuSw + @"\GameBar", "ShowStartupPanel",          0, "Disable Game Bar startup panel"); break;
                case "Game_FSO":
                    dw(cu + @"\System\GameConfigStore", "GameDVR_FSEBehaviorMode",          2, "Disable FSO globally");
                    dw(cu + @"\System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, "Honor FSO setting"); break;
                case "Game_GPUPower":
                    dw(gpuClassKey, "PerfLevelSrc", 0x3322, "GPU: Prefer Maximum Performance");
                    // "Turn off display after" = never while plugged in. Backed up, so Undo gives the old timeout back
                    if (activeSchemeGuid() is string scheme) applyPowerPart(displayNeverOffAc, scheme);
                    else report("GPU power: keep display awake", false, "Could not read active power scheme");
                    break;
                case "Game_WindowedOpt":    applyWindowedOptimisations(); break;
                case "Game_NvidiaTelemetry":
                    foreach (var s in nvidiaServices) disableService(s);
                    foreach (var t in nvidiaTasks) setTask(t, false); break;
                case "Game_StickyKeys":
                    // Keeps the accessibility features usable from Settings — only the 5×Shift / hold-key hotkeys are turned off
                    sz(cuPanel + @"\Accessibility\StickyKeys",        "Flags", "506", "Disable Sticky Keys hotkey");
                    sz(cuPanel + @"\Accessibility\ToggleKeys",        "Flags", "58",  "Disable Toggle Keys hotkey");
                    sz(cuPanel + @"\Accessibility\Keyboard Response", "Flags", "122", "Disable Filter Keys hotkey"); break;

                // ── NETWORK ───────────────────────────────────────────
                case "Net_Nagle":           modifyNagle(true); break;
                case "Net_RSS":             runCommand("netsh int tcp set global rss=enabled", "Enable RSS"); break;
                case "Net_TCPAutoTune":     runCommand("netsh int tcp set global autotuninglevel=normal", "TCP auto-tuning"); break;
                case "Net_Throttle":        dw(mmProfile, "NetworkThrottlingIndex", unchecked((int)0xffffffff), "Disable network throttling"); break;
                case "Net_MMResponsive":
                    dw(mmProfile, "SystemResponsiveness", 0, "Max multimedia responsiveness");
                    // MMCSS Games task — scheduling headroom for game threads; Pro Audio — lower DPC
                    // latency for the audio stack (Discord, game audio)
                    foreach (var task in new[] { "Games", "Pro Audio" })
                    {
                        string k = mmProfile + @"\Tasks\" + task, m = $"MMCSS {task}: ";
                        dw(k, "Affinity",            0,       m + "Affinity");
                        sz(k, "Background Only",     "False", m + "not background-only");
                        dw(k, "Clock Rate",          10000,   m + "Clock Rate");
                        dw(k, "GPU Priority",        8,       m + "GPU Priority");
                        dw(k, "Priority",            6,       m + "Priority");
                        sz(k, "Scheduling Category", "High",  m + "Scheduling Category");
                        sz(k, "SFIO Rate",           "High",  m + "SFIO Rate");
                    }
                    break;
                case "Net_LargeOffload":
                    // Some NIC drivers add inconsistent latency batching large sends
                    runPowerShell(lso("Disabled"), "Disable Large Send Offload (LSO) v2 IPv4/IPv6"); break;
                case "Net_TcpTimedWait":
                    // TIME_WAIT hold 240s → 30s
                    dw(svc + @"\Tcpip\Parameters", "TcpTimedWaitDelay", 30, "TCP TIME_WAIT delay → 30s"); break;
                case "Net_DoH":
                    dw(dnsParams, "EnableAutoDoh", 2, "Enable DoH");
                    foreach (var (ip, label) in new[] { ("1.1.1.1", "Cloudflare DoH template"), ("1.0.0.1", "Cloudflare DoH template (secondary)") })
                    {
                        string k = dnsParams + @"\DohWellKnownServers\" + ip;
                        dw(k, "DohFlags", 3, "Register " + ip);
                        sz(k, "DohTemplate", "https://cloudflare-dns.com/dns-query", label);
                    }
                    // The templates above only apply to servers they're registered for, so point the adapters at them too
                    applyDnsServers();
                    runCommand("ipconfig /flushdns", "Flush DNS cache");
                    break;
                case "Net_NicPower":        applyNicLatency(); break;
                case "Net_DeliveryOpt":     dw(lmPol + @"\Windows\DeliveryOptimization", "DODownloadMode", 0, "Delivery Optimization: HTTP only (no P2P)"); break;

                // ── SECURITY ──────────────────────────────────────────
                case "Sec_AutoRun":
                    sz(lm + @"\SOFTWARE\Microsoft\Windows NT\CurrentVersion\IniFileMapping\Autorun.inf",
                        "", "@SYS:DoesNotExist", "Block Autorun.inf");   // "" = the key's (Default) value
                    dw(cuCv + @"\Policies\Explorer", "NoDriveTypeAutoRun", 0xFF, "Disable AutoRun (user)");
                    dw(lmCv + @"\Policies\Explorer", "NoDriveTypeAutoRun", 0xFF, "Disable AutoRun (machine)"); break;
                case "Sec_RDP":
                    dw(ctl + @"\Terminal Server", "fDenyTSConnections", 1, "Disable RDP");
                    runCommand("netsh advfirewall firewall set rule group=\"Remote Desktop\" new enable=no 2>nul", "Block RDP firewall rule"); break;
                case "Sec_SMBv1":
                    runPowerShell("Set-SmbServerConfiguration -EnableSMB1Protocol $false -Force", "Disable SMBv1 server");
                    runPowerShell("Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart", "Remove SMBv1 feature"); break;
                case "Sec_NetBIOS":
                    runPowerShell("Get-WmiObject Win32_NetworkAdapterConfiguration | Where-Object { $_.TcpipNetbiosOptions -ne $null } | ForEach-Object { $_.SetTcpipNetbios(2) }",
                        "Disable NetBIOS over TCP/IP"); break;
                case "Sec_LLMNR":           dw(lmPol + @"\Windows NT\DNSClient", "EnableMulticast", 0, "Disable LLMNR"); break;
                case "Sec_Firewall":        applyFirewall(); break;
                case "Sec_DefenderPUA":
                    dw(defenderPolicy, "PUAProtection", 1, "Defender: block potentially unwanted apps");
                    dw(defenderPolicy + @"\Windows Defender Exploit Guard\Network Protection", "EnableNetworkProtection", 1, "Defender: Network Protection on"); break;
                case "Sec_PS2":             applyPowerShell2(); break;
                case "Sec_RemoteAssist":
                    dw(ctl + @"\Remote Assistance", "fAllowToGetHelp",   0, "Disable Remote Assistance invitations");
                    dw(ctl + @"\Remote Assistance", "fAllowFullControl", 0, "Disable Remote Assistance full control"); break;
                case "Sec_ShowExt":         dw(explorerAdvanced, "HideFileExt", 0, "Show file extensions"); break;
                case "Sec_Teredo":
                    foreach (var t in new[] { "Teredo_State", "6to4_State", "ISATAP_State" })
                        sz(transitionPolicy, t, "Disabled", "Disable IPv6 transition: " + t.Split('_')[0]);
                    break;
                case "Sec_Defender":
                    dw(lmPol + @"\Windows Defender", "DisableAntiSpyware", 0, "Ensure Defender not disabled");
                    dw(lmPol + @"\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 0, "Ensure Defender real-time ON");
                    runPowerShell("Set-MpPreference -DisableRealtimeMonitoring $false", "Enable Defender real-time"); break;

                // ── ADVANCED ──────────────────────────────────────────
                case "Adv_CoreParking":
                    // CPMINCORES 100 keeps every core unparked — parking causes micro-stutter on some desktops
                    runCommand(coreParking(100), "Disable CPU core parking"); break;
                case "Adv_MsiMode":
                    // No MessageNumberLimit override — forcing a value the device doesn't support can stop it initialising
                    setGpuInterrupt(msiSub, "MSISupported", 1, "Enable MSI mode for GPU", "Enable MSI mode for GPU"); break;
                case "Adv_IrqAffinity":
                    // DevicePolicy 5 = IrqPolicySpreadMessagesAcrossAllProcessors
                    setGpuInterrupt(affinitySub, "DevicePolicy", 5, "GPU IRQ: spread across all processors", "GPU IRQ affinity"); break;
                case "Adv_TscSync":         runCommand("bcdedit /set tscsyncpolicy legacy", "TSC sync policy → legacy"); break;
                case "Adv_X2Apic":          runCommand("bcdedit /set x2apicpolicy enable", "Enable x2APIC mode"); break;
                case "Adv_BoostMode":
                    // Only unhides the setting (Attributes=2) — doesn't set a boost value; backed up and undoable
                    dw(boostModeKey, "Attributes", 2, "Unhide Processor Performance Boost Mode"); break;

                case "Adv_VbsOff":
                    dw(deviceGuard, "EnableVirtualizationBasedSecurity", 0, "Turn off virtualization-based security");
                    dw(deviceGuard + @"\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, "Turn off Memory Integrity (HVCI)"); break;
                case "Adv_Mpo":             dw(dwmKey, "OverlayTestMode", 5, "Disable Multiplane Overlay"); break;

                // ── LAPTOP (registry-based; power-plan ones handled above) ─
                case "Lap_IndexOnBattery":  dw(lmPol + @"\Windows\Windows Search", "PreventIndexOnBattery", 1, "Pause search indexing on battery"); break;
                case "Lap_BackgroundApps":  dw(lmPol + @"\Windows\AppPrivacy", "LetAppsRunInBackground", 2, "Block Store apps running in background"); break;
                case "Lap_VoiceActivation":
                    dw(appPrivacy, "LetAppsActivateWithVoice",          2, "Block apps activating with voice");
                    dw(appPrivacy, "LetAppsActivateWithVoiceAboveLock", 2, "Block voice activation above the lock screen"); break;
                case "Lap_CrossDevice":
                    dw(lmPol + @"\Windows\System", "EnableCdp", 0, "Disable cross-device experiences (policy)");
                    dw(cuCv + @"\CDP", "CdpSessionUserAuthzPolicy",       0, "Share across devices: off");
                    dw(cuCv + @"\CDP", "NearShareChannelUserAuthzPolicy", 0, "Nearby sharing: off");
                    dw(cuCv + @"\CDP", "RomeSdkChannelUserAuthzPolicy",    0, "Cross-device channel: off"); break;
                case "Lap_SearchHighlights":
                    dw(cuCv + @"\SearchSettings", "IsDynamicSearchBoxEnabled", 0, "Disable Search Highlights");
                    dw(lmPol + @"\Windows\Windows Search", "EnableDynamicContentInWSB", 0, "Disable Search Highlights (policy)"); break;
                case "Lap_SettingsSync":
                    dw(lmPol + @"\Windows\SettingSync", "DisableSettingSync",             2, "Disable settings sync");
                    dw(lmPol + @"\Windows\SettingSync", "DisableSettingSyncUserOverride", 1, "Lock settings sync off"); break;
                case "Lap_MaintenanceWake":
                    dw(maintenanceKey, "WakeUp", 0, "Automatic Maintenance: don't wake the PC");
                    dw(lmPol + @"\Windows\ScheduledMaintenance", "WakeUp", 0, "Automatic Maintenance wake-up (policy)"); break;
                case "Lap_NicPower":        applyNicPower(); break;
                case "Lap_EdgeBackground":
                    dw(lmPol + @"\Edge", "StartupBoostEnabled",   0, "Disable Edge Startup Boost");
                    dw(lmPol + @"\Edge", "BackgroundModeEnabled", 0, "Stop Edge running after close"); break;
                case "Lap_Transparency":    dw(cuCv + @"\Themes\Personalize", "EnableTransparency", 0, "Disable transparency effects"); break;

                default: report($"Unknown key: {key}", false, "No handler"); break;
            }
        }
    }
}
