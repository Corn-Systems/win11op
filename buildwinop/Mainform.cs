using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper — byte-identical across Corn Systems repos

namespace Win11Optimizer
{
    public class MainForm : Form
    {
        private const string RunText    = "⚡  RUN SELECTED";
        private const string SearchHint = "Search tweaks...";
        private const string Rule       = "└─────────────────────────────────────";

        private static readonly string[] SidebarCategories =
            ["All", .. TweakCatalog.CategoryOrder, "Startup", "Services", "History", "Driver Cleanup", "Disk Cleanup"];

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
            ["Security"]       = "🛡",
            ["Laptop"]         = "💻",
            ["Startup"]        = "🚀",
            ["Services"]       = "🛠",
            ["History"]        = "📋",
            ["Driver Cleanup"] = "🔧",
            ["Disk Cleanup"]   = "🧹",
        };

        private Panel           _topBar, _sidebar, _mainArea, _bottomBar, _logPanel, _searchBar, _histPanel;
        private Panel           _progOuter, _progInner, _tooltip;
        private Label           _adminBadge, _updateBadge, _rebootBadge, _statusLabel, _selCountLabel, _ttTitle, _ttWhat;
        private TextBox         _searchBox;
        private FlatButton      _clearSearchBtn, _runBtn, _undoBtn;
        private CheckBox        _restoreChk;
        private FlowLayoutPanel _tileGrid;
        private RichTextBox     _logBox;
        private System.Windows.Forms.Timer _ttHideTimer;
        private Dictionary<string, ListTab> _tabs;

        private string _activeCategory = "All";
        private string _searchQuery    = "";
        // Field (not a local) so ClearSearch can reset it — otherwise clearing after a
        // search wrote the placeholder text back as a real query and hid every tile.
        private bool _searchHasPlaceholder = true;
        private bool _isRunning;
        private CancellationTokenSource _scanCts;

        private readonly List<TweakTile> _tiles = new();
        // Persists each tweak's checked state across grid rebuilds (category switches,
        // preset applies, profile loads) so Select All/None and manual toggles survive
        // a re-render instead of snapping back to DefaultOn every time.
        private readonly Dictionary<string, bool> _selectionState = new();
        // Tweak names applied this session that are still waiting on a reboot
        // or an Explorer restart to take full effect.
        private readonly List<string> _pendingReboot = new(), _pendingExplorer = new();

        public MainForm(bool showAdminWarning = false)
        {
            InitUI();
            DarkTitleBar.Apply(this);
            PopulateGrid("All");
            _adminBadge.Visible = showAdminWarning;
            CheckWhatsNew();
            _ = CheckForUpdatesAsync();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _scanCts?.Cancel();
            _ttHideTimer.Dispose();
            base.OnFormClosed(e);
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
            catch (Exception ex) { SessionLog.Write("UPDATE BADGE", ex); }   // form closed mid-check
        }

        // Offers the release notes once after an upgrade — not on a fresh install, which has no last_version.txt yet.
        private static void CheckWhatsNew()
        {
            string current = AppVersion.Current, file = AppPaths.LastVersionFile;
            try
            {
                string last = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                if (last == current) return;
                File.WriteAllText(file, current);
                if (last.Length > 0 && MessageBox.Show(
                        $"Win11 Optimizer has been updated to v{current}!\n\nWould you like to see what's new?",
                        "What's New in v" + current, MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                    Proc.Open($"{AppVersion.RepoUrl}/releases/tag/v{current}");
            }
            catch (Exception ex) { SessionLog.Write("WHATS NEW", ex); }
        }

        private void InitUI()
        {
            Dpi.Update(this);
            AutoScaleMode = AutoScaleMode.None;
            Text          = "Win11 Optimizer";
            Size          = new Size(Dpi.S(1200), Dpi.S(760));
            MinimumSize   = new Size(Dpi.S(960),  Dpi.S(620));
            BackColor     = Theme.BG;
            ForeColor     = Theme.TEXT_PRI;
            Font          = Theme.Ui(9f);
            StartPosition = FormStartPosition.CenterScreen;

            BuildTopBar();
            BuildSidebar();
            BuildMainArea();
            BuildBottomBar();
            BuildLogPanel();
            Controls.AddRange(new Control[] { _mainArea, _sidebar, _bottomBar, _topBar, _logPanel });
            BuildTooltip();

            // The log panel floats over the main area rather than docking, so re-seat it on resize
            Resize += (s, e) => LayoutAll();
            DpiChanged += (s, e) =>
            {
                Dpi.Update(e.DeviceDpiNew);
                if (e.SuggestedRectangle != Rectangle.Empty) Bounds = e.SuggestedRectangle;
                MinimumSize = new Size(Dpi.S(960), Dpi.S(620));
                RescalePanels();
            };
        }

        private void RescalePanels()
        {
            SuspendLayout();
            _topBar.Height    = Dpi.S(60);
            _bottomBar.Height = Dpi.S(136);
            _sidebar.Width    = Dpi.S(210);
            _logPanel.Height  = Dpi.S(180);   // hidden or not, so it opens at the right size later
            ResumeLayout(true);
            LayoutAll();
        }

        private void LayoutAll()
        {
            int logH = _logPanel.Visible ? _logPanel.Height : 0;
            if (logH > 0)
            {
                _logPanel.SetBounds(_sidebar.Width, ClientSize.Height - _bottomBar.Height - logH, ClientSize.Width - _sidebar.Width, logH);
                _logPanel.BringToFront();
            }
            _mainArea.Padding = new Padding(0, 0, 0, logH);
        }

        // ── TOP BAR ───────────────────────────────────────────────────────
        private void BuildTopBar()
        {
            _topBar = new Panel { BackColor = Theme.SURFACE, Height = Dpi.S(60), Dock = DockStyle.Top };
            _topBar.Paint += (s, e) =>
            {
                using var p = new Pen(Theme.BORDER);
                e.Graphics.DrawLine(p, 0, _topBar.Height - 1, _topBar.Width, _topBar.Height - 1);
                // Subtle gold top accent bar (like website nav)
                using var bar = new SolidBrush(Color.FromArgb(30, Theme.ACCENT));
                e.Graphics.FillRectangle(bar, 0, 0, _topBar.Width, 2);
            };

            _adminBadge = UiHelpers.Lbl("⚠  Not running as Administrator — some tweaks may fail", Theme.Mono(7.5f), Theme.WARNING, 18, 44);

            var ghLink = new FlatButton("⭐  GITHUB ↗", Theme.ACCENT) { Size = new Size(Dpi.S(115), Dpi.S(30)), Font = Theme.Mono(7.5f, true) };
            ghLink.Click += (s, e) => Proc.Open(AppVersion.RepoUrl);

            // Shown only when the launch-time GitHub check finds a newer release
            _updateBadge = new Label
            {
                Font = Theme.Mono(7.5f, true), ForeColor = Theme.ACCENT, BackColor = Color.FromArgb(30, Theme.ACCENT),
                AutoSize = true, Padding = new Padding(6, 4, 6, 4), Cursor = Cursors.Hand, Visible = false
            };
            _updateBadge.Click += (s, e) => Proc.Open(AppVersion.RepoUrl + "/releases/latest");

            void PositionTopRight(object s, EventArgs e)
            {
                ghLink.Location       = new Point(_topBar.Width - ghLink.Width - Dpi.S(16), (_topBar.Height - ghLink.Height) / 2);
                _updateBadge.Location = new Point(ghLink.Left - _updateBadge.Width - Dpi.S(10), (_topBar.Height - _updateBadge.Height) / 2);
            }
            _topBar.SizeChanged      += PositionTopRight;
            _updateBadge.SizeChanged += PositionTopRight;

            _topBar.Controls.AddRange(new Control[]
            {
                UiHelpers.Lbl("CORN_SYSTEMS", Theme.Mono(7f, true), Theme.ACCENT, 18, 10),   // website nav-logo style
                UiHelpers.Lbl("WIN11 OPTIMIZER  ⚡", Theme.Mono(9f, true), Theme.TEXT_PRI, 18, 30),
                _adminBadge, _updateBadge, ghLink
            });
        }

        // ── SIDEBAR ───────────────────────────────────────────────────────
        private void BuildSidebar()
        {
            // AutoScroll: with 14 category buttons plus the Select All/None row, the stack is taller
            // than the sidebar at minimum window height — without it the bottom entries clip off-screen.
            _sidebar = new Panel { BackColor = Theme.SURFACE, Width = Dpi.S(210), AutoScroll = true, Dock = DockStyle.Left };
            _sidebar.Paint += (s, e) =>
            {
                using var p = new Pen(Theme.BORDER);
                e.Graphics.DrawLine(p, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
                // Subtle purple gradient at top — matches website section hero
                using var grad = new LinearGradientBrush(new Rectangle(0, 0, _sidebar.Width, 80),
                    Color.FromArgb(18, Theme.SKY_PURPLE), Color.Transparent, LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(grad, 0, 0, _sidebar.Width, 80);
            };

            // "// CATEGORIES" — website section-label mono style with gold accent
            _sidebar.Controls.Add(UiHelpers.Lbl("// CATEGORIES", Theme.Mono(7f, true), Theme.ACCENT, Dpi.S(14), Dpi.S(14)));

            int y = Dpi.S(38);
            foreach (var cat in SidebarCategories)
            {
                if (cat is "Startup" or "History" or "Driver Cleanup")
                {
                    _sidebar.Controls.Add(new Panel { BackColor = Theme.BORDER, Bounds = new Rectangle(Dpi.S(8), y + 2, Dpi.S(194), 1) });
                    y += Dpi.S(10);
                }
                var btn = MakeSidebarBtn(cat);
                btn.SetBounds(Dpi.S(8), y, Dpi.S(194), Dpi.S(36));
                _sidebar.Controls.Add(btn);
                y += Dpi.S(38);
            }

            y += Dpi.S(8);
            var selAll  = new FlatButton("✔ Select All", Theme.ACCENT)  { Bounds = new Rectangle(Dpi.S(8),   y, Dpi.S(92), Dpi.S(28)) };
            var selNone = new FlatButton("✘ None",       Theme.SURFACE2) { Bounds = new Rectangle(Dpi.S(108), y, Dpi.S(94), Dpi.S(28)) };
            selAll.Click  += (s, e) => SetAllInView(true);
            selNone.Click += (s, e) => SetAllInView(false);
            _sidebar.Controls.AddRange(new Control[] { selAll, selNone });
        }

        private static string Emoji(string cat) => CatEmoji.TryGetValue(cat, out var em) ? em : "📦";

        private Button MakeSidebarBtn(string cat)
        {
            var btn = new Button
            {
                Text = $"  {Emoji(cat)}  {cat}", TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Tag = cat
            };
            btn.FlatAppearance.BorderSize = 0;
            StyleSidebarBtn(btn);

            // Badge paint — small pill with the selection count on the right
            btn.Paint += (s, e) =>
            {
                int count = _tiles.Count(t => t.IsChecked && (cat == "All" || t.Entry.Category == cat));
                if (count == 0) return;

                bool   active = cat == _activeCategory;
                string badge  = count.ToString();
                var    font   = Theme.Ui(7.5f, true);
                var    g      = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                SizeF sz = g.MeasureString(badge, font);
                int pw = (int)sz.Width + 10, ph = 16, px = btn.Width - pw - 8, py = (btn.Height - ph) / 2;
                using var pill = new SolidBrush(active ? Theme.ACCENT : Color.FromArgb(50, Theme.ACCENT));
                g.FillRoundedRect(pill, px, py, pw, ph, 8);
                using var text = new SolidBrush(active ? Theme.ACCENT_TEXT : Theme.ACCENT);
                g.DrawString(badge, font, text, px + (pw - sz.Width) / 2f, py + (ph - sz.Height) / 2f);
            };

            btn.Click += (s, e) =>
            {
                _activeCategory = cat;
                ClearSearch();
                RefreshSidebar();
                if (cat == "History")                        ShowView(_histPanel, BuildHistoryContent);
                else if (_tabs.TryGetValue(cat, out var tab)) ShowView(tab, tab.Activate);
                else                                          PopulateGrid(cat);
            };
            return btn;
        }

        private void StyleSidebarBtn(Button btn)
        {
            bool active   = (string)btn.Tag == _activeCategory;
            btn.BackColor = active ? Theme.ACCENT : Color.Transparent;
            btn.ForeColor = active ? Theme.ACCENT_TEXT : Theme.TEXT_DIM;
            btn.Font      = Theme.Mono(8f, active);
            btn.FlatAppearance.MouseOverBackColor = active ? Theme.ACCENT_HOV : Theme.SURFACE2;
        }

        private void RefreshSidebar()
        {
            foreach (var btn in _sidebar.Controls.OfType<Button>().Where(b => b.Tag is string)) StyleSidebarBtn(btn);
        }

        // ── MAIN AREA ─────────────────────────────────────────────────────
        private void BuildMainArea()
        {
            _mainArea = new Panel { BackColor = Theme.BG, Dock = DockStyle.Fill };
            BuildSearchBar();

            _tileGrid = new FlowLayoutPanel { AutoScroll = true, BackColor = Theme.BG, Padding = new Padding(12), Dock = DockStyle.Fill };
            _tileGrid.HorizontalScroll.Enabled = false;
            _tileGrid.HorizontalScroll.Visible = false;
            // Stock FlowLayoutPanel isn't double-buffered; with ~90 tiles repainting at once
            // (Select All / None) that can flash the grid mid-redraw. DoubleBuffered is protected.
            typeof(Panel).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(_tileGrid, true);

            _histPanel = new Panel { AutoScroll = true, BackColor = Theme.BG, Padding = new Padding(20), Dock = DockStyle.Fill, Visible = false };
            _tabs = new()
            {
                ["Startup"]        = new StartupTab(),
                ["Services"]       = new ServicesTab(),
                ["Driver Cleanup"] = new DriverCleanupTab(),
                ["Disk Cleanup"]   = new DiskCleanupTab(),
            };

            _mainArea.Controls.Add(_histPanel);
            _mainArea.Controls.AddRange(_tabs.Values.ToArray<Control>());
            _mainArea.Controls.Add(_tileGrid);
            _mainArea.Controls.Add(_searchBar);   // add last so it docks on top
        }

        private void BuildSearchBar()
        {
            _searchBar = new Panel { BackColor = Theme.SURFACE, Height = Dpi.S(88), Dock = DockStyle.Top };
            UiHelpers.BottomBorder(_searchBar);

            // ── Row 1: search box
            var searchIcon = new Label
            {
                Text = "🔍", Font = Theme.F("Segoe UI Emoji", 11f), BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(Dpi.S(28), Dpi.S(32)), Location = new Point(Dpi.S(10), Dpi.S(8))
            };

            _searchBox = new TextBox
            {
                Font = Theme.Ui(10f), ForeColor = Theme.TEXT_SEC, BackColor = Theme.CARD, BorderStyle = BorderStyle.None,
                Location = new Point(Dpi.S(42), Dpi.S(14)), Height = Dpi.S(24), Width = Dpi.S(280), Text = SearchHint
            };
            _searchBox.Enter += (s, e) =>
            {
                if (!_searchHasPlaceholder) return;
                _searchHasPlaceholder = false;
                _searchBox.Text       = "";
                _searchBox.ForeColor  = Theme.TEXT_PRI;
            };
            _searchBox.Leave += (s, e) => { if (string.IsNullOrWhiteSpace(_searchBox.Text)) ResetSearchBox(); };
            _searchBox.TextChanged += (s, e) =>
            {
                if (_searchHasPlaceholder) return;
                _searchQuery = _searchBox.Text.Trim().ToLower();
                _clearSearchBtn.Visible = _searchQuery.Length > 0;
                ApplySearchFilter();
            };

            _clearSearchBtn = new FlatButton("✕", Theme.SURFACE2)
            {
                Size = new Size(Dpi.S(22), Dpi.S(22)), Location = new Point(Dpi.S(326), Dpi.S(11)),
                Font = Theme.Ui(8f), ForeColor = Theme.TEXT_SEC, Visible = false
            };
            _clearSearchBtn.Click += (s, e) => ClearSearch();

            var rowDivider = new Panel
            {
                BackColor = Theme.BORDER, Bounds = new Rectangle(0, Dpi.S(42), _searchBar.Width, 1),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            // ── Row 2: preset strip (FlowLayoutPanel — auto-reflows, DPI-safe)
            var presetStrip = new FlowLayoutPanel
            {
                BackColor = Color.Transparent, WrapContents = false,
                Bounds = new Rectangle(0, Dpi.S(43), _searchBar.Width, Dpi.S(44)),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            _searchBar.SizeChanged += (s, e) => rowDivider.Width = presetStrip.Width = _searchBar.Width;
            presetStrip.Controls.Add(new Label
            {
                Text = "PRESETS", Font = Theme.Mono(6.5f, true), ForeColor = Theme.ACCENT, BackColor = Color.Transparent,
                AutoSize = true, Margin = new Padding(Dpi.S(8), Dpi.S(15), Dpi.S(4), 0)
            });

            FlatButton MakePreset(string label, string key, Color fg, Color bg, Color border, Color hover, int gap = 4)
            {
                var b = new FlatButton(label, bg)
                {
                    Size      = new Size(TextRenderer.MeasureText(label, Theme.Ui(8.5f)).Width + Dpi.S(20), Dpi.S(28)),
                    Margin    = new Padding(0, Dpi.S(7), Dpi.S(gap), 0),
                    ForeColor = fg
                };
                b.FlatAppearance.BorderColor        = border;
                b.FlatAppearance.MouseOverBackColor = hover;
                b.Click += (s, e) => ApplyPreset(key);
                return b;
            }

            var lime = Color.FromArgb(204, 255, 0);
            foreach (var (label, key) in new[]
            {
                ("⭐ Recommended", "Recommended"), ("🎮 Gaming PC", "Gaming"), ("🔒 Privacy", "Privacy"),
                ("🛡 Security", "Security"), ("🪶 Minimal", "Minimal"), ("💻 Laptop", "Laptop"),
                ("🧹 Clean Install", "CleanInstall"), ("🔬 Dev Machine", "DevMachine"),
            })
                presetStrip.Controls.Add(MakePreset(label, key, Theme.ACCENT, Theme.SURFACE2, lime, Color.FromArgb(30, lime)));
            // Nuclear stays red — intentionally different to signal danger
            presetStrip.Controls.Add(MakePreset("☢ Nuclear", "Nuclear", Theme.DANGER, Color.FromArgb(45, 20, 20),
                Color.FromArgb(100, 40, 40), Color.FromArgb(60, 30, 30), gap: 8));

            _searchBar.Controls.AddRange(new Control[] { searchIcon, _searchBox, _clearSearchBtn, rowDivider, presetStrip });
        }

        private bool Matches(TweakEntry e) =>
            _searchQuery.Length == 0 ||
            new[] { e.Name, e.Description, e.Category, e.WhatItChanges }.Any(f => f?.ToLower().Contains(_searchQuery) == true);

        // Hides non-matching tiles, and any section header left with no visible tile under it
        private void ApplySearchFilter()
        {
            SectionHeader header = null;
            bool headerHasHit = false;
            foreach (Control c in _tileGrid.Controls)
            {
                if (c is SectionHeader hdr)
                {
                    if (header != null) header.Visible = headerHasHit;
                    (header, headerHasHit) = (hdr, false);
                }
                else if (c is TweakTile tile)
                {
                    bool match = Matches(tile.Entry);
                    tile.Visible  = match;
                    headerHasHit |= match;
                }
            }
            if (header != null) header.Visible = headerHasHit;
            UpdateSelCount();
        }

        private void ResetSearchBox()
        {
            _searchHasPlaceholder = true;   // set BEFORE Text so TextChanged ignores it
            _searchBox.Text       = SearchHint;
            _searchBox.ForeColor  = Theme.TEXT_SEC;
        }

        private void ClearSearch()
        {
            _searchQuery = "";
            ResetSearchBox();
            _clearSearchBtn.Visible = false;
            ApplySearchFilter();
        }

        // Presets and profile imports span every category — switch to All first, otherwise one
        // clicked while viewing e.g. "Gaming" would only touch the tiles on screen.
        private void ShowAllTweaks()
        {
            if (_activeCategory == "All") return;
            _activeCategory = "All";
            RefreshSidebar();
            PopulateGrid("All");
        }

        private void ApplyPreset(string preset)
        {
            ShowAllTweaks();
            ClearSearch();

            bool hasBattery = SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
            (Func<TweakEntry, bool> Pick, string Msg, Color Col) p = preset switch
            {
                "Recommended" => (e => e.DefaultOn,
                    "Recommended (safe defaults)", Theme.ACCENT),
                "Gaming" => (e => e.DefaultOn || e.Category is "Gaming" or "Network",
                    "Gaming PC (recommended + gaming + network)", Theme.SUCCESS),
                "Privacy" => (e => e.DefaultOn || e.Category == "Privacy",
                    "Privacy (recommended + all privacy tweaks)", Color.FromArgb(168, 85, 247)),
                "Security" => (e => e.DefaultOn || e.Category == "Security",
                    "Security (recommended + all security tweaks)", Color.FromArgb(16, 185, 129)),
                // Everything except Bloatware (irreversible), Advanced (risky) and Laptop (use the Laptop preset)
                "Nuclear" => (e => e.Category is not ("Bloatware" or "Advanced" or "Laptop"),
                    "Nuclear — all tweaks except Bloatware, Advanced & Laptop", Theme.DANGER),
                // Only the lightest, safest tweaks — responsiveness + privacy basics
                "Minimal" => (e => e.TweakKey is
                        "Resp_MenuDelay" or "Resp_AppKill" or "Resp_ServiceKill" or
                        "Resp_AutoEndTasks" or "Resp_WinTips" or "Resp_SuggestedContent" or
                        "Priv_AdvertisingId" or "Priv_BingStart" or "Priv_ChatIcon" or
                        "Priv_Feedback" or "Priv_AppTracking" or "Priv_Recall" or
                        "Perf_StartupDelay" or "Perf_VisualFX" or "Priv_CloudContent",
                    "Minimal — safe UI & privacy tweaks only", Theme.TEXT_SEC),
                // Recommended + privacy/responsiveness/security + the Laptop battery section, minus
                // anything that trades battery for speed (or breaks hibernate-on-critical).
                "Laptop" => (e => (e.DefaultOn || e.Category is "Privacy" or "Responsiveness" or "Security" or "Laptop")
                        && e.TweakKey is not ("Perf_PowerPlan" or "Perf_Hibernate" or "Perf_MemCompression" or
                            "Perf_PowerThrottle" or "Perf_TimerRes" or "Resp_PlatformTick" or "Resp_VerboseStatus" or
                            "Adv_DynamicTick" or "Game_GPUPower" or "Game_HAGS" or
                            "Lap_BackgroundApps"),   // opt-in: breaks live notifications for Store apps
                    hasBattery ? "Laptop — battery-life tweaks + battery-safe privacy & responsiveness"
                               : "Laptop — note: no battery detected, the battery-only settings won't do anything on this PC",
                    hasBattery ? Color.FromArgb(56, 189, 248) : Theme.WARNING),
                "CleanInstall" => (e => e.Category is "Bloatware" or "Privacy"
                        || e.TweakKey is "Sec_Defender" or "Sec_NetBIOS" or "Sec_RDP"
                                      or "Resp_WinTips" or "Resp_SuggestedContent" or "Perf_StartupDelay",
                    "Clean Install — bloatware removed, telemetry killed, baseline secured", Color.FromArgb(52, 211, 153)),
                "DevMachine" => (e => (e.Category is "Performance" or "Responsiveness" or "Network"
                        || e.TweakKey is "Adv_ProcessorScheduling" or "Adv_CPUThrottle" or "Adv_DynamicTick" or "Adv_TRIM"
                                      or "Adv_Animations" or "Priv_Telemetry" or "Priv_TelemetryTasks" or "Priv_DiagTrack"
                                      or "Priv_Feedback" or "Priv_WER" or "Game_CPUPriority")
                        && e.TweakKey is not ("Perf_WSearch" or "Perf_Hibernate" or "Game_DVR" or "Game_HAGS"
                                              or "Game_FSO" or "Game_GameMode" or "Game_NvidiaTelemetry"),
                    "Dev Machine — max performance, keeps Search & Hibernate", Color.FromArgb(167, 139, 250)),
                _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preset")
            };

            foreach (var t in _tiles) t.IsChecked = p.Pick(t.Entry);
            SetStatus("Preset applied: " + p.Msg, p.Col);
            UpdateSelCount();
        }

        // Hides every full-page view in the main area, then shows the requested one.
        private void ShowView(Control view, Action init)
        {
            foreach (Control c in _mainArea.Controls) c.Visible = c == view;
            init?.Invoke();
        }

        private void PopulateGrid(string filter)
        {
            ShowView(_tileGrid, null);
            _searchBar.Visible = true;

            _tileGrid.SuspendLayout();
            UiHelpers.ClearAndDispose(_tileGrid);
            _tiles.Clear();
            SectionHeader.ResetIndex();

            foreach (var group in TweakCatalog.ForCategory(filter).GroupBy(t => t.Category).OrderBy(g => TweakCatalog.OrderOf(g.Key)))
            {
                _tileGrid.Controls.Add(new SectionHeader(group.Key, Emoji(group.Key)));
                foreach (var entry in group)
                {
                    // Restore whatever the user last set this tweak to; only fall back
                    // to DefaultOn the first time a tile is ever created.
                    var tile = new TweakTile(entry)
                    {
                        IsChecked = _selectionState.TryGetValue(entry.TweakKey, out var wasChecked) ? wasChecked : entry.DefaultOn
                    };
                    // Applied by the app in a previous session
                    if (AppliedState.IsApplied(entry.TweakKey)) tile.SetApplied(AppliedSource.AppliedByApp);
                    tile.CheckedChanged += (s, e) =>
                    {
                        _selectionState[entry.TweakKey] = tile.IsChecked;
                        UpdateSelCount();
                        SetStatus("Ready", Theme.TEXT_SEC);
                    };
                    foreach (var c in tile.Controls.Cast<Control>().Prepend(tile))
                    {
                        c.MouseEnter += (s, e) => ShowTooltip(tile);
                        c.MouseLeave += (s, e) => HideTooltip();
                    }
                    _tiles.Add(tile);
                    _tileGrid.Controls.Add(tile);
                }
            }

            _tileGrid.ResumeLayout(true);
            UpdateSelCount();
            _ = RunDetectionScanAsync();
        }

        private void SetAllInView(bool check)
        {
            _tileGrid.SuspendLayout();
            foreach (var t in _tiles.Where(t => t.Visible)) t.IsChecked = check;
            _tileGrid.ResumeLayout(true);
            UpdateSelCount();
        }

        private void UpdateSelCount()
        {
            // Run applies every checked tile in the view — including ones a search is
            // hiding — so the counter must count those too, and call them out.
            var selected = _tiles.Where(t => t.IsChecked).ToList();
            int hidden   = _searchQuery.Length == 0 ? 0 : selected.Count(t => !t.Visible);
            foreach (var btn in _sidebar.Controls.OfType<Button>()) btn.Invalidate();   // refresh count badges

            _selCountLabel.Text = selected.Count == 0
                ? "No tweaks selected"
                : $"{selected.Count} tweak{(selected.Count == 1 ? "" : "s")} selected" + (hidden > 0 ? $"  ({hidden} hidden by search)" : "");
            _undoBtn.Enabled = !_isRunning && selected.Any(t => TweakEngine.HasBackup(t.Entry.Category));
        }

        // Scans the live system state for each tile in the background. Tiles already applied by
        // the app keep that state; only unknown ones are checked against the live system. A new
        // grid cancels the previous scan instead of stacking another one on top of it.
        private async Task RunDetectionScanAsync()
        {
            _scanCts?.Cancel();
            var cts      = _scanCts = new CancellationTokenSource();
            var snapshot = _tiles.ToList();
            try
            {
                await Task.Run(() =>
                {
                    foreach (var tile in snapshot)
                    {
                        if (cts.IsCancellationRequested) return;
                        string key = tile.Entry.TweakKey;
                        if (AppliedState.IsApplied(key) || TweakDetector.Check(key) != true) continue;
                        AppliedState.MarkApplied(new[] { key });   // persist so it survives future sessions
                        BeginInvoke(new Action(() => tile.SetApplied(AppliedSource.DetectedOnSystem)));
                    }
                });
            }
            catch (Exception ex) { SessionLog.Write("DETECT SCAN", ex); }   // form closed mid-scan
            finally
            {
                if (_scanCts == cts) _scanCts = null;
                cts.Dispose();
            }
        }

        // ── HISTORY ───────────────────────────────────────────────────────
        private void BuildHistoryContent()
        {
            _histPanel.SuspendLayout();
            UiHelpers.ClearAndDispose(_histPanel);
            int w = _histPanel.ClientSize.Width;

            var topRow = new Panel
            {
                Bounds = new Rectangle(0, 0, w, 44), BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var clearBtn = new FlatButton("Clear History", Theme.DANGER)
            {
                Size = new Size(120, 28), Location = new Point(w - 124, 8), ForeColor = Color.White,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            clearBtn.Click += (s, e) =>
            {
                if (MessageBox.Show("Clear all run history?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ChangeLog.Clear();
                BeginInvoke(new Action(BuildHistoryContent));   // rebuilding disposes this button — let its click finish first
            };
            topRow.Controls.AddRange(new Control[] { UiHelpers.Lbl("// RUN HISTORY", Theme.Ui(9f, true), Theme.TEXT_SEC, 0, 13), clearBtn });
            _histPanel.Controls.Add(topRow);

            int y = 52;
            if (ChangeLog.Entries.Count == 0)
                _histPanel.Controls.Add(UiHelpers.Lbl("No runs recorded yet. Apply some tweaks to start tracking history.", Theme.Ui(10f), Theme.TEXT_SEC, 0, y + 10));

            foreach (var entry in ChangeLog.Entries)
            {
                var card = new Panel
                {
                    Bounds = new Rectangle(0, y, w - 4, 86), BackColor = Theme.SURFACE,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                card.Paint += (s, e) =>
                {
                    using var pen    = new Pen(Theme.BORDER);
                    using var stripe = new SolidBrush(Theme.ACCENT);
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                    e.Graphics.FillRectangle(stripe, 0, 0, 3, card.Height);
                };

                string stats = $"✔ {entry.Passed} succeeded   ✘ {entry.Failed} failed" + (entry.RestorePoint ? "   🛡 Restore Point" : "");
                card.Controls.AddRange(new Control[]
                {
                    UiHelpers.Lbl(entry.Timestamp,                   Theme.Ui(9f, true), Theme.ACCENT,   14, 10),
                    UiHelpers.Lbl(entry.WindowsVer,                  Theme.Ui(8.5f),     Theme.TEXT_SEC, 14, 28),
                    UiHelpers.Lbl($"Categories: {entry.Categories}", Theme.Ui(9f),       Theme.TEXT_PRI, 14, 46),
                    UiHelpers.Lbl(stats, Theme.Ui(9f, true), entry.Failed == 0 ? Theme.SUCCESS : Theme.WARNING, 14, 64),
                });

                var details = entry.Details ?? new();   // null when hand-edited JSON drops the field
                if (details.Count > 0)
                {
                    var dl = UiHelpers.Lbl(string.Join("  ·  ", details.Take(8)) + (details.Count > 8 ? $"  … +{details.Count - 8} more" : ""),
                                           Theme.F("Consolas", 7.5f), Theme.TEXT_SEC, 14, 82, 30);
                    dl.Width = card.Width - 30;
                    card.Controls.Add(dl);
                    card.Height = 118;
                }

                _histPanel.Controls.Add(card);
                y += card.Height + 8;
            }
            _histPanel.ResumeLayout(true);
        }

        // ── BOTTOM BAR ────────────────────────────────────────────────────
        private void BuildBottomBar()
        {
            _bottomBar = new Panel { BackColor = Theme.SURFACE, Height = Dpi.S(136), Dock = DockStyle.Bottom };
            _bottomBar.Paint += (s, e) =>
            {
                using var p = new Pen(Theme.BORDER);
                e.Graphics.DrawLine(p, 0, 0, _bottomBar.Width, 0);
            };

            _progInner = new Panel { BackColor = Theme.ACCENT, Size = new Size(0, Dpi.S(6)) };
            _progOuter = new Panel { BackColor = Theme.BORDER, Location = new Point(Dpi.S(16), Dpi.S(12)), Size = new Size(Dpi.S(500), Dpi.S(6)) };
            _progOuter.Controls.Add(_progInner);

            _statusLabel   = UiHelpers.Lbl("Ready",              null, Theme.TEXT_DIM, Dpi.S(16), Dpi.S(24));
            _selCountLabel = UiHelpers.Lbl("No tweaks selected", null, Theme.TEXT_DIM, Dpi.S(16), Dpi.S(46));

            _restoreChk = new CheckBox
            {
                Text = "🛡 Create Restore Point before running", ForeColor = Theme.TEXT_DIM, BackColor = Color.Transparent,
                Checked = true, AutoSize = true, Location = new Point(Dpi.S(16), Dpi.S(92)), FlatStyle = FlatStyle.Flat
            };
            _restoreChk.FlatAppearance.BorderColor        = Theme.BORDER;
            _restoreChk.FlatAppearance.CheckedBackColor   = Theme.ACCENT;
            _restoreChk.FlatAppearance.MouseOverBackColor = Theme.SURFACE;

            _undoBtn = new FlatButton("↩ Undo Selected", Theme.SURFACE2) { Size = new Size(Dpi.S(150), Dpi.S(36)), Enabled = false };
            _undoBtn.Click += OnUndoClicked;

            var clearBtn = new FlatButton("Clear Selection", Theme.SURFACE2) { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            clearBtn.Click += (s, e) => SetAllInView(false);

            _runBtn = new FlatButton(RunText, Theme.ACCENT) { Size = new Size(Dpi.S(165), Dpi.S(36)), Font = Theme.Mono(8.5f, true) };
            _runBtn.Click += OnRunClicked;

            var logToggle = new FlatButton("📋 Log", Theme.SURFACE2) { Size = new Size(Dpi.S(70), Dpi.S(26)) };
            logToggle.Click += (s, e) => { _logPanel.Visible = !_logPanel.Visible; LayoutAll(); };

            // Shell/UI tweaks (visual effects, menu delay, taskbar icons) only need Explorer
            // restarted — this makes those feel instant instead of waiting on a full reboot.
            var explorerBtn = new FlatButton("⟳ Restart Explorer", Theme.SURFACE2) { Size = new Size(Dpi.S(140), Dpi.S(26)), ForeColor = Theme.TEXT_SEC };
            explorerBtn.Click += (s, e) =>
            {
                if (MessageBox.Show(
                        "Restart Windows Explorer now?\n\nThe taskbar and open folder windows " +
                        "will briefly disappear and come back. Unsaved work in other apps is not affected.",
                        "Restart Explorer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    RestartExplorer();
            };

            // Amber badge listing exactly what's waiting on a reboot / Explorer
            // restart after a run — click it for the full tweak list.
            _rebootBadge = new Label
            {
                Font = Theme.Mono(7.5f, true), ForeColor = Theme.WARNING, BackColor = Color.FromArgb(30, Theme.WARNING),
                AutoSize = true, Padding = new Padding(6, 3, 6, 3), Location = new Point(Dpi.S(16), Dpi.S(66)),
                Cursor = Cursors.Hand, Visible = false
            };
            _rebootBadge.Click += (s, e) =>
            {
                string msg = (_pendingReboot.Count   > 0 ? "Waiting on a REBOOT:" + Bullets(_pendingReboot) + "\n\n" : "")
                           + (_pendingExplorer.Count > 0 ? "Waiting on an EXPLORER RESTART:" + Bullets(_pendingExplorer) : "");
                if (msg.Length > 0)
                    MessageBox.Show(msg.TrimEnd(), "Pending Changes", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            var exportBtn = new FlatButton("↑ Export Profile", Theme.SURFACE2) { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            exportBtn.Click += (s, e) => TweakProfile.Export(_tiles.Where(t => t.IsChecked).Select(t => t.Entry.TweakKey));

            var importBtn = new FlatButton("↓ Import Profile", Theme.SURFACE2) { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            importBtn.Click += (s, e) =>
            {
                var keys = TweakProfile.Import();
                if (keys == null) return;
                ShowAllTweaks();
                ClearSearch();
                var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                foreach (var t in _tiles) t.IsChecked = keySet.Contains(t.Entry.TweakKey);
                SetStatus($"Profile imported — {_tiles.Count(t => t.IsChecked)} tweaks selected.", Theme.ACCENT);
                UpdateSelCount();
            };

            _bottomBar.SizeChanged += (s, e) =>
            {
                int r = _bottomBar.Width - Dpi.S(16), y = Dpi.S(82);
                _runBtn.Location     = new Point(r - Dpi.S(160), y);
                clearBtn.Location    = new Point(r - Dpi.S(300), y);
                _undoBtn.Location    = new Point(r - Dpi.S(460), y);
                exportBtn.Location   = new Point(r - Dpi.S(606), y);
                importBtn.Location   = new Point(r - Dpi.S(750), y);
                logToggle.Location   = new Point(r - Dpi.S(76), Dpi.S(12));
                explorerBtn.Location = new Point(r - Dpi.S(76) - Dpi.S(6) - explorerBtn.Width, Dpi.S(12));
                _progOuter.Width     = Math.Max(Dpi.S(200), r - Dpi.S(96) - Dpi.S(6) - explorerBtn.Width);
            };

            _bottomBar.Controls.AddRange(new Control[]
            {
                _progOuter, _statusLabel, _selCountLabel, _rebootBadge, _restoreChk,
                _undoBtn, clearBtn, exportBtn, importBtn, _runBtn, logToggle, explorerBtn
            });
        }

        private static string Bullets(IEnumerable<string> items) => "\n  • " + string.Join("\n  • ", items);

        private void BuildLogPanel()
        {
            var bg = Color.FromArgb(8, 8, 20);
            _logBox = new DarkRichTextBox
            {
                BackColor = bg, ForeColor = Color.FromArgb(180, 200, 255), BorderStyle = BorderStyle.None, ReadOnly = true,
                Font = Theme.F("Consolas", 8.5f), Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical
            };
            _logPanel = new Panel { BackColor = bg, Visible = false, Height = Dpi.S(180) };

            var hdr = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.SURFACE };
            var closeBtn = new FlatButton("✕", Theme.SURFACE) { Size = new Size(24, 24), Font = Theme.Ui(8f), ForeColor = Theme.TEXT_SEC };
            closeBtn.Click += (s, e) => { _logPanel.Visible = false; LayoutAll(); };
            hdr.SizeChanged += (s, e) => closeBtn.Location = new Point(hdr.Width - 26, 1);
            hdr.Controls.AddRange(new Control[] { UiHelpers.Lbl("// OUTPUT LOG", Theme.Mono(7.5f, true), Theme.TEXT_SEC, 10, 6), closeBtn });

            _logPanel.Controls.Add(_logBox);
            _logPanel.Controls.Add(hdr);
        }

        // ── TOOLTIP ───────────────────────────────────────────────────────
        private void BuildTooltip()
        {
            _ttTitle = new Label
            {
                Font = Theme.F("Segoe UI Semibold", 9f), ForeColor = Theme.TEXT_PRI, BackColor = Color.Transparent,
                Location = new Point(Dpi.S(20), Dpi.S(10)), Size = new Size(Dpi.S(268), Dpi.S(18)),
                UseMnemonic = false   // "Inking & Typing" etc. — the & was being eaten as a mnemonic
            };
            _ttWhat = new Label
            {
                Font = Theme.F("Consolas", 7.5f), ForeColor = Theme.TEXT_SEC, BackColor = Color.Transparent,
                Location = new Point(Dpi.S(20), Dpi.S(32)), Size = new Size(Dpi.S(268), Dpi.S(120)), UseMnemonic = false
            };
            _tooltip = new Panel { BackColor = Color.FromArgb(13, 12, 28), Size = new Size(Dpi.S(300), 0), Visible = false };
            _tooltip.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(80, Theme.ACCENT), 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, _tooltip.Width - 1, _tooltip.Height - 1);
                using var bar = new SolidBrush(Theme.ACCENT);
                e.Graphics.FillRectangle(bar, 0, 0, 3, _tooltip.Height);
            };
            _tooltip.Controls.AddRange(new Control[] { _ttTitle, _ttWhat });

            // Hide timer — small delay so moving between tiles doesn't flicker
            _ttHideTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _ttHideTimer.Tick += (s, e) => { _ttHideTimer.Stop(); _tooltip.Visible = false; };

            Controls.Add(_tooltip);
            _tooltip.BringToFront();
        }

        private void ShowTooltip(TweakTile tile)
        {
            _ttHideTimer.Stop();
            string raw = tile.Entry.WhatItChanges;
            if (string.IsNullOrEmpty(raw)) return;

            _ttTitle.Text = tile.Entry.Name;
            // First line is the command / registry change, the rest explains it
            int split = raw.IndexOf('\n');
            _ttWhat.Text = split < 0 ? raw : raw[..split].Trim() + "\n\n" + raw[(split + 1)..].Trim();
            using (var g = _ttWhat.CreateGraphics())
                _ttWhat.Height = Math.Max((int)g.MeasureString(_ttWhat.Text, _ttWhat.Font, _ttWhat.Width).Height + 8, 30);
            _tooltip.Height = _ttWhat.Bottom + 16;

            // Right of the tile, flipped left if it would overflow; clamped above the bottom bar
            Point pt = PointToClient(tile.PointToScreen(Point.Empty));
            int x = pt.X + tile.Width + 6;
            if (x + _tooltip.Width > ClientSize.Width - 10) x = pt.X - _tooltip.Width - 6;
            int y = Math.Min(pt.Y, ClientSize.Height - _bottomBar.Height - _tooltip.Height - 10);

            _tooltip.Location = new Point(x, y);
            _tooltip.Visible  = true;
            _tooltip.BringToFront();
        }

        private void HideTooltip() => _ttHideTimer.Start();

        // ── RUN / UNDO ────────────────────────────────────────────────────
        private void SetRunning(bool running, string runText = null)
        {
            _isRunning      = running;
            _runBtn.Enabled = !running;
            if (runText != null) _runBtn.Text = runText;
            UpdateSelCount();   // also gates Undo on _isRunning
        }

        private async void OnRunClicked(object sender, EventArgs e)
        {
            if (_isRunning) return;

            var selected = _tiles.Where(t => t.IsChecked).OrderBy(t => TweakCatalog.OrderOf(t.Entry.Category)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select at least one tweak to run.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetRunning(true, "⏳ Running...");
            var results = new List<TweakEngine.TweakResult>();
            List<string> needReboot = new(), needExplorer = new();
            int ran = 0;
            try
            {
                TweakEngine.ClearResults();
                bool rpCreated = false;
                if (_restoreChk.Checked)
                {
                    SetStatus("Creating System Restore Point…", Theme.WARNING);
                    AppendLog("🛡 Creating System Restore Point…");
                    rpCreated = await Task.Run(() => TweakEngine.CreateRestorePoint("Win11Optimizer — before tweaks"));
                    AppendLog(rpCreated ? "🛡 Restore Point created." : "⚠ Restore Point failed or skipped.");
                }

                SetProgress(0, selected.Count);
                try
                {
                    await Task.Run(() =>
                    {
                        string lastCat = null;
                        foreach (var tile in selected)
                        {
                            var entry = tile.Entry;
                            Invoke(new Action(() =>
                            {
                                if (entry.Category != lastCat)
                                {
                                    if (lastCat != null) AppendLog(Rule);
                                    AppendLog($"┌─ {entry.Category.ToUpper()}");
                                }
                                // Mark the tile as running right before it executes (not all at once up front)
                                tile.SetStatus(TileStatus.Running);
                                SetProgress(ran + 1, selected.Count);
                                SetStatus($"[{ran + 1}/{selected.Count}]  {entry.Category} → {entry.Name}", Theme.WARNING);
                                AppendLog($"  → {entry.Name}…");
                                _tileGrid.ScrollControlIntoView(tile);
                            }));
                            lastCat = entry.Category;

                            var res = TweakEngine.Apply(entry);
                            results.AddRange(res);
                            ran++;
                            Invoke(new Action(() => { foreach (var r in res) AppendLog(r.Success ? $"  ✔  {r.Name}" : $"  ✘  {r.Name}: {r.Error}"); }));
                        }
                        if (lastCat != null) Invoke(new Action(() => AppendLog(Rule)));
                    });
                }
                catch (Exception ex)
                {
                    // Record whatever did run below instead of leaving the app stuck mid-run
                    SessionLog.Write("RUN", ex);
                    AppendLog($"✘ Run aborted: {ex.Message}");
                }

                var done = selected.Take(ran).ToList();
                int fail = results.Count(r => !r.Success), pass = results.Count - fail;
                foreach (var t in done)
                {
                    t.SetStatus(TileStatus.Done);
                    t.SetApplied(AppliedSource.AppliedByApp);
                }
                AppliedState.MarkApplied(done.Select(t => t.Entry.TweakKey));   // persist per-tweak applied state

                SetProgress(1, 1);
                SetStatus($"Complete — {pass} succeeded, {fail} failed.", fail == 0 ? Theme.SUCCESS : Theme.WARNING);

                // Classify exactly what's pending instead of a blanket "reboot recommended"
                (needReboot, needExplorer) = RebootInfo.Split(done.Select(t => t.Entry));
                _pendingReboot.AddRange(needReboot.Except(_pendingReboot).ToList());
                _pendingExplorer.AddRange(needExplorer.Except(_pendingExplorer).ToList());
                RefreshRebootBadge();

                string pendingNote = needReboot.Count   > 0 ? $" {needReboot.Count} tweak(s) need a reboot."
                                   : needExplorer.Count > 0 ? $" {needExplorer.Count} tweak(s) need an Explorer restart."
                                   : " No reboot needed.";
                AppendLog($"══ COMPLETE: {pass} succeeded, {fail} failed.{pendingNote} ══");

                ChangeLog.AddEntry(new ChangeLog.RunEntry
                {
                    Categories   = string.Join(", ", done.Select(t => t.Entry.Category).Distinct()),
                    Passed       = pass,
                    Failed       = fail,
                    RestorePoint = rpCreated,
                    Details      = results.Select(r => r.Line).ToList()
                });
            }
            finally { SetRunning(false, RunText); }

            if (IsDisposed) return;
            // Only nag about rebooting when something applied actually needs one
            if (needReboot.Count > 0) PromptReboot(needReboot);
            else if (needExplorer.Count > 0 && MessageBox.Show(
                         $"{needExplorer.Count} tweak(s) take effect after an Explorer restart.\n\n" +
                         "Restart Explorer now? (Taskbar and folder windows reload — other apps are unaffected.)",
                         "Explorer Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                         MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                RestartExplorer();
        }

        private async void OnUndoClicked(object sender, EventArgs e)
        {
            if (_isRunning) return;

            var undoCats = _tiles.Where(t => t.IsChecked && TweakEngine.HasBackup(t.Entry.Category))
                                 .Select(t => t.Entry.Category).Distinct().ToList();
            if (undoCats.Count == 0) return;

            // Undo restores a whole category, not individual tiles — say so before doing it
            if (MessageBox.Show(
                    "Undo restores every tweak this app changed in:\n" + Bullets(undoCats) +
                    "\n\n(not just the tiles you have selected). Continue?",
                    "Undo Tweaks", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            SetRunning(true);
            try
            {
                foreach (var cat in undoCats)
                {
                    AppendLog($"↩ Undoing {cat}…");
                    var res = await Task.Run(() => TweakEngine.Undo(cat));
                    foreach (var r in res.Where(r => !r.Success)) AppendLog($"  ✘  {r.Name}: {r.Error}");
                    AppendLog($"  ↩ {cat} done — {res.Count(r => r.Success)} restored, {res.Count(r => !r.Success)} failed.");
                }

                SetStatus("Undo complete. Reboot recommended.", Theme.SUCCESS);
                // The whole category was restored, so clear applied state for every tweak in it —
                // not only the tiles that happened to be checked.
                AppliedState.MarkUndone(TweakCatalog.All.Where(t => undoCats.Contains(t.Category)).Select(t => t.TweakKey).ToList());
                foreach (var t in _tiles.Where(t => undoCats.Contains(t.Entry.Category)))
                {
                    t.SetApplied(AppliedSource.None);
                    t.SetStatus(TileStatus.None);
                }
            }
            catch (Exception ex)
            {
                SessionLog.Write("UNDO", ex);
                AppendLog($"✘ Undo aborted: {ex.Message}");
            }
            finally { SetRunning(false); }
        }

        // ── STATUS / LOG ──────────────────────────────────────────────────
        private void OnUiThread(Action a)
        {
            if (InvokeRequired) Invoke(a); else a();
        }

        private void SetStatus(string msg, Color col = default) => OnUiThread(() =>
        {
            _statusLabel.Text      = msg;
            _statusLabel.ForeColor = col == default ? Theme.TEXT_SEC : col;
        });

        private void SetProgress(int done, int total) =>
            OnUiThread(() => _progInner.Width = total == 0 ? 0 : (int)((double)_progOuter.Width * done / total));

        private void AppendLog(string msg) => OnUiThread(() =>
        {
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            _logBox.ScrollToCaret();
        });

        private void PromptReboot(List<string> pendingNames)
        {
            string list = pendingNames.Count <= 6
                ? "\n\nWaiting on the reboot:" + Bullets(pendingNames)
                : $"\n\n{pendingNames.Count} applied tweaks are waiting on the reboot.";
            if (MessageBox.Show($"Some tweaks require a reboot to take full effect.{list}\n\nWould you like to reboot now?",
                    "Reboot Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try
            {
                var (code, _, err) = Proc.Run("shutdown.exe", "/r /t 10 /c \"Win11 Optimizer: Rebooting to apply tweaks.\"");
                if (code != 0) AppendLog($"✘ Reboot failed: {err.Trim()} (exit code {code})");
            }
            catch (Exception ex)
            {
                SessionLog.Write("REBOOT", ex);
                AppendLog($"✘ Reboot failed: {ex.Message}");
            }
        }

        private void RestartExplorer()
        {
            if (!ExplorerHelper.Restart(out string err))
            {
                AppendLog($"✘ Explorer restart failed: {err}");
                return;
            }
            AppendLog("⟳ Explorer restarted.");
            _pendingExplorer.Clear();
            RefreshRebootBadge();
        }

        private void RefreshRebootBadge()
        {
            var parts = new List<string>();
            if (_pendingReboot.Count   > 0) parts.Add($"{_pendingReboot.Count} awaiting reboot");
            if (_pendingExplorer.Count > 0) parts.Add($"{_pendingExplorer.Count} awaiting Explorer restart");
            _rebootBadge.Text    = "⚠ " + string.Join("  ·  ", parts) + "  (click for list)";
            _rebootBadge.Visible = parts.Count > 0;
        }
    }
}
