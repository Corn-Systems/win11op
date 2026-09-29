using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Win11Optimizer
{
    // Small construction/layout helpers shared by the main form and the list tabs.
    public static class UiHelpers
    {
        // Transparent label at (x, y). h = 0 → AutoSize; otherwise a fixed height (the caller's
        // layout sets the width) with an ellipsis on overflow. Mnemonics off so "&" renders.
        public static Label Lbl(string text, Font font, Color fg, int x, int y, int h = 0)
        {
            var l = new Label
            {
                Text = text, Font = font, ForeColor = fg, BackColor = Color.Transparent,
                Location = new Point(x, y), AutoSize = h == 0, UseMnemonic = false
            };
            if (h > 0) { l.Height = h; l.AutoEllipsis = true; }
            return l;
        }

        // Small pill-style label used for size/status/risk badges on list rows.
        public static Label MakeBadge(string text, Color bg, Color fg) => new()
        {
            Text      = text,
            Font      = Theme.Mono(6.5f),
            ForeColor = fg,
            BackColor = bg,
            AutoSize  = true,
            Padding   = new Padding(4, 2, 4, 2)
        };

        // Badge tinted from one colour: faint fill, full-strength text.
        public static Label Badge(string text, Color c) => MakeBadge(text, Color.FromArgb(30, c), c);

        public static void Tint(Label badge, string text, Color c)
        {
            badge.Text      = text;
            badge.ForeColor = c;
            badge.BackColor = Color.FromArgb(30, c);
        }

        // 1px BORDER line along a control's bottom edge.
        public static void BottomBorder(Control c) => c.Paint += (s, e) =>
        {
            using var p = new Pen(Theme.BORDER);
            e.Graphics.DrawLine(p, 0, c.Height - 1, c.Width, c.Height - 1);
        };

        // Controls.Clear() only detaches — the children and their window handles live on until
        // the GC finds them. Views that rebuild dispose what they replace.
        public static void ClearAndDispose(Control parent)
        {
            var old = parent.Controls.Cast<Control>().ToArray();
            parent.Controls.Clear();
            foreach (var c in old) c.Dispose();
        }

        // Lays out toolbar buttons right-to-left from the container's right edge.
        // Pass buttons in visual right-to-left order with the gap (px) to leave before the
        // next button to its left.
        public static void LayoutButtonsRightToLeft(Control container, int y,
            params (Control control, int gapAfter)[] buttons)
        {
            int r = container.Width - 12;
            foreach (var (control, gapAfter) in buttons)
            {
                control.Location = new Point(r - control.Width, y);
                r -= control.Width + gapAfter;
            }
        }

        // Stacks rows vertically inside innerPanel starting at startY, with `spacing` px
        // between rows, and resizes innerPanel.Height to fit.
        public static void ReflowRowsVertically(Panel innerPanel, IEnumerable<Control> rows, int startY, int spacing = 2)
        {
            int y = startY;
            foreach (var row in rows)
            {
                row.Width    = innerPanel.Width;
                row.Location = new Point(0, y);
                y += row.Height + spacing;
            }
            innerPanel.Height = y;
        }
    }
}
