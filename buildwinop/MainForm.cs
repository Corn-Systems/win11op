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
    public class mainForm : Form
    {
        private const string runText    = "⚡  RUN SELECTED";
        private const string searchHint = "Search tweaks...";
        private const string rule       = "└─────────────────────────────────────";

        private static readonly string[] sidebarCategories =
            ["All", .. tweakCatalog.categoryOrder, "Startup", "Services", "History", "Driver Cleanup", "Disk Cleanup"];

        private static readonly Dictionary<string, string> catEmoji = new()
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
        private flatButton      _clearSearchBtn, _runBtn, _undoBtn;
        private CheckBox        _restoreChk;
        private FlowLayoutPanel _tileGrid;
        private RichTextBox     _logBox;
        private System.Windows.Forms.Timer _ttHideTimer;
        private Dictionary<string, listTab> _tabs;

        private string _activeCategory = "All";
        private string _searchQuery    = "";
        // Field (not a local) so clearSearch can reset it — otherwise clearing after a
        // search wrote the placeholder text back as a real query and hid every tile.
        private bool _searchHasPlaceholder = true;
        private bool _isRunning;
        private CancellationTokenSource _scanCts;

        private readonly List<tweakTile> _tiles = new();
        // Persists each tweak's checked state across grid rebuilds (category switches,
        // preset applies, profile loads) so Select All/None and manual toggles survive
        // a re-render instead of snapping back to defaultOn every time.
        private readonly Dictionary<string, bool> _selectionState = new();
        // Tweak names applied this session that are still waiting on a reboot
        // or an Explorer restart to take full effect.
        private readonly List<string> _pendingReboot = new(), _pendingExplorer = new();

        public mainForm(bool showAdminWarning = false)
        {
            initUI();
            darkTitleBar.apply(this);
            populateGrid("All");
            _adminBadge.Visible = showAdminWarning;
            checkWhatsNew();
            _ = checkForUpdatesAsync();
            _ = detectHardwareAsync();
        }

        // The slow hardware probes run in the background; tiles are then greyed out or hidden in place
        // (no grid rebuild, so nothing jumps under the cursor).
        private async Task detectHardwareAsync()
        {
            try
            {
                await Task.Run(hardware.detect);
                applyHardwareState();
            }
            catch (Exception ex) { sessionLog.write("HARDWARE SCAN", ex); }   // form closed mid-scan
        }

        // Greys out or hides Laptop tiles this PC can't use, then refreshes visibility and the counter
        private void applyHardwareState()
        {
            foreach (var t in _tiles)
            {
                var (state, reason) = hardware.check(t.entry.tweakKey);
                t.setAvailability(state, reason);
            }
            applySearchFilter();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _scanCts?.Cancel();
            _ttHideTimer.Dispose();
            base.OnFormClosed(e);
        }

        private async Task checkForUpdatesAsync()
        {
            string newer = await updateChecker.checkAsync();
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
            catch (Exception ex) { sessionLog.write("UPDATE BADGE", ex); }   // form closed mid-check
        }

        // Offers the release notes once after an upgrade — not on a fresh install, which has no last_version.txt yet.
        private static void checkWhatsNew()
        {
            string current = appVersion.current, file = appPaths.lastVersionFile;
            try
            {
                string last = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                if (last == current) return;
                File.WriteAllText(file, current);
                if (last.Length > 0 && MessageBox.Show(
                        $"Win11 Optimizer has been updated to v{current}!\n\nWould you like to see what's new?",
                        "What's New in v" + current, MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                    proc.open($"{appVersion.repoUrl}/releases/tag/v{current}");
            }
            catch (Exception ex) { sessionLog.write("WHATS NEW", ex); }
        }

        private void initUI()
        {
            Dpi.Update(this);
            AutoScaleMode = AutoScaleMode.None;
            Text          = "Win11 Optimizer";
            Size          = new Size(Dpi.S(1200), Dpi.S(760));
            MinimumSize   = new Size(Dpi.S(960),  Dpi.S(620));
            BackColor     = theme.bg;
            ForeColor     = theme.textPri;
            Font          = theme.ui(9f);
            StartPosition = FormStartPosition.CenterScreen;

            buildTopBar();
            buildSidebar();
            buildMainArea();
            buildBottomBar();
            buildLogPanel();
            Controls.AddRange(new Control[] { _mainArea, _sidebar, _bottomBar, _topBar, _logPanel });
            buildTooltip();

            // The log panel floats over the main area rather than docking, so re-seat it on resize
            Resize += (s, e) => layoutAll();
            DpiChanged += (s, e) =>
            {
                Dpi.Update(e.DeviceDpiNew);
                if (e.SuggestedRectangle != Rectangle.Empty) Bounds = e.SuggestedRectangle;
                MinimumSize = new Size(Dpi.S(960), Dpi.S(620));
                rescalePanels();
            };
        }

        private void rescalePanels()
        {
            SuspendLayout();
            _topBar.Height    = Dpi.S(60);
            _bottomBar.Height = Dpi.S(136);
            _sidebar.Width    = Dpi.S(210);
            _logPanel.Height  = Dpi.S(180);   // hidden or not, so it opens at the right size later
            ResumeLayout(true);
            layoutAll();
        }

        private void layoutAll()
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
        private void buildTopBar()
        {
            _topBar = new Panel { BackColor = theme.surface, Height = Dpi.S(60), Dock = DockStyle.Top };
            _topBar.Paint += (s, e) =>
            {
                using var p = new Pen(theme.border);
                e.Graphics.DrawLine(p, 0, _topBar.Height - 1, _topBar.Width, _topBar.Height - 1);
                // Subtle gold top accent bar (like website nav)
                using var bar = new SolidBrush(Color.FromArgb(30, theme.accent));
                e.Graphics.FillRectangle(bar, 0, 0, _topBar.Width, 2);
            };

            _adminBadge = uiHelpers.lbl("⚠  Not running as Administrator — some tweaks may fail", theme.mono(7.5f), theme.warning, 18, 44);

            var ghLink = new flatButton("⭐  GITHUB ↗", theme.accent) { Size = new Size(Dpi.S(115), Dpi.S(30)), Font = theme.mono(7.5f, true) };
            ghLink.Click += (s, e) => proc.open(appVersion.repoUrl);

            // Shown only when the launch-time GitHub check finds a newer release
            _updateBadge = new Label
            {
                Font = theme.mono(7.5f, true), ForeColor = theme.accent, BackColor = Color.FromArgb(30, theme.accent),
                AutoSize = true, Padding = new Padding(6, 4, 6, 4), Cursor = Cursors.Hand, Visible = false
            };
            _updateBadge.Click += (s, e) => proc.open(appVersion.repoUrl + "/releases/latest");

            void positionTopRight(object s, EventArgs e)
            {
                ghLink.Location       = new Point(_topBar.Width - ghLink.Width - Dpi.S(16), (_topBar.Height - ghLink.Height) / 2);
                _updateBadge.Location = new Point(ghLink.Left - _updateBadge.Width - Dpi.S(10), (_topBar.Height - _updateBadge.Height) / 2);
            }
            _topBar.SizeChanged      += positionTopRight;
            _updateBadge.SizeChanged += positionTopRight;

            _topBar.Controls.AddRange(new Control[]
            {
                uiHelpers.lbl("CORN_SYSTEMS", theme.mono(7f, true), theme.accent, 18, 10),   // website nav-logo style
                uiHelpers.lbl("WIN11 OPTIMIZER  ⚡", theme.mono(9f, true), theme.textPri, 18, 30),
                _adminBadge, _updateBadge, ghLink
            });
        }

        // ── SIDEBAR ───────────────────────────────────────────────────────
        private void buildSidebar()
        {
            // AutoScroll: with 14 category buttons plus the Select All/None row, the stack is taller
            // than the sidebar at minimum window height — without it the bottom entries clip off-screen.
            _sidebar = new Panel { BackColor = theme.surface, Width = Dpi.S(210), AutoScroll = true, Dock = DockStyle.Left };
            _sidebar.Paint += (s, e) =>
            {
                using var p = new Pen(theme.border);
                e.Graphics.DrawLine(p, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
                // Subtle purple gradient at top — matches website section hero
                using var grad = new LinearGradientBrush(new Rectangle(0, 0, _sidebar.Width, 80),
                    Color.FromArgb(18, theme.skyPurple), Color.Transparent, LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(grad, 0, 0, _sidebar.Width, 80);
            };

            // "// CATEGORIES" — website section-label mono style with gold accent
            _sidebar.Controls.Add(uiHelpers.lbl("// CATEGORIES", theme.mono(7f, true), theme.accent, Dpi.S(14), Dpi.S(14)));

            int y = Dpi.S(38);
            foreach (var cat in sidebarCategories)
            {
                if (cat is "Startup" or "History" or "Driver Cleanup")
                {
                    _sidebar.Controls.Add(new Panel { BackColor = theme.border, Bounds = new Rectangle(Dpi.S(8), y + 2, Dpi.S(194), 1) });
                    y += Dpi.S(10);
                }
                var btn = makeSidebarBtn(cat);
                btn.SetBounds(Dpi.S(8), y, Dpi.S(194), Dpi.S(36));
                _sidebar.Controls.Add(btn);
                y += Dpi.S(38);
            }

            y += Dpi.S(8);
            var selAll  = new flatButton("✔ Select All", theme.accent)  { Bounds = new Rectangle(Dpi.S(8),   y, Dpi.S(92), Dpi.S(28)) };
            var selNone = new flatButton("✘ None",       theme.surface2) { Bounds = new Rectangle(Dpi.S(108), y, Dpi.S(94), Dpi.S(28)) };
            selAll.Click  += (s, e) => setAllInView(true);
            selNone.Click += (s, e) => setAllInView(false);
            _sidebar.Controls.AddRange(new Control[] { selAll, selNone });
        }

        private static string emoji(string cat) => catEmoji.TryGetValue(cat, out var em) ? em : "📦";

        private Button makeSidebarBtn(string cat)
        {
            var btn = new Button
            {
                Text = $"  {emoji(cat)}  {cat}", TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Tag = cat
            };
            btn.FlatAppearance.BorderSize = 0;
            styleSidebarBtn(btn);

            // Badge paint — small pill with the selection count on the right
            btn.Paint += (s, e) =>
            {
                int count = _tiles.Count(t => t.isChecked && (cat == "All" || t.entry.category == cat));
                if (count == 0) return;

                bool   active = cat == _activeCategory;
                string badge  = count.ToString();
                var    font   = theme.ui(7.5f, true);
                var    g      = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                SizeF sz = g.MeasureString(badge, font);
                int pw = (int)sz.Width + 10, ph = 16, px = btn.Width - pw - 8, py = (btn.Height - ph) / 2;
                using var pill = new SolidBrush(active ? theme.accent : Color.FromArgb(50, theme.accent));
                g.fillRoundedRect(pill, px, py, pw, ph, 8);
                using var text = new SolidBrush(active ? theme.accentText : theme.accent);
                g.DrawString(badge, font, text, px + (pw - sz.Width) / 2f, py + (ph - sz.Height) / 2f);
            };

            btn.Click += (s, e) =>
            {
                _activeCategory = cat;
                clearSearch();
                refreshSidebar();
                if (cat == "History")                        showView(_histPanel, buildHistoryContent);
                else if (_tabs.TryGetValue(cat, out var tab)) showView(tab, tab.activate);
                else                                          populateGrid(cat);
            };
            return btn;
        }

        private void styleSidebarBtn(Button btn)
        {
            bool active   = (string)btn.Tag == _activeCategory;
            btn.BackColor = active ? theme.accent : Color.Transparent;
            btn.ForeColor = active ? theme.accentText : theme.textDim;
            btn.Font      = theme.mono(8f, active);
            btn.FlatAppearance.MouseOverBackColor = active ? theme.accentHov : theme.surface2;
        }

        private void refreshSidebar()
        {
            foreach (var btn in _sidebar.Controls.OfType<Button>().Where(b => b.Tag is string)) styleSidebarBtn(btn);
        }

        // ── MAIN AREA ─────────────────────────────────────────────────────
        private void buildMainArea()
        {
            _mainArea = new Panel { BackColor = theme.bg, Dock = DockStyle.Fill };
            buildSearchBar();

            _tileGrid = new FlowLayoutPanel { AutoScroll = true, BackColor = theme.bg, Padding = new Padding(12), Dock = DockStyle.Fill };
            _tileGrid.HorizontalScroll.Enabled = false;
            _tileGrid.HorizontalScroll.Visible = false;
            // Stock FlowLayoutPanel isn't double-buffered; with ~90 tiles repainting at once
            // (Select All / None) that can flash the grid mid-redraw. DoubleBuffered is protected.
            typeof(Panel).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(_tileGrid, true);

            _histPanel = new Panel { AutoScroll = true, BackColor = theme.bg, Padding = new Padding(20), Dock = DockStyle.Fill, Visible = false };
            _tabs = new()
            {
                ["Startup"]        = new startupTab(),
                ["Services"]       = new servicesTab(),
                ["Driver Cleanup"] = new driverCleanupTab(),
                ["Disk Cleanup"]   = new diskCleanupTab(),
            };

            _mainArea.Controls.Add(_histPanel);
            _mainArea.Controls.AddRange(_tabs.Values.ToArray<Control>());
            _mainArea.Controls.Add(_tileGrid);
            _mainArea.Controls.Add(_searchBar);   // add last so it docks on top
        }

        private void buildSearchBar()
        {
            _searchBar = new Panel { BackColor = theme.surface, Height = Dpi.S(88), Dock = DockStyle.Top };
            uiHelpers.bottomBorder(_searchBar);

            // ── Row 1: search box
            var searchIcon = new Label
            {
                Text = "🔍", Font = theme.f("Segoe UI Emoji", 11f), BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(Dpi.S(28), Dpi.S(32)), Location = new Point(Dpi.S(10), Dpi.S(8))
            };

            _searchBox = new TextBox
            {
                Font = theme.ui(10f), ForeColor = theme.textSec, BackColor = theme.card, BorderStyle = BorderStyle.None,
                Location = new Point(Dpi.S(42), Dpi.S(14)), Height = Dpi.S(24), Width = Dpi.S(280), Text = searchHint
            };
            _searchBox.Enter += (s, e) =>
            {
                if (!_searchHasPlaceholder) return;
                _searchHasPlaceholder = false;
                _searchBox.Text       = "";
                _searchBox.ForeColor  = theme.textPri;
            };
            _searchBox.Leave += (s, e) => { if (string.IsNullOrWhiteSpace(_searchBox.Text)) resetSearchBox(); };
            _searchBox.TextChanged += (s, e) =>
            {
                if (_searchHasPlaceholder) return;
                _searchQuery = _searchBox.Text.Trim().ToLower();
                _clearSearchBtn.Visible = _searchQuery.Length > 0;
                applySearchFilter();
            };

            _clearSearchBtn = new flatButton("✕", theme.surface2)
            {
                Size = new Size(Dpi.S(22), Dpi.S(22)), Location = new Point(Dpi.S(326), Dpi.S(11)),
                Font = theme.ui(8f), ForeColor = theme.textSec, Visible = false
            };
            _clearSearchBtn.Click += (s, e) => clearSearch();

            var rowDivider = new Panel
            {
                BackColor = theme.border, Bounds = new Rectangle(0, Dpi.S(42), _searchBar.Width, 1),
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
                Text = "PRESETS", Font = theme.mono(6.5f, true), ForeColor = theme.accent, BackColor = Color.Transparent,
                AutoSize = true, Margin = new Padding(Dpi.S(8), Dpi.S(15), Dpi.S(4), 0)
            });

            flatButton makePreset(string label, string key, Color fg, Color bg, Color border, Color hover, int gap = 4)
            {
                var b = new flatButton(label, bg)
                {
                    Size      = new Size(TextRenderer.MeasureText(label, theme.ui(8.5f)).Width + Dpi.S(20), Dpi.S(28)),
                    Margin    = new Padding(0, Dpi.S(7), Dpi.S(gap), 0),
                    ForeColor = fg
                };
                b.FlatAppearance.BorderColor        = border;
                b.FlatAppearance.MouseOverBackColor = hover;
                if (key != "LaptopMenu") b.Click += (s, e) => applyPreset(key);   // that one opens the tier menu instead
                return b;
            }

            // Laptop is three presets behind one button — a small menu picks the tier
            var laptopMenu = new ContextMenuStrip { BackColor = theme.surface2, ForeColor = theme.textPri, ShowImageMargin = false, Font = theme.ui(8.5f) };
            foreach (var (text, key) in new[]
            {
                ("🔋  Light — invisible tweaks only",              "LaptopLight"),
                ("💻  Balanced — the everyday battery set",        "Laptop"),
                ("🪫  Max battery — also dims screen & blocks background apps", "LaptopMax"),
            })
                laptopMenu.Items.Add(text, null, (s, e) => applyPreset(key));

            var lime = Color.FromArgb(204, 255, 0);
            foreach (var (label, key) in new[]
            {
                ("⭐ Recommended", "Recommended"), ("🎮 Gaming PC", "Gaming"), ("🔒 Privacy", "Privacy"),
                ("🛡 Security", "Security"), ("🪶 Minimal", "Minimal"), ("💻 Laptop ▾", "LaptopMenu"),
                ("🧹 Clean Install", "CleanInstall"), ("🔬 Dev Machine", "DevMachine"),
            })
            {
                var btn = makePreset(label, key, theme.accent, theme.surface2, lime, Color.FromArgb(30, lime));
                if (key == "LaptopMenu")
                {
                    btn.Click += (s, e) => laptopMenu.Show(btn, new Point(0, btn.Height));
                    btn.Disposed += (s, e) => laptopMenu.Dispose();
                }
                presetStrip.Controls.Add(btn);
            }
            // Nuclear stays red — intentionally different to signal danger
            presetStrip.Controls.Add(makePreset("☢ Nuclear", "Nuclear", theme.danger, Color.FromArgb(45, 20, 20),
                Color.FromArgb(100, 40, 40), Color.FromArgb(60, 30, 30), gap: 8));

            _searchBar.Controls.AddRange(new Control[] { searchIcon, _searchBox, _clearSearchBtn, rowDivider, presetStrip });
        }

        private bool matches(tweakEntry e) =>
            _searchQuery.Length == 0 ||
            new[] { e.name, e.description, e.category, e.whatItChanges }.Any(f => f?.ToLower().Contains(_searchQuery) == true);

        // Hides non-matching tiles, and any section header left with no visible tile under it
        private void applySearchFilter()
        {
            sectionHeader header = null;
            bool headerHasHit = false;
            foreach (Control c in _tileGrid.Controls)
            {
                if (c is sectionHeader hdr)
                {
                    if (header != null) header.Visible = headerHasHit;
                    (header, headerHasHit) = (hdr, false);
                }
                else if (c is tweakTile tile)
                {
                    bool match = !tile.hiddenByHardware && matches(tile.entry);
                    tile.Visible  = match;
                    headerHasHit |= match;
                }
            }
            if (header != null) header.Visible = headerHasHit;
            updateSelCount();
        }

        private void resetSearchBox()
        {
            _searchHasPlaceholder = true;   // set BEFORE Text so TextChanged ignores it
            _searchBox.Text       = searchHint;
            _searchBox.ForeColor  = theme.textSec;
        }

        private void clearSearch()
        {
            _searchQuery = "";
            resetSearchBox();
            _clearSearchBtn.Visible = false;
            applySearchFilter();
        }

        // Presets and profile imports span every category — switch to All first, otherwise one
        // clicked while viewing e.g. "Gaming" would only touch the tiles on screen.
        private void showAllTweaks()
        {
            if (_activeCategory == "All") return;
            _activeCategory = "All";
            refreshSidebar();
            populateGrid("All");
        }

        private void applyPreset(string preset)
        {
            showAllTweaks();
            clearSearch();

            bool hasBattery = hardware.hasBattery;

            // Recommended + privacy/responsiveness/security + the Laptop tweaks up to a tier, minus
            // anything that trades battery for speed (or breaks hibernate-on-critical), and minus
            // cosmetic or security-trading tiles that have nothing to do with battery life.
            bool laptopPick(tweakEntry e, int maxTier) =>
                (e.defaultOn || e.category is "Privacy" or "Responsiveness" or "Security" or "Laptop")
                && e.tweakKey is not ("Perf_PowerPlan" or "Perf_Hibernate" or "Perf_MemCompression" or
                    "Perf_PowerThrottle" or "Perf_TimerRes" or "Resp_PlatformTick" or "Resp_VerboseStatus" or
                    "Adv_DynamicTick" or "Game_GPUPower" or "Game_HAGS" or
                    "Priv_SmartScreen" or "Priv_OneDrive" or "Resp_ThisPC" or "Resp_TaskbarClean" or "Sec_ShowExt")
                && (e.category != "Laptop" || laptopTiers.of(e.tweakKey) is > 0 && laptopTiers.of(e.tweakKey) <= maxTier);

            string noBattery = "Laptop — note: no battery detected, the battery-only settings won't do anything on this PC";
            (Func<tweakEntry, bool> pick, string msg, Color col) p = preset switch
            {
                "Recommended" => (e => e.defaultOn,
                    "Recommended (safe defaults)", theme.accent),
                // Net_DoH swaps your DNS provider — nothing to do with gaming. Fast Startup off gives a true cold boot
                "Gaming" => (e => (e.defaultOn || e.category is "Gaming" or "Network" || e.tweakKey == "Perf_FastStartup")
                                  && e.tweakKey != "Net_DoH",
                    "Gaming PC (recommended + gaming + network)", theme.success),
                // OneDrive sync is a workflow decision, not a privacy default — it's Clean Install / Nuclear only
                "Privacy" => (e => (e.defaultOn || e.category == "Privacy") && e.tweakKey != "Priv_OneDrive",
                    "Privacy (recommended + all privacy tweaks)", Color.FromArgb(168, 85, 247)),
                // A Security preset must not weaken security: no SmartScreen-off, and no hosts-file block (Defender flags it as a hijack)
                "Security" => (e => (e.defaultOn || e.category == "Security") && e.tweakKey is not ("Priv_SmartScreen" or "Priv_HostsBlock"),
                    "Security (recommended + all security tweaks)", Color.FromArgb(16, 185, 129)),
                // Everything except Bloatware (irreversible), Advanced (risky) and Laptop (use the Laptop preset)
                "Nuclear" => (e => e.category is not ("Bloatware" or "Advanced" or "Laptop"),
                    "Nuclear — all tweaks except Bloatware, Advanced & Laptop", theme.danger),
                // Only the lightest, safest tweaks — responsiveness + privacy basics
                "Minimal" => (e => e.tweakKey is
                        "Resp_MenuDelay" or "Resp_AppKill" or "Resp_ServiceKill" or
                        "Resp_AutoEndTasks" or "Resp_WinTips" or "Resp_SuggestedContent" or
                        "Priv_AdvertisingId" or "Priv_BingStart" or "Priv_ChatIcon" or
                        "Priv_Feedback" or "Priv_AppTracking" or "Priv_Recall" or
                        "Perf_StartupDelay" or "Perf_VisualFX" or "Priv_CloudContent" or
                        "Resp_NagScreens" or "Priv_SearchCloud",
                    "Minimal — safe UI & privacy tweaks only", theme.textSec),
                // Light: only the near-invisible battery tweaks — no privacy/responsiveness extras
                "LaptopLight" => (e => e.category == "Laptop" && laptopTiers.of(e.tweakKey) is > 0 and <= 1,
                    hasBattery ? "Laptop Light — invisible battery tweaks only" : noBattery,
                    hasBattery ? Color.FromArgb(125, 211, 252) : theme.warning),
                // Balanced: the everyday set. Lap_BackgroundApps and the brightness cap are Max-only (visible trade-offs)
                "Laptop" => (e => laptopPick(e, 2),
                    hasBattery ? "Laptop Balanced — battery-life tweaks + battery-safe privacy & responsiveness" : noBattery,
                    hasBattery ? Color.FromArgb(56, 189, 248) : theme.warning),
                "LaptopMax" => (e => laptopPick(e, 3),
                    hasBattery ? "Laptop Max battery — everything in Balanced, plus dimmer screen, passive cooling & no background Store apps" : noBattery,
                    hasBattery ? Color.FromArgb(14, 165, 233) : theme.warning),
                "CleanInstall" => (e => (e.category is "Bloatware" or "Privacy"
                        || e.tweakKey is "Sec_Defender" or "Sec_NetBIOS" or "Sec_RDP"
                                      or "Resp_WinTips" or "Resp_SuggestedContent" or "Perf_StartupDelay"
                                      or "Resp_NagScreens" or "Resp_NoForcedRestart" or "Resp_ThisPC" or "Resp_TaskbarClean"
                                      or "Perf_ReservedStorage" or "Sec_ShowExt" or "Sec_LLMNR" or "Sec_Firewall"
                                      or "Sec_DefenderPUA" or "Sec_PS2" or "Sec_RemoteAssist")
                        && e.tweakKey != "Priv_SmartScreen",
                    "Clean Install — bloatware removed, telemetry killed, baseline secured", Color.FromArgb(52, 211, 153)),
                "DevMachine" => (e => (e.category is "Performance" or "Responsiveness" or "Network"
                        || e.tweakKey is "Adv_ProcessorScheduling" or "Adv_CPUThrottle" or "Adv_DynamicTick" or "Adv_TRIM"
                                      or "Adv_Animations" or "Priv_Telemetry" or "Priv_TelemetryTasks" or "Priv_DiagTrack"
                                      or "Priv_Feedback" or "Priv_WER" or "Game_CPUPriority"
                                      or "Priv_AppCompatTelemetry" or "Sec_ShowExt")
                        // Net_DoH would swap the DNS provider (breaks VPN / split-DNS); ReservedStorage matters when a dev disk fills
                        && e.tweakKey is not ("Perf_WSearch" or "Perf_Hibernate" or "Game_DVR" or "Game_HAGS"
                                              or "Game_FSO" or "Game_GameMode" or "Game_NvidiaTelemetry"
                                              or "Net_DoH" or "Perf_ReservedStorage" or "Resp_TaskbarClean"),
                    "Dev Machine — max performance, keeps Search & Hibernate", Color.FromArgb(167, 139, 250)),
                _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preset")
            };

            foreach (var t in _tiles) t.isChecked = p.pick(t.entry);
            setStatus("Preset applied: " + p.msg, p.col);
            updateSelCount();
        }

        // Hides every full-page view in the main area, then shows the requested one.
        private void showView(Control view, Action init)
        {
            foreach (Control c in _mainArea.Controls) c.Visible = c == view;
            init?.Invoke();
        }

        private void populateGrid(string filter)
        {
            showView(_tileGrid, null);
            _searchBar.Visible = true;

            _tileGrid.SuspendLayout();
            uiHelpers.clearAndDispose(_tileGrid);
            _tiles.Clear();
            sectionHeader.resetIndex();

            foreach (var group in tweakCatalog.forCategory(filter).GroupBy(t => t.category).OrderBy(g => tweakCatalog.orderOf(g.Key)))
            {
                _tileGrid.Controls.Add(new sectionHeader(group.Key, emoji(group.Key)));
                foreach (var entry in group)
                {
                    // Restore whatever the user last set this tweak to; only fall back
                    // to defaultOn the first time a tile is ever created.
                    var tile = new tweakTile(entry)
                    {
                        isChecked = _selectionState.TryGetValue(entry.tweakKey, out var wasChecked) ? wasChecked : entry.defaultOn
                    };
                    // Applied by the app in a previous session
                    if (appliedState.isApplied(entry.tweakKey)) tile.setApplied(appliedSource.appliedByApp);
                    tile.checkedChanged += (s, e) =>
                    {
                        _selectionState[entry.tweakKey] = tile.isChecked;
                        updateSelCount();
                        setStatus("Ready", theme.textSec);
                    };
                    foreach (var c in tile.Controls.Cast<Control>().Prepend(tile))
                    {
                        c.MouseEnter += (s, e) => showTooltip(tile);
                        c.MouseLeave += (s, e) => hideTooltip();
                    }
                    _tiles.Add(tile);
                    _tileGrid.Controls.Add(tile);
                }
            }

            _tileGrid.ResumeLayout(true);
            applyHardwareState();   // also refreshes the counter; the slow probes re-run it once they finish
            _ = runDetectionScanAsync();
        }

        private void setAllInView(bool check)
        {
            _tileGrid.SuspendLayout();
            foreach (var t in _tiles.Where(t => t.Visible)) t.isChecked = check;
            _tileGrid.ResumeLayout(true);
            updateSelCount();
        }

        private void updateSelCount()
        {
            // Run applies every checked tile in the view — including ones a search is
            // hiding — so the counter must count those too, and call them out.
            var selected = _tiles.Where(t => t.isChecked).ToList();
            int hidden   = _searchQuery.Length == 0 ? 0 : selected.Count(t => !t.Visible);
            foreach (var btn in _sidebar.Controls.OfType<Button>()) btn.Invalidate();   // refresh count badges

            _selCountLabel.Text = selected.Count == 0
                ? "No tweaks selected"
                : $"{selected.Count} tweak{(selected.Count == 1 ? "" : "s")} selected" + (hidden > 0 ? $"  ({hidden} hidden by search)" : "");
            _undoBtn.Enabled = !_isRunning && selected.Any(t => tweakEngine.hasBackup(t.entry.category));
        }

        // Scans the live system state for each tile in the background. Tiles already applied by
        // the app keep that state; only unknown ones are checked against the live system. A new
        // grid cancels the previous scan instead of stacking another one on top of it.
        private async Task runDetectionScanAsync()
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
                        string key = tile.entry.tweakKey;
                        if (appliedState.isApplied(key) || tweakDetector.check(key) != true) continue;
                        appliedState.markApplied(new[] { key });   // persist so it survives future sessions
                        BeginInvoke(new Action(() => tile.setApplied(appliedSource.detectedOnSystem)));
                    }
                });
            }
            catch (Exception ex) { sessionLog.write("DETECT SCAN", ex); }   // form closed mid-scan
            finally
            {
                if (_scanCts == cts) _scanCts = null;
                cts.Dispose();
            }
        }

        // ── HISTORY ───────────────────────────────────────────────────────
        private void buildHistoryContent()
        {
            _histPanel.SuspendLayout();
            uiHelpers.clearAndDispose(_histPanel);
            int w = _histPanel.ClientSize.Width;

            var topRow = new Panel
            {
                Bounds = new Rectangle(0, 0, w, 44), BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var clearBtn = new flatButton("Clear History", theme.danger)
            {
                Size = new Size(120, 28), Location = new Point(w - 124, 8), ForeColor = Color.White,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            clearBtn.Click += (s, e) =>
            {
                if (MessageBox.Show("Clear all run history?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                changeLog.clear();
                BeginInvoke(new Action(buildHistoryContent));   // rebuilding disposes this button — let its click finish first
            };
            topRow.Controls.AddRange(new Control[] { uiHelpers.lbl("// RUN HISTORY", theme.ui(9f, true), theme.textSec, 0, 13), clearBtn });
            _histPanel.Controls.Add(topRow);

            int y = 52;
            if (changeLog.entries.Count == 0)
                _histPanel.Controls.Add(uiHelpers.lbl("No runs recorded yet. Apply some tweaks to start tracking history.", theme.ui(10f), theme.textSec, 0, y + 10));

            foreach (var entry in changeLog.entries)
            {
                var card = new Panel
                {
                    Bounds = new Rectangle(0, y, w - 4, 86), BackColor = theme.surface,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                card.Paint += (s, e) =>
                {
                    using var pen    = new Pen(theme.border);
                    using var stripe = new SolidBrush(theme.accent);
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                    e.Graphics.FillRectangle(stripe, 0, 0, 3, card.Height);
                };

                string stats = $"✔ {entry.passed} succeeded   ✘ {entry.failed} failed" + (entry.restorePoint ? "   🛡 Restore Point" : "");
                card.Controls.AddRange(new Control[]
                {
                    uiHelpers.lbl(entry.timestamp,                   theme.ui(9f, true), theme.accent,   14, 10),
                    uiHelpers.lbl(entry.windowsVer,                  theme.ui(8.5f),     theme.textSec, 14, 28),
                    uiHelpers.lbl($"Categories: {entry.categories}", theme.ui(9f),       theme.textPri, 14, 46),
                    uiHelpers.lbl(stats, theme.ui(9f, true), entry.failed == 0 ? theme.success : theme.warning, 14, 64),
                });

                var details = entry.details ?? new();   // null when hand-edited JSON drops the field
                if (details.Count > 0)
                {
                    var dl = uiHelpers.lbl(string.Join("  ·  ", details.Take(8)) + (details.Count > 8 ? $"  … +{details.Count - 8} more" : ""),
                                           theme.f("Consolas", 7.5f), theme.textSec, 14, 82, 30);
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
        private void buildBottomBar()
        {
            _bottomBar = new Panel { BackColor = theme.surface, Height = Dpi.S(136), Dock = DockStyle.Bottom };
            _bottomBar.Paint += (s, e) =>
            {
                using var p = new Pen(theme.border);
                e.Graphics.DrawLine(p, 0, 0, _bottomBar.Width, 0);
            };

            _progInner = new Panel { BackColor = theme.accent, Size = new Size(0, Dpi.S(6)) };
            _progOuter = new Panel { BackColor = theme.border, Location = new Point(Dpi.S(16), Dpi.S(12)), Size = new Size(Dpi.S(500), Dpi.S(6)) };
            _progOuter.Controls.Add(_progInner);

            _statusLabel   = uiHelpers.lbl("Ready",              null, theme.textDim, Dpi.S(16), Dpi.S(24));
            _selCountLabel = uiHelpers.lbl("No tweaks selected", null, theme.textDim, Dpi.S(16), Dpi.S(46));

            _restoreChk = new CheckBox
            {
                Text = "🛡 Create Restore Point before running", ForeColor = theme.textDim, BackColor = Color.Transparent,
                Checked = true, AutoSize = true, Location = new Point(Dpi.S(16), Dpi.S(92)), FlatStyle = FlatStyle.Flat
            };
            _restoreChk.FlatAppearance.BorderColor        = theme.border;
            _restoreChk.FlatAppearance.CheckedBackColor   = theme.accent;
            _restoreChk.FlatAppearance.MouseOverBackColor = theme.surface;

            _undoBtn = new flatButton("↩ Undo Selected", theme.surface2) { Size = new Size(Dpi.S(150), Dpi.S(36)), Enabled = false };
            _undoBtn.Click += onUndoClicked;

            var clearBtn = new flatButton("Clear Selection", theme.surface2) { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            clearBtn.Click += (s, e) => setAllInView(false);

            _runBtn = new flatButton(runText, theme.accent) { Size = new Size(Dpi.S(165), Dpi.S(36)), Font = theme.mono(8.5f, true) };
            _runBtn.Click += onRunClicked;

            var logToggle = new flatButton("📋 Log", theme.surface2) { Size = new Size(Dpi.S(70), Dpi.S(26)) };
            logToggle.Click += (s, e) => { _logPanel.Visible = !_logPanel.Visible; layoutAll(); };

            // Shell/UI tweaks (visual effects, menu delay, taskbar icons) only need Explorer
            // restarted — this makes those feel instant instead of waiting on a full reboot.
            var explorerBtn = new flatButton("⟳ Restart Explorer", theme.surface2) { Size = new Size(Dpi.S(140), Dpi.S(26)), ForeColor = theme.textSec };
            explorerBtn.Click += (s, e) =>
            {
                if (MessageBox.Show(
                        "Restart Windows Explorer now?\n\nThe taskbar and open folder windows " +
                        "will briefly disappear and come back. Unsaved work in other apps is not affected.",
                        "Restart Explorer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    restartExplorer();
            };

            // Amber badge listing exactly what's waiting on a reboot / Explorer
            // restart after a run — click it for the full tweak list.
            _rebootBadge = new Label
            {
                Font = theme.mono(7.5f, true), ForeColor = theme.warning, BackColor = Color.FromArgb(30, theme.warning),
                AutoSize = true, Padding = new Padding(6, 3, 6, 3), Location = new Point(Dpi.S(16), Dpi.S(66)),
                Cursor = Cursors.Hand, Visible = false
            };
            _rebootBadge.Click += (s, e) =>
            {
                string msg = (_pendingReboot.Count   > 0 ? "Waiting on a REBOOT:" + bullets(_pendingReboot) + "\n\n" : "")
                           + (_pendingExplorer.Count > 0 ? "Waiting on an EXPLORER RESTART:" + bullets(_pendingExplorer) : "");
                if (msg.Length > 0)
                    MessageBox.Show(msg.TrimEnd(), "Pending Changes", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            var exportBtn = new flatButton("↑ Export Profile", theme.surface2) { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            exportBtn.Click += (s, e) => tweakProfile.export(_tiles.Where(t => t.isChecked).Select(t => t.entry.tweakKey));

            var importBtn = new flatButton("↓ Import Profile", theme.surface2) { Size = new Size(Dpi.S(130), Dpi.S(36)) };
            importBtn.Click += (s, e) =>
            {
                var keys = tweakProfile.import();
                if (keys == null) return;
                showAllTweaks();
                clearSearch();
                var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                foreach (var t in _tiles) t.isChecked = keySet.Contains(t.entry.tweakKey);
                setStatus($"Profile imported — {_tiles.Count(t => t.isChecked)} tweaks selected.", theme.accent);
                updateSelCount();
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

        private static string bullets(IEnumerable<string> items) => "\n  • " + string.Join("\n  • ", items);

        private void buildLogPanel()
        {
            var bg = Color.FromArgb(8, 8, 20);
            _logBox = new darkRichTextBox
            {
                BackColor = bg, ForeColor = Color.FromArgb(180, 200, 255), BorderStyle = BorderStyle.None, ReadOnly = true,
                Font = theme.f("Consolas", 8.5f), Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical
            };
            _logPanel = new Panel { BackColor = bg, Visible = false, Height = Dpi.S(180) };

            var hdr = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = theme.surface };
            var closeBtn = new flatButton("✕", theme.surface) { Size = new Size(24, 24), Font = theme.ui(8f), ForeColor = theme.textSec };
            closeBtn.Click += (s, e) => { _logPanel.Visible = false; layoutAll(); };
            hdr.SizeChanged += (s, e) => closeBtn.Location = new Point(hdr.Width - 26, 1);
            hdr.Controls.AddRange(new Control[] { uiHelpers.lbl("// OUTPUT LOG", theme.mono(7.5f, true), theme.textSec, 10, 6), closeBtn });

            _logPanel.Controls.Add(_logBox);
            _logPanel.Controls.Add(hdr);
        }

        // ── TOOLTIP ───────────────────────────────────────────────────────
        private void buildTooltip()
        {
            _ttTitle = new Label
            {
                Font = theme.f("Segoe UI Semibold", 9f), ForeColor = theme.textPri, BackColor = Color.Transparent,
                Location = new Point(Dpi.S(20), Dpi.S(10)), Size = new Size(Dpi.S(268), Dpi.S(18)),
                UseMnemonic = false   // "Inking & Typing" etc. — the & was being eaten as a mnemonic
            };
            _ttWhat = new Label
            {
                Font = theme.f("Consolas", 7.5f), ForeColor = theme.textSec, BackColor = Color.Transparent,
                Location = new Point(Dpi.S(20), Dpi.S(32)), Size = new Size(Dpi.S(268), Dpi.S(120)), UseMnemonic = false
            };
            _tooltip = new Panel { BackColor = Color.FromArgb(13, 12, 28), Size = new Size(Dpi.S(300), 0), Visible = false };
            _tooltip.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(80, theme.accent), 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, _tooltip.Width - 1, _tooltip.Height - 1);
                using var bar = new SolidBrush(theme.accent);
                e.Graphics.FillRectangle(bar, 0, 0, 3, _tooltip.Height);
            };
            _tooltip.Controls.AddRange(new Control[] { _ttTitle, _ttWhat });

            // Hide timer — small delay so moving between tiles doesn't flicker
            _ttHideTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _ttHideTimer.Tick += (s, e) => { _ttHideTimer.Stop(); _tooltip.Visible = false; };

            Controls.Add(_tooltip);
            _tooltip.BringToFront();
        }

        // Live "what is it set to right now" lines for power-plan tweaks. Read off the UI thread the
        // first time a tile is hovered, cached until the next run/undo. "" = nothing to show.
        private readonly Dictionary<string, string> _nowCache = new();
        private readonly HashSet<string> _nowPending = new();
        private tweakTile _ttTile;

        private async void loadCurrentValueAsync(string key)
        {
            if (!_nowPending.Add(key)) return;
            try
            {
                string now = await Task.Run(() => tweakDetector.currentSummary(key));
                _nowCache[key] = now ?? "";
                if (_ttTile?.entry.tweakKey == key && _tooltip.Visible) showTooltip(_ttTile);
            }
            catch (Exception ex) { sessionLog.write("TOOLTIP NOW", ex); }   // form closed mid-read
            finally { _nowPending.Remove(key); }
        }

        private void showTooltip(tweakTile tile)
        {
            _ttHideTimer.Stop();
            string raw = tile.entry.whatItChanges;
            if (string.IsNullOrEmpty(raw)) return;

            _ttTile       = tile;
            _ttTitle.Text = tile.entry.name;
            // First line is the command / registry change, the rest explains it
            int split = raw.IndexOf('\n');
            _ttWhat.Text = split < 0 ? raw : raw[..split].Trim() + "\n\n" + raw[(split + 1)..].Trim();

            if (tile.unavailableReason != null) _ttWhat.Text += "\n\n⛔ Not available here: " + tile.unavailableReason;
            else if (tweakEngine.powerTweaks.ContainsKey(tile.entry.tweakKey))
            {
                string side = tile.entry.tweakKey.StartsWith("Lap_", StringComparison.Ordinal) ? "battery" : "plugged in";
                if (_nowCache.TryGetValue(tile.entry.tweakKey, out string now)) { if (now.Length > 0) _ttWhat.Text += $"\n\nNow ({side}): " + now; }
                else loadCurrentValueAsync(tile.entry.tweakKey);
            }
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

        private void hideTooltip() => _ttHideTimer.Start();

        // ── RUN / UNDO ────────────────────────────────────────────────────
        private void setRunning(bool running, string runText = null)
        {
            _isRunning      = running;
            if (!running) _nowCache.Clear();   // a run/undo just changed the power plan — re-read the tooltip values
            _runBtn.Enabled = !running;
            if (runText != null) _runBtn.Text = runText;
            updateSelCount();   // also gates Undo on _isRunning
        }

        private async void onRunClicked(object sender, EventArgs e)
        {
            if (_isRunning) return;

            var selected = _tiles.Where(t => t.isChecked).OrderBy(t => tweakCatalog.orderOf(t.entry.category)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select at least one tweak to run.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            setRunning(true, "⏳ Running...");
            var results = new List<tweakEngine.tweakResult>();
            List<string> needReboot = new(), needExplorer = new();
            int ran = 0;
            try
            {
                tweakEngine.clearResults();
                bool rpCreated = false;
                if (_restoreChk.Checked)
                {
                    setStatus("Creating System Restore Point…", theme.warning);
                    appendLog("🛡 Creating System Restore Point…");
                    rpCreated = await Task.Run(() => tweakEngine.createRestorePoint("Win11Optimizer — before tweaks"));
                    appendLog(rpCreated ? "🛡 Restore Point created." : "⚠ Restore Point failed or skipped.");
                }

                setProgress(0, selected.Count);
                try
                {
                    await Task.Run(() =>
                    {
                        string lastCat = null;
                        foreach (var tile in selected)
                        {
                            var entry = tile.entry;
                            Invoke(new Action(() =>
                            {
                                if (entry.category != lastCat)
                                {
                                    if (lastCat != null) appendLog(rule);
                                    appendLog($"┌─ {entry.category.ToUpper()}");
                                }
                                // Mark the tile as running right before it executes (not all at once up front)
                                tile.setStatus(tileStatus.running);
                                setProgress(ran + 1, selected.Count);
                                setStatus($"[{ran + 1}/{selected.Count}]  {entry.category} → {entry.name}", theme.warning);
                                appendLog($"  → {entry.name}…");
                                _tileGrid.ScrollControlIntoView(tile);
                            }));
                            lastCat = entry.category;

                            var res = tweakEngine.apply(entry);
                            results.AddRange(res);
                            ran++;
                            Invoke(new Action(() => { foreach (var r in res) appendLog(r.success ? $"  ✔  {r.name}" : $"  ✘  {r.name}: {r.error}"); }));
                        }
                        if (lastCat != null) Invoke(new Action(() => appendLog(rule)));
                    });
                }
                catch (Exception ex)
                {
                    // Record whatever did run below instead of leaving the app stuck mid-run
                    sessionLog.write("RUN", ex);
                    appendLog($"✘ Run aborted: {ex.Message}");
                }

                var done = selected.Take(ran).ToList();
                int fail = results.Count(r => !r.success), pass = results.Count - fail;
                foreach (var t in done)
                {
                    t.setStatus(tileStatus.done);
                    t.setApplied(appliedSource.appliedByApp);
                }
                appliedState.markApplied(done.Select(t => t.entry.tweakKey));   // persist per-tweak applied state

                setProgress(1, 1);
                setStatus($"Complete — {pass} succeeded, {fail} failed.", fail == 0 ? theme.success : theme.warning);

                // Classify exactly what's pending instead of a blanket "reboot recommended"
                (needReboot, needExplorer) = rebootInfo.split(done.Select(t => t.entry));
                _pendingReboot.AddRange(needReboot.Except(_pendingReboot).ToList());
                _pendingExplorer.AddRange(needExplorer.Except(_pendingExplorer).ToList());
                refreshRebootBadge();

                string pendingNote = needReboot.Count   > 0 ? $" {needReboot.Count} tweak(s) need a reboot."
                                   : needExplorer.Count > 0 ? $" {needExplorer.Count} tweak(s) need an Explorer restart."
                                   : " No reboot needed.";
                appendLog($"══ COMPLETE: {pass} succeeded, {fail} failed.{pendingNote} ══");

                changeLog.addEntry(new changeLog.runEntry
                {
                    categories   = string.Join(", ", done.Select(t => t.entry.category).Distinct()),
                    passed       = pass,
                    failed       = fail,
                    restorePoint = rpCreated,
                    details      = results.Select(r => r.line).ToList()
                });
            }
            finally { setRunning(false, runText); }

            if (IsDisposed) return;
            // Only nag about rebooting when something applied actually needs one
            if (needReboot.Count > 0) promptReboot(needReboot);
            else if (needExplorer.Count > 0 && MessageBox.Show(
                         $"{needExplorer.Count} tweak(s) take effect after an Explorer restart.\n\n" +
                         "Restart Explorer now? (Taskbar and folder windows reload — other apps are unaffected.)",
                         "Explorer Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                         MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                restartExplorer();
        }

        private async void onUndoClicked(object sender, EventArgs e)
        {
            if (_isRunning) return;

            var undoCats = _tiles.Where(t => t.isChecked && tweakEngine.hasBackup(t.entry.category))
                                 .Select(t => t.entry.category).Distinct().ToList();
            if (undoCats.Count == 0) return;

            // Undo restores a whole category, not individual tiles — say so before doing it
            if (MessageBox.Show(
                    "Undo restores every tweak this app changed in:\n" + bullets(undoCats) +
                    "\n\n(not just the tiles you have selected). Continue?",
                    "Undo Tweaks", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            setRunning(true);
            try
            {
                foreach (var cat in undoCats)
                {
                    appendLog($"↩ Undoing {cat}…");
                    var res = await Task.Run(() => tweakEngine.undo(cat));
                    foreach (var r in res.Where(r => !r.success)) appendLog($"  ✘  {r.name}: {r.error}");
                    appendLog($"  ↩ {cat} done — {res.Count(r => r.success)} restored, {res.Count(r => !r.success)} failed.");
                }

                setStatus("Undo complete. Reboot recommended.", theme.success);
                // The whole category was restored, so clear applied state for every tweak in it —
                // not only the tiles that happened to be checked.
                appliedState.markUndone(tweakCatalog.all.Where(t => undoCats.Contains(t.category)).Select(t => t.tweakKey).ToList());
                foreach (var t in _tiles.Where(t => undoCats.Contains(t.entry.category)))
                {
                    t.setApplied(appliedSource.none);
                    t.setStatus(tileStatus.none);
                }
            }
            catch (Exception ex)
            {
                sessionLog.write("UNDO", ex);
                appendLog($"✘ Undo aborted: {ex.Message}");
            }
            finally { setRunning(false); }
        }

        // ── STATUS / LOG ──────────────────────────────────────────────────
        private void onUiThread(Action a)
        {
            if (InvokeRequired) Invoke(a); else a();
        }

        private void setStatus(string msg, Color col = default) => onUiThread(() =>
        {
            _statusLabel.Text      = msg;
            _statusLabel.ForeColor = col == default ? theme.textSec : col;
        });

        private void setProgress(int done, int total) =>
            onUiThread(() => _progInner.Width = total == 0 ? 0 : (int)((double)_progOuter.Width * done / total));

        private void appendLog(string msg) => onUiThread(() =>
        {
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            _logBox.ScrollToCaret();
        });

        private void promptReboot(List<string> pendingNames)
        {
            string list = pendingNames.Count <= 6
                ? "\n\nWaiting on the reboot:" + bullets(pendingNames)
                : $"\n\n{pendingNames.Count} applied tweaks are waiting on the reboot.";
            if (MessageBox.Show($"Some tweaks require a reboot to take full effect.{list}\n\nWould you like to reboot now?",
                    "Reboot Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try
            {
                var (code, _, err) = proc.run("shutdown.exe", "/r /t 10 /c \"Win11 Optimizer: Rebooting to apply tweaks.\"");
                if (code != 0) appendLog($"✘ Reboot failed: {err.Trim()} (exit code {code})");
            }
            catch (Exception ex)
            {
                sessionLog.write("REBOOT", ex);
                appendLog($"✘ Reboot failed: {ex.Message}");
            }
        }

        private void restartExplorer()
        {
            if (!explorerHelper.restart(out string err))
            {
                appendLog($"✘ Explorer restart failed: {err}");
                return;
            }
            appendLog("⟳ Explorer restarted.");
            _pendingExplorer.Clear();
            refreshRebootBadge();
        }

        private void refreshRebootBadge()
        {
            var parts = new List<string>();
            if (_pendingReboot.Count   > 0) parts.Add($"{_pendingReboot.Count} awaiting reboot");
            if (_pendingExplorer.Count > 0) parts.Add($"{_pendingExplorer.Count} awaiting Explorer restart");
            _rebootBadge.Text    = "⚠ " + string.Join("  ·  ", parts) + "  (click for list)";
            _rebootBadge.Visible = parts.Count > 0;
        }
    }
}
