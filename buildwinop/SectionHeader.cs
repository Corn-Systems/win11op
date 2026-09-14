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
public class SectionHeader : Panel
    {
        private static int _sectionIndex = 0;
        public static void ResetIndex() => _sectionIndex = 0;

        public SectionHeader(string title, string emoji)
        {
            _sectionIndex++;
            string index = _sectionIndex.ToString("D2");

            Height    = 52;
            Margin    = new Padding(5, 20, 5, 4);
            BackColor = Color.Transparent;

            // "01 — CATEGORY" monospace label, website style
            var indexLbl = new Label
            {
                Text      = $"{index} — {title.ToUpper()}",
                Font      = new Font("Courier New", 7f, FontStyle.Bold),
                ForeColor = Theme.ACCENT,
                AutoSize  = true,
                Location  = new Point(6, 8),
                BackColor = Color.Transparent
            };

            var nameLbl = new Label
            {
                Text      = $"{emoji}  {title}",
                Font      = new Font("Courier New", 9f, FontStyle.Bold),
                ForeColor = Theme.TEXT_PRI,
                AutoSize  = true,
                Location  = new Point(4, 24),
                BackColor = Color.Transparent
            };

            Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER, 1);
                e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            };

            Controls.AddRange(new Control[] { indexLbl, nameLbl });

            ParentChanged += (s, e) => FitToParent();
            ParentChanged += (s, e) =>
            {
                if (Parent != null)
                    Parent.SizeChanged += (ps, pe) => FitToParent();
            };
        }

        private void FitToParent()
        {
            if (Parent == null) return;
            int w = Parent.ClientSize.Width
                  - Parent.Padding.Horizontal
                  - Margin.Horizontal
                  - SystemInformation.VerticalScrollBarWidth
                  - 2;
            if (w > 0) Width = w;
            Invalidate();
        }
    }

}
