using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CornSystems
{
    // Dark, palette-coloured title bar for WinForms windows.
    // Shared across Corn Systems repos (like Dpi.cs) — copy changes to every repo.
    //
    //   Windows 10 1809–1909 : immersive dark mode via the undocumented attribute 19
    //   Windows 10 2004+     : immersive dark mode via DWMWA_USE_IMMERSIVE_DARK_MODE (20)
    //   Windows 11 22000+    : additionally caption / text / border colours (34–36), so the
    //                          title bar can match the app's top bar exactly
    //
    // Every call is best-effort: on older Windows the attributes simply return an error.
    public static class DarkTitleBar
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE         = 20;
        private const int DWMWA_BORDER_COLOR                    = 34;
        private const int DWMWA_CAPTION_COLOR                   = 35;
        private const int DWMWA_TEXT_COLOR                      = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Call once from the form's constructor. Re-applies automatically if WinForms recreates
        // the window handle (changing ShowInTaskbar, RightToLeft, etc. does that).
        public static void Apply(Form form, Color caption, Color text, Color border)
        {
            if (form == null) return;
            if (form.IsHandleCreated) Set(form.Handle, caption, text, border);
            form.HandleCreated += (s, e) => Set(form.Handle, caption, text, border);
        }

        private static void Set(IntPtr hwnd, Color caption, Color text, Color border)
        {
            var os = Environment.OSVersion.Version;
            if (hwnd == IntPtr.Zero || os.Major < 10 || os.Build < 17763) return;
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE20H1, ref on, sizeof(int));

                if (os.Build >= 22000)
                {
                    int cap = ColorRef(caption), txt = ColorRef(text), brd = ColorRef(border);
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref cap, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR,    ref txt, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR,  ref brd, sizeof(int));
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[DarkTitleBar] {ex.Message}"); }   // dwmapi missing / blocked
        }

        // COLORREF is 0x00BBGGRR.
        private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
    }
}
