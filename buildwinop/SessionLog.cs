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
    public static class sessionLog
    {
        private static readonly object _lock = new object();
        private const int retentionDays = 14;
        private static bool _pruned;

        public static string logsDir => Path.Combine(appPaths.dataDir, "logs");
        public static string currentFile => Path.Combine(logsDir, $"{DateTime.Now:yyyy-MM-dd}.log");

        public static void write(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(logsDir);
                    if (!_pruned) { prune(); _pruned = true; }
                    File.AppendAllText(currentFile,
                        $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch { /* logging must never take the app down */ }
        }

        public static void write(string tag, Exception ex)
        {
            if (ex == null) return;
            write($"[{tag}] {ex.GetType().Name}: {ex.Message}");
        }

        private static void prune()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (var f in Directory.GetFiles(logsDir, "*.log")
                             .Where(f => File.GetLastWriteTime(f) < cutoff))
                    File.Delete(f);
            }
            catch { /* prune is opportunistic */ }
        }
    }
}
