using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace Win11Optimizer
{
    static class Program
    {
        // Prevents two copies racing on the same Data\*.json files (registry/service
        // tweaks and their Undo state aren't safe to write from two processes at once).
        private const string MutexName = @"Global\CornSystems.Win11Optimizer.SingleInstance";

        [STAThread]
        static void Main(string[] args)
        {
            Mutex mutex = null;
            bool owned = false;
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => LogCrash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash(e.ExceptionObject as Exception);

                AppPaths.MigrateLegacyFiles();   // also creates Data\

                mutex = new Mutex(true, MutexName, out owned);
                if (!owned)
                {
                    // args.Length > 0 almost always means a scripted/CLI invocation — give it a console
                    // message and a distinct exit code instead of a MessageBox nothing will dismiss.
                    if (args.Length > 0)
                    {
                        Console.Error.WriteLine("Win11 Optimizer is already running — close it before running from the command line.");
                        Environment.ExitCode = 1;
                    }
                    else MessageBox.Show("Win11 Optimizer is already running.", "Win11 Optimizer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                WinVersion.Detect();
                ChangeLog.Load();
                AppliedState.Load();
                TweakEngine.LoadBackups();   // so per-category Undo survives an app restart

                // Headless mode: --apply / --list-tweaks / --help run without the GUI
                if (CliRunner.TryRun(args, out int cliExit))
                {
                    Environment.ExitCode = cliExit;
                    return;
                }

                bool admin = IsAdmin();
                if (!admin && MessageBox.Show(
                        "Win11 Optimizer needs Administrator privileges to apply registry and service tweaks.\n\n" +
                        "Would you like to relaunch as Administrator now?",
                        "Administrator Required", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                {
                    // Free the single-instance lock first so the elevated copy can't lose the race for it
                    mutex.ReleaseMutex();
                    owned = false;
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = Application.ExecutablePath, UseShellExecute = true, Verb = "runas" })?.Dispose();
                        return;
                    }
                    catch (Exception ex) { SessionLog.Write("RELAUNCH AS ADMIN", ex); }   // UAC declined — carry on unelevated
                    if (!(owned = mutex.WaitOne(0))) return;
                }

                Application.Run(new MainForm(showAdminWarning: !admin));
            }
            catch (Exception ex) { LogCrash(ex); }
            finally
            {
                // Only the owner may release — a second instance releasing here used to throw on exit
                if (owned) mutex.ReleaseMutex();
                mutex?.Dispose();
            }
        }

        internal static bool IsAdmin()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) { SessionLog.Write("ADMIN CHECK", ex); return false; }
        }

        static void LogCrash(Exception ex)
        {
            try
            {
                AppPaths.EnsureDataDir();
                File.AppendAllText(AppPaths.CrashLog, $"[{DateTime.Now}]\n{ex}\n\n");
                MessageBox.Show($"Crash logged to:\n{AppPaths.CrashLog}\n\n{ex?.Message}",
                    "Win11Optimizer — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* nothing left to log to — last-ditch crash handler */ }
        }
    }
}
