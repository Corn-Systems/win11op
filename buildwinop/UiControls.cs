using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Win11Optimizer
{
    // RichTextBox with dark-mode scrollbars, for the output log.
    public class DarkRichTextBox : RichTextBox
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string app, string id);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowTheme(Handle, "DarkMode_Explorer", null);
        }
    }

    public class FlatButton : Button
    {
        public FlatButton(string text, Color bg)
        {
            // Dark text on gold, light text on dark surfaces. (This used to test for the old lime
            // accent, so gold buttons fell through to light text, a border and a computed hover.)
            bool accent = bg.ToArgb() == Theme.ACCENT.ToArgb();
            Text      = text;
            BackColor = bg;
            ForeColor = accent ? Theme.ACCENT_TEXT : Theme.TEXT_PRI;
            FlatStyle = FlatStyle.Flat;
            Cursor    = Cursors.Hand;
            Font      = Theme.Ui(8.5f);
            FlatAppearance.BorderSize         = accent ? 0 : 1;
            FlatAppearance.BorderColor        = Theme.BORDER;
            FlatAppearance.MouseDownBackColor = bg;
            FlatAppearance.MouseOverBackColor = accent ? Theme.ACCENT_HOV
                : Color.FromArgb(Math.Min(bg.R + 15, 255), Math.Min(bg.G + 15, 255), Math.Min(bg.B + 15, 255));
        }
    }

    public class SectionHeader : Panel
    {
        private static int _index;
        private Control _host;
        public static void ResetIndex() => _index = 0;

        public SectionHeader(string title, string emoji)
        {
            Height    = 52;
            Margin    = new Padding(5, 20, 5, 4);
            BackColor = Color.Transparent;
            Controls.AddRange(new Control[]
            {
                // "01 — CATEGORY" monospace label, website style
                UiHelpers.Lbl($"{++_index:D2} — {title.ToUpper()}", Theme.Mono(7f, true), Theme.ACCENT, 6, 8),
                UiHelpers.Lbl($"{emoji}  {title}", Theme.Mono(9f, true), Theme.TEXT_PRI, 4, 24),
            });
            UiHelpers.BottomBorder(this);
        }

        // Tracks exactly one parent. This used to add a SizeChanged handler on every re-parent and
        // never remove it, so each grid rebuild leaked every header into the grid's event list.
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (_host != null) _host.SizeChanged -= FitToParent;
            if ((_host = Parent) != null) _host.SizeChanged += FitToParent;
            FitToParent(this, e);
        }

        private void FitToParent(object s, EventArgs e)
        {
            if (Parent == null) return;
            int w = Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal
                  - SystemInformation.VerticalScrollBarWidth - 2;
            if (w > 0) Width = w;
            Invalidate();
        }
    }

    internal static class GraphicsEx
    {
        public static void FillRoundedRect(this Graphics g, Brush brush, int x, int y, int w, int h, int r)
        {
            int d = r * 2;
            using var path = new GraphicsPath();
            path.AddArc(x,         y,         d, d, 180, 90);
            path.AddArc(x + w - d, y,         d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d,   0, 90);
            path.AddArc(x,         y + h - d, d, d,  90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }
}
