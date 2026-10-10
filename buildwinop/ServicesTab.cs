using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class servicesTab : listTab
    {
        private readonly flatButton _scanBtn;
        private List<managedService> _services = new();
        private readonly List<managedServiceRow> _rows = new();

        public servicesTab() : base("SERVICES", 150)
        {
            _scanBtn = ghost("↺ Rescan", 96, async (s, e) => await scanAsync());
            alignRight((_scanBtn, 0));
            inner.Controls.Add(uiHelpers.lbl(
                "⚠  Only genuinely optional services are listed. Disabling records the original startup type so Restore puts back exactly what you had.",
                theme.mono(7.5f), theme.warning, 0, 0));
        }

        protected override void onFirstActivate() => _ = scanAsync();

        protected override void reflow()
        {
            if (inner.Width >= 10) uiHelpers.reflowRowsVertically(inner, _rows, 30);   // below the info line
        }

        private async Task scanAsync()
        {
            if (!await busy("Scanning services...", async () => _services = await Task.Run(() => servicesManager.loadAll()), _scanBtn)) return;
            setRows(_rows, _services.Select(s => new managedServiceRow(s)), updateSummary);
            updateSummary();
        }

        private void updateSummary()
        {
            var present = _services.Where(s => s.exists).ToList();
            summary.Text = $"{present.Count} services  ·  {present.Count(s => s.isRunning)} running  ·  {present.Count(s => s.isDisabled)} disabled";
        }
    }

    public class managedServiceRow : cardRow
    {
        private readonly Label      _nameLbl, _descLbl, _stateBadge, _startBadge, _riskBadge;
        private readonly flatButton _actionBtn;
        private readonly Color      _riskColor;

        public managedService service { get; }

        public managedServiceRow(managedService svc) : base(62)
        {
            service    = svc;
            _riskColor = svc.caution ? theme.warning : theme.success;
            _nameLbl   = uiHelpers.lbl(svc.exists ? svc.displayName : $"{svc.displayName}  (not installed)",
                             theme.mono(8.5f, true), svc.exists ? theme.textPri : theme.textDim, 14, 8, 18);
            _descLbl    = uiHelpers.lbl(svc.description, theme.ui(7.5f), theme.textDim, 14, 27, 30);
            _stateBadge = uiHelpers.badge("", theme.textDim);
            _startBadge = uiHelpers.badge("", theme.textDim);
            _riskBadge  = uiHelpers.badge(svc.caution ? "⚠ Caution" : "✔ Safe", _riskColor);
            _actionBtn  = new flatButton("", theme.surface2) { Size = new Size(110, 28), Font = theme.mono(7.5f, true), Visible = svc.exists };
            _actionBtn.Click += async (s, e) => await toggleAsync();
            Controls.AddRange(new Control[] { _nameLbl, _descLbl, _stateBadge, _startBadge, _riskBadge, _actionBtn });
            refreshVisuals();
        }

        protected override Color stripe => !service.exists ? theme.border : service.isDisabled ? theme.textDim : _riskColor;

        private async Task toggleAsync()
        {
            _actionBtn.Enabled = false;
            bool   disabling = !service.isDisabled;
            string error     = null;
            bool ok = await Task.Run(() => disabling
                ? servicesManager.disable(service, out error)
                : servicesManager.restore(service, out error));

            if (!ok)
                MessageBox.Show($"{(disabling ? "Disable" : "Restore")} failed for {service.displayName}:\n\n{error}",
                    "Service Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            refreshVisuals();
            _actionBtn.Enabled = true;
            Invalidate();
            onChanged();
        }

        private void refreshVisuals()
        {
            _stateBadge.Visible = _startBadge.Visible = service.exists;
            if (service.exists)
            {
                bool off = service.isDisabled;
                uiHelpers.tint(_stateBadge, service.isRunning ? "● Running" : "○ Stopped", service.isRunning ? theme.success : theme.textDim);
                uiHelpers.tint(_startBadge, service.startTypeLabel, off ? theme.meteor : theme.accent);
                _actionBtn.Text      = off ? "↩ Restore" : "✘ Disable";
                _actionBtn.BackColor = off ? theme.surface2 : theme.meteor;
                _actionBtn.ForeColor = off ? theme.textSec : Color.White;
            }
            layoutRow();
        }

        protected override void layoutRow()
        {
            int r = place(Width - 10, _actionBtn) - 10;
            place(r, _riskBadge, -12);
            place(r, _startBadge, 12);
            r = place(r - Math.Max(_riskBadge.Width, _startBadge.Width) - 8, _stateBadge) - 8;
            _nameLbl.Width = _descLbl.Width = Math.Max(60, r - 14);
        }
    }
}
