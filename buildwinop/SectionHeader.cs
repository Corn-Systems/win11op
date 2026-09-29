using System;
using System.Drawing;
using System.Windows.Forms;

namespace Win11Optimizer
{
    public class SectionHeader : Panel
    {
        private static int _index;
        private Control _host;
        public static void ResetIndex() => _index = 0;

        public SectionHeader(string title, string emoji)
        {
            Height    = 52;
            Margin    = new Padding(5, 20, 5, 4);
            BackColor = Color.Transparent;
            Controls.AddRange(new Control[]
            {
                // "01 — CATEGORY" monospace label, website style
                UiHelpers.Lbl($"{++_index:D2} — {title.ToUpper()}", Theme.Mono(7f, true), Theme.ACCENT, 6, 8),
                UiHelpers.Lbl($"{emoji}  {title}", Theme.Mono(9f, true), Theme.TEXT_PRI, 4, 24),
            });
            UiHelpers.BottomBorder(this);
        }

        // Tracks exactly one parent. This used to add a SizeChanged handler on every re-parent and
        // never remove it, so each grid rebuild leaked every header into the grid's event list.
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (_host != null) _host.SizeChanged -= FitToParent;
            if ((_host = Parent) != null) _host.SizeChanged += FitToParent;
            FitToParent(this, e);
        }

        private void FitToParent(object s, EventArgs e)
        {
            if (Parent == null) return;
            int w = Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal
                  - SystemInformation.VerticalScrollBarWidth - 2;
            if (w > 0) Width = w;
            Invalidate();
        }
    }
}
