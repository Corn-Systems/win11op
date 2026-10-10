using System.Collections.Concurrent;
using System.Drawing;

namespace Win11Optimizer
{
    public static class theme
    {
        public static readonly Color bg          = Color.FromArgb(  8,   8,  18);  // --bg:  #080812
        public static readonly Color surface     = Color.FromArgb( 19,  18,  42);  // --surface: #13122a
        public static readonly Color surface2    = Color.FromArgb( 26,  24,  53);  // --surface2: #1a1835
        public static readonly Color card        = Color.FromArgb( 16,  15,  34);  // --card: #100f22

        public static readonly Color accent      = Color.FromArgb(245, 200,  66);  // --accent: #f5c842
        public static readonly Color accentHov  = Color.FromArgb(255, 215,  90);  // lighter gold on hover
        public static readonly Color accentGlow = Color.FromArgb( 40,  30,   8);  // subtle warm tint when selected

        public static readonly Color meteor      = Color.FromArgb(244,  81,  30);  // --meteor: #f4511e

        public static readonly Color skyDeep    = Color.FromArgb( 43,  45, 184);  // --sky-deep: #2b2db8
        public static readonly Color skyPurple  = Color.FromArgb(124,  58, 237);  // --sky-purple: #7c3aed

        public static readonly Color success     = Color.FromArgb( 34, 197,  94);  // green
        public static readonly Color warning     = Color.FromArgb(251, 191,  36);  // amber
        public static readonly Color danger      = Color.FromArgb(239,  68,  68);  // red

        public static readonly Color textPri    = Color.FromArgb(240, 238, 252);  // --text: #f0eefc
        public static readonly Color textSec    = Color.FromArgb(106, 103, 155);  // between --text-dim and --muted
        public static readonly Color textDim    = Color.FromArgb(160, 157, 192);  // --text-dim: #a09dc0

        public static readonly Color border      = Color.FromArgb( 42,  40,  80);  // --border: #2a2850
        public static readonly Color border2     = Color.FromArgb( 61,  58, 112);  // --border2: #3d3a70

        public static readonly Color accentText = Color.FromArgb(  8,   8,  18);  // dark text on gold buttons

        // Shared font cache. Controls never dispose their Font, so every label/tile/row that did
        // `new Font(...)` leaked a GDI+ font per rebuild — one instance per (family, size, style)
        // instead. Never wrap one of these in `using`.
        private static readonly ConcurrentDictionary<(string, float, FontStyle), Font> _fonts = new();
        public static Font f(string family, float size, FontStyle style = FontStyle.Regular) =>
            _fonts.GetOrAdd((family, size, style), k => new Font(k.Item1, k.Item2, k.Item3));
        public static Font mono(float size, bool bold = false) => f("Courier New", size, bold ? FontStyle.Bold : FontStyle.Regular);
        public static Font ui(float size, bool bold = false)   => f("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
    }
}
