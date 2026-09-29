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
    public static class AppPaths
    {
        public static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        public static string DataDir => Path.Combine(BaseDir, "Data");

        public static string ChangeLogFile      => Data("changelog.json");
        public static string TweaksBackupFile   => Data("tweaks_backup.json");
        public static string AppliedStateFile   => Data("applied_tweaks.json");
        public static string ServicesBackupFile => Data("services_backup.json");
        public static string CrashLog           => Data("crash.log");
        public static string CliRunLog          => Data("cli_run.log");
        public static string LastVersionFile    => Data("last_version.txt");

        private static string Data(string name) => Path.Combine(DataDir, name);

        internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        // One-time move of any files a pre-1.4.2 build dropped loose next to the exe
        // into the Data\ subfolder, so upgrading in place doesn't reset anyone's
        // tweak history or Undo state. Also creates Data\.
        public static void MigrateLegacyFiles()
        {
            EnsureDataDir();
            foreach (var name in new[] { ChangeLogFile, TweaksBackupFile, AppliedStateFile, CrashLog, CliRunLog, LastVersionFile }.Select(Path.GetFileName))
            {
                string from = Path.Combine(BaseDir, name), to = Data(name);
                try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); }
                catch (Exception ex) { SessionLog.Write("MIGRATE " + name, ex); }   // locked or cross-volume — leave the old copy
            }
        }

        public static void EnsureDataDir()
        {
            try { Directory.CreateDirectory(DataDir); }
            catch (Exception ex) { SessionLog.Write("DATA DIR", ex); }   // callers wrap their own file I/O too
        }

        // JSON state files under Data\. Load returns null when the file is missing or unreadable.
        public static T LoadJson<T>(string path, string tag) where T : class
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
            catch (Exception ex) { SessionLog.Write(tag + " LOAD", ex); return null; }
        }

        public static void SaveJson<T>(string path, T value, string tag)
        {
            try { EnsureDataDir(); File.WriteAllText(path, JsonSerializer.Serialize(value, Indented)); }
            catch (Exception ex) { SessionLog.Write(tag + " SAVE", ex); }
        }
    }
}
