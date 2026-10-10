using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class startupTab : listTab
    {
        private List<startupEntry> _entries = new();
        private readonly List<startupEntryRow> _rows = new();

        public startupTab() : base("STARTUP MANAGER", 240)
        {
            alignRight((ghost("✘ Disable All", 100, (s, e) => setAll(false), theme.danger,  theme.mono(7.5f)), 6),
                       (ghost("✔ Enable All",  96,  (s, e) => setAll(true),  theme.success, theme.mono(7.5f)), 6),
                       (ghost("↺ Refresh",     84,  (s, e) => loadEntries()), 0));
        }

        // Reloads on every visit — entries change outside the app
        public override void activate() => loadEntries();

        private void loadEntries()
        {
            uiHelpers.clearAndDispose(inner);
            _rows.Clear();
            _entries = startupManager.loadAll();

            if (_entries.Count == 0)
            {
                inner.Controls.Add(uiHelpers.lbl("No startup entries found.", theme.mono(9f), theme.textDim, 0, 20));
                summary.Text = "No entries found";
                return;
            }

            foreach (var (label, source) in new[]
            {
                ("Registry (Current User)",  startupSource.registryCurrentUser),
                ("Registry (Local Machine)", startupSource.registryLocalMachine),
                ("Startup Folder",           startupSource.startupFolder),
            })
            {
                var batch = _entries.Where(e => e.source == source).ToList();
                if (batch.Count == 0) continue;
                inner.Controls.Add(groupHeader(label));
                foreach (var entry in batch)
                {
                    var row = new startupEntryRow(entry);
                    row.toggleRequested += onToggle;
                    row.deleteRequested += onDelete;
                    _rows.Add(row);
                    inner.Controls.Add(row);
                }
            }

            fitInner();
            reflow();
            updateCountLabel();
        }

        private static Panel groupHeader(string title)
        {
            var hdr = new Panel { Height = 36, BackColor = Color.Transparent };
            hdr.Controls.Add(uiHelpers.lbl($"// {title.ToUpper()}", theme.mono(7.5f, true), theme.accent, 0, 10));
            uiHelpers.bottomBorder(hdr);
            return hdr;
        }

        protected override void reflow()
        {
            if (inner.Width >= 10) uiHelpers.reflowRowsVertically(inner, inner.Controls.Cast<Control>().ToList(), 0);
        }

        private void onToggle(object sender, startupEntry entry)
        {
            if (entry.source == startupSource.startupFolder)
                MessageBox.Show("Startup folder shortcuts can't be disabled — only deleted.\n\n" +
                                "To prevent this item from launching, use the Delete button.",
                    "Can't Disable Folder Item", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (!startupManager.setEnabled(entry, !entry.isEnabled))
                MessageBox.Show($"Could not {(entry.isEnabled ? "disable" : "enable")} \"{entry.name}\".\n" +
                                "Make sure the app is running as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            ((startupEntryRow)sender).refreshState();
            updateCountLabel();
        }

        private void onDelete(object sender, startupEntry entry)
        {
            if (MessageBox.Show($"Remove \"{entry.name}\" from startup?\n\nThis cannot be undone.", "Confirm Delete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            if (!startupManager.delete(entry))
            {
                MessageBox.Show($"Could not delete \"{entry.name}\".\nMake sure the app is running as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var row = (startupEntryRow)sender;
            _rows.Remove(row);
            inner.Controls.Remove(row);
            BeginInvoke(new Action(row.Dispose));   // we're inside the row's own button click — dispose once it returns
            _entries.Remove(entry);
            reflow();
            updateCountLabel();
        }

        private void setAll(bool enable)
        {
            var failed = new List<string>();
            foreach (var row in _rows.Where(r => r.entry.source != startupSource.startupFolder && r.entry.isEnabled != enable))
            {
                if (!startupManager.setEnabled(row.entry, enable)) failed.Add(row.entry.name);
                row.refreshState();
            }
            updateCountLabel();
            if (failed.Count > 0)
                MessageBox.Show($"Could not {(enable ? "enable" : "disable")}:\n\n{string.Join("\n", failed)}\n\n" +
                                "Make sure the app is running as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void updateCountLabel()
        {
            int enabled = _entries.Count(e => e.isEnabled);
            summary.Text = $"{_entries.Count} items  ·  {enabled} enabled  ·  {_entries.Count - enabled} disabled";
        }
    }

    public class startupEntryRow : cardRow
    {
        private readonly CheckBox _toggle;
        private readonly Label    _nameLbl, _pubLbl, _cmdLbl, _sourceBadge, _impactBadge;
        private readonly Button   _deleteBtn;
        private bool _syncing;

        public startupEntry entry { get; }
        public event EventHandler<startupEntry> toggleRequested, deleteRequested;

        public startupEntryRow(startupEntry entry) : base(66)
        {
            this.entry = entry;

            _toggle = new CheckBox
            {
                Checked = entry.isEnabled, Size = new Size(44, 44), Location = new Point(10, 11),
                FlatStyle = FlatStyle.Flat, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter,
                Font = theme.f("Segoe UI Emoji", 14f), Cursor = Cursors.Hand, BackColor = Color.Transparent
            };
            _toggle.FlatAppearance.BorderSize = 0;
            _toggle.FlatAppearance.CheckedBackColor = _toggle.FlatAppearance.MouseOverBackColor = Color.Transparent;
            // Programmatic syncs (refreshState) must not raise toggleRequested — they used to, which
            // re-toggled the entry straight back, and Enable/Disable All recursed through it until
            // the stack overflowed.
            _toggle.CheckedChanged += (s, e) =>
            {
                updateToggleText();
                if (!_syncing) toggleRequested?.Invoke(this, this.entry);
            };

            _nameLbl = uiHelpers.lbl(entry.name, theme.mono(8.5f, true), theme.textPri, 60, 10, 20);
            _pubLbl  = uiHelpers.lbl(entry.publisher, theme.ui(8f), theme.textDim, 60, 30, 16);
            _cmdLbl  = uiHelpers.lbl(entry.command, theme.f("Consolas", 7f), Color.FromArgb(90, 85, 140), 60, 47, 14);

            Color src = entry.source switch
            {
                startupSource.registryCurrentUser  => Color.FromArgb(99, 102, 241),
                startupSource.registryLocalMachine => Color.FromArgb(251, 191, 36),
                _                                  => Color.FromArgb(16, 185, 129),
            };
            Color imp = entry.impactColor;
            _sourceBadge = uiHelpers.makeBadge(entry.sourceLabel, Color.FromArgb(30, src), Color.FromArgb(180, src));
            _impactBadge = uiHelpers.makeBadge($"⚡ {entry.impactLabel} impact", Color.FromArgb(30, imp), Color.FromArgb(180, imp));

            _deleteBtn = new Button
            {
                Text = "🗑", Font = theme.f("Segoe UI Emoji", 11f), FlatStyle = FlatStyle.Flat, Size = new Size(32, 32),
                BackColor = Color.Transparent, ForeColor = theme.danger, Cursor = Cursors.Hand
            };
            _deleteBtn.FlatAppearance.BorderSize         = 0;
            _deleteBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, theme.danger);
            _deleteBtn.Click += (s, e) => deleteRequested?.Invoke(this, this.entry);

            Controls.AddRange(new Control[] { _toggle, _nameLbl, _pubLbl, _cmdLbl, _sourceBadge, _impactBadge, _deleteBtn });
            refreshState();
            layoutRow();
        }

        protected override Color stripe => entry.isEnabled ? theme.success : theme.border;

        protected override void layoutRow()
        {
            int r = place(Width - 8, _deleteBtn) - 8;
            r = place(r, _impactBadge) - 6;
            r = place(r, _sourceBadge) - 8;
            _nameLbl.Width = _pubLbl.Width = _cmdLbl.Width = Math.Max(50, r - 60);
        }

        private void updateToggleText() => _toggle.Text = _toggle.Checked ? "✅" : "⬜";

        public void refreshState()
        {
            if (InvokeRequired) { Invoke(new Action(refreshState)); return; }
            _syncing = true;
            _toggle.Checked = entry.isEnabled;
            _syncing = false;
            _nameLbl.ForeColor = entry.isEnabled ? theme.textPri : theme.textDim;
            updateToggleText();
            Invalidate();
        }
    }
}
