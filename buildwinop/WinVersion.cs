using System;
using Microsoft.Win32;

namespace Win11Optimizer
{
    public static class winVersion
    {
        public static string displayName { get; private set; } = "Unknown";

        public static void detect()
        {
            try
            {
                const string cv = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                int    build = int.TryParse(Registry.GetValue(cv, "CurrentBuildNumber", "0")?.ToString(), out int b) ? b : 0;
                string dv    = Registry.GetValue(cv, "DisplayVersion", "")?.ToString();
                displayName  = $"{(build >= 22000 ? "Windows 11" : "Windows 10")}{(string.IsNullOrWhiteSpace(dv) ? "" : " " + dv)} (Build {build})";
            }
            catch (Exception ex) { displayName = "Unknown Windows"; sessionLog.write("WINVER", ex); }
        }
    }
}
