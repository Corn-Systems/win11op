using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CornSystems;

namespace Win11Optimizer
{
    // Thin auto-hiding overlay scrollbar in the Claude-desktop style, recolored to the
    // Corn Systems palette. WinForms can't restyle the native scrollbar directly, so
    // this hides it (native AutoScroll keeps doing the real work — wheel, keyboard,
    // drag-to-scroll all still function) and draws a themed pill thumb on top that
    // reads/writes the target's VerticalScroll state.
    //
    // Usage: call CornScrollBar.Attach(panel) once, right after `panel` has been added
    // to its parent's Controls collection (Attach needs panel.Parent to already be set).
    public class CornScrollBar : Control
    {
        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, [MarshalAs(UnmanagedType.Bool)] bool bShow);
        private const int SB_VERT = 1;

        private readonly ScrollableControl _target;
        private readonly Timer _hideTimer;
        private readonly Timer _fadeTimer;
        private readonly Timer _nativeGuardTimer;

        private bool  _hovering;
        private bool  _dragging;
        private int   _dragMouseOffsetInThumb;
        private float _alpha;               // 0..1 current visibility of the thumb
        private const float FadeStep = 0.12f;

        public static CornScrollBar Attach(ScrollableControl target)
        {
            if (target?.Parent == null)
                throw new InvalidOperationException("CornScrollBar.Attach requires target.Parent to be set — attach after adding the panel to its parent.");

            var bar = new CornScrollBar(target);
            target.Parent.Controls.Add(bar);
            bar.BringToFront();
            return bar;
        }

        private CornScrollBar(ScrollableControl target)
        {
            _target = target;

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            Width     = Dpi.S(10);
            BackColor = _target.BackColor;
            Cursor    = Cursors.Default;

            _target.Resize        += (s, e) => SyncBounds();
            _target.SizeChanged   += (s, e) => { SyncBounds(); Invalidate(); };
            _target.ControlAdded  += (s, e) => Invalidate();
            _target.Scroll        += (s, e) => { Reveal(); Invalidate(); };
            _target.MouseWheel    += (s, e) => { Reveal(); Invalidate(); };
            _target.HandleCreated += (s, e) => HideNativeBar();
            if (_target.Parent != null) _target.Parent.Resize += (s, e) => SyncBounds();

            MouseEnter += (s, e) => { _hovering = true;  Reveal(); };
            MouseLeave += (s, e) => { _hovering = false; ScheduleHide(); };
            MouseDown  += OnMouseDown;
            MouseMove  += OnMouseMove;
            MouseUp    += OnMouseUp;

            // Debounced "stop showing the thumb" after interaction settles.
            _hideTimer = new Timer { Interval = 900 };
            _hideTimer.Tick += (s, e) => { _hideTimer.Stop(); if (!_hovering && !_dragging) StartFade(); };

            // Smooth-ish fade rather than a hard pop out of existence.
            _fadeTimer = new Timer { Interval = 16 };
            _fadeTimer.Tick += (s, e) =>
            {
                _alpha = Math.Max(0f, _alpha - FadeStep);
                Invalidate();
                if (_alpha <= 0f) _fadeTimer.Stop();
            };

            // WinForms re-asserts the native WS_VSCROLL style on its own layout passes
            // (resize, content changes), so a single "hide it once" call doesn't stick —
            // keep stripping it on a light timer rather than chasing every internal hook.
            _nativeGuardTimer = new Timer { Interval = 250 };
            _nativeGuardTimer.Tick += (s, e) => HideNativeBar();
            _nativeGuardTimer.Start();

            Disposed += (s, e) =>
            {
                _hideTimer.Dispose();
                _fadeTimer.Dispose();
                _nativeGuardTimer.Dispose();
            };

            SyncBounds();
            HideNativeBar();
        }

        private void HideNativeBar()
        {
            try { if (_target.IsHandleCreated) ShowScrollBar(_target.Handle, SB_VERT, false); }
            catch (Exception ex) { SessionLog.Write("SCROLLBAR", ex); }
        }

        private void SyncBounds()
        {
            try
            {
                if (_target.Parent == null) return;
                Bounds = new Rectangle(_target.Right - Width, _target.Top, Width, _target.Height);
            }
            catch (Exception ex) { SessionLog.Write("SCROLLBAR", ex); }
        }

        private void Reveal()
        {
            _alpha = 1f;
            _fadeTimer.Stop();
            _hideTimer.Stop();
            _hideTimer.Start();
            Invalidate();
        }

        private void ScheduleHide()
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void StartFade()
        {
            if (!_fadeTimer.Enabled) _fadeTimer.Start();
        }

        // ── Thumb geometry ──────────────────────────────────────────────────
        private bool GetThumb(out int thumbY, out int thumbH)
        {
            thumbY = 0; thumbH = 0;
            var vs = _target.VerticalScroll;
            int min   = vs.Minimum;
            int max   = vs.Maximum;
            int large = Math.Max(vs.LargeChange, 1);
            int total = Math.Max(max - min + 1, 1);

            if (total <= large) return false;   // nothing to scroll — no thumb

            float frac = Math.Min(1f, (float)large / total);
            thumbH = Math.Max((int)(Height * frac), Dpi.S(24));
            int trackAvail = Math.Max(Height - thumbH, 1);

            int range = Math.Max(max - min - large + 1, 1);
            float pos = Math.Max(0, Math.Min(1f, (float)(vs.Value - min) / range));
            thumbY = (int)(trackAvail * pos);
            return true;
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (!GetThumb(out int thumbY, out int thumbH)) return;

            if (e.Y >= thumbY && e.Y <= thumbY + thumbH)
            {
                _dragging = true;
                _dragMouseOffsetInThumb = e.Y - thumbY;
            }
            else
            {
                // Click in the track above/below the thumb — jump one page.
                var vs = _target.VerticalScroll;
                int large = Math.Max(vs.LargeChange, 1);
                int delta = e.Y < thumbY ? -large : large;
                SetScrollValue(vs.Value + delta);
            }
            Reveal();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            if (!GetThumb(out _, out int thumbH)) return;

            int trackAvail = Math.Max(Height - thumbH, 1);
            var vs = _target.VerticalScroll;
            int range = Math.Max(vs.Maximum - vs.Minimum - Math.Max(vs.LargeChange, 1) + 1, 1);

            float pos = (float)(e.Y - _dragMouseOffsetInThumb) / trackAvail;
            pos = Math.Max(0f, Math.Min(1f, pos));
            SetScrollValue(vs.Minimum + (int)(pos * range));
            Reveal();
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            _dragging = false;
            ScheduleHide();
        }

        private void SetScrollValue(int value)
        {
            try
            {
                var vs = _target.VerticalScroll;
                int large = Math.Max(vs.LargeChange, 1);
                int clamped = Math.Max(vs.Minimum, Math.Min(value, Math.Max(vs.Maximum - large + 1, vs.Minimum)));
                _target.AutoScrollPosition = new Point(0, clamped);
                Invalidate();
            }
            catch (Exception ex) { SessionLog.Write("SCROLLBAR", ex); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            if (_alpha <= 0f) return;
            if (!GetThumb(out int thumbY, out int thumbH)) return;

            Color baseColor = _dragging ? Theme.SCROLL_THUMB_DRAG
                             : _hovering ? Theme.SCROLL_THUMB_HOVER
                             : Theme.SCROLL_THUMB;
            Color drawColor = Color.FromArgb((int)(baseColor.A * _alpha), baseColor.R, baseColor.G, baseColor.B);

            int pad = Dpi.S(2);
            int thumbW = Width - pad * 2;
            using var brush = new SolidBrush(drawColor);
            e.Graphics.FillRoundedRect(brush, pad, thumbY, thumbW, thumbH, thumbW / 2);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideNativeBar();
            SyncBounds();
        }
    }
}
