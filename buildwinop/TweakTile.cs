using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper — byte-identical across Corn Systems repos

namespace Win11Optimizer
{
    public enum AppliedSource { None, AppliedByApp, DetectedOnSystem }
    public enum TileStatus    { None, Running, Done }

    public class TweakTile : Panel
    {
        private bool          _checked;
        private AppliedSource _applied;
        private TileStatus    _status;
        private string        _statusText = "";
        private Color         _statusColor;

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

        // Each category gets a distinct accent so tiles read apart at a glance
        private static readonly Dictionary<string, Color> CatAccent = new()
        {
            ["Performance"]    = Color.FromArgb(204, 255,   0),  // lime
            ["Privacy"]        = Color.FromArgb(139,  92, 246),  // violet
            ["Responsiveness"] = Color.FromArgb(  6, 182, 212),  // cyan
            ["Gaming"]         = Color.FromArgb( 34, 197,  94),  // green
            ["Network"]        = Color.FromArgb(251, 191,  36),  // amber
            ["Bloatware"]      = Color.FromArgb(239,  68,  68),  // red
            ["Advanced"]       = Color.FromArgb(249, 115,  22),  // orange
            ["Security"]       = Color.FromArgb( 20, 184, 166),  // teal
            ["Laptop"]         = Color.FromArgb( 56, 189, 248),  // sky blue (matches Laptop preset)
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

            Color ac = CatAccent.TryGetValue(entry.Category, out var c) ? c : Theme.ACCENT;

            Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var border = new Pen(_checked ? Color.FromArgb(120, ac) : Theme.BORDER, 1f);
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                using var accent = new SolidBrush(_checked ? ac : Color.FromArgb(60, ac));
                g.FillRectangle(accent, 0, 0, 2, Height);

                if (_checked)
                {
                    // Checkmark indicator — filled accent circle with a dark check
                    g.FillEllipse(accent, Width - 20, 8, 12, 12);
                    using var tick = new Pen(Theme.ACCENT_TEXT, 1.5f);
                    g.DrawLines(tick, new[] { new Point(Width - 18, 14), new Point(Width - 15, 17), new Point(Width - 11, 11) });
                }

                // "Already applied" indicator — bottom-left dot + label
                if (_applied != AppliedSource.None && _status == TileStatus.None)
                {
                    Color dot = _applied == AppliedSource.DetectedOnSystem ? Theme.SKY_PURPLE : Theme.SUCCESS;
                    using var dotBr = new SolidBrush(dot);
                    using var txtBr = new SolidBrush(Color.FromArgb(80, dot));
                    g.FillEllipse(dotBr, 10, Height - 16, 6, 6);
                    g.DrawString("applied", Theme.Mono(6.5f), txtBr, 20, Height - 18);
                }

                if (_statusText.Length > 0)
                {
                    using var sb = new SolidBrush(_statusColor);
                    g.DrawString(_statusText, Theme.Mono(7f), sb, 12, Height - 18);
                }
            };

            Controls.AddRange(new Control[]
            {
                new Label
                {
                    Text = entry.Icon, Font = Theme.F("Segoe UI Emoji", 18f), BackColor = Color.Transparent,
                    Size = new Size(Dpi.S(40), Dpi.S(40)), Location = new Point(Dpi.S(8), Dpi.S(8)),
                    TextAlign = ContentAlignment.MiddleCenter
                },
                new Label
                {
                    Text = entry.Name, Font = Theme.Mono(7.5f, true), ForeColor = Theme.TEXT_PRI, BackColor = Color.Transparent,
                    Size = new Size(Dpi.S(170), Dpi.S(40)), Location = new Point(Dpi.S(54), Dpi.S(8)),
                    AutoEllipsis = true, UseMnemonic = false
                },
                new Label
                {
                    Text = entry.Description, Font = Theme.Ui(7.5f), ForeColor = Theme.TEXT_DIM, BackColor = Color.Transparent,
                    Size = new Size(Dpi.S(218), Dpi.S(38)), Location = new Point(Dpi.S(10), Dpi.S(74))
                },
                new Label
                {
                    Text = entry.IsAdvanced ? "⚠ ADV" : entry.Category.ToUpper(), Font = Theme.Mono(6.5f, true),
                    ForeColor = Color.FromArgb(180, ac), BackColor = Color.FromArgb(30, ac),
                    AutoSize = true, Location = new Point(Dpi.S(10), Dpi.S(52)), Padding = new Padding(3, 1, 3, 1)
                },
            });

            void Toggle(object s, EventArgs e) => IsChecked = !_checked;
            Click += Toggle;
            foreach (Control child in Controls) child.Click += Toggle;
            MouseEnter += (s, e) => { if (!_checked) BackColor = Theme.SURFACE; };
            MouseLeave += (s, e) => { if (!_checked) BackColor = Theme.CARD; };
        }

        public void SetApplied(AppliedSource source)
        {
            _applied = source;
            Invalidate();
        }

        public void SetStatus(TileStatus status)
        {
            _status = status;
            (_statusText, _statusColor) = status switch
            {
                TileStatus.Running => ("⏳ Running...", Theme.WARNING),
                TileStatus.Done    => ("✔ Done", Theme.SUCCESS),
                _                  => ("", _statusColor)
            };
            Invalidate();
        }
    }
}
