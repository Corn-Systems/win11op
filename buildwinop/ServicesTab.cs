using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class ServicesTab : ListTab
    {
        private readonly FlatButton _scanBtn;
        private List<ManagedService> _services = new();
        private readonly List<ManagedServiceRow> _rows = new();

        public ServicesTab() : base("SERVICES", 150)
        {
            _scanBtn = Ghost("↺ Rescan", 96, async (s, e) => await ScanAsync());
            AlignRight((_scanBtn, 0));
            Inner.Controls.Add(UiHelpers.Lbl(
                "⚠  Only genuinely optional services are listed. Disabling records the original startup type so Restore puts back exactly what you had.",
                Theme.Mono(7.5f), Theme.WARNING, 0, 0));
        }

        protected override void OnFirstActivate() => _ = ScanAsync();

        protected override void Reflow()
        {
            if (Inner.Width >= 10) UiHelpers.ReflowRowsVertically(Inner, _rows, 30);   // below the info line
        }

        private async Task ScanAsync()
        {
            if (!await Busy("Scanning services...", async () => _services = await Task.Run(() => ServicesManager.LoadAll()), _scanBtn)) return;
            SetRows(_rows, _services.Select(s => new ManagedServiceRow(s)), UpdateSummary);
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var present = _services.Where(s => s.Exists).ToList();
            Summary.Text = $"{present.Count} services  ·  {present.Count(s => s.IsRunning)} running  ·  {present.Count(s => s.IsDisabled)} disabled";
        }
    }

    public class ManagedServiceRow : CardRow
    {
        private readonly Label      _nameLbl, _descLbl, _stateBadge, _startBadge, _riskBadge;
        private readonly FlatButton _actionBtn;
        private readonly Color      _riskColor;

        public ManagedService Service { get; }

        public ManagedServiceRow(ManagedService svc) : base(62)
        {
            Service    = svc;
            _riskColor = svc.Caution ? Theme.WARNING : Theme.SUCCESS;
            _nameLbl   = UiHelpers.Lbl(svc.Exists ? svc.DisplayName : $"{svc.DisplayName}  (not installed)",
                             Theme.Mono(8.5f, true), svc.Exists ? Theme.TEXT_PRI : Theme.TEXT_DIM, 14, 8, 18);
            _descLbl    = UiHelpers.Lbl(svc.Description, Theme.Ui(7.5f), Theme.TEXT_DIM, 14, 27, 30);
            _stateBadge = UiHelpers.Badge("", Theme.TEXT_DIM);
            _startBadge = UiHelpers.Badge("", Theme.TEXT_DIM);
            _riskBadge  = UiHelpers.Badge(svc.Caution ? "⚠ Caution" : "✔ Safe", _riskColor);
            _actionBtn  = new FlatButton("", Theme.SURFACE2) { Size = new Size(110, 28), Font = Theme.Mono(7.5f, true), Visible = svc.Exists };
            _actionBtn.Click += async (s, e) => await ToggleAsync();
            Controls.AddRange(new Control[] { _nameLbl, _descLbl, _stateBadge, _startBadge, _riskBadge, _actionBtn });
            RefreshVisuals();
        }

        protected override Color Stripe => !Service.Exists ? Theme.BORDER : Service.IsDisabled ? Theme.TEXT_DIM : _riskColor;

        private async Task ToggleAsync()
        {
            _actionBtn.Enabled = false;
            bool   disabling = !Service.IsDisabled;
            string error     = null;
            bool ok = await Task.Run(() => disabling
                ? ServicesManager.Disable(Service, out error)
                : ServicesManager.Restore(Service, out error));

            if (!ok)
                MessageBox.Show($"{(disabling ? "Disable" : "Restore")} failed for {Service.DisplayName}:\n\n{error}",
                    "Service Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            RefreshVisuals();
            _actionBtn.Enabled = true;
            Invalidate();
            OnChanged();
        }

        private void RefreshVisuals()
        {
            _stateBadge.Visible = _startBadge.Visible = Service.Exists;
            if (Service.Exists)
            {
                bool off = Service.IsDisabled;
                UiHelpers.Tint(_stateBadge, Service.IsRunning ? "● Running" : "○ Stopped", Service.IsRunning ? Theme.SUCCESS : Theme.TEXT_DIM);
                UiHelpers.Tint(_startBadge, Service.StartTypeLabel, off ? Theme.METEOR : Theme.ACCENT);
                _actionBtn.Text      = off ? "↩ Restore" : "✘ Disable";
                _actionBtn.BackColor = off ? Theme.SURFACE2 : Theme.METEOR;
                _actionBtn.ForeColor = off ? Theme.TEXT_SEC : Color.White;
            }
            LayoutRow();
        }

        protected override void LayoutRow()
        {
            int r = Place(Width - 10, _actionBtn) - 10;
            Place(r, _riskBadge, -12);
            Place(r, _startBadge, 12);
            r = Place(r - Math.Max(_riskBadge.Width, _startBadge.Width) - 8, _stateBadge) - 8;
            _nameLbl.Width = _descLbl.Width = Math.Max(60, r - 14);
        }
    }
}
