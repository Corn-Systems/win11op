using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class ServicesTab : Panel
    {
        private Panel      _toolbar;
        private Label      _summaryLabel;
        private FlatButton _scanBtn;
        private Panel      _scrollPanel;
        private Panel      _innerPanel;

        private List<ManagedService>    _services = new();
        private List<ManagedServiceRow> _rows     = new();
        private bool _loaded = false;

        public ServicesTab()
        {
            BackColor = Theme.BG;
            Dock      = DockStyle.Fill;
            Visible   = false;

            BuildToolbar();
            BuildScrollArea();

            Controls.Add(_scrollPanel);
            Controls.Add(_toolbar);
        }

        public void Activate()
        {
            if (!_loaded) { _loaded = true; _ = ScanAsync(); }
        }

        private void BuildToolbar()
        {
            _toolbar = new Panel { BackColor = Theme.SURFACE, Height = 54, Dock = DockStyle.Top };
            _toolbar.Paint += (s, e) =>
            {
                using var p = new Pen(Theme.BORDER);
                e.Graphics.DrawLine(p, 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
            };

            var title = new Label
            {
                Text      = "// SERVICES",
                Font      = new Font("Courier New", 9.5f, FontStyle.Bold),
                ForeColor = Theme.TEXT_PRI,
                AutoSize  = true,
                Location  = new Point(16, 15)
            };

            _summaryLabel = new Label
            {
                Text      = "",
                Font      = new Font("Courier New", 7.5f),
                ForeColor = Theme.TEXT_DIM,
                AutoSize  = true,
                Location  = new Point(150, 18)
            };

            _scanBtn = new FlatButton("↺ Rescan", Theme.SURFACE2)
                { Size = new Size(96, 28), Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.TEXT_SEC };
            _scanBtn.Click += async (s, e) => await ScanAsync();

            _toolbar.SizeChanged += (s, e) =>
                _scanBtn.Location = new Point(_toolbar.Width - _scanBtn.Width - 12, 13);

            _toolbar.Controls.AddRange(new Control[] { title, _summaryLabel, _scanBtn });
        }

        private void BuildScrollArea()
        {
            _scrollPanel = new Panel
            {
                BackColor  = Theme.BG,
                Dock       = DockStyle.Fill,
                AutoScroll = true,
                Padding    = new Padding(16, 10, 16, 10)
            };

            _innerPanel = new Panel { BackColor = Theme.BG, AutoSize = true, Location = new Point(0, 0) };

            var infoLbl = new Label
            {
                Text      = "⚠  Only genuinely optional services are listed. Disabling records the original startup type so Restore puts back exactly what you had.",
                Font      = new Font("Courier New", 7.5f),
                ForeColor = Theme.WARNING,
                AutoSize  = true,
                BackColor = Color.Transparent,
                Location  = new Point(0, 0)
            };
            _innerPanel.Controls.Add(infoLbl);

            _scrollPanel.Controls.Add(_innerPanel);
            _scrollPanel.Resize += (s, e) =>
            {
                _innerPanel.Width = _scrollPanel.ClientSize.Width - _scrollPanel.Padding.Horizontal;
                ReflowRows();
            };
        }

        private async Task ScanAsync()
        {
            _scanBtn.Enabled   = false;
            _summaryLabel.Text = "Scanning services...";

            _services = await Task.Run(() => ServicesManager.LoadAll());

            RenderRows();
            _scanBtn.Enabled = true;
            UpdateSummary();
        }

        private void RenderRows()
        {
            foreach (var row in _rows) _innerPanel.Controls.Remove(row);
            _rows.Clear();

            foreach (var svc in _services)
            {
                var row = new ManagedServiceRow(svc);
                row.StateChanged += (s, e) => UpdateSummary();
                _rows.Add(row);
                _innerPanel.Controls.Add(row);
            }

            _innerPanel.Width = Math.Max(100, _scrollPanel.ClientSize.Width - _scrollPanel.Padding.Horizontal);
            ReflowRows();
        }

        private void ReflowRows()
        {
            if (_innerPanel.Width < 10) return;
            int y = 30; // below the info line
            foreach (var row in _rows)
            {
                row.Width    = _innerPanel.Width;
                row.Location = new Point(0, y);
                y += row.Height + 2;
            }
            _innerPanel.Height = y;
        }

        private void UpdateSummary()
        {
            int present  = _services.Count(s => s.Exists);
            int running  = _services.Count(s => s.Exists && s.IsRunning);
            int disabled = _services.Count(s => s.Exists && s.IsDisabled);
            _summaryLabel.Text =
                $"{present} services  ·  {running} running  ·  {disabled} disabled";
        }
    }

    public class ManagedServiceRow : Panel
    {
        public ManagedService Service { get; }
        public event EventHandler StateChanged;

        private Label      _nameLbl;
        private Label      _descLbl;
        private Label      _stateBadge;
        private Label      _startBadge;
        private Label      _riskBadge;
        private FlatButton _actionBtn;

        public ManagedServiceRow(ManagedService svc)
        {
            Service   = svc;
            Height    = 62;
            BackColor = Theme.CARD;

            _nameLbl = new Label
            {
                Text         = svc.Exists ? svc.DisplayName : $"{svc.DisplayName}  (not installed)",
                Font         = new Font("Courier New", 8.5f, FontStyle.Bold),
                ForeColor    = svc.Exists ? Theme.TEXT_PRI : Theme.TEXT_DIM,
                AutoSize     = false,
                Height       = 18,
                Location     = new Point(14, 8),
                BackColor    = Color.Transparent,
                AutoEllipsis = true
            };

            _descLbl = new Label
            {
                Text         = svc.Description,
                Font         = new Font("Segoe UI", 7.5f),
                ForeColor    = Theme.TEXT_DIM,
                AutoSize     = false,
                Height       = 30,
                Location     = new Point(14, 27),
                BackColor    = Color.Transparent,
                AutoEllipsis = true
            };

            _stateBadge = MakeBadge("", Color.Transparent, Theme.TEXT_DIM);
            _startBadge = MakeBadge("", Color.Transparent, Theme.TEXT_DIM);

            Color riskColor = svc.RiskLevel == "Caution" ? Theme.WARNING : Theme.SUCCESS;
            _riskBadge = MakeBadge(svc.RiskLevel == "Caution" ? "⚠ Caution" : "✔ Safe",
                Color.FromArgb(30, riskColor.R, riskColor.G, riskColor.B), riskColor);

            _actionBtn = new FlatButton("", Theme.SURFACE2)
            {
                Size    = new Size(110, 28),
                Font    = new Font("Courier New", 7.5f, FontStyle.Bold),
                Visible = svc.Exists
            };
            _actionBtn.Click += async (s, e) => await ToggleAsync();

            Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                Color stripe = !Service.Exists   ? Theme.BORDER
                             : Service.IsDisabled ? Theme.TEXT_DIM
                             : riskColor;
                using var br = new SolidBrush(stripe);
                e.Graphics.FillRectangle(br, 0, 0, 3, Height);
            };

            SizeChanged += (s, e) => LayoutRow();

            Controls.AddRange(new Control[]
                { _nameLbl, _descLbl, _stateBadge, _startBadge, _riskBadge, _actionBtn });

            RefreshVisuals();
            LayoutRow();
        }

        private async Task ToggleAsync()
        {
            _actionBtn.Enabled = false;
            bool   disabling = !Service.IsDisabled;
            string error     = null;

            bool ok = await Task.Run(() => disabling
                ? ServicesManager.Disable(Service, out error)
                : ServicesManager.Restore(Service, out error));

            if (!ok)
                MessageBox.Show(
                    $"{(disabling ? "Disable" : "Restore")} failed for {Service.DisplayName}:\n\n{error}",
                    "Service Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            RefreshVisuals();
            _actionBtn.Enabled = true;
            Invalidate();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RefreshVisuals()
        {
            if (!Service.Exists)
            {
                _stateBadge.Visible = _startBadge.Visible = false;
                return;
            }

            _stateBadge.Text      = Service.IsRunning ? "● Running" : "○ Stopped";
            _stateBadge.ForeColor = Service.IsRunning ? Theme.SUCCESS : Theme.TEXT_DIM;
            _stateBadge.BackColor = Color.FromArgb(30,
                _stateBadge.ForeColor.R, _stateBadge.ForeColor.G, _stateBadge.ForeColor.B);

            Color startCol = Service.IsDisabled ? Theme.METEOR : Theme.ACCENT;
            _startBadge.Text      = Service.StartTypeLabel;
            _startBadge.ForeColor = startCol;
            _startBadge.BackColor = Color.FromArgb(30, startCol.R, startCol.G, startCol.B);

            if (Service.IsDisabled)
            {
                _actionBtn.Text      = "↩ Restore";
                _actionBtn.BackColor = Theme.SURFACE2;
                _actionBtn.ForeColor = Theme.TEXT_SEC;
            }
            else
            {
                _actionBtn.Text      = "✘ Disable";
                _actionBtn.BackColor = Theme.METEOR;
                _actionBtn.ForeColor = Color.White;
            }

            LayoutRow();
        }

        private void LayoutRow()
        {
            int r = Width - 10;
            _actionBtn.Location = new Point(r - _actionBtn.Width, (Height - _actionBtn.Height) / 2);
            r -= _actionBtn.Width + 10;
            _riskBadge.Location  = new Point(r - _riskBadge.Width, (Height - _riskBadge.Height) / 2 - 12);
            _startBadge.Location = new Point(r - _startBadge.Width, (Height - _startBadge.Height) / 2 + 12);
            int badgeW = Math.Max(_riskBadge.Width, _startBadge.Width);
            r -= badgeW + 8;
            _stateBadge.Location = new Point(r - _stateBadge.Width, (Height - _stateBadge.Height) / 2);
            r -= _stateBadge.Width + 8;

            int labelW     = Math.Max(60, r - 14);
            _nameLbl.Width = labelW;
            _descLbl.Width = labelW;
        }

        private static Label MakeBadge(string text, Color bg, Color fg) => new Label
        {
            Text      = text,
            Font      = new Font("Courier New", 6.5f),
            ForeColor = fg,
            BackColor = bg,
            AutoSize  = true,
            Padding   = new Padding(4, 2, 4, 2)
        };
    }
}