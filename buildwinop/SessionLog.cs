using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Win11Optimizer
{
    // Persistent per-day log at <exe folder>\Data\logs\yyyy-MM-dd.log, 14-day
    // retention. Every previously-swallowed exception in the codebase reports
    // here now instead of vanishing into an empty catch or a Debug.WriteLine
    // that gets compiled out entirely in a Release build (DebugType=none in
    // this csproj means [Conditional("DEBUG")] Debug.WriteLine calls are dead
    // code in the shipped .exe — they were never actually logging anything).
    public static class SessionLog
    {
        private static readonly object _lock = new object();
        private const int RetentionDays = 14;
        private static bool _pruned;

        public static string LogsDir => Path.Combine(AppPaths.DataDir, "logs");
        public static string CurrentFile => Path.Combine(LogsDir, $"{DateTime.Now:yyyy-MM-dd}.log");

        public static void Write(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(LogsDir);
                    if (!_pruned) { Prune(); _pruned = true; }
                    File.AppendAllText(CurrentFile,
                        $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch { /* logging must never take the app down */ }
        }

        public static void Write(string tag, Exception ex)
        {
            if (ex == null) return;
            Write($"[{tag}] {ex.GetType().Name}: {ex.Message}");
        }

        private static void Prune()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (var f in Directory.GetFiles(LogsDir, "*.log")
                             .Where(f => File.GetLastWriteTime(f) < cutoff))
                    File.Delete(f);
            }
            catch { /* prune is opportunistic */ }
        }
    }
}
