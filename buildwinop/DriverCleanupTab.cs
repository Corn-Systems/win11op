using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class DriverCleanupTab : ListTab
    {
        private readonly FlatButton _scanBtn, _removeBtn;
        private readonly Label _emptyLabel = UiHelpers.Lbl("Click \"Scan Driver Store\" to look for removable driver packages.",
                                                           Theme.Mono(9f), Theme.TEXT_DIM, 0, 56);
        private List<DriverPackage> _packages = new();
        private readonly List<DriverPackageRow> _rows = new();

        public DriverCleanupTab() : base("DRIVER CLEANUP", 210)
        {
            _scanBtn   = Ghost("↺ Scan Driver Store", 150, async (s, e) => await ScanAsync());
            _removeBtn = Primary("🗑 Remove Selected", Theme.METEOR, async (s, e) => await RemoveSelectedAsync());
            AlignRight((_removeBtn, 6), (_scanBtn, 14),
                       (Ghost("✘ None", 66, (s, e) => SetAllSelectable(false)), 6),
                       (Ghost("✔ Select Unused", 125, (s, e) => SetAllSelectable(true)), 0));

            // Added straight to the stack — it used to sit in a default 200px-wide panel that clipped the text
            _emptyLabel.Visible = false;
            Inner.Controls.AddRange(new Control[]
            {
                UiHelpers.Lbl("⚠  Removing a driver package is more disruptive than a registry tweak — packages currently in use are locked and can't be selected.",
                    Theme.Mono(7.5f), Theme.WARNING, 0, 0),
                _emptyLabel
            });
        }

        protected override void OnFirstActivate() => _ = ScanAsync();

        protected override void Reflow()
        {
            // Rows start below the warning banner (and the empty-state hint when it shows)
            if (Inner.Width >= 10) UiHelpers.ReflowRowsVertically(Inner, _rows, _emptyLabel.Visible ? 86 : 56);
        }

        // In-use packages have their checkbox disabled — this only touches packages the scan confirmed removable.
        private void SetAllSelectable(bool check)
        {
            foreach (var row in _rows) row.SetChecked(check);
            UpdateSummary();
        }

        private async Task ScanAsync()
        {
            if (!await Busy("Scanning driver store...", async () => _packages = await Task.Run(() => DriverManager.LoadAll()), _scanBtn, _removeBtn)) return;
            _emptyLabel.Visible = _packages.Count == 0;
            SetRows(_rows, _packages.Select(p => new DriverPackageRow(p)), UpdateSummary);
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var unused = _packages.Where(p => !p.InUse).ToList();
            var sel    = _rows.Where(r => r.IsSelected).ToList();
            Summary.Text = _packages.Count == 0 ? "" :
                $"{_packages.Count} packages  ·  {unused.Count} unused ({SizeFormat.Bytes(unused.Sum(p => p.SizeBytes))})  ·  " +
                $"{sel.Count} selected ({SizeFormat.Bytes(sel.Sum(r => r.Package.SizeBytes))})";
            _removeBtn.Enabled = sel.Count > 0;
        }

        private async Task RemoveSelectedAsync()
        {
            var toRemove = _rows.Where(r => r.IsSelected).Select(r => r.Package).ToList();
            if (toRemove.Count == 0) return;

            if (MessageBox.Show(
                    $"Remove {toRemove.Count} driver package(s) and free ~{SizeFormat.Bytes(toRemove.Sum(p => p.SizeBytes))}?\n\n" +
                    "This uninstalls the packages from the Windows Driver Store. " +
                    "If a device that needs one of these is plugged back in, Windows will need to " +
                    "reinstall the driver (from Windows Update or the manufacturer).",
                    "Confirm Driver Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var failures = new List<string>();
            await Busy("Removing driver packages...", () => Task.Run(() =>
            {
                foreach (var p in toRemove)
                    if (!DriverManager.Delete(p, out string error)) failures.Add($"{p.PublishedName} ({p.OriginalName}): {error}");
            }), _scanBtn, _removeBtn);

            if (failures.Count > 0)
                MessageBox.Show("Some driver packages could not be removed:\n\n" + string.Join("\n", failures),
                    "Removal Errors", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await ScanAsync();
        }
    }

    public class DriverPackageRow : CardRow
    {
        private readonly CheckBox _checkbox;
        private readonly Label    _nameLbl, _providerLbl, _sizeBadge, _statusBadge;

        public DriverPackage Package { get; }
        public bool IsSelected => _checkbox.Checked;

        public DriverPackageRow(DriverPackage pkg) : base(58)
        {
            Package   = pkg;
            _checkbox = new CheckBox
            {
                Size = new Size(24, 24), Location = new Point(10, 17),
                Enabled = !pkg.InUse, Cursor = pkg.InUse ? Cursors.No : Cursors.Hand
            };
            _checkbox.CheckedChanged += (s, e) => OnChanged();
            _nameLbl     = UiHelpers.Lbl($"{pkg.OriginalName}  ({pkg.PublishedName})", Theme.Mono(8.5f, true), pkg.InUse ? Theme.TEXT_DIM : Theme.TEXT_PRI, 46, 8, 18);
            _providerLbl = UiHelpers.Lbl($"{pkg.ProviderName}  ·  {pkg.ClassName}  ·  v{pkg.Version}  ·  {pkg.DateLabel}", Theme.Ui(7.5f), Theme.TEXT_DIM, 46, 28, 16);
            _sizeBadge   = UiHelpers.Badge(pkg.SizeLabel, Theme.ACCENT);
            _statusBadge = pkg.InUse ? UiHelpers.Badge("🔒 In Use", Theme.TEXT_DIM) : UiHelpers.Badge("Unused", Theme.SUCCESS);
            Controls.AddRange(new Control[] { _checkbox, _nameLbl, _providerLbl, _sizeBadge, _statusBadge });
            LayoutRow();
        }

        // Respects the in-use lock — a disabled checkbox is never toggled.
        public void SetChecked(bool value)
        {
            if (_checkbox.Enabled) _checkbox.Checked = value;
        }

        protected override Color Stripe => Package.InUse ? Theme.BORDER : Theme.SUCCESS;

        protected override void LayoutRow()
        {
            int r = Place(Width - 10, _statusBadge) - 8;
            r = Place(r, _sizeBadge) - 8;
            _nameLbl.Width = _providerLbl.Width = Math.Max(60, r - 46);
        }
    }
}
