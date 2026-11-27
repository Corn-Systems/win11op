using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper

namespace Win11Optimizer
{
    // Small construction/layout helpers shared by the main form and the list tabs.
    // Every position / size / gap argument here is in LOGICAL (96-dpi) pixels and is scaled with
    // Dpi.S() inside, so call sites read like the design numbers and stay right at any scaling.
    public static class uiHelpers
    {
        // Transparent label at (x, y). h = 0 → AutoSize; otherwise a fixed height (the caller's
        // layout sets the width) with an ellipsis on overflow. Mnemonics off so "&" renders.
        public static Label lbl(string text, Font font, Color fg, int x, int y, int h = 0)
        {
            var l = new Label
            {
                Text = text, Font = font, ForeColor = fg, BackColor = Color.Transparent,
                Location = new Point(Dpi.S(x), Dpi.S(y)), AutoSize = h == 0, UseMnemonic = false
            };
            if (h > 0) { l.Height = Dpi.S(h); l.AutoEllipsis = true; }
            return l;
        }

        // Small pill-style label used for size/status/risk badges on list rows.
        public static Label makeBadge(string text, Color bg, Color fg) => new()
        {
            Text      = text,
            Font      = theme.mono(6.5f),
            ForeColor = fg,
            BackColor = bg,
            AutoSize  = true,
            Padding   = new Padding(Dpi.S(4), Dpi.S(2), Dpi.S(4), Dpi.S(2))
        };

        // Multi-line notice that wraps to the width it's given instead of running off the right edge.
        public static Label banner(string text, Color fg)
        {
            var l = lbl(text, theme.mono(7.5f), fg, 0, 0);
            l.MaximumSize = new Size(Dpi.S(600), 0);
            return l;
        }

        // Badge tinted from one colour: faint fill, full-strength text.
        public static Label badge(string text, Color c) => makeBadge(text, Color.FromArgb(30, c), c);

        public static void tint(Label badge, string text, Color c)
        {
            badge.Text      = text;
            badge.ForeColor = c;
            badge.BackColor = Color.FromArgb(30, c);
        }

        // 1px BORDER line along a control's bottom edge.
        public static void bottomBorder(Control c) => c.Paint += (s, e) =>
        {
            using var p = new Pen(theme.border);
            e.Graphics.DrawLine(p, 0, c.Height - 1, c.Width, c.Height - 1);
        };

        // Controls.Clear() only detaches — the children and their window handles live on until
        // the GC finds them. Views that rebuild dispose what they replace.
        public static void clearAndDispose(Control parent)
        {
            var old = parent.Controls.Cast<Control>().ToArray();
            parent.Controls.Clear();
            foreach (var c in old) c.Dispose();
        }

        // Lays out toolbar buttons right-to-left from the container's right edge.
        // Pass buttons in visual right-to-left order with the gap to leave before the
        // next button to its left.
        public static void layoutButtonsRightToLeft(Control container, int y,
            params (Control control, int gapAfter)[] buttons)
        {
            int r = container.Width - Dpi.S(12);
            foreach (var (control, gapAfter) in buttons)
            {
                control.Location = new Point(r - control.Width, Dpi.S(y));
                r -= control.Width + Dpi.S(gapAfter);
            }
        }

        // Stacks rows vertically inside innerPanel starting at startY, with `spacing`
        // between rows, and resizes innerPanel.Height to fit.
        public static void reflowRowsVertically(Panel innerPanel, IEnumerable<Control> rows, int startY, int spacing = 2) =>
            stackRows(innerPanel, rows, Dpi.S(startY), spacing);

        // Same, but startYPx is already in device pixels (e.g. just below a wrapped banner).
        public static void stackRows(Panel innerPanel, IEnumerable<Control> rows, int startYPx, int spacing = 2)
        {
            int y = startYPx;
            foreach (var row in rows)
            {
                row.Width    = innerPanel.Width;
                row.Location = new Point(0, y);
                y += row.Height + Dpi.S(spacing);
            }
            innerPanel.Height = y;
        }
    }
}
