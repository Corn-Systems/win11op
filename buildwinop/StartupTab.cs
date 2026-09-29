using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class StartupTab : ListTab
    {
        private List<StartupEntry> _entries = new();
        private readonly List<StartupEntryRow> _rows = new();

        public StartupTab() : base("STARTUP MANAGER", 240)
        {
            AlignRight((Ghost("✘ Disable All", 100, (s, e) => SetAll(false), Theme.DANGER,  Theme.Mono(7.5f)), 6),
                       (Ghost("✔ Enable All",  96,  (s, e) => SetAll(true),  Theme.SUCCESS, Theme.Mono(7.5f)), 6),
                       (Ghost("↺ Refresh",     84,  (s, e) => LoadEntries()), 0));
        }

        // Reloads on every visit — entries change outside the app
        public override void Activate() => LoadEntries();

        private void LoadEntries()
        {
            UiHelpers.ClearAndDispose(Inner);
            _rows.Clear();
            _entries = StartupManager.LoadAll();

            if (_entries.Count == 0)
            {
                Inner.Controls.Add(UiHelpers.Lbl("No startup entries found.", Theme.Mono(9f), Theme.TEXT_DIM, 0, 20));
                Summary.Text = "No entries found";
                return;
            }

            foreach (var (label, source) in new[]
            {
                ("Registry (Current User)",  StartupSource.RegistryCurrentUser),
                ("Registry (Local Machine)", StartupSource.RegistryLocalMachine),
                ("Startup Folder",           StartupSource.StartupFolder),
            })
            {
                var batch = _entries.Where(e => e.Source == source).ToList();
                if (batch.Count == 0) continue;
                Inner.Controls.Add(GroupHeader(label));
                foreach (var entry in batch)
                {
                    var row = new StartupEntryRow(entry);
                    row.ToggleRequested += OnToggle;
                    row.DeleteRequested += OnDelete;
                    _rows.Add(row);
                    Inner.Controls.Add(row);
                }
            }

            FitInner();
            Reflow();
            UpdateCountLabel();
        }

        private static Panel GroupHeader(string title)
        {
            var hdr = new Panel { Height = 36, BackColor = Color.Transparent };
            hdr.Controls.Add(UiHelpers.Lbl($"// {title.ToUpper()}", Theme.Mono(7.5f, true), Theme.ACCENT, 0, 10));
            UiHelpers.BottomBorder(hdr);
            return hdr;
        }

        protected override void Reflow()
        {
            if (Inner.Width >= 10) UiHelpers.ReflowRowsVertically(Inner, Inner.Controls.Cast<Control>().ToList(), 0);
        }

        private void OnToggle(object sender, StartupEntry entry)
        {
            if (entry.Source == StartupSource.StartupFolder)
                MessageBox.Show("Startup folder shortcuts can't be disabled — only deleted.\n\n" +
                                "To prevent this item from launching, use the Delete button.",
                    "Can't Disable Folder Item", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (!StartupManager.SetEnabled(entry, !entry.IsEnabled))
                MessageBox.Show($"Could not {(entry.IsEnabled ? "disable" : "enable")} \"{entry.Name}\".\n" +
                                "Make sure the app is running as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            ((StartupEntryRow)sender).RefreshState();
            UpdateCountLabel();
        }

        private void OnDelete(object sender, StartupEntry entry)
        {
            if (MessageBox.Show($"Remove \"{entry.Name}\" from startup?\n\nThis cannot be undone.", "Confirm Delete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            if (!StartupManager.Delete(entry))
            {
                MessageBox.Show($"Could not delete \"{entry.Name}\".\nMake sure the app is running as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var row = (StartupEntryRow)sender;
            _rows.Remove(row);
            Inner.Controls.Remove(row);
            BeginInvoke(new Action(row.Dispose));   // we're inside the row's own button click — dispose once it returns
            _entries.Remove(entry);
            Reflow();
            UpdateCountLabel();
        }

        private void SetAll(bool enable)
        {
            var failed = new List<string>();
            foreach (var row in _rows.Where(r => r.Entry.Source != StartupSource.StartupFolder && r.Entry.IsEnabled != enable))
            {
                if (!StartupManager.SetEnabled(row.Entry, enable)) failed.Add(row.Entry.Name);
                row.RefreshState();
            }
            UpdateCountLabel();
            if (failed.Count > 0)
                MessageBox.Show($"Could not {(enable ? "enable" : "disable")}:\n\n{string.Join("\n", failed)}\n\n" +
                                "Make sure the app is running as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void UpdateCountLabel()
        {
            int enabled = _entries.Count(e => e.IsEnabled);
            Summary.Text = $"{_entries.Count} items  ·  {enabled} enabled  ·  {_entries.Count - enabled} disabled";
        }
    }

    public class StartupEntryRow : CardRow
    {
        private readonly CheckBox _toggle;
        private readonly Label    _nameLbl, _pubLbl, _cmdLbl, _sourceBadge, _impactBadge;
        private readonly Button   _deleteBtn;
        private bool _syncing;

        public StartupEntry Entry { get; }
        public event EventHandler<StartupEntry> ToggleRequested, DeleteRequested;

        public StartupEntryRow(StartupEntry entry) : base(66)
        {
            Entry = entry;

            _toggle = new CheckBox
            {
                Checked = entry.IsEnabled, Size = new Size(44, 44), Location = new Point(10, 11),
                FlatStyle = FlatStyle.Flat, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter,
                Font = Theme.F("Segoe UI Emoji", 14f), Cursor = Cursors.Hand, BackColor = Color.Transparent
            };
            _toggle.FlatAppearance.BorderSize = 0;
            _toggle.FlatAppearance.CheckedBackColor = _toggle.FlatAppearance.MouseOverBackColor = Color.Transparent;
            // Programmatic syncs (RefreshState) must not raise ToggleRequested — they used to, which
            // re-toggled the entry straight back, and Enable/Disable All recursed through it until
            // the stack overflowed.
            _toggle.CheckedChanged += (s, e) =>
            {
                UpdateToggleText();
                if (!_syncing) ToggleRequested?.Invoke(this, Entry);
            };

            _nameLbl = UiHelpers.Lbl(entry.Name, Theme.Mono(8.5f, true), Theme.TEXT_PRI, 60, 10, 20);
            _pubLbl  = UiHelpers.Lbl(entry.Publisher, Theme.Ui(8f), Theme.TEXT_DIM, 60, 30, 16);
            _cmdLbl  = UiHelpers.Lbl(entry.Command, Theme.F("Consolas", 7f), Color.FromArgb(90, 85, 140), 60, 47, 14);

            Color src = entry.Source switch
            {
                StartupSource.RegistryCurrentUser  => Color.FromArgb(99, 102, 241),
                StartupSource.RegistryLocalMachine => Color.FromArgb(251, 191, 36),
                _                                  => Color.FromArgb(16, 185, 129),
            };
            Color imp = entry.ImpactColor;
            _sourceBadge = UiHelpers.MakeBadge(entry.SourceLabel, Color.FromArgb(30, src), Color.FromArgb(180, src));
            _impactBadge = UiHelpers.MakeBadge($"⚡ {entry.ImpactLabel} impact", Color.FromArgb(30, imp), Color.FromArgb(180, imp));

            _deleteBtn = new Button
            {
                Text = "🗑", Font = Theme.F("Segoe UI Emoji", 11f), FlatStyle = FlatStyle.Flat, Size = new Size(32, 32),
                BackColor = Color.Transparent, ForeColor = Theme.DANGER, Cursor = Cursors.Hand
            };
            _deleteBtn.FlatAppearance.BorderSize         = 0;
            _deleteBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, Theme.DANGER);
            _deleteBtn.Click += (s, e) => DeleteRequested?.Invoke(this, Entry);

            Controls.AddRange(new Control[] { _toggle, _nameLbl, _pubLbl, _cmdLbl, _sourceBadge, _impactBadge, _deleteBtn });
            RefreshState();
            LayoutRow();
        }

        protected override Color Stripe => Entry.IsEnabled ? Theme.SUCCESS : Theme.BORDER;

        protected override void LayoutRow()
        {
            int r = Place(Width - 8, _deleteBtn) - 8;
            r = Place(r, _impactBadge) - 6;
            r = Place(r, _sourceBadge) - 8;
            _nameLbl.Width = _pubLbl.Width = _cmdLbl.Width = Math.Max(50, r - 60);
        }

        private void UpdateToggleText() => _toggle.Text = _toggle.Checked ? "✅" : "⬜";

        public void RefreshState()
        {
            if (InvokeRequired) { Invoke(new Action(RefreshState)); return; }
            _syncing = true;
            _toggle.Checked = Entry.IsEnabled;
            _syncing = false;
            _nameLbl.ForeColor = Entry.IsEnabled ? Theme.TEXT_PRI : Theme.TEXT_DIM;
            UpdateToggleText();
            Invalidate();
        }
    }
}
