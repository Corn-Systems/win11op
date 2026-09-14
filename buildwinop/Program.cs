using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper — byte-identical across Corn Systems repos

namespace Win11Optimizer
{
    static class Program
    {
        // Prevents two copies racing on the same Data\*.json files (registry/service
        // tweaks and their Undo state aren't safe to write from two processes at once).
        private const string SingleInstanceMutexName = @"Global\CornSystems.Win11Optimizer.SingleInstance";
        private static Mutex _singleInstanceMutex;

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException +=
                    (s, e) => LogCrash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException +=
                    (s, e) => LogCrash(e.ExceptionObject as Exception);

                AppPaths.EnsureDataDir();
                AppPaths.MigrateLegacyFiles();

                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isNewInstance);
                if (!isNewInstance)
                {
                    // args.Length > 0 almost always means a scripted/CLI invocation —
                    // give it a console message and a distinct exit code instead of a
                    // MessageBox nothing will be there to dismiss.
                    if (args.Length > 0)
                    {
                        Console.Error.WriteLine("Win11 Optimizer is already running — close it before running from the command line.");
                        Environment.Exit(1);
                    }
                    else
                    {
                        MessageBox.Show("Win11 Optimizer is already running.",
                            "Win11 Optimizer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                WinVersion.Detect();
                ChangeLog.Load();
                AppliedState.Load();
                // Previously never called — tweaks_backup.json was written on
                // every apply but never read back, so per-category Undo silently
                // stopped working after an app restart.
                TweakEngine.LoadBackups();

                // Headless mode: --apply / --list-tweaks / --help run without the GUI
                if (CliRunner.TryRun(args, out int cliExit))
                    Environment.Exit(cliExit);

                if (!IsAdmin())
                {
                    var choice = MessageBox.Show(
                        "Win11 Optimizer needs Administrator privileges to apply " +
                        "registry and service tweaks.\n\n" +
                        "Would you like to relaunch as Administrator now?",
                        "Administrator Required",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button1);

                    if (choice == DialogResult.Yes)
                    {
                        try { Process.Start(new ProcessStartInfo
                            { FileName = Application.ExecutablePath,
                              UseShellExecute = true, Verb = "runas" }); }
                        catch (Exception ex) { SessionLog.Write("RELAUNCH AS ADMIN", ex); }
                        return;
                    }
                    AdminWarning.Show = true;
                }

                Application.Run(new MainForm());
            }
            catch (Exception ex) { LogCrash(ex); }
            finally
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
        }

        static bool IsAdmin()
        {
            try { using var id = WindowsIdentity.GetCurrent();
                  return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        static void LogCrash(Exception ex)
        {
            try
            {
                AppPaths.EnsureDataDir();
                string lp = AppPaths.CrashLog;
                File.AppendAllText(lp, $"[{DateTime.Now}]\n{ex}\n\n");
                MessageBox.Show($"Crash logged to:\n{lp}\n\n{ex?.Message}",
                    "Win11Optimizer — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* nothing left to log to — last-ditch handler, matches the crash-handler exemption */ }
        }
    }

}
