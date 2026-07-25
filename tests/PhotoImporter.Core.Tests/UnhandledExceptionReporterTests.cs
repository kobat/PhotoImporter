using PhotoImporter.App;
using System;
using System.IO;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class UnhandledExceptionReporterTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "PhotoImporterUnhandledExceptionTests",
            Guid.NewGuid().ToString("N"));

        [Fact]
        public void FatalExceptionIsLoggedDisplayedAndTerminatesThroughCallback()
        {
            var logPath = Path.Combine(_root, "errors.log");
            string displayed = null;
            var shutdown = false;
            var exception = new InvalidOperationException("simulated fatal failure");

            UnhandledExceptionReporter.HandleFatal(
                exception,
                logPath,
                message => displayed = message,
                () => shutdown = true);

            Assert.True(shutdown);
            Assert.Contains("アプリを終了", displayed);
            Assert.Contains(logPath, displayed);
            Assert.Contains("simulated fatal failure", File.ReadAllText(logPath));
            Assert.Contains("UI thread unhandled exception", File.ReadAllText(logPath));
        }

        [Fact]
        public void FatalExceptionStillTerminatesWhenErrorDialogFails()
        {
            var shutdown = false;

            UnhandledExceptionReporter.HandleFatal(
                new Exception("fatal"),
                Path.Combine(_root, "errors.log"),
                _ => throw new InvalidOperationException("dialog failure"),
                () => shutdown = true);

            Assert.True(shutdown);
        }

        [Fact]
        public void UnobservedTaskExceptionIsRecordedForDiagnostics()
        {
            var logPath = Path.Combine(_root, "errors.log");

            UnhandledExceptionReporter.RecordUnobserved(
                new Exception("background failure"), logPath);

            var log = File.ReadAllText(logPath);
            Assert.Contains("Unobserved task exception", log);
            Assert.Contains("background failure", log);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
