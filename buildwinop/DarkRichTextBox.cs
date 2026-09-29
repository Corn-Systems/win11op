using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Win11Optimizer
{
    // RichTextBox with dark-mode scrollbars, for the output log.
    public class DarkRichTextBox : RichTextBox
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string app, string id);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowTheme(Handle, "DarkMode_Explorer", null);
        }
    }
}
