using System;
using Microsoft.Win32;

namespace Win11Optimizer
{
    public static class WinVersion
    {
        public static string DisplayName { get; private set; } = "Unknown";

        public static void Detect()
        {
            try
            {
                const string cv = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                int    build = int.TryParse(Registry.GetValue(cv, "CurrentBuildNumber", "0")?.ToString(), out int b) ? b : 0;
                string dv    = Registry.GetValue(cv, "DisplayVersion", "")?.ToString();
                DisplayName  = $"{(build >= 22000 ? "Windows 11" : "Windows 10")}{(string.IsNullOrWhiteSpace(dv) ? "" : " " + dv)} (Build {build})";
            }
            catch (Exception ex) { DisplayName = "Unknown Windows"; SessionLog.Write("WINVER", ex); }
        }
    }
}
