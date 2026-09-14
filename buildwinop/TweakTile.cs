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
    public enum AppliedSource { None, AppliedByApp, DetectedOnSystem }
    public enum TileStatus    { None, Running, Done }

    public class TweakTile : Panel
    {
        private bool         _checked;
        private AppliedSource _appliedSource = AppliedSource.None;
        private TileStatus   _status      = TileStatus.None;
        private string       _statusText  = "";
        private Color        _statusColor = Theme.TEXT_SEC;

        public TweakEntry Entry { get; }
        public event EventHandler CheckedChanged;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsChecked
        {
            get => _checked;
            set
            {
                _checked  = value;
                // Subtle gold-tinted warm surface when selected — matches website featured card
                BackColor = value ? Color.FromArgb(22, 19, 38) : Theme.CARD;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private static readonly Dictionary<string, Color> CatAccent = new()
        {
            // Each category gets a distinct color — website accent is lime,
            // but tiles need contrast so we use a reduced palette.
            ["Performance"]    = Color.FromArgb(204, 255,   0),  // lime (primary accent)
            ["Privacy"]        = Color.FromArgb(139, 92,  246),  // violet
            ["Responsiveness"] = Color.FromArgb(  6, 182, 212),  // cyan
            ["Gaming"]         = Color.FromArgb( 34, 197,  94),  // green
            ["Network"]        = Color.FromArgb(251, 191,  36),  // amber
            ["Bloatware"]      = Color.FromArgb(239,  68,  68),  // red
            ["Advanced"]       = Color.FromArgb(249, 115,  22),  // orange
            ["Security"]       = Color.FromArgb( 20, 184, 166),  // teal
        };

        public TweakTile(TweakEntry entry)
        {
            Entry     = entry;
            Size      = new Size(Dpi.S(230), Dpi.S(130));
            BackColor = Theme.CARD;
            Margin    = new Padding(Dpi.S(5));
            Cursor    = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            Color accent = CatAccent.TryGetValue(entry.Category, out var ac) ? ac : Theme.ACCENT;

            Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                using var borderPen = new Pen(_checked ? Color.FromArgb(120, accent.R, accent.G, accent.B) : Theme.BORDER, 1f);
                g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

                using var accentBr = new SolidBrush(_checked ? accent : Color.FromArgb(60, accent.R, accent.G, accent.B));
                g.FillRectangle(accentBr, 0, 0, 2, Height);

                if (_checked)
                {
                    // Checkmark indicator — gold filled circle with dark check
                    g.FillEllipse(accentBr, Width - 20, 8, 12, 12);
                    using var wp = new Pen(Theme.ACCENT_TEXT, 1.5f);
                    g.DrawLines(wp, new[]
                    {
                        new Point(Width - 18, 14),
                        new Point(Width - 15, 17),
                        new Point(Width - 11, 11)
                    });
                }

                // "Already applied" indicator — bottom-left dot + label
                if (_appliedSource != AppliedSource.None && _status == TileStatus.None)
                {
                    bool detected = _appliedSource == AppliedSource.DetectedOnSystem;
                    Color dotColor = detected ? Theme.SKY_PURPLE : Theme.SUCCESS;
                    string label   = "applied";
                    using var dotBr  = new SolidBrush(dotColor);
                    using var txtFnt = new Font("Courier New", 6.5f);
                    using var txtBr  = new SolidBrush(Color.FromArgb(80, dotColor.R, dotColor.G, dotColor.B));
                    g.FillEllipse(dotBr, 10, Height - 16, 6, 6);
                    g.DrawString(label, txtFnt, txtBr, new PointF(20, Height - 18));
                }

                if (!string.IsNullOrEmpty(_statusText))
                {
                    using var sf = new Font("Courier New", 7f);
                    using var sb = new SolidBrush(_statusColor);
                    g.DrawString(_statusText, sf, sb, new PointF(12, Height - 18));
                }
            };

            var iconLbl = new Label
            {
                Text      = entry.Icon,
                Font      = new Font("Segoe UI Emoji", 18f),
                AutoSize  = false,
                Size      = new Size(Dpi.S(40), Dpi.S(40)),
                Location  = new Point(Dpi.S(8),  Dpi.S(8)),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };

            var nameLbl = new Label
            {
                Text         = entry.Name,
                Font         = new Font("Courier New", 7.5f, FontStyle.Bold),
                ForeColor    = Theme.TEXT_PRI,
                AutoSize     = false,
                Size         = new Size(Dpi.S(170), Dpi.S(40)),
                Location     = new Point(Dpi.S(54), Dpi.S(8)),
                BackColor    = Color.Transparent,
                AutoEllipsis = true,
                UseMnemonic  = false
            };

            var descLbl = new Label
            {
                Text      = entry.Description,
                Font      = new Font("Segoe UI", 7.5f),
                ForeColor = Theme.TEXT_DIM,
                AutoSize  = false,
                Size      = new Size(Dpi.S(218), Dpi.S(38)),
                Location  = new Point(Dpi.S(10),  Dpi.S(74)),
                BackColor = Color.Transparent
            };

            Color badgeBg = Color.FromArgb(30, accent.R, accent.G, accent.B);
            Color badgeFg = Color.FromArgb(180, accent.R, accent.G, accent.B);
            var catBadge = new Label
            {
                Text      = entry.IsAdvanced ? "⚠ ADV" : entry.Category.ToUpper(),
                Font      = new Font("Courier New", 6.5f, FontStyle.Bold),
                ForeColor = badgeFg,
                BackColor = badgeBg,
                AutoSize  = true,
                Location  = new Point(Dpi.S(10), Dpi.S(52)),
                Padding   = new Padding(3, 1, 3, 1)
            };

            Controls.AddRange(new Control[] { iconLbl, nameLbl, descLbl, catBadge });

            void Toggle(object s, EventArgs ev) => IsChecked = !_checked;
            base.Click    += Toggle;
            iconLbl.Click  += Toggle;
            nameLbl.Click  += Toggle;
            descLbl.Click  += Toggle;
            catBadge.Click += Toggle;

            MouseEnter += (s, ev) => { if (!_checked) BackColor = Theme.SURFACE; };
            MouseLeave += (s, ev) => { if (!_checked) BackColor = Theme.CARD; };
        }

        public void SetApplied(AppliedSource source)
        {
            _appliedSource = source;
            Invalidate();
        }

        public void SetStatus(TileStatus status)
        {
            _status = status;
            switch (status)
            {
                case TileStatus.Running:
                    _statusText  = "⏳ Running...";
                    _statusColor = Theme.WARNING;
                    break;
                case TileStatus.Done:
                    _statusText  = "✔ Done";
                    _statusColor = Theme.SUCCESS;
                    break;
                default:
                    _statusText = "";
                    break;
            }
            Invalidate();
        }
    }

}
