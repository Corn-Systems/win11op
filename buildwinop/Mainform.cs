using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper — byte-identical across Corn Systems repos

namespace Win11Optimizer
{
public class MainForm : Form
    {
        private Panel _topBar;
        private Panel _sidebar;
        private Panel _mainArea;
        private Panel _bottomBar;
        private Panel _logPanel;

        private Label _winVerBadge;
        private Label _adminBadge;
        private Label _updateBadge;
        private Label _rebootBadge;

        private Panel       _searchBar;
        private TextBox     _searchBox;
        private FlatButton  _clearSearchBtn;
        private string      _searchQuery = "";

        private static readonly string[] SidebarCategories =
        {
            "All", "Performance", "Privacy", "Responsiveness",
            "Gaming", "Network", "Bloatware", "Security", "Advanced",
            "Startup", "Services", "History", "Driver Cleanup", "Disk Cleanup"
        };

        private static readonly Dictionary<string, string> CatEmoji = new()
        {
            ["All"]            = "🏠",
            ["Performance"]    = "⚡",
            ["Privacy"]        = "🔒",
            ["Responsiveness"] = "🖥",
            ["Gaming"]         = "🎮",
            ["Network"]        = "🌐",
            ["Bloatware"]      = "🗑",
            ["Advanced"]       = "⚠",
            ["Security"]       = "🔒",
            ["Startup"]        = "🚀",
            ["Services"]       = "🛠",
            ["History"]        = "📋",
            ["Driver Cleanup"] = "🔧",
            ["Disk Cleanup"]   = "🧹",
        };

        private string _activeCategory = "All";

        private FlowLayoutPanel          _tileGrid;
        private readonly List<TweakTile> _tiles = new();

        // Persists each tweak's checked state across grid rebuilds (category switches,
        // preset applies, profile loads) so Select All/None and manual toggles survive
        // a re-render instead of snapping back to DefaultOn every time.
        private readonly Dictionary<string, bool> _selectionState = new();
        private Label      _statusLabel;
        private Label      _selCountLabel;
        private Panel      _progOuter;
        private Panel      _progInner;
        private FlatButton _runBtn;
        private FlatButton _undoBtn;
        private FlatButton _clearBtn;
        private FlatButton _exportBtn;
        private FlatButton _importBtn;
        private CheckBox   _restoreChk;

        private Panel _histPanel;

        private StartupTab       _startupTab;
        private ServicesTab      _servicesTab;
        private DriverCleanupTab _driverTab;
        private DiskCleanupTab   _diskCleanupTab;

        private RichTextBox _logBox;

        private Panel       _tooltip;
        private Label       _ttTitle;
        private Label       _ttWhat;
        private System.Windows.Forms.Timer _ttHideTimer;

        private bool _isRunning = false;
        private int  _totalTweaks, _doneTweaks;

        // Tweak names applied this session that are still waiting on a reboot
        // or an Explorer restart to take full effect.
        private readonly List<string> _pendingReboot   = new();
        private readonly List<string> _pendingExplorer = new();

        public MainForm()
        {
            InitUI();
            DarkTitleBar.Apply(this);
            PopulateGrid("All");
            UpdateSelCount();
            if (AdminWarning.Show) _adminBadge.Visible = true;
            CheckWhatsNew();
            _ = CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            string newer = await UpdateChecker.CheckAsync();
            if (newer == null || IsDisposed) return;
            try
            {
                Invoke(new Action(() =>
                {
                    _updateBadge.Text    = $"⬆ v{newer} available";
                    _updateBadge.Visible = true;
                    _topBar.PerformLayout();
                }));
            }
            catch { /* form closed mid-check */ }
        }

        private void CheckWhatsNew()
        {
            // Single source of truth — this was previously a hardcoded "1.1.0"
            // that never got bumped, so the What's New dialog compared against
            // the wrong version forever.
            string CurrentVersion = AppVersion.Current;
            string versionFile = AppPaths.LastVersionFile;

            try
            {
                string lastVersion = File.Exists(versionFile)
                    ? File.ReadAllText(versionFile).Trim()
                    : "";

                if (lastVersion != CurrentVersion)
                {
                    File.WriteAllText(versionFile, CurrentVersion);

                    // Only show dialog if this isn't a completely fresh install
                    // (fresh installs have no last_version.txt — we don't want to
                    //  show "What's New" the very first time someone runs it)
                    if (!string.IsNullOrEmpty(lastVersion))
                    {
                        var result = MessageBox.Show(
                            $"Win11 Optimizer has been updated to v{CurrentVersion}!\n\n" +
                            "Would you like to see what's new?",
                            "What's New in v" + CurrentVersion,
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information,
                            MessageBoxDefaultButton.Button1);

                        if (result == DialogResult.Yes)
                            Process.Start(new ProcessStartInfo
                            {
                                FileName       = "https://github.com/Corn-Systems/win11op/releases/tag/v" + CurrentVersion,
                                UseShellExecute = true
                            });
                    }
                }
            }
            catch { }
        }

        private void InitUI()
        {
            Dpi.Update(this);
            AutoScaleMode   = AutoScaleMode.None;

            Text            = "Win11 Optimizer";
            Size            = new Size(Dpi.S(1200), Dpi.S(760));
            MinimumSize     = new Size(Dpi.S(960),  Dpi.S(620));
            BackColor       = Theme.BG;
            ForeColor       = Theme.TEXT_PRI;
            Font            = new Font("Segoe UI", 9f);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            BuildTopBar();
            BuildSidebar();
            BuildMainArea();
            BuildBottomBar();
            BuildLogPanel();

            _topBar.Dock    = DockStyle.Top;
            _bottomBar.Dock = DockStyle.Bottom;
            _sidebar.Dock   = DockStyle.Left;
            _mainArea.Dock  = DockStyle.Fill;
            _logPanel.Dock  = DockStyle.None;

            Controls.Add(_mainArea);
            Controls.Add(_sidebar);
            Controls.Add(_bottomBar);
            Controls.Add(_topBar);

            CornScrollBar.Attach(_sidebar);
            Controls.Add(_logPanel);
            BuildTooltip();

            DpiChanged += (s, e) =>
            {
                Dpi.Update(e.DeviceDpiNew);
                if (e.SuggestedRectangle != Rectangle.Empty)
                    SetBounds(e.SuggestedRectangle.X, e.SuggestedRectangle.Y,
                              e.SuggestedRectangle.Width, e.SuggestedRectangle.Height);
                MinimumSize = new Size(Dpi.S(960), Dpi.S(620));
                RescalePanels();
            };
        }

        private void RescalePanels()
        {
            SuspendLayout();
            try
            {
                if (_topBar    != null) _topBar.Height    = Dpi.S(60);
                if (_bottomBar != null) _bottomBar.Height = Dpi.S(136);
                if (_sidebar   != null) _sidebar.Width    = Dpi.S(210);
                if (_logPanel  != null && _logPanel.Visible)
                    _logPanel.Height = Dpi.S(180);
            }
            finally
            {
                ResumeLayout(performLayout: true);
                LayoutAll();
            }
        }

        private void LayoutAll()
        {
            if (_logPanel.Visible)
            {
                int logH  = _logPanel.Height;
                int sideW = _sidebar.Width;
                int botY  = ClientSize.Height - _bottomBar.Height - logH;
                int logW  = ClientSize.Width - sideW;
                _logPanel.SetBounds(sideW, botY, logW, logH);
                _logPanel.BringToFront();
                _mainArea.Padding = new Padding(0, 0, 0, logH);
            }
            else
            {
                _mainArea.Padding = new Padding(0);
            }
        }

private void BuildTopBar()
        {
            _topBar = new Panel { BackColor = Theme.SURFACE, Height = Dpi.S(60) };
            _topBar.Paint += (s, e) =>
            {
                var g = e.Graphics;
                // Bottom border line
                using var p = new Pen(Theme.BORDER);
                g.DrawLine(p, 0, _topBar.Height - 1, _topBar.Width, _topBar.Height - 1);
                // Subtle gold top accent bar (like website nav)
                using var bar = new SolidBrush(Color.FromArgb(30, Theme.ACCENT.R, Theme.ACCENT.G, Theme.ACCENT.B));
                g.FillRectangle(bar, 0, 0, _topBar.Width, 2);
            };

            // "CORN_SYSTEMS" style logo label — matches website nav-logo font
            var cornLbl = new Label
            {
                Text      = "CORN_SYSTEMS",
                Font      = new Font("Courier New", 7f, FontStyle.Bold),
                ForeColor = Theme.ACCENT,
                AutoSize  = true,
                Location  = new Point(18, 10),
                BackColor = Color.Transparent
            };

            var titleLbl = new Label
            {
                Text      = "WIN11 OPTIMIZER  ⚡",
                Font      = new Font("Courier New", 9f, FontStyle.Bold),
                ForeColor = Theme.TEXT_PRI,
                AutoSize  = true,
                Location  = new Point(18, 30)
            };

            _winVerBadge = new Label { Visible = false };

            _adminBadge = new Label
            {
                Text      = "⚠  Not running as Administrator — some tweaks may fail",
                AutoSize  = true,
                Font      = new Font("Courier New", 7.5f),
                ForeColor = Theme.WARNING,
                Location  = new Point(18, 44),
                Visible   = false
            };

            var ghLink = new FlatButton("⭐  GITHUB ↗", Theme.ACCENT)
            {
                AutoSize  = false,
                Size      = new Size(Dpi.S(115), Dpi.S(30)),
                Font      = new Font("Courier New", 7.5f, FontStyle.Bold),
                ForeColor = Theme.ACCENT_TEXT,
                Cursor    = Cursors.Hand
            };
            ghLink.FlatAppearance.BorderSize         = 0;
            ghLink.FlatAppearance.MouseOverBackColor = Theme.ACCENT_HOV;
            ghLink.Click += (s, e) => Process.Start(new ProcessStartInfo
                { FileName = AppVersion.RepoUrl, UseShellExecute = true });

            // Shown only when the launch-time GitHub check finds a newer release
            _updateBadge = new Label
            {
                Text      = "",
                Font      = new Font("Courier New", 7.5f, FontStyle.Bold),
                ForeColor = Theme.ACCENT,
                BackColor = Color.FromArgb(30, Theme.ACCENT.R, Theme.ACCENT.G, Theme.ACCENT.B),
                AutoSize  = true,
                Padding   = new Padding(6, 4, 6, 4),
                Cursor    = Cursors.Hand,
                Visible   = false
            };
            _updateBadge.Click += (s, e) => Process.Start(new ProcessStartInfo
                { FileName = AppVersion.RepoUrl + "/releases/latest", UseShellExecute = true });

            void PositionTopRight(object s, EventArgs e)
            {
                ghLink.Location = new Point(_topBar.Width - ghLink.Width - Dpi.S(16),
                                            (_topBar.Height - ghLink.Height) / 2);
                _updateBadge.Location = new Point(ghLink.Left - _updateBadge.Width - Dpi.S(10),
                                            (_topBar.Height - _updateBadge.Height) / 2);
            }
            _topBar.SizeChanged  += PositionTopRight;
            _updateBadge.SizeChanged += PositionTopRight;

            _topBar.Controls.AddRange(new Control[]
                { cornLbl, titleLbl, _winVerBadge, _adminBadge, _updateBadge, ghLink });
        }

        //  SIDEBAR --

        private void BuildSidebar()
        {
            // AutoScroll: with 14 category buttons plus the Select All/None row,
            // the stack is taller than the sidebar at minimum window height —
            // without this, the bottom entries silently clip off-screen.
            _sidebar = new Panel { BackColor = Theme.SURFACE, Width = Dpi.S(210), AutoScroll = true };
            _sidebar.Paint += (s, e) =>
            {
                var g = e.Graphics;
                using var p = new Pen(Theme.BORDER);
                g.DrawLine(p, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
                // Subtle purple gradient at top — matches website section hero
                using var grad = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, _sidebar.Width, 80),
                    Color.FromArgb(18, Theme.SKY_PURPLE.R, Theme.SKY_PURPLE.G, Theme.SKY_PURPLE.B),
                    Color.Transparent,
                    System.Drawing.Drawing2D.LinearGradientMode.Vertical);
                g.FillRectangle(grad, 0, 0, _sidebar.Width, 80);
            };

            // "// CATEGORIES" — website section-label mono style with gold accent
            var hdr = new Label
            {
                Text      = "// CATEGORIES",
                Font      = new Font("Courier New", 7f, FontStyle.Bold),
                ForeColor = Theme.ACCENT,
                AutoSize  = true,
                Location  = new Point(Dpi.S(14), Dpi.S(14))
            };
            _sidebar.Controls.Add(hdr);

            int y = Dpi.S(38);
            foreach (var cat in SidebarCategories)
            {
                if (cat == "Startup" || cat == "History" || cat == "Driver Cleanup")
                {
                    var div = new Panel
                    {
                        BackColor = Theme.BORDER,
                        Bounds    = new Rectangle(Dpi.S(8), y + 2, Dpi.S(194), 1)
                    };
                    _sidebar.Controls.Add(div);
                    y += Dpi.S(10);
                }

                var btn = MakeSidebarBtn(cat);
                btn.SetBounds(Dpi.S(8), y, Dpi.S(194), Dpi.S(36));
                _sidebar.Controls.Add(btn);
                y += Dpi.S(38);
            }

            y += Dpi.S(8);
            var selAll = new FlatButton("✔ Select All", Theme.ACCENT);
            selAll.SetBounds(Dpi.S(8), y, Dpi.S(92), Dpi.S(28));
            selAll.Click += (s, e) => SetAllInView(true);

            var selNone = new FlatButton("✘ None", Theme.SURFACE2);
            selNone.SetBounds(Dpi.S(108), y, Dpi.S(94), Dpi.S(28));
            selNone.Click += (s, e) => SetAllInView(false);

            _sidebar.Controls.AddRange(new Control[] { selAll, selNone });
        }

        private Button MakeSidebarBtn(string cat)
        {
            bool   active = _activeCategory == cat;
            string emoji  = CatEmoji.TryGetValue(cat, out var em) ? em : "📦";

            var btn = new Button
            {
                Text      = $"  {emoji}  {cat}",
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                BackColor = active ? Theme.ACCENT : Color.Transparent,
                ForeColor = active ? Theme.ACCENT_TEXT : Theme.TEXT_DIM,
                Font      = new Font("Courier New", 8f, active ? FontStyle.Bold : FontStyle.Regular),
                Cursor    = Cursors.Hand,
                Tag       = cat
            };
            btn.FlatAppearance.BorderSize           = 0;
            btn.FlatAppearance.MouseOverBackColor   = active
                ? Theme.ACCENT_HOV
                : Color.FromArgb(26, 24, 53);  // website --surface2

            // Badge paint — draws a small pill with selection count on the right
            btn.Paint += (s, e) =>
            {
                int selCount = cat == "All"
                    ? _tiles.Count(t => t.IsChecked)
                    : _tiles.Count(t => t.IsChecked && t.Entry.Category == cat);
                if (selCount == 0) return;

                string badge = selCount.ToString();
                bool   isActive = cat == _activeCategory;
                Color  pillBg   = isActive
                    ? Theme.ACCENT
                    : Color.FromArgb(50, Theme.ACCENT.R, Theme.ACCENT.G, Theme.ACCENT.B);
                Color  pillFg   = isActive
                    ? Theme.ACCENT_TEXT
                    : Theme.ACCENT;

                using var badgeFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                SizeF  sz      = g.MeasureString(badge, badgeFont);
                int    pw      = (int)sz.Width + 10;
                int    ph      = 16;
                int    px      = btn.Width - pw - 8;
                int    py      = (btn.Height - ph) / 2;

                using var pillBr = new SolidBrush(pillBg);
                g.FillRoundedRect(pillBr, px, py, pw, ph, 8);
                using var textBr = new SolidBrush(pillFg);
                g.DrawString(badge, badgeFont, textBr,
                    px + (pw - sz.Width) / 2f,
                    py + (ph - sz.Height) / 2f);
            };

            btn.Click += (s, e) =>
            {
                _activeCategory = cat;
                ClearSearch();
                RefreshSidebar();
                if      (cat == "History")        ShowHistory();
                else if (cat == "Startup")        ShowStartup();
                else if (cat == "Services")        ShowServices();
                else if (cat == "Driver Cleanup")  ShowDriverCleanup();
                else if (cat == "Disk Cleanup")    ShowDiskCleanup();
                else                               PopulateGrid(cat);
            };
            return btn;
        }

        private void RefreshSidebar()
        {
            foreach (Control c in _sidebar.Controls)
            {
                if (c is Button btn && btn.Tag is string cat && CatEmoji.ContainsKey(cat))
                {
                    bool active = cat == _activeCategory;
                    btn.BackColor = active ? Theme.ACCENT : Color.Transparent;
                    btn.ForeColor = active ? Theme.ACCENT_TEXT : Theme.TEXT_DIM;
                    btn.Font      = new Font("Courier New", 8f, active ? FontStyle.Bold : FontStyle.Regular);
                    btn.FlatAppearance.MouseOverBackColor = active
                        ? Theme.ACCENT_HOV
                        : Color.FromArgb(26, 24, 53);
                }
            }
        }

private void BuildMainArea()
        {
            _mainArea = new Panel { BackColor = Theme.BG };

            BuildSearchBar();

            _tileGrid = new FlowLayoutPanel
            {
                AutoScroll    = true,
                WrapContents  = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor     = Theme.BG,
                Padding       = new Padding(12),
                Dock          = DockStyle.Fill,
            };
            _tileGrid.HorizontalScroll.Enabled = false;
            _tileGrid.HorizontalScroll.Visible = false;
            // Stock FlowLayoutPanel isn't double-buffered by default; with ~35 tiles
            // repainting at once (e.g. Select All / None), that can flash/blank the
            // grid mid-redraw. DoubleBuffered is protected, so flip it via reflection.
            typeof(Panel)
                .GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(_tileGrid, true, null);

            _histPanel = new Panel
            {
                AutoScroll = true,
                BackColor  = Theme.BG,
                Padding    = new Padding(20),
                Dock       = DockStyle.Fill,
                Visible    = false
            };

            _startupTab     = new StartupTab();
            _servicesTab    = new ServicesTab();
            _driverTab      = new DriverCleanupTab();
            _diskCleanupTab = new DiskCleanupTab();

            _mainArea.Controls.Add(_histPanel);
            _mainArea.Controls.Add(_startupTab);
            _mainArea.Controls.Add(_servicesTab);
            _mainArea.Controls.Add(_driverTab);
            _mainArea.Controls.Add(_diskCleanupTab);
            _mainArea.Controls.Add(_tileGrid);
            _mainArea.Controls.Add(_searchBar); // add last so it docks on top

            CornScrollBar.Attach(_tileGrid);
            CornScrollBar.Attach(_histPanel);
        }

private void BuildSearchBar()
        {
            _searchBar = new Panel
            {
                BackColor = Theme.SURFACE,
                Height    = Dpi.S(88),
                Dock      = DockStyle.Top,
            };
            _searchBar.Paint += (s, e) =>
            {
                using var p = new Pen(Theme.BORDER);
                e.Graphics.DrawLine(p, 0, _searchBar.Height - 1,
                    _searchBar.Width, _searchBar.Height - 1);
            };

            // ── Row 1: search box ────────────────────────────────────────────

            var searchIcon = new Label
            {
                Text      = "🔍",
                Font      = new Font("Segoe UI Emoji", 11f),
                AutoSize  = false,
                Size      = new Size(28, 32),
                Location  = new Point(10, 8),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };

            _searchBox = new TextBox
            {
                Font        = new Font("Segoe UI", 10f),
                ForeColor   = Theme.TEXT_SEC,
                BackColor   = Theme.CARD,
                BorderStyle = BorderStyle.None,
                Location    = new Point(42, 14),
                Height      = 24,
                Width       = 280,
                Text        = "Search tweaks..."
            };

            bool hasPlaceholder = true;

            _searchBox.Enter += (s, e) =>
            {
                if (hasPlaceholder)
                {
                    _searchBox.Text      = "";
                    _searchBox.ForeColor = Theme.TEXT_PRI;
                    hasPlaceholder       = false;
                }
            };
            _searchBox.Leave += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_searchBox.Text))
                {
                    _searchBox.Text      = "Search tweaks...";
                    _searchBox.ForeColor = Theme.TEXT_SEC;
                    hasPlaceholder       = true;
                }
            };
            _searchBox.TextChanged += (s, e) =>
            {
                if (hasPlaceholder) return;
                _searchQuery = _searchBox.Text.Trim().ToLower();
                _clearSearchBtn.Visible = !string.IsNullOrEmpty(_searchQuery);
                ApplySearchFilter();
            };

            _clearSearchBtn = new FlatButton("✕", Theme.SURFACE2)
            {
                Size      = new Size(22, 22),
                Location  = new Point(326, 11),
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Theme.TEXT_SEC,
                Visible   = false
            };
            _clearSearchBtn.Click += (s, e) => ClearSearch();

            var rowDivider = new Panel
            {
                BackColor = Theme.BORDER,
                Bounds    = new Rectangle(0, 42, _searchBar.Width, 1),
                Anchor    = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            _searchBar.SizeChanged += (s, e) => rowDivider.Width = _searchBar.Width;

            // ── Row 2: preset strip (FlowLayoutPanel — auto-reflows, DPI-safe) ─

            var presetLbl = new Label
            {
                Text      = "PRESETS",
                Font      = new Font("Courier New", 6.5f, FontStyle.Bold),
                ForeColor = Theme.ACCENT,
                AutoSize  = true,
                Margin    = new Padding(8, 15, 4, 0),
                BackColor = Color.Transparent,
            };

            var presetStrip = new FlowLayoutPanel
            {
                BackColor     = Color.Transparent,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Bounds        = new Rectangle(0, 43, _searchBar.Width, 44),
                Anchor        = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                Padding       = new Padding(0),
            };
            _searchBar.SizeChanged += (s, e) => presetStrip.Width = _searchBar.Width;

            static void StylePreset(FlatButton b)
            {
                b.FlatAppearance.BorderSize         = 1;
                b.FlatAppearance.BorderColor        = Color.FromArgb(204, 255, 0);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 204, 255, 0);
            }

            FlatButton MakePreset(string label, string key)
            {
                var b = new FlatButton(label, Theme.SURFACE2)
                {
                    AutoSize  = false,
                    Size      = new Size(TextRenderer.MeasureText(label,
                                    new Font("Segoe UI", 8.5f)).Width + 20, 28),
                    Margin    = new Padding(0, 7, 4, 0),
                    Font      = new Font("Segoe UI", 8.5f),
                    ForeColor = Theme.ACCENT
                };
                StylePreset(b);
                b.Click += (s, e) => ApplyPreset(key);
                return b;
            }

            presetStrip.Controls.Add(presetLbl);
            presetStrip.Controls.Add(MakePreset("⭐ Recommended",  "Recommended"));
            presetStrip.Controls.Add(MakePreset("🎮 Gaming PC",    "Gaming"));
            presetStrip.Controls.Add(MakePreset("🔒 Privacy",      "Privacy"));
            presetStrip.Controls.Add(MakePreset("🛡 Security",     "Security"));
            presetStrip.Controls.Add(MakePreset("🪶 Minimal",      "Minimal"));
            presetStrip.Controls.Add(MakePreset("💻 Laptop",       "Laptop"));
            presetStrip.Controls.Add(MakePreset("🧹 Clean Install", "CleanInstall"));
            presetStrip.Controls.Add(MakePreset("🔬 Dev Machine",  "DevMachine"));

            // Nuclear stays red — intentionally different to signal danger
            var presetNuclear = new FlatButton("☢ Nuclear", Color.FromArgb(45, 20, 20))
            {
                AutoSize  = false,
                Size      = new Size(TextRenderer.MeasureText("☢ Nuclear",
                                new Font("Segoe UI", 8.5f)).Width + 20, 28),
                Margin    = new Padding(0, 7, 8, 0),
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = Theme.DANGER
            };
            presetNuclear.FlatAppearance.BorderSize         = 1;
            presetNuclear.FlatAppearance.BorderColor        = Color.FromArgb(100, 40, 40);
            presetNuclear.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 30, 30);
            presetNuclear.Click += (s, e) => ApplyPreset("Nuclear");
            presetStrip.Controls.Add(presetNuclear);

            _searchBar.Controls.AddRange(new Control[]
            {
                searchIcon, _searchBox, _clearSearchBtn, rowDivider, presetStrip
            });
        }

private void ApplySearchFilter()
        {
            if (string.IsNullOrEmpty(_searchQuery))
            {
                // No filter — show everything normally
                foreach (Control c in _tileGrid.Controls)
                    c.Visible = true;
                UpdateSelCount();
                return;
            }

            // Track which section headers have at least one visible tile
            SectionHeader currentHeader = null;
            bool          headerHasHit  = false;

            foreach (Control c in _tileGrid.Controls)
            {
                if (c is SectionHeader hdr)
                {
                    // Finalize previous header visibility
                    if (currentHeader != null)
                        currentHeader.Visible = headerHasHit;

                    currentHeader = hdr;
                    headerHasHit  = false;
                }
                else if (c is TweakTile tile)
                {
                    bool match = tile.Entry.Name.ToLower().Contains(_searchQuery)
                              || tile.Entry.Description.ToLower().Contains(_searchQuery)
                              || tile.Entry.Category.ToLower().Contains(_searchQuery)
                              || (tile.Entry.WhatItChanges?.ToLower().Contains(_searchQuery) ?? false);

                    tile.Visible = match;
                    if (match) headerHasHit = true;
                }
            }

            // Finalize last header
            if (currentHeader != null)
                currentHeader.Visible = headerHasHit;

            UpdateSelCount();
        }

        private void ClearSearch()
        {
            _searchQuery            = "";
            _searchBox.Text         = "Search tweaks...";
            _searchBox.ForeColor    = Theme.TEXT_SEC;
            _clearSearchBtn.Visible = false;
            // Re-show everything
            foreach (Control c in _tileGrid.Controls)
                c.Visible = true;
            UpdateSelCount();
        }

private void ApplyPreset(string preset)
        {
            // If we're on History, switch to All first
            if (_activeCategory == "History" || _activeCategory == "Startup" || _activeCategory == "Driver Cleanup" || _activeCategory == "Disk Cleanup")
            {
                _activeCategory = "All";
                RefreshSidebar();
                PopulateGrid("All");
            }

            // Clear search so all tiles are visible
            ClearSearch();

            switch (preset)
            {
                case "Recommended":
                    // Select only DefaultOn tiles
                    foreach (var t in _tiles)
                        t.IsChecked = t.Entry.DefaultOn;
                    SetStatus("Preset applied: Recommended (safe defaults)", Theme.ACCENT);
                    break;

                case "Gaming":
                    // Recommended defaults + all Gaming and Network tweaks
                    foreach (var t in _tiles)
                        t.IsChecked = t.Entry.DefaultOn
                                   || t.Entry.Category == "Gaming"
                                   || t.Entry.Category == "Network";
                    SetStatus("Preset applied: Gaming PC (recommended + gaming + network)", Theme.SUCCESS);
                    break;

                case "Privacy":
                    // All Privacy tiles + recommended defaults
                    foreach (var t in _tiles)
                        t.IsChecked = t.Entry.DefaultOn
                                   || t.Entry.Category == "Privacy";
                    SetStatus("Preset applied: Privacy (recommended + all privacy tweaks)", Color.FromArgb(168, 85, 247));
                    break;

                case "Security":
                    // All Security tiles + recommended defaults
                    foreach (var t in _tiles)
                        t.IsChecked = t.Entry.DefaultOn
                                   || t.Entry.Category == "Security";
                    SetStatus("Preset applied: Security (recommended + all security tweaks)", Color.FromArgb(16, 185, 129));
                    break;

                case "Nuclear":
                    // Everything except Bloatware (irreversible) and Advanced (risky)
                    foreach (var t in _tiles)
                        t.IsChecked = t.Entry.Category != "Bloatware"
                                   && t.Entry.Category != "Advanced";
                    SetStatus("Preset applied: Nuclear — all tweaks except Bloatware & Advanced", Theme.DANGER);
                    break;

                case "Minimal":
                    // Only the lightest, safest tweaks — responsiveness + privacy basics
                    foreach (var t in _tiles)
                        t.IsChecked = t.Entry.TweakKey is
                            "Resp_MenuDelay" or "Resp_AppKill" or "Resp_ServiceKill" or
                            "Resp_AutoEndTasks" or "Resp_WinTips" or "Resp_SuggestedContent" or
                            "Priv_AdvertisingId" or "Priv_BingStart" or "Priv_ChatIcon" or
                            "Priv_Feedback" or "Priv_AppTracking" or "Priv_Recall" or
                            "Perf_StartupDelay" or "Perf_VisualFX" or "Priv_CloudContent";
                    SetStatus("Preset applied: Minimal — safe UI & privacy tweaks only", Theme.TEXT_SEC);
                    break;

                case "Laptop":
                    foreach (var t in _tiles)
                        t.IsChecked = (t.Entry.DefaultOn
                                   || t.Entry.Category == "Privacy"
                                   || t.Entry.Category == "Responsiveness"
                                   || t.Entry.Category == "Security")
                                   && t.Entry.TweakKey is not (
                                       "Perf_PowerPlan"
                                    or "Perf_Hibernate"
                                    or "Perf_MemCompression"
                                    or "Perf_PowerThrottle"
                                    or "Adv_DynamicTick"
                                    or "Game_GPUPower"
                                    or "Game_HAGS");
                    SetStatus("Preset applied: Laptop — battery-safe privacy & responsiveness tweaks", Color.FromArgb(56, 189, 248));
                    break;

                case "CleanInstall":
                    foreach (var t in _tiles)
                        t.IsChecked = t.Entry.Category == "Bloatware"
                                   || t.Entry.Category == "Privacy"
                                   || t.Entry.TweakKey is
                                       "Sec_Defender" or "Sec_NetBIOS" or "Sec_RDP"
                                    or "Resp_WinTips" or "Resp_SuggestedContent"
                                    or "Perf_StartupDelay";
                    SetStatus("Preset applied: Clean Install — bloatware removed, telemetry killed, baseline secured", Color.FromArgb(52, 211, 153));
                    break;

                case "DevMachine":
                    foreach (var t in _tiles)
                        t.IsChecked = (t.Entry.Category == "Performance"
                                   || t.Entry.Category == "Responsiveness"
                                   || t.Entry.Category == "Network"
                                   || t.Entry.TweakKey is
                                       "Adv_ProcessorScheduling" or "Adv_CPUThrottle"
                                    or "Adv_DynamicTick"         or "Adv_TRIM"
                                    or "Adv_Animations"          or "Priv_Telemetry"
                                    or "Priv_TelemetryTasks"     or "Priv_DiagTrack"
                                    or "Priv_Feedback"           or "Priv_WER"
                                    or "Game_CPUPriority")
                                   && t.Entry.TweakKey is not (
                                       "Perf_WSearch"
                                    or "Perf_Hibernate"
                                    or "Game_DVR"
                                    or "Game_HAGS"
                                    or "Game_FSO"
                                    or "Game_GameMode"
                                    or "Game_NvidiaTelemetry");
                    SetStatus("Preset applied: Dev Machine — max performance, keeps Search & Hibernate", Color.FromArgb(167, 139, 250));
                    break;
            }

            UpdateSelCount();
        }

private static readonly string[] CategoryOrder =
        {
            "Performance", "Privacy", "Responsiveness",
            "Gaming", "Network", "Bloatware", "Security", "Advanced"
        };

        private void PopulateGrid(string filter)
        {
            _histPanel.Visible      = false;
            _startupTab.Visible     = false;
            _servicesTab.Visible    = false;
            _driverTab.Visible      = false;
            _diskCleanupTab.Visible = false;
            _tileGrid.Visible       = true;
            _searchBar.Visible      = true;

            _tileGrid.SuspendLayout();
            _tileGrid.Controls.Clear();
            _tiles.Clear();

            IEnumerable<TweakEntry> source = filter == "All"
                ? TweakCatalog.All
                : TweakCatalog.All.Where(t => t.Category == filter);

            var groups = source
                .GroupBy(t => t.Category)
                .OrderBy(g => Array.IndexOf(CategoryOrder, g.Key));

            SectionHeader.ResetIndex();
            foreach (var group in groups)
            {
                string emoji = CatEmoji.TryGetValue(group.Key, out var em) ? em : "📦";

                var hdr = new SectionHeader(group.Key, emoji);
                _tileGrid.Controls.Add(hdr);

                foreach (var entry in group)
                {
                    var tile = new TweakTile(entry);
                    // Restore whatever the user last set this tweak to; only fall back
                    // to DefaultOn the first time a tile is ever created.
                    tile.IsChecked = _selectionState.TryGetValue(entry.TweakKey, out var wasChecked)
                        ? wasChecked
                        : entry.DefaultOn;
                    // Mark already-applied tiles (applied by app in a previous session)
                    if (AppliedState.IsApplied(entry.TweakKey))
                        tile.SetApplied(AppliedSource.AppliedByApp);
                    tile.CheckedChanged += (s, e_) =>
                    {
                        _selectionState[entry.TweakKey] = tile.IsChecked;
                        UpdateSelCount();
                        SetStatus("Ready", Theme.TEXT_SEC);
                    };
                    // Tooltip wiring
                    tile.MouseEnter += (s, e_) => ShowTooltip((TweakTile)s);
                    tile.MouseLeave += (s, e_) => HideTooltip();
                    foreach (Control child in tile.Controls)
                    {
                        child.MouseEnter += (s, e_) => ShowTooltip(tile);
                        child.MouseLeave += (s, e_) => HideTooltip();
                    }
                    _tiles.Add(tile);
                    _tileGrid.Controls.Add(tile);
                }
            }

            _tileGrid.ResumeLayout(true);
            UpdateSelCount();

            // Kick off background live-system detection scan
            _ = RunDetectionScanAsync();
        }

        private void SetAllInView(bool check)
        {
            _tileGrid.SuspendLayout();
            foreach (var t in _tiles.Where(t => t.Visible))
                t.IsChecked = check;
            _tileGrid.ResumeLayout(true);
            UpdateSelCount();
        }

        private void UpdateSelCount()
        {
            // Count only visible checked tiles so search doesn't confuse the counter
            int count = _tiles.Count(t => t.IsChecked && t.Visible);
            if (_selCountLabel == null) return;
            // Repaint sidebar buttons so badges refresh
            foreach (Control c in _sidebar.Controls)
                if (c is Button) c.Invalidate();

            _selCountLabel.Text = count == 0
                ? "No tweaks selected"
                : $"{count} tweak{(count == 1 ? "" : "s")} selected";

            if (_undoBtn != null)
            {
                var cats = _tiles.Where(t => t.IsChecked).Select(t => t.Entry.Category).Distinct();
                _undoBtn.Enabled = cats.Any(c => TweakEngine.HasBackup(c));
            }
        }

        // Hides every full-page view; callers then flip on the one they want.
        private void HideAllTabs()
        {
            _tileGrid.Visible       = false;
            _searchBar.Visible      = false;
            _histPanel.Visible      = false;
            _startupTab.Visible     = false;
            _servicesTab.Visible    = false;
            _driverTab.Visible      = false;
            _diskCleanupTab.Visible = false;
        }

        private void ShowHistory()
        {
            HideAllTabs();
            _histPanel.Visible = true;
            BuildHistoryContent();
        }

        private void ShowStartup()
        {
            HideAllTabs();
            _startupTab.Visible = true;
            _startupTab.Activate();
        }

        private void ShowServices()
        {
            HideAllTabs();
            _servicesTab.Visible = true;
            _servicesTab.Activate();
        }

        private void ShowDriverCleanup()
        {
            HideAllTabs();
            _driverTab.Visible = true;
            _driverTab.Activate();
        }

        private void ShowDiskCleanup()
        {
            HideAllTabs();
            _diskCleanupTab.Visible = true;
            _diskCleanupTab.Activate();
        }

        // Scans the live system state for each tile in the background.
        // Tiles that are already applied by the app take priority; only
        // tiles with no known state get checked against the live system.
        private async Task RunDetectionScanAsync()
        {
            // Snapshot the current tile list so the scan isn't affected by
            // the user switching categories mid-scan
            var snapshot = _tiles.ToList();

            await Task.Run(() =>
            {
                foreach (var tile in snapshot)
                {
                    // Already marked as applied by app — no need to overwrite
                    if (AppliedState.IsApplied(tile.Entry.TweakKey)) continue;

                    bool? detected = TweakDetector.Check(tile.Entry.TweakKey);

                    if (detected == true)
                    {
                        // Also persist to AppliedState so it survives future sessions
                        AppliedState.MarkApplied(new[] { tile.Entry.TweakKey });

                        try
                        {
                            Invoke(new Action(() =>
                            {
                                tile.SetApplied(AppliedSource.DetectedOnSystem);
                            }));
                        }
                        catch { /* Form may have closed */ }
                    }
                }
            });
        }

        private void BuildHistoryContent()
        {
            _histPanel.Controls.Clear();
            int y = 0;

            var topRow = new Panel
            {
                Left      = 0, Top = y, Height = 44,
                Width     = _histPanel.ClientSize.Width,
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent
            };
            var titleLbl = new Label
            {
                Text      = "// RUN HISTORY",
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Theme.TEXT_SEC,
                AutoSize  = true,
                Location  = new Point(0, 13)
            };
            var clearBtn = new FlatButton("Clear History", Theme.DANGER)
            {
                Size      = new Size(120, 28),
                Anchor    = AnchorStyles.Right | AnchorStyles.Top,
                ForeColor = Color.White
            };
            topRow.SizeChanged += (s, e) => clearBtn.Location = new Point(topRow.Width - 124, 8);
            clearBtn.Location   = new Point(topRow.Width - 124, 8);
            clearBtn.Click += (s, e) =>
            {
                if (MessageBox.Show("Clear all run history?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                { ChangeLog.Clear(); BuildHistoryContent(); }
            };
            topRow.Controls.Add(titleLbl);
            topRow.Controls.Add(clearBtn);
            _histPanel.Controls.Add(topRow);
            y += 52;

            if (ChangeLog.Entries.Count == 0)
            {
                _histPanel.Controls.Add(new Label
                {
                    Text      = "No runs recorded yet. Apply some tweaks to start tracking history.",
                    Font      = new Font("Segoe UI", 10f),
                    ForeColor = Theme.TEXT_SEC,
                    AutoSize  = true,
                    Location  = new Point(0, y + 10)
                });
                return;
            }

            foreach (var entry in ChangeLog.Entries)
            {
                var card = new Panel
                {
                    Left      = 0, Top = y,
                    Width     = _histPanel.ClientSize.Width - 4,
                    BackColor = Theme.SURFACE,
                    Padding   = new Padding(14, 10, 14, 10),
                    Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                card.Paint += (s, e) =>
                {
                    using var pen    = new Pen(Theme.BORDER);
                    using var stripe = new SolidBrush(Theme.ACCENT);
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                    e.Graphics.FillRectangle(stripe, 0, 0, 3, card.Height);
                };

                Label HL(string text, float size, FontStyle fs, Color fg, int y)
                    => new Label { Text=text, Font=new Font("Segoe UI",size,fs), ForeColor=fg,
                                   AutoSize=true, Location=new Point(14,y), BackColor=Color.Transparent };
                Color sc = entry.Failed == 0 ? Theme.SUCCESS : Theme.WARNING;
                string stText = $"✔ {entry.Passed} succeeded   ✘ {entry.Failed} failed"
                              + (entry.RestorePoint ? "   🛡 Restore Point" : "");
                card.Controls.Add(HL(entry.Timestamp,                9f,  FontStyle.Bold,    Theme.ACCENT,   10));
                card.Controls.Add(HL(entry.WindowsVer,               8.5f,FontStyle.Regular, Theme.TEXT_SEC, 28));
                card.Controls.Add(HL($"Categories: {entry.Categories}",9f,FontStyle.Regular, Theme.TEXT_PRI, 46));
                card.Controls.Add(HL(stText,                         9f,  FontStyle.Bold,    sc,             64));

                int cardH = 86;
                if (entry.Details.Count > 0)
                {
                    string dt = string.Join("  ·  ", entry.Details.Take(8))
                        + (entry.Details.Count > 8 ? $"  … +{entry.Details.Count - 8} more" : "");
                    var dL = new Label
                    {
                        Text      = dt,
                        Font      = new Font("Consolas", 7.5f),
                        ForeColor = Theme.TEXT_SEC,
                        AutoSize  = false,
                        Width     = card.Width - 30,
                        Height    = 30,
                        Location  = new Point(14, 82),
                        BackColor = Color.Transparent
                    };
                    card.Controls.Add(dL);
                    cardH = 118;
                }

                card.Height = cardH;
                _histPanel.Controls.Add(card);
                y += cardH + 8;
            }
        }

private void BuildBottomBar()
        {
            _bottomBar = new Panel { BackColor = Theme.SURFACE, Height = Dpi.S(136) };
            _bottomBar.Paint += (s, e) =>
            {
                using var p = new Pen(Theme.BORDER);
                e.Graphics.DrawLine(p, 0, 0, _bottomBar.Width, 0);
            };

            _progOuter = new Panel
            {
                BackColor = Theme.BORDER,
                Location  = new Point(Dpi.S(16), Dpi.S(12)),
                Size      = new Size(Dpi.S(500), Dpi.S(6))
            };
            _progInner = new Panel
            {
                BackColor = Theme.ACCENT,
                Location  = Point.Empty,
                Size      = new Size(0, Dpi.S(6))
            };
            _progOuter.Controls.Add(_progInner);

            _statusLabel = new Label
            {
                Text      = "Ready",
                ForeColor = Theme.TEXT_DIM,
                AutoSize  = true,
                Location  = new Point(Dpi.S(16), Dpi.S(24))
            };

            _selCountLabel = new Label
            {
                Text      = "No tweaks selected",
                ForeColor = Theme.TEXT_DIM,
                AutoSize  = true,
                Location  = new Point(Dpi.S(16), Dpi.S(46))
            };

            _restoreChk = new CheckBox
            {
                Text      = "🛡 Create Restore Point before running",
                ForeColor = Theme.TEXT_DIM,
                BackColor = Color.Transparent,
                Checked   = true,
                AutoSize  = true,
                Location  = new Point(Dpi.S(16), Dpi.S(92)),
                FlatStyle = FlatStyle.Flat
            };
            _restoreChk.FlatAppearance.BorderColor        = Theme.BORDER;
            _restoreChk.FlatAppearance.CheckedBackColor   = Theme.ACCENT;
            _restoreChk.FlatAppearance.MouseOverBackColor = Theme.SURFACE;

            _undoBtn = new FlatButton("↩ Undo Selected", Theme.SURFACE2)
            {
                Size    = new Size(Dpi.S(150), Dpi.S(36)),
                Enabled = false
            };
            _undoBtn.Click += OnUndoClicked;

            _clearBtn = new FlatButton("Clear Selection", Theme.SURFACE2)
                { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            _clearBtn.Click += (s, e) => SetAllInView(false);

            _runBtn = new FlatButton("⚡  RUN SELECTED", Theme.ACCENT)
            {
                Size      = new Size(Dpi.S(165), Dpi.S(36)),
                Font      = new Font("Courier New", 8.5f, FontStyle.Bold),
                ForeColor = Theme.ACCENT_TEXT
            };
            _runBtn.Click += OnRunClicked;

            var logToggle = new FlatButton("📋 Log", Theme.SURFACE2)
                { Size = new Size(Dpi.S(70), Dpi.S(26)) };
            logToggle.Click += (s, e) => ToggleLog();

            // Shell/UI tweaks (visual effects, menu delay, taskbar icons) only
            // need Explorer restarted — this makes those feel instant instead
            // of waiting on a full reboot.
            var explorerBtn = new FlatButton("⟳ Restart Explorer", Theme.SURFACE2)
                { Size = new Size(Dpi.S(140), Dpi.S(26)), ForeColor = Theme.TEXT_SEC };
            explorerBtn.Click += (s, e) =>
            {
                if (MessageBox.Show(
                        "Restart Windows Explorer now?\n\nThe taskbar and open folder windows " +
                        "will briefly disappear and come back. Unsaved work in other apps is not affected.",
                        "Restart Explorer", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                    != DialogResult.Yes) return;

                if (ExplorerHelper.Restart(out string err))
                {
                    AppendLog("⟳ Explorer restarted.");
                    ClearPendingImpact(RebootImpact.ExplorerRestart);
                }
                else
                {
                    AppendLog($"✘ Explorer restart failed: {err}");
                }
            };

            // Amber badge listing exactly what's waiting on a reboot / Explorer
            // restart after a run — click it for the full tweak list.
            _rebootBadge = new Label
            {
                Text      = "",
                Font      = new Font("Courier New", 7.5f, FontStyle.Bold),
                ForeColor = Theme.WARNING,
                BackColor = Color.FromArgb(30, Theme.WARNING.R, Theme.WARNING.G, Theme.WARNING.B),
                AutoSize  = true,
                Padding   = new Padding(6, 3, 6, 3),
                Location  = new Point(Dpi.S(16), Dpi.S(66)),
                Cursor    = Cursors.Hand,
                Visible   = false
            };
            _rebootBadge.Click += (s, e) =>
            {
                string msg = "";
                if (_pendingReboot.Count > 0)
                    msg += "Waiting on a REBOOT:\n  • " + string.Join("\n  • ", _pendingReboot) + "\n\n";
                if (_pendingExplorer.Count > 0)
                    msg += "Waiting on an EXPLORER RESTART:\n  • " + string.Join("\n  • ", _pendingExplorer);
                if (msg.Length > 0)
                    MessageBox.Show(msg.TrimEnd(), "Pending Changes",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            _exportBtn = new FlatButton("↑ Export Profile", Theme.SURFACE2)
                { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            _exportBtn.Click += (s, e) =>
            {
                var keys = _tiles.Where(t => t.IsChecked).Select(t => t.Entry.TweakKey);
                TweakProfile.Export(keys);
            };

            _importBtn = new FlatButton("↓ Import Profile", Theme.SURFACE2)
                { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            _importBtn.Click += (s, e) =>
            {
                var keys = TweakProfile.Import();
                if (keys == null) return;
                // Switch to All view so all tiles are visible
                if (_activeCategory is "History" or "Startup" or "Services" or "Driver Cleanup" or "Disk Cleanup")
                {
                    _activeCategory = "All";
                    RefreshSidebar();
                    PopulateGrid("All");
                }
                ClearSearch();
                var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                foreach (var t in _tiles)
                    t.IsChecked = keySet.Contains(t.Entry.TweakKey);
                SetStatus($"Profile imported — {keySet.Count} tweaks selected.", Theme.ACCENT);
                UpdateSelCount();
            };

            _bottomBar.SizeChanged += (s, e) =>
            {
                int r = _bottomBar.Width - Dpi.S(16);
                _runBtn.Location    = new Point(r - Dpi.S(160), Dpi.S(82));
                _clearBtn.Location  = new Point(r - Dpi.S(300), Dpi.S(82));
                _undoBtn.Location   = new Point(r - Dpi.S(460), Dpi.S(82));
                _exportBtn.Location = new Point(r - Dpi.S(606), Dpi.S(82));
                _importBtn.Location = new Point(r - Dpi.S(750), Dpi.S(82));
                logToggle.Location   = new Point(r - Dpi.S(76),  Dpi.S(12));
                explorerBtn.Location = new Point(r - Dpi.S(76) - Dpi.S(6) - explorerBtn.Width, Dpi.S(12));
                _progOuter.Width     = Math.Max(Dpi.S(200), r - Dpi.S(96) - Dpi.S(6) - explorerBtn.Width);
            };

            _bottomBar.Controls.AddRange(new Control[]
            {
                _progOuter, _statusLabel, _selCountLabel, _rebootBadge,
                _restoreChk, _undoBtn, _clearBtn, _exportBtn, _importBtn, _runBtn,
                logToggle, explorerBtn
            });
        }

private void BuildLogPanel()
        {
            _logBox = new RichTextBox
            {
                BackColor   = Color.FromArgb(8, 8, 20),
                ForeColor   = Color.FromArgb(180, 200, 255),
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                Font        = new Font("Consolas", 8.5f),
                Dock        = DockStyle.Fill,
                ScrollBars  = RichTextBoxScrollBars.Vertical
            };

            _logPanel = new Panel
            {
                BackColor = Color.FromArgb(8, 8, 20),
                Visible   = false,
                Height    = Dpi.S(180)
            };

            var hdr = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.SURFACE };
            var hdrLbl = new Label
            {
                Text      = "// OUTPUT LOG",
                Font      = new Font("Courier New", 7.5f, FontStyle.Bold),
                ForeColor = Theme.TEXT_SEC,
                AutoSize  = true,
                Location  = new Point(10, 6),
                BackColor = Color.Transparent
            };
            var closeBtn = new FlatButton("✕", Theme.SURFACE)
            {
                Size      = new Size(24, 24),
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Theme.TEXT_SEC
            };
            closeBtn.Click += (s, e) => { _logPanel.Visible = false; LayoutAll(); };
            hdr.SizeChanged += (s, e) => closeBtn.Location = new Point(hdr.Width - 26, 1);
            hdr.Controls.AddRange(new Control[] { hdrLbl, closeBtn });

            _logPanel.Controls.Add(_logBox);
            _logPanel.Controls.Add(hdr);
        }

        private void ToggleLog()
        {
            _logPanel.Visible = !_logPanel.Visible;
            LayoutAll();
        }

private void BuildTooltip()
        {
            _ttTitle = new Label
            {
                Font      = new Font("Segoe UI Semibold", 9f),
                ForeColor = Theme.TEXT_PRI,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(Dpi.S(20), Dpi.S(10)),
                Size      = new Size(Dpi.S(268), Dpi.S(18))
            };

            _ttWhat = new Label
            {
                Font      = new Font("Consolas", 7.5f),
                ForeColor = Theme.TEXT_SEC,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(Dpi.S(20), Dpi.S(32)),
                Size      = new Size(Dpi.S(268), Dpi.S(120))
            };

            _tooltip = new Panel
            {
                BackColor = Color.FromArgb(13, 12, 28),
                Size      = new Size(Dpi.S(300), 0),
                Visible   = false,
                Padding   = new Padding(Dpi.S(12))
            };
            _tooltip.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(80, Theme.ACCENT.R, Theme.ACCENT.G, Theme.ACCENT.B), 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, _tooltip.Width - 1, _tooltip.Height - 1);
                using var bar = new SolidBrush(Theme.ACCENT);
                e.Graphics.FillRectangle(bar, 0, 0, 3, _tooltip.Height);
            };
            _tooltip.Controls.Add(_ttTitle);
            _tooltip.Controls.Add(_ttWhat);

            // Hide timer — small delay so moving between tiles doesn't flicker
            _ttHideTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _ttHideTimer.Tick += (s, e) => { _ttHideTimer.Stop(); _tooltip.Visible = false; };

            Controls.Add(_tooltip);
            _tooltip.BringToFront();
        }

        private void ShowTooltip(TweakTile tile)
        {
            _ttHideTimer.Stop();
            if (string.IsNullOrEmpty(tile.Entry.WhatItChanges)) return;

            _ttTitle.Text = tile.Entry.Name;

            // WhatItChanges uses literal \n — split into cmd line + explanation
            string raw   = tile.Entry.WhatItChanges;
            int    split = raw.IndexOf("\n");
            if (split >= 0)
            {
                _ttWhat.Font      = new Font("Consolas", 7.5f);
                string cmdLine    = raw.Substring(0, split).Trim();
                string explain    = raw.Substring(split + 1).Trim();
                _ttWhat.Text      = cmdLine + "\n\n" + explain;
                _ttWhat.ForeColor = Theme.TEXT_SEC;
            }
            else
            {
                _ttWhat.Text      = raw;
                _ttWhat.ForeColor = Theme.TEXT_SEC;
            }

            // Measure required height dynamically
            using (var g = _ttWhat.CreateGraphics())
            {
                var szF          = g.MeasureString(_ttWhat.Text, _ttWhat.Font, _ttWhat.Width);
                int txtH         = (int)szF.Height + 8;
                _ttWhat.Height   = Math.Max(txtH, 30);
                _tooltip.Height  = _ttWhat.Top + _ttWhat.Height + 16;
            }

            // Position to the right of tile, flip left if needed, clamp vertically
            Point screenPt   = tile.PointToScreen(Point.Empty);
            Point clientPt   = PointToClient(screenPt);
            int   tx         = clientPt.X + tile.Width + 6;
            int   ty         = clientPt.Y;
            if (tx + _tooltip.Width > ClientSize.Width - 10)
                tx = clientPt.X - _tooltip.Width - 6;
            if (ty + _tooltip.Height > ClientSize.Height - _bottomBar.Height - 10)
                ty = ClientSize.Height - _bottomBar.Height - _tooltip.Height - 10;

            _tooltip.Location = new Point(tx, ty);
            _tooltip.Visible  = true;
            _tooltip.BringToFront();
        }

        private void HideTooltip() => _ttHideTimer.Start();

private async void OnRunClicked(object sender, EventArgs e)
        {
            if (_isRunning) return;

            var selected = _tiles.Where(t => t.IsChecked).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select at least one tweak to run.",
                    "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _isRunning      = true;
            _runBtn.Enabled = false;
            _runBtn.Text    = "⏳ Running...";
            TweakEngine.ClearResults();

            bool rpCreated = false;
            if (_restoreChk.Checked)
            {
                SetStatus("Creating System Restore Point…", Theme.WARNING);
                AppendLog("🛡 Creating System Restore Point…");
                bool ok = await Task.Run(() =>
                    TweakEngine.CreateRestorePoint("Win11Optimizer — before tweaks"));
                AppendLog(ok ? "🛡 Restore Point created." : "⚠ Restore Point failed or skipped.");
                rpCreated = ok;
            }

            _totalTweaks = selected.Count;
            _doneTweaks  = 0;
            SetProgress(0, _totalTweaks);

            var catNames   = new List<string>();
            var logDetails = new List<string>();
            int prevCount  = 0;

            void LogTweak(string tileName)
            {
                var all = TweakEngine.GetResults();
                var sec = all.Skip(prevCount).ToList();
                prevCount = all.Count;
                foreach (var r in sec)
                {
                    AppendLog(r.Success ? $"  ✔  {r.Name}" : $"  ✘  {r.Name}: {r.Error}");
                    logDetails.Add((r.Success ? "✔ " : "✘ ") + r.Name);
                }
            }

            await Task.Run(() =>
            {
                var ordered = selected
                    .OrderBy(t => Array.IndexOf(new[] {
                        "Performance","Privacy","Responsiveness",
                        "Gaming","Network","Bloatware","Security","Advanced"
                    }, t.Entry.Category))
                    .ToList();

                string lastCat = null;
                foreach (var tile in ordered)
                {
                    var entry = tile.Entry;
                    if (entry.Category != lastCat)
                    {
                        if (lastCat != null)
                            Invoke(new Action(() => AppendLog("└─────────────────────────────────────")));
                        Invoke(new Action(() => AppendLog($"┌─ {entry.Category.ToUpper()}")));
                        if (!catNames.Contains(entry.Category))
                            catNames.Add(entry.Category);
                        lastCat = entry.Category;
                    }

                    // Mark tile as running right before it executes (not all at once up front)
                    Invoke(new Action(() =>
                    {
                        tile.SetStatus(TileStatus.Running);
                        _doneTweaks++;
                        SetProgress(_doneTweaks, _totalTweaks);
                        SetStatus($"[{_doneTweaks}/{_totalTweaks}]  {entry.Category} → {entry.Name}", Theme.WARNING);
                        AppendLog($"  → {entry.Name}…");
                        // Scroll the tile into view
                        _tileGrid.ScrollControlIntoView(tile);
                    }));

                    if (entry.Category == "Bloatware")
                        TweakEngine.ApplyBloatwareTweak(entry.TweakKey);
                    else if (entry.IsAdvanced && entry.AdvancedKey != null)
                        TweakEngine.ApplyAdvancedTweak(entry.AdvancedKey);
                    else
                        TweakEngine.ApplyTweak(entry.TweakKey);

                    Invoke(new Action(() => LogTweak(entry.Name)));
                }
                if (lastCat != null)
                    Invoke(new Action(() => AppendLog("└─────────────────────────────────────")));
            });

            var results = TweakEngine.GetResults();
            int pass = results.Count(r => r.Success);
            int fail = results.Count(r => !r.Success);

            foreach (var t in selected)
            {
                t.SetStatus(TileStatus.Done);
                t.SetApplied(AppliedSource.AppliedByApp);
            }

            // Persist per-tweak applied state
            AppliedState.MarkApplied(selected.Select(t => t.Entry.TweakKey));

            SetProgress(_totalTweaks, _totalTweaks);
            SetStatus($"Complete — {pass} succeeded, {fail} failed.",
                fail == 0 ? Theme.SUCCESS : Theme.WARNING);

            // Classify exactly what's pending instead of a blanket "reboot recommended"
            var (needReboot, needExplorer) = RebootInfo.Split(selected.Select(t => t.Entry));
            foreach (var n in needReboot)   if (!_pendingReboot.Contains(n))   _pendingReboot.Add(n);
            foreach (var n in needExplorer) if (!_pendingExplorer.Contains(n)) _pendingExplorer.Add(n);
            RefreshRebootBadge();

            string pendingNote =
                  needReboot.Count   > 0 ? $" {needReboot.Count} tweak(s) need a reboot."
                : needExplorer.Count > 0 ? $" {needExplorer.Count} tweak(s) need an Explorer restart."
                : " No reboot needed.";
            AppendLog($"══ COMPLETE: {pass} succeeded, {fail} failed.{pendingNote} ══");

            ChangeLog.AddEntry(new ChangeLog.RunEntry
            {
                Categories   = string.Join(", ", catNames),
                Passed       = pass,
                Failed       = fail,
                RestorePoint = rpCreated,
                Details      = logDetails
            });

            _runBtn.Text    = "⚡  Run Selected";
            _runBtn.Enabled = true;
            _isRunning      = false;
            UpdateSelCount();

            // Only nag about rebooting when something applied actually needs one
            if (needReboot.Count > 0)
                PromptReboot(needReboot);
            else if (needExplorer.Count > 0 &&
                     MessageBox.Show(
                         $"{needExplorer.Count} tweak(s) take effect after an Explorer restart.\n\n" +
                         "Restart Explorer now? (Taskbar and folder windows reload — other apps are unaffected.)",
                         "Explorer Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                         MessageBoxDefaultButton.Button1) == DialogResult.Yes)
            {
                if (ExplorerHelper.Restart(out string err))
                {
                    AppendLog("⟳ Explorer restarted.");
                    ClearPendingImpact(RebootImpact.ExplorerRestart);
                }
                else AppendLog($"✘ Explorer restart failed: {err}");
            }
        }

private async void OnUndoClicked(object sender, EventArgs e)
        {
            var undoCats = _tiles
                .Where(t => t.IsChecked && TweakEngine.HasBackup(t.Entry.Category))
                .Select(t => t.Entry.Category)
                .Distinct()
                .ToList();

            if (undoCats.Count == 0) return;
            _undoBtn.Enabled = false;

            foreach (var cat in undoCats)
            {
                AppendLog($"↩ Undoing {cat}…");
                List<TweakEngine.TweakResult> res = null;

                await Task.Run(() =>
                {
                    res = cat switch
                    {
                        "Performance"    => TweakEngine.UndoPerformanceTweaks(),
                        "Privacy"        => TweakEngine.UndoPrivacyTweaks(),
                        "Responsiveness" => TweakEngine.UndoResponsivenessTweaks(),
                        "Gaming"         => TweakEngine.UndoGamingTweaks(),
                        "Network"        => TweakEngine.UndoNetworkTweaks(),
                        "Advanced"       => TweakEngine.UndoAdvancedTweaks(),
                        "Security"       => TweakEngine.UndoSecurityTweaks(),
                        _                => new List<TweakEngine.TweakResult>()
                    };
                });

                AppendLog($"  ↩ {cat} done — {res.Count(r => r.Success)} restored.");
            }

            SetStatus("Undo complete. Reboot recommended.", Theme.SUCCESS);
            // Remove undone tweaks from persisted applied state
            var undoneKeys = _tiles
                .Where(t => t.IsChecked && undoCats.Contains(t.Entry.Category))
                .Select(t => t.Entry.TweakKey);
            AppliedState.MarkUndone(undoneKeys);
            UpdateSelCount();
        }

private void SetStatus(string msg, Color col = default)
        {
            if (InvokeRequired) { Invoke(new Action(() => SetStatus(msg, col))); return; }
            _statusLabel.Text      = msg;
            _statusLabel.ForeColor = col == default ? Theme.TEXT_SEC : col;
        }

        private void SetProgress(int done, int total)
        {
            if (InvokeRequired) { Invoke(new Action(() => SetProgress(done, total))); return; }
            int w = total == 0 ? 0 : (int)((double)_progOuter.Width * done / total);
            _progInner.Width = w;
        }

        private void AppendLog(string msg)
        {
            if (_logBox.InvokeRequired)
            { _logBox.Invoke(new Action(() => AppendLog(msg))); return; }
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            _logBox.ScrollToCaret();
        }

        private void PromptReboot(List<string> pendingNames)
        {
            string list = pendingNames.Count <= 6
                ? "\n\nWaiting on the reboot:\n  • " + string.Join("\n  • ", pendingNames)
                : $"\n\n{pendingNames.Count} applied tweaks are waiting on the reboot.";

            if (MessageBox.Show(
                    $"Some tweaks require a reboot to take full effect.{list}\n\nWould you like to reboot now?",
                    "Reboot Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("shutdown.exe",
                    "/r /t 10 /c \"Win11 Optimizer: Rebooting to apply tweaks.\"")
                    { UseShellExecute = false, CreateNoWindow = true });
        }

        private void RefreshRebootBadge()
        {
            if (InvokeRequired) { Invoke(new Action(RefreshRebootBadge)); return; }

            if (_pendingReboot.Count == 0 && _pendingExplorer.Count == 0)
            {
                _rebootBadge.Visible = false;
                return;
            }

            var parts = new List<string>();
            if (_pendingReboot.Count > 0)
                parts.Add($"{_pendingReboot.Count} awaiting reboot");
            if (_pendingExplorer.Count > 0)
                parts.Add($"{_pendingExplorer.Count} awaiting Explorer restart");

            _rebootBadge.Text    = "⚠ " + string.Join("  ·  ", parts) + "  (click for list)";
            _rebootBadge.Visible = true;
        }

        private void ClearPendingImpact(RebootImpact kind)
        {
            if (kind == RebootImpact.ExplorerRestart) _pendingExplorer.Clear();
            if (kind == RebootImpact.Reboot)          _pendingReboot.Clear();
            RefreshRebootBadge();
        }
    }

}
