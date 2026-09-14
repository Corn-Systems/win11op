using System.Drawing;

namespace Win11Optimizer
{
    public static class Theme
    {
        public static readonly Color BG          = Color.FromArgb(  8,   8,  18);  // --bg:  #080812
        public static readonly Color SURFACE     = Color.FromArgb( 19,  18,  42);  // --surface: #13122a
        public static readonly Color SURFACE2    = Color.FromArgb( 26,  24,  53);  // --surface2: #1a1835
        public static readonly Color CARD        = Color.FromArgb( 16,  15,  34);  // --card: #100f22

        public static readonly Color ACCENT      = Color.FromArgb(245, 200,  66);  // --accent: #f5c842
        public static readonly Color ACCENT_HOV  = Color.FromArgb(255, 215,  90);  // lighter gold on hover
        public static readonly Color ACCENT_GLOW = Color.FromArgb( 40,  30,   8);  // subtle warm tint when selected

        public static readonly Color METEOR      = Color.FromArgb(244,  81,  30);  // --meteor: #f4511e

        public static readonly Color SKY_DEEP    = Color.FromArgb( 43,  45, 184);  // --sky-deep: #2b2db8
        public static readonly Color SKY_PURPLE  = Color.FromArgb(124,  58, 237);  // --sky-purple: #7c3aed

        public static readonly Color SUCCESS     = Color.FromArgb( 34, 197,  94);  // green
        public static readonly Color WARNING     = Color.FromArgb(251, 191,  36);  // amber
        public static readonly Color DANGER      = Color.FromArgb(239,  68,  68);  // red

        public static readonly Color TEXT_PRI    = Color.FromArgb(240, 238, 252);  // --text: #f0eefc
        public static readonly Color TEXT_SEC    = Color.FromArgb(106, 103, 155);  // between --text-dim and --muted
        public static readonly Color TEXT_DIM    = Color.FromArgb(160, 157, 192);  // --text-dim: #a09dc0

        public static readonly Color BORDER      = Color.FromArgb( 42,  40,  80);  // --border: #2a2850
        public static readonly Color BORDER2     = Color.FromArgb( 61,  58, 112);  // --border2: #3d3a70

        public static readonly Color ACCENT_TEXT = Color.FromArgb(  8,   8,  18);  // dark text on gold buttons

        // ── Scrollbar (Claude-desktop-style thin overlay scrollbar) ───────────
        public static readonly Color SCROLL_TRACK      = Color.FromArgb(  0,   0,   0,   0);  // fully transparent track
        public static readonly Color SCROLL_THUMB      = Color.FromArgb( 90,  70,  61, 112);  // dim BORDER2, low alpha
        public static readonly Color SCROLL_THUMB_HOVER = Color.FromArgb(160, 245, 200,  66);  // ACCENT, translucent
        public static readonly Color SCROLL_THUMB_DRAG  = Color.FromArgb(220, 245, 200,  66);  // ACCENT, near-opaque
    }
}
