using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    // Shared shell for the full-page list tabs (Startup, Services, Driver / Disk Cleanup): a toolbar
    // with a "// TITLE" label, live summary and right-aligned buttons over an auto-scrolling row stack.
    public abstract class listTab : Panel
    {
        protected readonly Panel toolbar  = new() { BackColor = theme.surface, Height = 54, Dock = DockStyle.Top };
        protected readonly Panel scroller = new() { BackColor = theme.bg, Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 10, 16, 10) };
        protected readonly Panel inner    = new() { BackColor = theme.bg, AutoSize = true };
        protected readonly Label summary;
        private bool _loaded;

        protected listTab(string title, int summaryX)
        {
            SuspendLayout();   // no layout (and so no Reflow) until the subclass has built its controls
            BackColor = theme.bg;
            Dock      = DockStyle.Fill;
            Visible   = false;
            uiHelpers.bottomBorder(toolbar);
            summary = uiHelpers.lbl("", theme.mono(7.5f), theme.textDim, summaryX, 18);
            toolbar.Controls.AddRange(new Control[] { uiHelpers.lbl("// " + title, theme.mono(9.5f, true), theme.textPri, 16, 15), summary });
            scroller.Controls.Add(inner);
            scroller.Resize += (s, e) => { inner.Width = scroller.ClientSize.Width - scroller.Padding.Horizontal; reflow(); };
            Controls.Add(scroller);
            Controls.Add(toolbar);   // docked top, added last
            ResumeLayout(false);
        }

        // First view loads the tab; startupTab overrides to reload on every visit.
        public virtual void activate()
        {
            if (_loaded) return;
            _loaded = true;
            onFirstActivate();
        }

        protected virtual void onFirstActivate() { }
        protected abstract void reflow();

        protected static flatButton ghost(string text, int w, EventHandler click, Color? fg = null, Font font = null)
        {
            var b = new flatButton(text, theme.surface2) { Size = new Size(w, 28), ForeColor = fg ?? theme.textSec };
            if (font != null) b.Font = font;
            b.Click += click;
            return b;
        }

        protected static flatButton primary(string text, Color bg, EventHandler click)
        {
            var b = new flatButton(text, bg) { Size = new Size(150, 28), Font = theme.mono(7.5f, true) };
            b.Click += click;
            return b;
        }

        // Adds toolbar buttons and keeps them right-aligned — pass them right-to-left, each with the gap after it.
        protected void alignRight(params (Control c, int gap)[] buttons)
        {
            foreach (var (c, _) in buttons) toolbar.Controls.Add(c);
            toolbar.SizeChanged += (s, e) => uiHelpers.layoutButtonsRightToLeft(toolbar, 13, buttons);
        }

        protected void fitInner() => inner.Width = Math.Max(100, scroller.ClientSize.Width - scroller.Padding.Horizontal);

        // Swaps in a fresh set of rows (disposing the old ones) and wires each row's Changed event.
        protected void setRows<t>(List<t> rows, IEnumerable<t> fresh, Action changed) where t : cardRow
        {
            foreach (var r in rows) r.Dispose();
            rows.Clear();
            foreach (var r in fresh)
            {
                r.changed += (s, e) => changed();
                rows.Add(r);
                inner.Controls.Add(r);
            }
            fitInner();
            reflow();
        }

        // Runs a background job with the given controls disabled. A failure is logged and shown in
        // the summary instead of escaping an async handler and leaving the buttons disabled for good.
        protected async Task<bool> busy(string status, Func<Task> work, params Control[] locked)
        {
            foreach (var c in locked) c.Enabled = false;
            summary.Text = status;
            try { await work(); return true; }
            catch (Exception ex)
            {
                sessionLog.write(GetType().Name.ToUpper(), ex);
                summary.Text = "✘ " + ex.Message;
                return false;
            }
            finally { foreach (var c in locked) c.Enabled = true; }
        }
    }

    // Bordered list card with a 3px status stripe down its left edge.
    public abstract class cardRow : Panel
    {
        public event EventHandler changed;

        protected cardRow(int height)
        {
            Height      = height;
            BackColor   = theme.card;
            SizeChanged += (s, e) => layoutRow();   // subscribed after Height, so it can't fire before the subclass builds
        }

        protected abstract Color stripe { get; }
        protected abstract void layoutRow();
        protected void onChanged() => changed?.Invoke(this, EventArgs.Empty);

        // Right-aligns c against `right`, vertically centred (+dy); returns its left edge.
        protected int place(int right, Control c, int dy = 0)
        {
            c.Location = new Point(right - c.Width, (Height - c.Height) / 2 + dy);
            return c.Left;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(theme.border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using var br = new SolidBrush(stripe);
            e.Graphics.FillRectangle(br, 0, 0, 3, Height);
        }
    }
}
