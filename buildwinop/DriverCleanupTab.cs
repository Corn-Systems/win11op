using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class DriverCleanupTab : Panel
    {
        private Panel      _toolbar;
        private Label      _summaryLabel;
        private FlatButton _selUnusedBtn;
        private FlatButton _selNoneBtn;
        private FlatButton _scanBtn;
        private FlatButton _removeBtn;
        private Panel      _scrollPanel;
        private Panel      _innerPanel;
        private Label      _emptyLabel;

        private List<DriverPackage>    _packages = new();
        private List<DriverPackageRow> _rows     = new();
        private bool _loaded = false;

        public DriverCleanupTab()
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
                Text      = "// DRIVER CLEANUP",
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
                Location  = new Point(210, 18)
            };

            _selUnusedBtn = new FlatButton("✔ Select Unused", Theme.SURFACE2)
                { Size = new Size(125, 28), Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.TEXT_SEC };
            _selUnusedBtn.Click += (s, e) => SetAllSelectable(true);

            _selNoneBtn = new FlatButton("✘ None", Theme.SURFACE2)
                { Size = new Size(66, 28), Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.TEXT_SEC };
            _selNoneBtn.Click += (s, e) => SetAllSelectable(false);

            _scanBtn = new FlatButton("↺ Scan Driver Store", Theme.SURFACE2)
                { Size = new Size(150, 28), Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.TEXT_SEC };
            _scanBtn.Click += async (s, e) => await ScanAsync();

            _removeBtn = new FlatButton("🗑 Remove Selected", Theme.METEOR)
                { Size = new Size(150, 28), Font = new Font("Courier New", 7.5f, FontStyle.Bold) };
            _removeBtn.Click += async (s, e) => await RemoveSelectedAsync();

            _toolbar.SizeChanged += (s, e) => PositionToolbarButtons();
            _toolbar.Controls.AddRange(new Control[]
                { title, _summaryLabel, _selUnusedBtn, _selNoneBtn, _scanBtn, _removeBtn });
        }

        private void PositionToolbarButtons() =>
            UiHelpers.LayoutButtonsRightToLeft(_toolbar, 13,
                (_removeBtn, 6), (_scanBtn, 14), (_selNoneBtn, 6), (_selUnusedBtn, 0));

        private void SetAllSelectable(bool check)
        {
            // In-use packages have their checkbox disabled — this only ever
            // touches packages the scan confirmed are safe to remove.
            foreach (var row in _rows) row.SetChecked(check);
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

            var warnBanner = new Panel { BackColor = Color.Transparent, Height = 40, Location = new Point(0, 0) };
            var warnLbl = new Label
            {
                Text      = "⚠  Removing a driver package is more disruptive than a registry tweak — packages currently in use are locked and can't be selected.",
                Font      = new Font("Courier New", 7.5f),
                ForeColor = Theme.WARNING,
                AutoSize  = true,
                BackColor = Color.Transparent
            };
            warnBanner.Controls.Add(warnLbl);

            _emptyLabel = new Label
            {
                Text      = "Click \"Scan Driver Store\" to look for removable driver packages.",
                Font      = new Font("Courier New", 9f),
                ForeColor = Theme.TEXT_DIM,
                AutoSize  = true,
                Location  = new Point(0, 46),
                Visible   = false
            };

            _innerPanel = new Panel { BackColor = Theme.BG, AutoSize = true, Location = new Point(0, 0) };
            _innerPanel.Controls.Add(warnBanner);
            _innerPanel.Controls.Add(_emptyLabel);

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
            _removeBtn.Enabled = false;
            _summaryLabel.Text = "Scanning driver store...";

            var result = await Task.Run(() => DriverManager.LoadAll());

            _packages = result;
            RenderRows();
            _scanBtn.Enabled = true;
            UpdateSummary();
        }

        private void RenderRows()
        {
            foreach (var row in _rows) _innerPanel.Controls.Remove(row);
            _rows.Clear();

            _emptyLabel.Visible = _packages.Count == 0;
            if (_packages.Count == 0)
            {
                ReflowRows();
                return;
            }

            foreach (var pkg in _packages)
            {
                var row = new DriverPackageRow(pkg);
                row.SelectionChanged += (s, e) => UpdateSummary();
                _rows.Add(row);
                _innerPanel.Controls.Add(row);
            }

            _innerPanel.Width = Math.Max(100, _scrollPanel.ClientSize.Width - _scrollPanel.Padding.Horizontal);
            ReflowRows();
        }

        private void ReflowRows()
        {
            if (_innerPanel.Width < 10) return;
            int y = 56; // below the warning banner

            _emptyLabel.Location = new Point(0, y);
            if (_emptyLabel.Visible) y += 30;

            UiHelpers.ReflowRowsVertically(_innerPanel, _rows, y);
        }

        private void UpdateSummary()
        {
            int  orphaned   = _packages.Count(p => !p.InUse);
            int  selected   = _rows.Count(r => r.IsSelected);
            long selBytes   = _rows.Where(r => r.IsSelected).Sum(r => r.Package.SizeBytes);
            long totalOrph  = _packages.Where(p => !p.InUse).Sum(p => p.SizeBytes);

            _summaryLabel.Text = _packages.Count == 0
                ? ""
                : $"{_packages.Count} packages  ·  {orphaned} unused ({SizeFormat.Bytes(totalOrph)})  ·  {selected} selected ({SizeFormat.Bytes(selBytes)})";

            _removeBtn.Enabled = selected > 0;
        }

        private async Task RemoveSelectedAsync()
        {
            var toRemove = _rows.Where(r => r.IsSelected).ToList();
            if (toRemove.Count == 0) return;

            long totalBytes = toRemove.Sum(r => r.Package.SizeBytes);
            var confirm = MessageBox.Show(
                $"Remove {toRemove.Count} driver package(s) and free ~{SizeFormat.Bytes(totalBytes)}?\n\n" +
                "This uninstalls the packages from the Windows Driver Store. " +
                "If a device that needs one of these is plugged back in, Windows will need to " +
                "reinstall the driver (from Windows Update or the manufacturer).",
                "Confirm Driver Removal",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            _removeBtn.Enabled = false;
            _scanBtn.Enabled   = false;

            var failures = new List<string>();
            await Task.Run(() =>
            {
                foreach (var row in toRemove)
                {
                    if (!DriverManager.Delete(row.Package, out string error))
                        failures.Add($"{row.Package.PublishedName} ({row.Package.OriginalName}): {error}");
                }
            });

            if (failures.Count > 0)
            {
                MessageBox.Show(
                    "Some driver packages could not be removed:\n\n" + string.Join("\n", failures),
                    "Removal Errors", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            await ScanAsync();
        }
    }

    public class DriverPackageRow : Panel
    {
        public DriverPackage Package { get; }
        public bool IsSelected => _checkbox.Checked;
        public event EventHandler SelectionChanged;

        // Respects the in-use lock — a disabled checkbox is never toggled.
        public void SetChecked(bool value)
        {
            if (_checkbox.Enabled) _checkbox.Checked = value;
        }

        private CheckBox _checkbox;
        private Label    _nameLbl;
        private Label    _providerLbl;
        private Label    _sizeBadge;
        private Label    _statusBadge;

        public DriverPackageRow(DriverPackage pkg)
        {
            Package   = pkg;
            Height    = 58;
            BackColor = Theme.CARD;

            _checkbox = new CheckBox
            {
                AutoSize = false,
                Size     = new Size(24, 24),
                Location = new Point(10, 17),
                Enabled  = !pkg.InUse,
                Cursor   = pkg.InUse ? Cursors.No : Cursors.Hand
            };
            _checkbox.CheckedChanged += (s, e) => SelectionChanged?.Invoke(this, EventArgs.Empty);

            _nameLbl = new Label
            {
                Text         = $"{pkg.OriginalName}  ({pkg.PublishedName})",
                Font         = new Font("Courier New", 8.5f, FontStyle.Bold),
                ForeColor    = pkg.InUse ? Theme.TEXT_DIM : Theme.TEXT_PRI,
                AutoSize     = false,
                Height       = 18,
                Location     = new Point(46, 8),
                BackColor    = Color.Transparent,
                AutoEllipsis = true
            };

            _providerLbl = new Label
            {
                Text      = $"{pkg.ProviderName}  ·  {pkg.ClassName}  ·  v{pkg.Version}  ·  {pkg.DateLabel}",
                Font      = new Font("Segoe UI", 7.5f),
                ForeColor = Theme.TEXT_DIM,
                AutoSize  = false,
                Height    = 16,
                Location  = new Point(46, 28),
                BackColor = Color.Transparent
            };

            _sizeBadge = UiHelpers.MakeBadge(pkg.SizeLabel,
                Color.FromArgb(30, Theme.ACCENT.R, Theme.ACCENT.G, Theme.ACCENT.B), Theme.ACCENT);

            _statusBadge = pkg.InUse
                ? UiHelpers.MakeBadge("🔒 In Use", Color.FromArgb(30, Theme.TEXT_DIM.R, Theme.TEXT_DIM.G, Theme.TEXT_DIM.B), Theme.TEXT_DIM)
                : UiHelpers.MakeBadge("Unused", Color.FromArgb(30, Theme.SUCCESS.R, Theme.SUCCESS.G, Theme.SUCCESS.B), Theme.SUCCESS);

            Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                Color stripe = pkg.InUse ? Theme.BORDER : Theme.SUCCESS;
                using var br = new SolidBrush(stripe);
                e.Graphics.FillRectangle(br, 0, 0, 3, Height);
            };

            SizeChanged += (s, e) => LayoutRow();

            Controls.AddRange(new Control[]
                { _checkbox, _nameLbl, _providerLbl, _sizeBadge, _statusBadge });

            // Same first-layout fix as CleanupCategoryRow — position badges once
            // in case the row is added at its final width and SizeChanged never fires.
            LayoutRow();
        }

        private void LayoutRow()
        {
            int r = Width - 10;
            _statusBadge.Location = new Point(r - _statusBadge.Width, (Height - _statusBadge.Height) / 2);
            r -= _statusBadge.Width + 8;
            _sizeBadge.Location   = new Point(r - _sizeBadge.Width, (Height - _sizeBadge.Height) / 2);
            r -= _sizeBadge.Width + 8;

            int labelW      = Math.Max(60, r - 46);
            _nameLbl.Width     = labelW;
            _providerLbl.Width = labelW;
        }
    }
}