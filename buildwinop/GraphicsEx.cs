using System.Drawing;
using System.Drawing.Drawing2D;

namespace Win11Optimizer
{
    internal static class GraphicsEx
    {
        public static void FillRoundedRect(this Graphics g,
            Brush brush, int x, int y, int w, int h, int r)
        {
            int d = r * 2;
            var path = new GraphicsPath();
            path.AddArc(x,         y,         d, d, 180, 90);
            path.AddArc(x + w - d, y,         d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d,   0, 90);
            path.AddArc(x,         y + h - d, d, d,  90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }
}
