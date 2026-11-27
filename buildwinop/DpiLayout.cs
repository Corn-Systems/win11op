using System;
using System.Drawing;
using System.Windows.Forms;
using CornSystems;

namespace Win11Optimizer
{
    // Per-monitor DPI support for code-built WinForms UI (ported from CornDownloader's DpiLayout).
    //
    // Every control in this app is built once with Dpi.S() sizes and theme fonts, so the simplest
    // correct response to a DPI change is to build the UI again at the new scale. Attach() wires
    // the triggers and calls `rescale` whenever the form's monitor DPI no longer matches the DPI
    // the controls were laid out for:
    //   • DpiChanged — dragged to another monitor, or the user changed display scaling.
    //   • Shown      — opened on a secondary monitor whose DPI differs from the system DPI
    //                  (DeviceDpi before the handle exists is always the system DPI).
    //   • Activated  — Dpi.Current is process-wide, so re-check when focus comes back, and run a
    //                  rebuild that was postponed because a tweak run was in progress.
    internal static class DpiLayout
    {
        // `rescale` returns false when it can't run right now (e.g. mid-run); the next trigger retries.
        public static Action Attach(Form form, Func<bool> rescale)
        {
            int laidOutFor = (int)Dpi.Current;

            void CheckNow()
            {
                if (form.IsDisposed || !form.IsHandleCreated) return;
                int dpi = form.DeviceDpi;
                if (dpi <= 0 || dpi == laidOutFor) return;
                float before = Dpi.Current;
                Dpi.Update(dpi);
                bool done = false;
                form.SuspendLayout();
                try     { done = rescale(); }
                catch (Exception ex) { sessionLog.write("DPI", ex); }
                finally { form.ResumeLayout(true); }
                if (!done)
                {
                    // Postponed — keep everything (painting, new rows) at the scale the UI is built for
                    Dpi.Update((int)before);
                    return;
                }
                laidOutFor = dpi;
                sessionLog.write($"[DPI] {form.GetType().Name} now at {dpi} dpi ({Dpi.Scale * 100:0}%)");
                form.Invalidate(true);
            }

            form.DpiChanged += (s, e) =>
            {
                // Windows' suggested rectangle keeps the window under the cursor at the new scale.
                // Applying it to a maximized window would knock it out of maximized.
                if (form.WindowState == FormWindowState.Normal && e.SuggestedRectangle != Rectangle.Empty)
                {
                    // The old monitor's MinimumSize would stop a move to a lower-scale monitor from
                    // shrinking the window — the rebuild sets the new one.
                    form.MinimumSize = Size.Empty;
                    form.SetBounds(e.SuggestedRectangle.X, e.SuggestedRectangle.Y,
                                   e.SuggestedRectangle.Width, e.SuggestedRectangle.Height);
                }

                // Defer: WinForms is still rescaling the existing children while this event runs.
                // Running afterwards means our rebuilt values are the ones that stick.
                try { form.BeginInvoke((Action)CheckNow); }
                catch (Exception ex) { sessionLog.write("DPI", ex); }
            };
            form.Shown     += (s, e) => CheckNow();
            form.Activated += (s, e) => CheckNow();
            return CheckNow;   // call it to retry a rebuild that was postponed
        }
    }
}
