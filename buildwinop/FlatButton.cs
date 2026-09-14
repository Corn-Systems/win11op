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
    public class FlatButton : Button
    {
        public FlatButton(string text, Color bg)
        {
            Text      = text;
            BackColor = bg;
            // Black text on lime, white text on dark surfaces
            bool isAccent = bg.R > 150 && bg.G > 200 && bg.B < 50;
            ForeColor = isAccent ? Theme.ACCENT_TEXT : Theme.TEXT_PRI;
            FlatStyle = FlatStyle.Flat;
            Cursor    = Cursors.Hand;
            Font      = new Font("Segoe UI", 8.5f);
            FlatAppearance.BorderSize  = isAccent ? 0 : 1;
            FlatAppearance.BorderColor = Theme.BORDER;
            FlatAppearance.MouseOverBackColor = isAccent
                ? Theme.ACCENT_HOV
                : Color.FromArgb(Math.Min(bg.R + 15, 255),
                                 Math.Min(bg.G + 15, 255),
                                 Math.Min(bg.B + 15, 255));
            FlatAppearance.MouseDownBackColor = isAccent
                ? Color.FromArgb(180, 230, 0)
                : bg;
        }
    }

}
