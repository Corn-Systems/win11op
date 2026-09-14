using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class DiskCleanupTab : Panel
    {
        private Panel      _toolbar;
        private Label      _summaryLabel;
        private FlatButton _selAllBtn;
        private FlatButton _selNoneBtn;
        private FlatButton _scanBtn;
        private FlatButton _cleanBtn;
        private Panel      _scrollPanel;
        private Panel      _innerPanel;

        private List<CleanupCategory>    _categories = DiskCleanupManager.GetCategories();
        private List<CleanupCategoryRow> _rows       = new();
        private bool _loaded = false;

        public DiskCleanupTab()
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
            if (!_loaded) { _loaded = true; RenderRows(); _ = ScanAsync(); }
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
                Text      = "// DISK CLEANUP",
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
                Location  = new Point(190, 18)
            };

            _selAllBtn = new FlatButton("✔ All", Theme.SURFACE2)
                { Size = new Size(60, 28), Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.TEXT_SEC };
            _selAllBtn.Click += (s, e) => SetAllRows(true);

            _selNoneBtn = new FlatButton("✘ None", Theme.SURFACE2)
                { Size = new Size(66, 28), Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.TEXT_SEC };
            _selNoneBtn.Click += (s, e) => SetAllRows(false);

            _scanBtn = new FlatButton("↺ Scan", Theme.SURFACE2)
                { Size = new Size(84, 28), Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.TEXT_SEC };
            _scanBtn.Click += async (s, e) => await ScanAsync();

            _cleanBtn = new FlatButton("🧹 Clean Selected", Theme.ACCENT)
                { Size = new Size(150, 28), Font = new Font("Courier New", 7.5f, FontStyle.Bold) };
            _cleanBtn.Click += async (s, e) => await CleanSelectedAsync();

            _toolbar.SizeChanged += (s, e) => PositionToolbarButtons();
            _toolbar.Controls.AddRange(new Control[]
                { title, _summaryLabel, _selAllBtn, _selNoneBtn, _scanBtn, _cleanBtn });
        }

        private void PositionToolbarButtons() =>
            UiHelpers.LayoutButtonsRightToLeft(_toolbar, 13,
                (_cleanBtn, 6), (_scanBtn, 14), (_selNoneBtn, 6), (_selAllBtn, 0));

        private void SetAllRows(bool check)
        {
            foreach (var row in _rows) row.IsChecked = check;
            UpdateSummary();
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
            _scrollPanel.Controls.Add(_innerPanel);
            _scrollPanel.Resize += (s, e) =>
            {
                _innerPanel.Width = _scrollPanel.ClientSize.Width - _scrollPanel.Padding.Horizontal;
                ReflowRows();
            };
        }

        private void RenderRows()
        {
            _innerPanel.Controls.Clear();
            _rows.Clear();

            foreach (var cat in _categories)
            {
                var row = new CleanupCategoryRow(cat) { IsChecked = cat.DefaultOn };
                row.SelectionChanged += (s, e) => UpdateSummary();
                _rows.Add(row);
                _innerPanel.Controls.Add(row);
            }

            _innerPanel.Width = Math.Max(100, _scrollPanel.ClientSize.Width - _scrollPanel.Padding.Horizontal);
            ReflowRows();
            UpdateSummary();
        }

        private void ReflowRows()
        {
            if (_innerPanel.Width < 10) return;
            UiHelpers.ReflowRowsVertically(_innerPanel, _rows, 0);
        }

        private async Task ScanAsync()
        {
            _scanBtn.Enabled  = false;
            _cleanBtn.Enabled = false;
            _summaryLabel.Text = "Scanning...";

            await Task.Run(() => DiskCleanupManager.ScanSizes(_categories));

            foreach (var row in _rows) row.RefreshSize();
            _scanBtn.Enabled = true;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            long selectedBytes = _rows.Where(r => r.IsChecked).Sum(r => r.Category.SizeBytes);
            int  selectedCount = _rows.Count(r => r.IsChecked);
            _summaryLabel.Text = $"{selectedCount} selected  ·  ~{SizeFormat.Bytes(selectedBytes)} reclaimable";
            _cleanBtn.Enabled  = selectedCount > 0;
        }

        private async Task CleanSelectedAsync()
        {
            var selected = _rows.Where(r => r.IsChecked).Select(r => r.Category).ToList();
            if (selected.Count == 0) return;

            bool hasCaution = selected.Any(c => c.RiskLevel == "Caution");
            long totalBytes = selected.Sum(c => c.SizeBytes);

            string warnExtra = hasCaution
                ? "\n\nOne or more selected items are marked Caution (e.g. Recycle Bin, Windows.old, Prefetch, Event Logs) — these are safe but not easily reversible."
                : "";

            var confirm = MessageBox.Show(
                $"Clean {selected.Count} selected item(s), freeing an estimated {SizeFormat.Bytes(totalBytes)}?{warnExtra}",
                "Confirm Cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            _scanBtn.Enabled  = false;
            _cleanBtn.Enabled = false;
            _summaryLabel.Text = selected.Any(c => c.Key == "ComponentStore")
                ? "Cleaning... (Component Store via DISM can take several minutes)"
                : "Cleaning...";

            var results = await Task.Run(() => DiskCleanupManager.Clean(selected));

            long freed = results.Where(r => r.Success).Sum(r => r.BytesFreed);
            var failed = results.Where(r => !r.Success).ToList();

            if (failed.Count > 0)
            {
                MessageBox.Show(
                    "Some cleanup steps reported errors:\n\n" +
                    string.Join("\n", failed.Select(f => $"{f.Name}: {f.Error}")),
                    "Cleanup Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            MessageBox.Show(
                $"Cleanup complete — approximately {SizeFormat.Bytes(freed)} freed.",
                "Disk Cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information);

            await ScanAsync();
        }
    }

    public class CleanupCategoryRow : Panel
    {
        public CleanupCategory Category { get; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        [System.ComponentModel.Browsable(false)]
        public bool IsChecked { get => _checkbox.Checked; set => _checkbox.Checked = value; }
        public event EventHandler SelectionChanged;

        private CheckBox _checkbox;
        private Label    _nameLbl;
        private Label    _descLbl;
        private Label    _sizeBadge;
        private Label    _riskBadge;

        public CleanupCategoryRow(CleanupCategory cat)
        {
            Category  = cat;
            Height    = 62;
            BackColor = Theme.CARD;

            _checkbox = new CheckBox
            {
                AutoSize = false,
                Size     = new Size(24, 24),
                Location = new Point(10, 19),
                Cursor   = Cursors.Hand
            };
            _checkbox.CheckedChanged += (s, e) => SelectionChanged?.Invoke(this, EventArgs.Empty);

            _nameLbl = new Label
            {
                Text         = cat.Name,
                Font         = new Font("Courier New", 8.5f, FontStyle.Bold),
                ForeColor    = Theme.TEXT_PRI,
                AutoSize     = false,
                Height       = 18,
                Location     = new Point(46, 8),
                BackColor    = Color.Transparent,
                AutoEllipsis = true
            };

            _descLbl = new Label
            {
                Text         = cat.Description,
                Font         = new Font("Segoe UI", 7.5f),
                ForeColor    = Theme.TEXT_DIM,
                AutoSize     = false,
                Height       = 30,
                Location     = new Point(46, 27),
                BackColor    = Color.Transparent,
                AutoEllipsis = true
            };

            _sizeBadge = UiHelpers.MakeBadge(cat.SizeLabel,
                Color.FromArgb(30, Theme.ACCENT.R, Theme.ACCENT.G, Theme.ACCENT.B), Theme.ACCENT);

            Color riskColor = cat.RiskLevel == "Caution" ? Theme.WARNING : Theme.SUCCESS;
            _riskBadge = UiHelpers.MakeBadge(cat.RiskLevel == "Caution" ? "⚠ Caution" : "✔ Safe",
                Color.FromArgb(30, riskColor.R, riskColor.G, riskColor.B), riskColor);

            Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                using var br = new SolidBrush(riskColor);
                e.Graphics.FillRectangle(br, 0, 0, 3, Height);
            };

            SizeChanged += (s, e) => LayoutRow();

            Controls.AddRange(new Control[] { _checkbox, _nameLbl, _descLbl, _sizeBadge, _riskBadge });

            // Badges sit at their AutoSize default until the first SizeChanged
            // fires — if the row is created at its final width, that never
            // happens, so position everything once up front.
            LayoutRow();
        }

        public void RefreshSize()
        {
            _sizeBadge.Text = Category.SizeLabel;
            LayoutRow();
        }

        private void LayoutRow()
        {
            int r = Width - 10;
            _riskBadge.Location = new Point(r - _riskBadge.Width, (Height - _riskBadge.Height) / 2 - 12);
            _sizeBadge.Location = new Point(r - _sizeBadge.Width, (Height - _sizeBadge.Height) / 2 + 12);
            int badgeW = Math.Max(_riskBadge.Width, _sizeBadge.Width);
            r -= badgeW + 10;

            int labelW      = Math.Max(60, r - 46);
            _nameLbl.Width  = labelW;
            _descLbl.Width  = labelW;
        }
    }
}