using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using moddingSuite.BL;
using moddingSuite.View;
using moddingSuite.View.DialogProvider;
using moddingSuite.View.Edata;
using moddingSuite.ViewModel.Edata;
using moddingSuite.ViewModel.UnhandledException;

namespace moddingSuite
{
    public partial class App : Application
    {
        private static string _logFilePath;

        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            try
            {
                var path = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
                if (!string.IsNullOrEmpty(path))
                {
                    Directory.CreateDirectory(Path.Combine(path, "logs"));
                    _logFilePath = Path.Combine(path, "logs",
                        $"logging_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.dat");

                    Trace.Listeners.Add(new TextWriterTraceListener(_logFilePath));
                    Trace.AutoFlush = true;
                    Trace.TraceInformation("=== Modding Suite startup ===");
                    Trace.TraceInformation(DiagnosticReport.Build());
                }
            }
            catch (Exception ex)
            {
                EmergencyWrite("app_init_error.txt", ex.ToString());
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                Trace.TraceInformation("OnStartup: begin");

                var settings = SettingsManager.Load();
                Trace.TraceInformation("Settings loaded. InitialSettings={0}", settings.InitialSettings);

                var startApplication = false;

                EdataManagerView mgr;
                try
                {
                    mgr = new EdataManagerView();
                }
                catch (Exception ex)
                {
                    ReportStartupFailure("Failed to construct main window (EdataManagerView).", ex);
                    Shutdown(1);
                    return;
                }

                if (settings.InitialSettings)
                {
                    var settingsView = new SettingsView { DataContext = settings };
                    var result = settingsView.ShowDialog();
                    if (result.GetValueOrDefault(false))
                    {
                        if (Directory.Exists(settings.SavePath) && Directory.Exists(settings.WargamePath))
                            settings.InitialSettings = false;

                        SettingsManager.Save(settings);
                        startApplication = true;
                    }
                    else
                    {
                        Trace.TraceInformation("Initial settings dialog cancelled, exiting.");
                    }
                }
                else
                {
                    startApplication = true;
                }

                if (!startApplication) return;

                EdataManagerViewModel mainVm;
                try
                {
                    Trace.TraceInformation("Creating EdataManagerViewModel...");
                    mainVm = new EdataManagerViewModel();
                    Trace.TraceInformation("EdataManagerViewModel created.");
                }
                catch (Exception ex)
                {
                    ReportStartupFailure(
                        "Failed to initialize the main view model.\n" +
                        "This usually means the WARNO install path is wrong, " +
                        "or required tools (atlas_cli, gfx_cli, quickbms) are missing.",
                        ex);
                    Shutdown(1);
                    return;
                }

                mgr.DataContext = mainVm;
                mgr.Show();

                // Safety: if WindowStartupLocation=CenterScreen lands the
                // window off-screen (happens on multi-monitor / scale changes),
                // pull it back onto the primary work area.
                var primary = SystemParameters.WorkArea;
                if (mgr.Left + mgr.Width < primary.Left + 40 ||
                    mgr.Left > primary.Right - 40 ||
                    mgr.Top  + mgr.Height < primary.Top + 40 ||
                    mgr.Top  > primary.Bottom - 40)
                {
                    Trace.TraceInformation("Main window was off-screen ({0},{1}), recentering.", mgr.Left, mgr.Top);
                    mgr.Left = primary.Left + Math.Max(0, (primary.Width  - mgr.Width)  / 2);
                    mgr.Top  = primary.Top  + Math.Max(0, (primary.Height - mgr.Height) / 2);
                }

                Trace.TraceInformation("Main window shown.");
            }
            catch (Exception ex)
            {
                ReportStartupFailure("Unexpected error during startup.", ex);
                Shutdown(1);
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            LogException("DispatcherUnhandledException", e.Exception);

            try
            {
                var vm = new UnhandledExceptionViewModel(e.Exception);
                DialogProvider.ProvideView(vm);
            }
            catch (Exception dispatcherEx)
            {
                Trace.TraceError("FATAL: handler crashed: {0}", dispatcherEx);
                EmergencyMessageBox("Modding Suite — handler crashed", dispatcherEx.ToString());
            }
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogException("AppDomain.UnhandledException", ex);

            if (e.IsTerminating)
            {
                ReportStartupFailure(
                    "A fatal background error has occurred. Modding Suite will exit.\n" +
                    "A diagnostic dump has been written next to the .exe — please send it to the developer.",
                    ex);
            }
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LogException("UnobservedTaskException", e.Exception);
            e.SetObserved();
        }

        private static void LogException(string source, Exception ex)
        {
            var current = ex;
            int depth = 0;
            while (current != null && depth < 10)
            {
                Trace.TraceError("[{0}] depth {1}: {2}", source, depth, current);
                current = current.InnerException;
                depth++;
            }
        }

        private static void ReportStartupFailure(string userMessage, Exception ex)
        {
            LogException("StartupFailure", ex);

            var dumpPath = WriteDiagnosticDump(ex);

            var msg = new StringBuilder();
            msg.AppendLine(userMessage);
            msg.AppendLine();
            if (ex != null)
            {
                msg.AppendLine("Reason: " + ex.GetType().Name + ": " + ex.Message);
                msg.AppendLine();
            }
            msg.AppendLine("Diagnostic dump written to:");
            msg.AppendLine(dumpPath ?? "(failed to write dump)");
            if (!string.IsNullOrEmpty(_logFilePath))
            {
                msg.AppendLine();
                msg.AppendLine("Full log: " + _logFilePath);
            }
            msg.AppendLine();
            msg.AppendLine("Send these files to the developer when reporting the issue.");

            EmergencyMessageBox("Modding Suite — failed to start", msg.ToString());
        }

        private static string WriteDiagnosticDump(Exception ex)
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? Environment.CurrentDirectory;
                var path = Path.Combine(dir, $"diagnostic_dump_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

                var sb = new StringBuilder();
                sb.AppendLine(DiagnosticReport.Build());
                sb.AppendLine();
                sb.AppendLine("=== Exception ===");
                sb.AppendLine(ex?.ToString() ?? "(no exception object)");

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                return path;
            }
            catch (Exception writeEx)
            {
                EmergencyWrite("diagnostic_dump_error.txt", writeEx.ToString());
                return null;
            }
        }

        private static void EmergencyWrite(string fileName, string content)
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? Environment.CurrentDirectory;
                File.WriteAllText(Path.Combine(dir, fileName), content);
            }
            catch { /* nothing we can do */ }
        }

        private static void EmergencyMessageBox(string title, string text)
        {
            try
            {
                MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                NativeMessageBox(text, title);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private static void NativeMessageBox(string text, string caption)
        {
            try { MessageBoxW(IntPtr.Zero, text, caption, 0x10); } catch { }
        }
    }
}
