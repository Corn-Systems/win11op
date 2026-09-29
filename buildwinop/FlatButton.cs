using System;
using System.Drawing;
using System.Windows.Forms;

namespace Win11Optimizer
{
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
}
