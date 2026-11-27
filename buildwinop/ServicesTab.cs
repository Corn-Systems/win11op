using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper

namespace Win11Optimizer
{
    public class servicesTab : listTab
    {
        private readonly flatButton _scanBtn;
        private readonly Label _banner = uiHelpers.banner(
            "⚠  Only genuinely optional services are listed. Disabling records the original startup type so Restore puts back exactly what you had.",
            theme.warning);
        private List<managedService> _services = new();
        private readonly List<managedServiceRow> _rows = new();

        public servicesTab() : base("SERVICES", 150)
        {
            _scanBtn = ghost("↺ Rescan", 96, async (s, e) => await scanAsync());
            alignRight((_scanBtn, 0));
            inner.Controls.Add(_banner);
        }

        protected override void onFirstActivate() => _ = scanAsync();

        protected override void reflow()
        {
            if (inner.Width < 10) return;
            _banner.MaximumSize = new Size(inner.Width, 0);   // wrap instead of widening the list
            uiHelpers.stackRows(inner, _rows, _banner.Bottom + Dpi.S(12));
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
            _actionBtn  = new flatButton("", theme.surface2) { Size = new Size(Dpi.S(110), Dpi.S(28)), Font = theme.mono(7.5f, true), Visible = svc.exists };
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
            bool ok;
            try
            {
                ok = await Task.Run(() => disabling
                    ? servicesManager.disable(service, out error)
                    : servicesManager.restore(service, out error));
            }
            catch (Exception ex) { sessionLog.write("SERVICE TOGGLE", ex); (ok, error) = (false, ex.Message); }
            if (IsDisposed) return;   // the list was rebuilt (rescan / DPI change) while sc.exe ran

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
            int r = place(Width - Dpi.S(10), _actionBtn) - Dpi.S(10);
            place(r, _riskBadge, -12);
            place(r, _startBadge, 12);
            r = place(r - Math.Max(_riskBadge.Width, _startBadge.Width) - Dpi.S(8), _stateBadge) - Dpi.S(8);
            _nameLbl.Width = _descLbl.Width = Math.Max(Dpi.S(60), r - Dpi.S(14));
        }
    }
}
