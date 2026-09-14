using System;
using System.IO;

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
        public const string DataFolderName = "Data";

        public static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        public static string DataDir => Path.Combine(BaseDir, DataFolderName);

        public static string ChangeLogFile      => Path.Combine(DataDir, "changelog.json");
        public static string TweaksBackupFile   => Path.Combine(DataDir, "tweaks_backup.json");
        public static string AppliedStateFile   => Path.Combine(DataDir, "applied_tweaks.json");
        public static string ServicesBackupFile => Path.Combine(DataDir, "services_backup.json");
        public static string CrashLog           => Path.Combine(DataDir, "crash.log");
        public static string CliRunLog          => Path.Combine(DataDir, "cli_run.log");
        public static string LastVersionFile    => Path.Combine(DataDir, "last_version.txt");

        // One-time move of any files a pre-1.4.2 build dropped loose next to the exe
        // (changelog.json, tweaks_backup.json, applied_tweaks.json, crash.log) into
        // the new Data\ subfolder, so upgrading in place doesn't reset anyone's
        // tweak history or Undo state.
        public static void MigrateLegacyFiles()
        {
            try
            {
                EnsureDataDir();
                foreach (var name in new[] { "changelog.json", "tweaks_backup.json", "applied_tweaks.json", "crash.log", "cli_run.log", "last_version.txt" })
                {
                    string oldPath = Path.Combine(BaseDir, name);
                    string newPath = Path.Combine(DataDir, name);
                    if (File.Exists(oldPath) && !File.Exists(newPath))
                    {
                        try { File.Move(oldPath, newPath); }
                        catch { /* locked or cross-volume — leave the old copy, not fatal */ }
                    }
                }
            }
            catch { /* best-effort; callers still work if this fails */ }
        }

        public static void EnsureDataDir()
        {
            try { Directory.CreateDirectory(DataDir); }
            catch { /* best effort — callers already wrap their own file I/O in try/catch */ }
        }
    }
}
