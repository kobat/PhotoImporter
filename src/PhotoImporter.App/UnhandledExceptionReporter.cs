using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PhotoImporter.App
{
    internal static class UnhandledExceptionReporter
    {
        internal static void HandleFatal(
            Exception exception,
            string logPath,
            Action<string> showError,
            Action shutdown)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            if (showError == null) throw new ArgumentNullException(nameof(showError));
            if (shutdown == null) throw new ArgumentNullException(nameof(shutdown));

            TryWrite(logPath, "UI thread unhandled exception", exception);
            try
            {
                showError(
                    "回復できないエラーが発生したため、アプリを終了します。\n" +
                    "コピー先に残った一時ファイルは自動削除しません。\n\n" +
                    "診断ログ: " + logPath + "\n\n" + exception.Message);
            }
            catch
            {
                // Do not let a failing error dialog escape the last-resort handler.
            }
            finally
            {
                shutdown();
            }
        }

        internal static void RecordUnobserved(Exception exception, string logPath)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            TryWrite(logPath, "Unobserved task exception", exception);
        }

        private static void TryWrite(string logPath, string context, Exception exception)
        {
            try
            {
                var directory = Path.GetDirectoryName(logPath);
                if (string.IsNullOrEmpty(directory)) return;
                Directory.CreateDirectory(directory);
                var entry = new StringBuilder()
                    .AppendLine("------------------------------------------------------------")
                    .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                    .Append(" / ")
                    .AppendLine(context)
                    .AppendLine(exception.ToString())
                    .ToString();
                File.AppendAllText(logPath, entry, new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException ||
                                       ex is System.Security.SecurityException)
            {
                // The last-resort handler must still show the error and terminate safely.
            }
        }
    }
}
