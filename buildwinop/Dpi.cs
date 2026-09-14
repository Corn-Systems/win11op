using System;
using System.Drawing;
using System.Windows.Forms;

// Shared Corn Systems DPI helper. This file is byte-identical across Corn Systems
// WinForms projects (CornDownloader, win11op, CornTools, CornWatch). Don't edit it
// in one repo without copying the change to the others — or link it via
// <Compile Include="..\..\shared\Dpi.cs" Link="Dpi.cs" /> once a shared folder exists.
namespace CornSystems
{
    public static class Dpi
    {
        private const float BASE_DPI = 96f;
        public static float Current { get; private set; } = BASE_DPI;
        public static float Scale   => Current / BASE_DPI;

        public static int   S(int pixels)   => (int)Math.Round(pixels * Scale);
        public static float S(float pixels) => pixels * Scale;
        public static Size  S(Size sz)      => new Size(S(sz.Width), S(sz.Height));
        public static Point S(Point pt)     => new Point(S(pt.X), S(pt.Y));

        public static void Update(Control ctrl)
        {
            try   { Current = ctrl.DeviceDpi; }
            catch { try { using var g = ctrl.CreateGraphics(); Current = g.DpiX; } catch { /* keep last known DPI */ } }
        }
        public static void Update(int newDpi) { if (newDpi > 0) Current = newDpi; }
    }
}
