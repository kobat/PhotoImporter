using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace PhotoImporter.Core.Settings
{
    public sealed class RecentInputHistoryStore
    {
        private const int CurrentVersion = 1;
        private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(10);
        private readonly string _historyPath;
        private readonly TimeSpan _lockTimeout;

        public RecentInputHistoryStore(string historyPath)
            : this(historyPath, DefaultLockTimeout)
        {
        }

        internal RecentInputHistoryStore(string historyPath, TimeSpan lockTimeout)
        {
            if (string.IsNullOrWhiteSpace(historyPath))
                throw new ArgumentException("The history path is required.", nameof(historyPath));
            if (lockTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(lockTimeout));

            _historyPath = Path.GetFullPath(historyPath);
            _lockTimeout = lockTimeout;
        }

        public string HistoryPath => _historyPath;

        public RecentInputHistory Load()
        {
            if (!File.Exists(_historyPath)) return RecentInputHistory.Empty;

            try
            {
                return ReadDocument(_historyPath);
            }
            catch (UnsupportedHistoryVersionException ex)
            {
                throw new InvalidDataException(ex.Message, ex);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is XmlException || ex is ArgumentException)
            {
                throw new InvalidDataException("入力履歴ファイルを読み込めませんでした。", ex);
            }
        }

        public RecentInputHistory Record(
            string sourceFolder,
            string destinationFolder,
            string templateText,
            int limit)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder))
                throw new ArgumentException("The source folder is required.", nameof(sourceFolder));
            if (string.IsNullOrWhiteSpace(destinationFolder))
                throw new ArgumentException("The destination folder is required.", nameof(destinationFolder));
            if (string.IsNullOrEmpty(templateText))
                throw new ArgumentException("The template is required.", nameof(templateText));
            if (limit < 0 || limit > PhotoImporterSettings.MaximumInputHistoryLimit)
                throw new ArgumentOutOfRangeException(nameof(limit));

            var normalizedSource = NormalizeFolderPath(sourceFolder);
            var normalizedDestination = NormalizeFolderPath(destinationFolder);

            Mutex mutex = null;
            var ownsMutex = false;
            try
            {
                mutex = new Mutex(false, CreateMutexName(_historyPath));
                try
                {
                    ownsMutex = mutex.WaitOne(_lockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                    throw new TimeoutException("別の PhotoImporter が入力履歴を使用しています。しばらく待ってから再試行してください。");

                RecentInputHistory history;
                try
                {
                    history = File.Exists(_historyPath)
                        ? ReadDocument(_historyPath)
                        : RecentInputHistory.Empty;
                }
                catch (InvalidDataException)
                {
                    MoveCorruptFile();
                    history = RecentInputHistory.Empty;
                }

                var updated = history.Record(
                    normalizedSource,
                    normalizedDestination,
                    templateText,
                    limit);
                WriteDocumentAtomic(updated);
                return updated;
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
                mutex?.Dispose();
            }
        }

        internal static string CreateMutexName(string historyPath)
        {
            var normalized = Path.GetFullPath(historyPath).ToUpperInvariant();
            byte[] hash;
            using (var sha256 = SHA256.Create())
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            return "PhotoImporter.InputHistory." +
                   string.Concat(hash.Take(12).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static RecentInputHistory ReadDocument(string path)
        {
            XDocument document;
            try
            {
                document = XDocument.Load(path, LoadOptions.None);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)
            {
                throw new InvalidDataException("入力履歴ファイルを読み込めませんでした。", ex);
            }

            var root = document.Root;
            int version;
            if (root == null || root.Name != "PhotoImporterInputHistory" ||
                !int.TryParse((string)root.Attribute("version"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out version))
            {
                throw new InvalidDataException("入力履歴ファイルの形式が正しくありません。");
            }
            if (version != CurrentVersion)
                throw new UnsupportedHistoryVersionException(
                    "入力履歴ファイルのバージョンに対応していません。");

            return new RecentInputHistory(
                ReadItems(root, "SourceFolders", true, StringComparer.OrdinalIgnoreCase),
                ReadItems(root, "DestinationFolders", true, StringComparer.OrdinalIgnoreCase),
                ReadItems(root, "Templates", false, StringComparer.Ordinal));
        }

        private static IEnumerable<string> ReadItems(
            XElement root,
            string containerName,
            bool normalizePath,
            StringComparer comparer)
        {
            var container = root.Element(containerName);
            if (container == null) return Enumerable.Empty<string>();

            var result = new List<string>();
            foreach (var element in container.Elements("Item"))
            {
                var value = element.Value;
                if (string.IsNullOrEmpty(value)) continue;
                if (normalizePath)
                {
                    try
                    {
                        if (!Path.IsPathRooted(value))
                            throw new InvalidDataException("入力履歴に相対フォルダーパスがあります。");
                        value = NormalizeFolderPath(value);
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                               ex is PathTooLongException)
                    {
                        throw new InvalidDataException("入力履歴に不正なフォルダーパスがあります。", ex);
                    }
                }

                if (!result.Contains(value, comparer)) result.Add(value);
            }
            return result;
        }

        private static string NormalizeFolderPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            while (fullPath.Length > root.Length &&
                   (fullPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                    fullPath.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)))
            {
                fullPath = fullPath.Substring(0, fullPath.Length - 1);
            }
            return fullPath;
        }

        private void WriteDocumentAtomic(RecentInputHistory history)
        {
            var directory = Path.GetDirectoryName(_historyPath);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("入力履歴ファイルの保存先を特定できません。");
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(
                directory,
                ".history_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var document = new XDocument(
                    new XElement("PhotoImporterInputHistory",
                        new XAttribute("version", CurrentVersion),
                        WriteItems("SourceFolders", history.SourceFolders),
                        WriteItems("DestinationFolders", history.DestinationFolders),
                        WriteItems("Templates", history.Templates)));
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(true),
                    Indent = true,
                    NewLineChars = "\r\n",
                    NewLineHandling = NewLineHandling.Replace
                };
                using (var writer = XmlWriter.Create(temporaryPath, settings))
                    document.Save(writer);

                if (File.Exists(_historyPath)) File.Replace(temporaryPath, _historyPath, null);
                else File.Move(temporaryPath, _historyPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static XElement WriteItems(string containerName, IEnumerable<string> values) =>
            new XElement(containerName, values.Select(value => new XElement("Item", value)));

        private void MoveCorruptFile()
        {
            if (!File.Exists(_historyPath)) return;
            var directory = Path.GetDirectoryName(_historyPath);
            var baseName = Path.Combine(directory, "history.bad.xml");
            var destination = baseName;
            var suffix = 1;
            while (File.Exists(destination))
            {
                destination = Path.Combine(directory, string.Format(
                    CultureInfo.InvariantCulture,
                    "history.bad.{0}.xml",
                    suffix++));
            }
            File.Move(_historyPath, destination);
        }

        private sealed class UnsupportedHistoryVersionException : NotSupportedException
        {
            public UnsupportedHistoryVersionException(string message) : base(message) { }
        }
    }
}
