using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper — byte-identical across Corn Systems repos

namespace Win11Optimizer
{
    public static class WinVersion
    {
        public static int    Build       { get; private set; }
        public static string DisplayName { get; private set; } = "Unknown";
        public static bool   IsWin11     => Build >= 22000;
        public static bool   IsWin10     => Build >= 10240 && Build < 22000;

        public static void Detect()
        {
            try
            {
                const string cv = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                string raw = Microsoft.Win32.Registry.GetValue(cv, "CurrentBuildNumber", "0")?.ToString() ?? "0";
                Build = int.TryParse(raw, out int b) ? b : 0;
                string dv      = Microsoft.Win32.Registry.GetValue(cv, "DisplayVersion", "")?.ToString() ?? "";
                string winName = Build >= 22000 ? "Windows 11" : "Windows 10";
                DisplayName    = string.IsNullOrWhiteSpace(dv)
                    ? $"{winName} (Build {Build})"
                    : $"{winName} {dv} (Build {Build})";
            }
            catch { Build = 0; DisplayName = "Unknown Windows"; }
        }
    }

}
