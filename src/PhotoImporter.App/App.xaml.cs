using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PhotoImporter.App
{
    public partial class App : Application
    {
        private readonly string _errorLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoImporter",
            "errors.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DispatcherUnhandledException -= App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
            base.OnExit(e);
        }

        private void App_DispatcherUnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            UnhandledExceptionReporter.HandleFatal(
                e.Exception,
                _errorLogPath,
                message => MessageBox.Show(
                    message,
                    "Photo Importer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error),
                () => Shutdown(-1));
        }

        private void TaskScheduler_UnobservedTaskException(
            object sender,
            UnobservedTaskExceptionEventArgs e)
        {
            UnhandledExceptionReporter.RecordUnobserved(e.Exception, _errorLogPath);
            e.SetObserved();
        }
    }
}
