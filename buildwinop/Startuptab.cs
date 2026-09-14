using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class StartupTab : Panel
    {
        private Panel           _toolbar;
        private Label           _countLabel;
        private FlatButton      _refreshBtn;
        private FlatButton      _disableAllBtn;
        private FlatButton      _enableAllBtn;
        private Panel           _scrollPanel;       // auto-scroll container
        private Panel           _innerPanel;        // actual stacking panel inside scroll

        private List<StartupEntry>    _entries = new();
        private List<StartupEntryRow> _rows    = new();

        public StartupTab()
        {
            BackColor = Theme.BG;
            Dock      = DockStyle.Fill;
            Visible   = false;

            BuildToolbar();
            BuildScrollArea();

            Controls.Add(_scrollPanel);
            Controls.Add(_toolbar);   // docked top, added last
        }

        public void Activate() => LoadEntries();

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
                Text      = "// STARTUP MANAGER",
                Font      = new Font("Courier New", 9.5f, FontStyle.Bold),
                ForeColor = Theme.TEXT_PRI,
                AutoSize  = true,
                Location  = new Point(16, 15)
            };

            _countLabel = new Label
            {
                Text      = "",
                Font      = new Font("Courier New", 7.5f),
                ForeColor = Theme.TEXT_DIM,
                AutoSize  = true,
                Location  = new Point(240, 18)
            };

            _refreshBtn = new FlatButton("↺ Refresh", Theme.SURFACE2)
                { Size = new Size(84, 28), Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.TEXT_SEC };
            _refreshBtn.Click += (s, e) => LoadEntries();

            _enableAllBtn = new FlatButton("✔ Enable All", Theme.SURFACE2)
                { Size = new Size(96, 28), Font = new Font("Courier New", 7.5f), ForeColor = Theme.SUCCESS };
            _enableAllBtn.Click += (s, e) => SetAll(true);

            _disableAllBtn = new FlatButton("✘ Disable All", Theme.SURFACE2)
                { Size = new Size(100, 28), Font = new Font("Courier New", 7.5f), ForeColor = Theme.DANGER };
            _disableAllBtn.Click += (s, e) => SetAll(false);

            _toolbar.SizeChanged += (s, e) => PositionToolbarButtons();
            _toolbar.Controls.AddRange(new Control[] { title, _countLabel, _refreshBtn, _enableAllBtn, _disableAllBtn });
        }

        private void PositionToolbarButtons()
        {
            int r = _toolbar.Width - 12;
            _disableAllBtn.Location = new Point(r - _disableAllBtn.Width, 13);
            r -= _disableAllBtn.Width + 6;
            _enableAllBtn.Location  = new Point(r - _enableAllBtn.Width, 13);
            r -= _enableAllBtn.Width + 6;
            _refreshBtn.Location    = new Point(r - _refreshBtn.Width, 13);
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

            _innerPanel = new Panel
            {
                BackColor = Theme.BG,
                AutoSize  = true,
                Location  = new Point(0, 0)
            };

            _scrollPanel.Controls.Add(_innerPanel);

            // Keep innerPanel width in sync with scroll area
            _scrollPanel.Resize += (s, e) =>
            {
                _innerPanel.Width = _scrollPanel.ClientSize.Width - _scrollPanel.Padding.Horizontal;
                ReflowRows();
            };
        }

        private void LoadEntries()
        {
            _innerPanel.Controls.Clear();
            _rows.Clear();

            _entries = StartupManager.LoadAll();

            if (_entries.Count == 0)
            {
                var lbl = new Label
                {
                    Text      = "No startup entries found.",
                    Font      = new Font("Courier New", 9f),
                    ForeColor = Theme.TEXT_DIM,
                    AutoSize  = true,
                    Location  = new Point(0, 20)
                };
                _innerPanel.Controls.Add(lbl);
                _countLabel.Text = "No entries found";
                return;
            }

            var groups = new[]
            {
                (Label: "Registry (Current User)",  Source: StartupSource.RegistryCurrentUser),
                (Label: "Registry (Local Machine)", Source: StartupSource.RegistryLocalMachine),
                (Label: "Startup Folder",           Source: StartupSource.StartupFolder),
            };

            foreach (var group in groups)
            {
                var batch = _entries.Where(e => e.Source == group.Source).ToList();
                if (batch.Count == 0) continue;

                var hdr = new StartupSectionHeader(group.Label);
                _innerPanel.Controls.Add(hdr);

                foreach (var entry in batch)
                {
                    var row = new StartupEntryRow(entry);
                    row.ToggleRequested += OnToggle;
                    row.DeleteRequested += OnDelete;
                    _rows.Add(row);
                    _innerPanel.Controls.Add(row);
                }
            }

            // Set widths and reflow
            _innerPanel.Width = Math.Max(100,
                _scrollPanel.ClientSize.Width - _scrollPanel.Padding.Horizontal);
            ReflowRows();
            UpdateCountLabel();
        }

        private void ReflowRows()
        {
            if (_innerPanel.Width < 10) return;
            int y   = 0;
            int w   = _innerPanel.Width;

            foreach (Control c in _innerPanel.Controls)
            {
                c.Width    = w;
                c.Location = new Point(0, y);
                y         += c.Height + 2;
            }
            _innerPanel.Height = y;
        }

        private void OnToggle(object sender, StartupEntry entry)
        {
            if (entry.Source == StartupSource.StartupFolder)
            {
                MessageBox.Show(
                    "Startup folder shortcuts can't be disabled — only deleted.\n\n" +
                    "To prevent this item from launching, use the Delete button.",
                    "Can't Disable Folder Item",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (sender is StartupEntryRow row) row.RefreshState();
                return;
            }

            bool newState = !entry.IsEnabled;
            bool ok       = StartupManager.SetEnabled(entry, newState);
            if (!ok)
            {
                MessageBox.Show(
                    $"Could not {(newState ? "enable" : "disable")} \"{entry.Name}\".\n" +
                    "Make sure the app is running as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (sender is StartupEntryRow row) row.RefreshState();
                return;
            }

            if (sender is StartupEntryRow r) r.RefreshState();
            UpdateCountLabel();
        }

        private void OnDelete(object sender, StartupEntry entry)
        {
            var result = MessageBox.Show(
                $"Remove \"{entry.Name}\" from startup?\n\nThis cannot be undone.",
                "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes) return;

            bool ok = StartupManager.Delete(entry);
            if (!ok)
            {
                MessageBox.Show(
                    $"Could not delete \"{entry.Name}\".\n" +
                    "Make sure the app is running as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (sender is StartupEntryRow row)
            {
                _rows.Remove(row);
                _innerPanel.Controls.Remove(row);
                row.Dispose();
            }
            _entries.Remove(entry);
            ReflowRows();
            UpdateCountLabel();
        }

        private void SetAll(bool enable)
        {
            foreach (var row in _rows.ToList())
            {
                if (row.Entry.Source == StartupSource.StartupFolder) continue;
                if (row.Entry.IsEnabled == enable) continue;
                StartupManager.SetEnabled(row.Entry, enable);
                row.RefreshState();
            }
            UpdateCountLabel();
        }

        private void UpdateCountLabel()
        {
            int total    = _entries.Count;
            int enabled  = _entries.Count(e => e.IsEnabled);
            int disabled = total - enabled;
            _countLabel.Text = $"{total} items  ·  {enabled} enabled  ·  {disabled} disabled";
        }
    }

    public class StartupSectionHeader : Panel
    {
        public StartupSectionHeader(string title)
        {
            Height    = 36;
            BackColor = Color.Transparent;

            var lbl = new Label
            {
                Text      = $"// {title.ToUpper()}",
                Font      = new Font("Courier New", 7.5f, FontStyle.Bold),
                ForeColor = Theme.ACCENT,
                AutoSize  = true,
                Location  = new Point(0, 10),
                BackColor = Color.Transparent
            };

            Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER, 1);
                e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            };

            Controls.AddRange(new Control[] { lbl });
        }
    }

    public class StartupEntryRow : Panel
    {
        public StartupEntry Entry { get; }
        public event EventHandler<StartupEntry> ToggleRequested;
        public event EventHandler<StartupEntry> DeleteRequested;

        private CheckBox _toggle;
        private Label    _nameLbl;
        private Label    _pubLbl;
        private Label    _cmdLbl;
        private Label    _sourceBadge;
        private Label    _impactBadge;
        private Button   _deleteBtn;

        private static readonly Dictionary<string, Color> SourceColors = new()
        {
            ["Registry (User)"]   = Color.FromArgb(99,  102, 241),
            ["Registry (System)"] = Color.FromArgb(251, 191,  36),
            ["Startup Folder"]    = Color.FromArgb(16,  185, 129),
        };

        public StartupEntryRow(StartupEntry entry)
        {
            Entry     = entry;
            Height    = 66;
            BackColor = Theme.CARD;
            Cursor    = Cursors.Default;

            _toggle = new CheckBox
            {
                Checked    = entry.IsEnabled,
                AutoSize   = false,
                Size       = new Size(44, 44),
                Location   = new Point(10, 11),
                FlatStyle  = FlatStyle.Flat,
                Appearance = Appearance.Button,
                TextAlign  = ContentAlignment.MiddleCenter,
                Font       = new Font("Segoe UI Emoji", 14f),
                Cursor     = Cursors.Hand,
                BackColor  = Color.Transparent
            };
            _toggle.FlatAppearance.BorderSize         = 0;
            _toggle.FlatAppearance.CheckedBackColor   = Color.Transparent;
            _toggle.FlatAppearance.MouseOverBackColor = Color.Transparent;
            UpdateToggleText();
            _toggle.CheckedChanged += (s, e) => { UpdateToggleText(); ToggleRequested?.Invoke(this, Entry); };

            _nameLbl = new Label
            {
                Text         = entry.Name,
                Font         = new Font("Courier New", 8.5f, FontStyle.Bold),
                ForeColor    = entry.IsEnabled ? Theme.TEXT_PRI : Theme.TEXT_DIM,
                AutoSize     = false,
                Height       = 20,
                Location     = new Point(60, 10),
                BackColor    = Color.Transparent,
                AutoEllipsis = true,
                UseMnemonic  = false
            };

            _pubLbl = new Label
            {
                Text      = entry.Publisher,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Theme.TEXT_DIM,
                AutoSize  = false,
                Height    = 16,
                Location  = new Point(60, 30),
                BackColor = Color.Transparent
            };

            _cmdLbl = new Label
            {
                Text         = entry.Command,
                Font         = new Font("Consolas", 7f),
                ForeColor    = Color.FromArgb(90, 85, 140),
                AutoSize     = false,
                Height       = 14,
                Location     = new Point(60, 47),
                BackColor    = Color.Transparent,
                AutoEllipsis = true,
                UseMnemonic  = false
            };

            Color srcColor = SourceColors.TryGetValue(entry.SourceLabel, out var sc)
                ? sc : Theme.ACCENT;
            _sourceBadge = MakeBadge(entry.SourceLabel,
                Color.FromArgb(30, srcColor.R, srcColor.G, srcColor.B),
                Color.FromArgb(180, srcColor.R, srcColor.G, srcColor.B));

            Color impColor = entry.ImpactColor;
            _impactBadge = MakeBadge($"⚡ {entry.ImpactLabel} impact",
                Color.FromArgb(30, impColor.R, impColor.G, impColor.B),
                Color.FromArgb(180, impColor.R, impColor.G, impColor.B));

            _deleteBtn = new Button
            {
                Text      = "🗑",
                Font      = new Font("Segoe UI Emoji", 11f),
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(32, 32),
                BackColor = Color.Transparent,
                ForeColor = Theme.DANGER,
                Cursor    = Cursors.Hand
            };
            _deleteBtn.FlatAppearance.BorderSize         = 0;
            _deleteBtn.FlatAppearance.MouseOverBackColor =
                Color.FromArgb(40, Theme.DANGER.R, Theme.DANGER.G, Theme.DANGER.B);
            _deleteBtn.Click += (s, e) => DeleteRequested?.Invoke(this, Entry);

            Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                Color stripe = Entry.IsEnabled ? Theme.SUCCESS : Theme.BORDER;
                using var br = new SolidBrush(stripe);
                e.Graphics.FillRectangle(br, 0, 0, 3, Height);
            };

            SizeChanged += (s, e) => LayoutRow();

            Controls.AddRange(new Control[]
                { _toggle, _nameLbl, _pubLbl, _cmdLbl, _sourceBadge, _impactBadge, _deleteBtn });
        }

        private void LayoutRow()
        {
            int r = Width - 8;

            _deleteBtn.Location   = new Point(r - _deleteBtn.Width,   (Height - _deleteBtn.Height) / 2);
            r -= _deleteBtn.Width + 8;

            _impactBadge.Location = new Point(r - _impactBadge.Width, (Height - _impactBadge.Height) / 2);
            r -= _impactBadge.Width + 6;

            _sourceBadge.Location = new Point(r - _sourceBadge.Width, (Height - _sourceBadge.Height) / 2);
            r -= _sourceBadge.Width + 8;

            int labelW     = Math.Max(50, r - 60);
            _nameLbl.Width = labelW;
            _pubLbl.Width  = labelW;
            _cmdLbl.Width  = labelW;
        }

        private static Label MakeBadge(string text, Color bg, Color fg) => new Label
        {
            Text      = text,
            Font      = new Font("Courier New", 6.5f),
            ForeColor = fg,
            BackColor = bg,
            AutoSize  = true,
            Padding   = new Padding(4, 2, 4, 2)
        };

        private void UpdateToggleText()
        {
            _toggle.Text = _toggle.Checked ? "✅" : "⬜";
        }

        public void RefreshState()
        {
            if (InvokeRequired) { Invoke(new Action(RefreshState)); return; }
            _toggle.Checked    = Entry.IsEnabled;
            _nameLbl.ForeColor = Entry.IsEnabled ? Theme.TEXT_PRI : Theme.TEXT_SEC;
            UpdateToggleText();
            Invalidate();
        }
    }
}