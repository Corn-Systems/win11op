using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Win11Optimizer
{
    // Every on-disk location the app writes to, in one place. Win11 Optimizer ships
    // both installed (Program Files, via the .iss installer) and portable (a bare
    // .exe the user runs from wherever — often straight out of Downloads), and it
    // needs to behave the same in both: everything the app writes lives in a "Data"
    // folder next to the executable, not scattered loose beside the .exe and not in
    // %AppData% either. That keeps the portable build actually portable (copy the
    // folder, keep your history) and keeps a Downloads folder from filling up with
    // stray .json/.log files.
    public static class appPaths
    {
        public static string baseDir => AppDomain.CurrentDomain.BaseDirectory;
        public static string dataDir => Path.Combine(baseDir, "Data");

        public static string changeLogFile      => data("changelog.json");
        public static string tweaksBackupFile   => data("tweaks_backup.json");
        public static string appliedStateFile   => data("applied_tweaks.json");
        public static string servicesBackupFile => data("services_backup.json");
        public static string crashLog           => data("crash.log");
        public static string cliRunLog          => data("cli_run.log");
        public static string lastVersionFile    => data("last_version.txt");

        private static string data(string name) => Path.Combine(dataDir, name);

        internal static readonly JsonSerializerOptions indented = new() { WriteIndented = true };

        // One-time move of any files a pre-1.4.2 build dropped loose next to the exe
        // into the Data\ subfolder, so upgrading in place doesn't reset anyone's
        // tweak history or Undo state. Also creates Data\.
        public static void migrateLegacyFiles()
        {
            ensureDataDir();
            foreach (var name in new[] { changeLogFile, tweaksBackupFile, appliedStateFile, crashLog, cliRunLog, lastVersionFile }.Select(Path.GetFileName))
            {
                string from = Path.Combine(baseDir, name), to = data(name);
                try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); }
                catch (Exception ex) { sessionLog.write("MIGRATE " + name, ex); }   // locked or cross-volume — leave the old copy
            }
        }

        public static void ensureDataDir()
        {
            try { Directory.CreateDirectory(dataDir); }
            catch (Exception ex) { sessionLog.write("DATA DIR", ex); }   // callers wrap their own file I/O too
        }

        // JSON state files under Data\. Load returns null when the file is missing or unreadable.
        public static t loadJson<t>(string path, string tag) where t : class
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<t>(File.ReadAllText(path)) : null; }
            catch (Exception ex) { sessionLog.write(tag + " LOAD", ex); return null; }
        }

        public static void saveJson<t>(string path, t value, string tag)
        {
            try { ensureDataDir(); File.WriteAllText(path, JsonSerializer.Serialize(value, indented)); }
            catch (Exception ex) { sessionLog.write(tag + " SAVE", ex); }
        }
    }
}
