using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Optimizer
{
    // Shared shell for the full-page list tabs (Startup, Services, Driver / Disk Cleanup): a toolbar
    // with a "// TITLE" label, live summary and right-aligned buttons over an auto-scrolling row stack.
    public abstract class ListTab : Panel
    {
        protected readonly Panel Toolbar  = new() { BackColor = Theme.SURFACE, Height = 54, Dock = DockStyle.Top };
        protected readonly Panel Scroller = new() { BackColor = Theme.BG, Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 10, 16, 10) };
        protected readonly Panel Inner    = new() { BackColor = Theme.BG, AutoSize = true };
        protected readonly Label Summary;
        private bool _loaded;

        protected ListTab(string title, int summaryX)
        {
            SuspendLayout();   // no layout (and so no Reflow) until the subclass has built its controls
            BackColor = Theme.BG;
            Dock      = DockStyle.Fill;
            Visible   = false;
            UiHelpers.BottomBorder(Toolbar);
            Summary = UiHelpers.Lbl("", Theme.Mono(7.5f), Theme.TEXT_DIM, summaryX, 18);
            Toolbar.Controls.AddRange(new Control[] { UiHelpers.Lbl("// " + title, Theme.Mono(9.5f, true), Theme.TEXT_PRI, 16, 15), Summary });
            Scroller.Controls.Add(Inner);
            Scroller.Resize += (s, e) => { Inner.Width = Scroller.ClientSize.Width - Scroller.Padding.Horizontal; Reflow(); };
            Controls.Add(Scroller);
            Controls.Add(Toolbar);   // docked top, added last
            ResumeLayout(false);
        }

        // First view loads the tab; StartupTab overrides to reload on every visit.
        public virtual void Activate()
        {
            if (_loaded) return;
            _loaded = true;
            OnFirstActivate();
        }

        protected virtual void OnFirstActivate() { }
        protected abstract void Reflow();

        protected static FlatButton Ghost(string text, int w, EventHandler click, Color? fg = null, Font font = null)
        {
            var b = new FlatButton(text, Theme.SURFACE2) { Size = new Size(w, 28), ForeColor = fg ?? Theme.TEXT_SEC };
            if (font != null) b.Font = font;
            b.Click += click;
            return b;
        }

        protected static FlatButton Primary(string text, Color bg, EventHandler click)
        {
            var b = new FlatButton(text, bg) { Size = new Size(150, 28), Font = Theme.Mono(7.5f, true) };
            b.Click += click;
            return b;
        }

        // Adds toolbar buttons and keeps them right-aligned — pass them right-to-left, each with the gap after it.
        protected void AlignRight(params (Control c, int gap)[] buttons)
        {
            foreach (var (c, _) in buttons) Toolbar.Controls.Add(c);
            Toolbar.SizeChanged += (s, e) => UiHelpers.LayoutButtonsRightToLeft(Toolbar, 13, buttons);
        }

        protected void FitInner() => Inner.Width = Math.Max(100, Scroller.ClientSize.Width - Scroller.Padding.Horizontal);

        // Swaps in a fresh set of rows (disposing the old ones) and wires each row's Changed event.
        protected void SetRows<T>(List<T> rows, IEnumerable<T> fresh, Action changed) where T : CardRow
        {
            foreach (var r in rows) r.Dispose();
            rows.Clear();
            foreach (var r in fresh)
            {
                r.Changed += (s, e) => changed();
                rows.Add(r);
                Inner.Controls.Add(r);
            }
            FitInner();
            Reflow();
        }

        // Runs a background job with the given controls disabled. A failure is logged and shown in
        // the summary instead of escaping an async handler and leaving the buttons disabled for good.
        protected async Task<bool> Busy(string status, Func<Task> work, params Control[] locked)
        {
            foreach (var c in locked) c.Enabled = false;
            Summary.Text = status;
            try { await work(); return true; }
            catch (Exception ex)
            {
                SessionLog.Write(GetType().Name.ToUpper(), ex);
                Summary.Text = "✘ " + ex.Message;
                return false;
            }
            finally { foreach (var c in locked) c.Enabled = true; }
        }
    }

    // Bordered list card with a 3px status stripe down its left edge.
    public abstract class CardRow : Panel
    {
        public event EventHandler Changed;

        protected CardRow(int height)
        {
            Height      = height;
            BackColor   = Theme.CARD;
            SizeChanged += (s, e) => LayoutRow();   // subscribed after Height, so it can't fire before the subclass builds
        }

        protected abstract Color Stripe { get; }
        protected abstract void LayoutRow();
        protected void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

        // Right-aligns c against `right`, vertically centred (+dy); returns its left edge.
        protected int Place(int right, Control c, int dy = 0)
        {
            c.Location = new Point(right - c.Width, (Height - c.Height) / 2 + dy);
            return c.Left;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.BORDER);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using var br = new SolidBrush(Stripe);
            e.Graphics.FillRectangle(br, 0, 0, 3, Height);
        }
    }
}
