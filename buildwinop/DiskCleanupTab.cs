using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class diskCleanupTab : listTab
    {
        private readonly flatButton _scanBtn, _cleanBtn;
        private readonly List<cleanupCategory>    _categories = diskCleanupManager.getCategories();
        private readonly List<cleanupCategoryRow> _rows       = new();

        public diskCleanupTab() : base("DISK CLEANUP", 190)
        {
            _scanBtn  = ghost("↺ Scan", 84, async (s, e) => await scanAsync());
            _cleanBtn = primary("🧹 Clean Selected", theme.accent, async (s, e) => await cleanSelectedAsync());
            alignRight((_cleanBtn, 6), (_scanBtn, 14),
                       (ghost("✘ None", 66, (s, e) => setAllRows(false)), 6),
                       (ghost("✔ All",  60, (s, e) => setAllRows(true)),  0));
        }

        protected override void onFirstActivate()
        {
            setRows(_rows, _categories.Select(c => new cleanupCategoryRow(c) { isChecked = c.defaultOn }), updateSummary);
            updateSummary();
            _ = scanAsync();
        }

        protected override void reflow()
        {
            if (inner.Width >= 10) uiHelpers.reflowRowsVertically(inner, _rows, 0);
        }

        private void setAllRows(bool check)
        {
            foreach (var row in _rows) row.isChecked = check;
            updateSummary();
        }

        private async Task scanAsync()
        {
            if (!await busy("Scanning...", () => Task.Run(() => diskCleanupManager.scanSizes(_categories)), _scanBtn, _cleanBtn)) return;
            foreach (var row in _rows) row.refreshSize();
            updateSummary();
        }

        private void updateSummary()
        {
            var sel = _rows.Where(r => r.isChecked).ToList();
            summary.Text = $"{sel.Count} selected  ·  ~{sizeFormat.bytes(sel.Sum(r => r.category.sizeBytes))} reclaimable";
            _cleanBtn.Enabled = sel.Count > 0;
        }

        private async Task cleanSelectedAsync()
        {
            var selected = _rows.Where(r => r.isChecked).Select(r => r.category).ToList();
            if (selected.Count == 0) return;

            string warn = selected.Any(c => c.caution)
                ? "\n\nOne or more selected items are marked Caution (e.g. Recycle Bin, Windows.old, Prefetch, Event Logs) — these are safe but not easily reversible."
                : "";
            if (MessageBox.Show($"Clean {selected.Count} selected item(s), freeing an estimated {sizeFormat.bytes(selected.Sum(c => c.sizeBytes))}?{warn}",
                    "Confirm Cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            List<cleanupResult> results = null;
            string status = selected.Any(c => c.key == "ComponentStore")
                ? "Cleaning... (Component Store via DISM can take several minutes)" : "Cleaning...";
            if (!await busy(status, async () => results = await Task.Run(() => diskCleanupManager.clean(selected)), _scanBtn, _cleanBtn)) return;

            var failed = results.Where(r => !r.success).ToList();
            if (failed.Count > 0)
                MessageBox.Show("Some cleanup steps reported errors:\n\n" + string.Join("\n", failed.Select(f => $"{f.name}: {f.error}")),
                    "Cleanup Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            MessageBox.Show($"Cleanup complete — approximately {sizeFormat.bytes(results.Where(r => r.success).Sum(r => r.bytesFreed))} freed.",
                "Disk Cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information);

            await scanAsync();
        }
    }

    public class cleanupCategoryRow : cardRow
    {
        private readonly CheckBox _checkbox = new() { Size = new Size(24, 24), Location = new Point(10, 19), Cursor = Cursors.Hand };
        private readonly Label    _nameLbl, _descLbl, _sizeBadge, _riskBadge;
        private readonly Color    _riskColor;

        public cleanupCategory category { get; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
        public bool isChecked { get => _checkbox.Checked; set => _checkbox.Checked = value; }

        public cleanupCategoryRow(cleanupCategory cat) : base(62)
        {
            category   = cat;
            _riskColor = cat.caution ? theme.warning : theme.success;
            _checkbox.CheckedChanged += (s, e) => onChanged();
            _nameLbl   = uiHelpers.lbl(cat.name, theme.mono(8.5f, true), theme.textPri, 46, 8, 18);
            _descLbl   = uiHelpers.lbl(cat.description, theme.ui(7.5f), theme.textDim, 46, 27, 30);
            _sizeBadge = uiHelpers.badge(cat.sizeLabel, theme.accent);
            _riskBadge = uiHelpers.badge(cat.caution ? "⚠ Caution" : "✔ Safe", _riskColor);
            Controls.AddRange(new Control[] { _checkbox, _nameLbl, _descLbl, _sizeBadge, _riskBadge });
            // Position once up front — if the row is created at its final width, SizeChanged never fires
            layoutRow();
        }

        protected override Color stripe => _riskColor;

        public void refreshSize()
        {
            _sizeBadge.Text = category.sizeLabel;
            layoutRow();
        }

        protected override void layoutRow()
        {
            int r = Width - 10;
            place(r, _riskBadge, -12);
            place(r, _sizeBadge, 12);
            r -= Math.Max(_riskBadge.Width, _sizeBadge.Width) + 10;
            _nameLbl.Width = _descLbl.Width = Math.Max(60, r - 46);
        }
    }
}
