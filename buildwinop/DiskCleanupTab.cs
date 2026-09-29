using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class DiskCleanupTab : ListTab
    {
        private readonly FlatButton _scanBtn, _cleanBtn;
        private readonly List<CleanupCategory>    _categories = DiskCleanupManager.GetCategories();
        private readonly List<CleanupCategoryRow> _rows       = new();

        public DiskCleanupTab() : base("DISK CLEANUP", 190)
        {
            _scanBtn  = Ghost("↺ Scan", 84, async (s, e) => await ScanAsync());
            _cleanBtn = Primary("🧹 Clean Selected", Theme.ACCENT, async (s, e) => await CleanSelectedAsync());
            AlignRight((_cleanBtn, 6), (_scanBtn, 14),
                       (Ghost("✘ None", 66, (s, e) => SetAllRows(false)), 6),
                       (Ghost("✔ All",  60, (s, e) => SetAllRows(true)),  0));
        }

        protected override void OnFirstActivate()
        {
            SetRows(_rows, _categories.Select(c => new CleanupCategoryRow(c) { IsChecked = c.DefaultOn }), UpdateSummary);
            UpdateSummary();
            _ = ScanAsync();
        }

        protected override void Reflow()
        {
            if (Inner.Width >= 10) UiHelpers.ReflowRowsVertically(Inner, _rows, 0);
        }

        private void SetAllRows(bool check)
        {
            foreach (var row in _rows) row.IsChecked = check;
            UpdateSummary();
        }

        private async Task ScanAsync()
        {
            if (!await Busy("Scanning...", () => Task.Run(() => DiskCleanupManager.ScanSizes(_categories)), _scanBtn, _cleanBtn)) return;
            foreach (var row in _rows) row.RefreshSize();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var sel = _rows.Where(r => r.IsChecked).ToList();
            Summary.Text = $"{sel.Count} selected  ·  ~{SizeFormat.Bytes(sel.Sum(r => r.Category.SizeBytes))} reclaimable";
            _cleanBtn.Enabled = sel.Count > 0;
        }

        private async Task CleanSelectedAsync()
        {
            var selected = _rows.Where(r => r.IsChecked).Select(r => r.Category).ToList();
            if (selected.Count == 0) return;

            string warn = selected.Any(c => c.Caution)
                ? "\n\nOne or more selected items are marked Caution (e.g. Recycle Bin, Windows.old, Prefetch, Event Logs) — these are safe but not easily reversible."
                : "";
            if (MessageBox.Show($"Clean {selected.Count} selected item(s), freeing an estimated {SizeFormat.Bytes(selected.Sum(c => c.SizeBytes))}?{warn}",
                    "Confirm Cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            List<CleanupResult> results = null;
            string status = selected.Any(c => c.Key == "ComponentStore")
                ? "Cleaning... (Component Store via DISM can take several minutes)" : "Cleaning...";
            if (!await Busy(status, async () => results = await Task.Run(() => DiskCleanupManager.Clean(selected)), _scanBtn, _cleanBtn)) return;

            var failed = results.Where(r => !r.Success).ToList();
            if (failed.Count > 0)
                MessageBox.Show("Some cleanup steps reported errors:\n\n" + string.Join("\n", failed.Select(f => $"{f.Name}: {f.Error}")),
                    "Cleanup Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            MessageBox.Show($"Cleanup complete — approximately {SizeFormat.Bytes(results.Where(r => r.Success).Sum(r => r.BytesFreed))} freed.",
                "Disk Cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information);

            await ScanAsync();
        }
    }

    public class CleanupCategoryRow : CardRow
    {
        private readonly CheckBox _checkbox = new() { Size = new Size(24, 24), Location = new Point(10, 19), Cursor = Cursors.Hand };
        private readonly Label    _nameLbl, _descLbl, _sizeBadge, _riskBadge;
        private readonly Color    _riskColor;

        public CleanupCategory Category { get; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
        public bool IsChecked { get => _checkbox.Checked; set => _checkbox.Checked = value; }

        public CleanupCategoryRow(CleanupCategory cat) : base(62)
        {
            Category   = cat;
            _riskColor = cat.Caution ? Theme.WARNING : Theme.SUCCESS;
            _checkbox.CheckedChanged += (s, e) => OnChanged();
            _nameLbl   = UiHelpers.Lbl(cat.Name, Theme.Mono(8.5f, true), Theme.TEXT_PRI, 46, 8, 18);
            _descLbl   = UiHelpers.Lbl(cat.Description, Theme.Ui(7.5f), Theme.TEXT_DIM, 46, 27, 30);
            _sizeBadge = UiHelpers.Badge(cat.SizeLabel, Theme.ACCENT);
            _riskBadge = UiHelpers.Badge(cat.Caution ? "⚠ Caution" : "✔ Safe", _riskColor);
            Controls.AddRange(new Control[] { _checkbox, _nameLbl, _descLbl, _sizeBadge, _riskBadge });
            // Position once up front — if the row is created at its final width, SizeChanged never fires
            LayoutRow();
        }

        protected override Color Stripe => _riskColor;

        public void RefreshSize()
        {
            _sizeBadge.Text = Category.SizeLabel;
            LayoutRow();
        }

        protected override void LayoutRow()
        {
            int r = Width - 10;
            Place(r, _riskBadge, -12);
            Place(r, _sizeBadge, 12);
            r -= Math.Max(_riskBadge.Width, _sizeBadge.Width) + 10;
            _nameLbl.Width = _descLbl.Width = Math.Max(60, r - 46);
        }
    }
}
