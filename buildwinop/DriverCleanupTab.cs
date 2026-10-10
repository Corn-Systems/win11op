using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class driverCleanupTab : listTab
    {
        private readonly flatButton _scanBtn, _removeBtn;
        private readonly Label _emptyLabel = uiHelpers.lbl("Click \"Scan Driver Store\" to look for removable driver packages.",
                                                           theme.mono(9f), theme.textDim, 0, 56);
        private List<driverPackage> _packages = new();
        private readonly List<driverPackageRow> _rows = new();

        public driverCleanupTab() : base("DRIVER CLEANUP", 210)
        {
            _scanBtn   = ghost("↺ Scan Driver Store", 150, async (s, e) => await scanAsync());
            _removeBtn = primary("🗑 Remove Selected", theme.meteor, async (s, e) => await removeSelectedAsync());
            alignRight((_removeBtn, 6), (_scanBtn, 14),
                       (ghost("✘ None", 66, (s, e) => setAllSelectable(false)), 6),
                       (ghost("✔ Select Unused", 125, (s, e) => setAllSelectable(true)), 0));

            // Added straight to the stack — it used to sit in a default 200px-wide panel that clipped the text
            _emptyLabel.Visible = false;
            inner.Controls.AddRange(new Control[]
            {
                uiHelpers.lbl("⚠  Removing a driver package is more disruptive than a registry tweak — packages currently in use are locked and can't be selected.",
                    theme.mono(7.5f), theme.warning, 0, 0),
                _emptyLabel
            });
        }

        protected override void onFirstActivate() => _ = scanAsync();

        protected override void reflow()
        {
            // Rows start below the warning banner (and the empty-state hint when it shows)
            if (inner.Width >= 10) uiHelpers.reflowRowsVertically(inner, _rows, _emptyLabel.Visible ? 86 : 56);
        }

        // In-use packages have their checkbox disabled — this only touches packages the scan confirmed removable.
        private void setAllSelectable(bool check)
        {
            foreach (var row in _rows) row.setChecked(check);
            updateSummary();
        }

        private async Task scanAsync()
        {
            if (!await busy("Scanning driver store...", async () => _packages = await Task.Run(() => driverManager.loadAll()), _scanBtn, _removeBtn)) return;
            _emptyLabel.Visible = _packages.Count == 0;
            setRows(_rows, _packages.Select(p => new driverPackageRow(p)), updateSummary);
            updateSummary();
        }

        private void updateSummary()
        {
            var unused = _packages.Where(p => !p.inUse).ToList();
            var sel    = _rows.Where(r => r.isSelected).ToList();
            summary.Text = _packages.Count == 0 ? "" :
                $"{_packages.Count} packages  ·  {unused.Count} unused ({sizeFormat.bytes(unused.Sum(p => p.sizeBytes))})  ·  " +
                $"{sel.Count} selected ({sizeFormat.bytes(sel.Sum(r => r.package.sizeBytes))})";
            _removeBtn.Enabled = sel.Count > 0;
        }

        private async Task removeSelectedAsync()
        {
            var toRemove = _rows.Where(r => r.isSelected).Select(r => r.package).ToList();
            if (toRemove.Count == 0) return;

            if (MessageBox.Show(
                    $"Remove {toRemove.Count} driver package(s) and free ~{sizeFormat.bytes(toRemove.Sum(p => p.sizeBytes))}?\n\n" +
                    "This uninstalls the packages from the Windows Driver Store. " +
                    "If a device that needs one of these is plugged back in, Windows will need to " +
                    "reinstall the driver (from Windows Update or the manufacturer).",
                    "Confirm Driver Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var failures = new List<string>();
            await busy("Removing driver packages...", () => Task.Run(() =>
            {
                foreach (var p in toRemove)
                    if (!driverManager.delete(p, out string error)) failures.Add($"{p.publishedName} ({p.originalName}): {error}");
            }), _scanBtn, _removeBtn);

            if (failures.Count > 0)
                MessageBox.Show("Some driver packages could not be removed:\n\n" + string.Join("\n", failures),
                    "Removal Errors", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await scanAsync();
        }
    }

    public class driverPackageRow : cardRow
    {
        private readonly CheckBox _checkbox;
        private readonly Label    _nameLbl, _providerLbl, _sizeBadge, _statusBadge;

        public driverPackage package { get; }
        public bool isSelected => _checkbox.Checked;

        public driverPackageRow(driverPackage pkg) : base(58)
        {
            package   = pkg;
            _checkbox = new CheckBox
            {
                Size = new Size(24, 24), Location = new Point(10, 17),
                Enabled = !pkg.inUse, Cursor = pkg.inUse ? Cursors.No : Cursors.Hand
            };
            _checkbox.CheckedChanged += (s, e) => onChanged();
            _nameLbl     = uiHelpers.lbl($"{pkg.originalName}  ({pkg.publishedName})", theme.mono(8.5f, true), pkg.inUse ? theme.textDim : theme.textPri, 46, 8, 18);
            _providerLbl = uiHelpers.lbl($"{pkg.providerName}  ·  {pkg.className}  ·  v{pkg.version}  ·  {pkg.dateLabel}", theme.ui(7.5f), theme.textDim, 46, 28, 16);
            _sizeBadge   = uiHelpers.badge(pkg.sizeLabel, theme.accent);
            _statusBadge = pkg.inUse ? uiHelpers.badge("🔒 In Use", theme.textDim) : uiHelpers.badge("Unused", theme.success);
            Controls.AddRange(new Control[] { _checkbox, _nameLbl, _providerLbl, _sizeBadge, _statusBadge });
            layoutRow();
        }

        // Respects the in-use lock — a disabled checkbox is never toggled.
        public void setChecked(bool value)
        {
            if (_checkbox.Enabled) _checkbox.Checked = value;
        }

        protected override Color stripe => package.inUse ? theme.border : theme.success;

        protected override void layoutRow()
        {
            int r = place(Width - 10, _statusBadge) - 8;
            r = place(r, _sizeBadge) - 8;
            _nameLbl.Width = _providerLbl.Width = Math.Max(60, r - 46);
        }
    }
}
