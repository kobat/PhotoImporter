using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;

namespace PhotoImporter.Core.Metadata
{
    public sealed class ExifCacheCardInfo
    {
        internal ExifCacheCardInfo(
            string cacheRoot,
            uint volumeSerialNumber,
            ExifCacheMetadataSnapshot metadata,
            long cacheSizeBytes,
            bool isCurrentSource,
            string warning)
        {
            CacheRoot = cacheRoot;
            VolumeSerialNumber = volumeSerialNumber;
            DisplayName = metadata?.DisplayName;
            VolumeLabel = metadata?.VolumeLabel;
            FileSystemName = metadata?.FileSystemName;
            DriveType = metadata?.DriveType;
            TotalBytes = metadata?.TotalBytes;
            FirstUsedUtcDate = metadata?.FirstUsedUtcDate;
            LastUsedUtcDate = metadata?.LastUsedUtcDate;
            EntryCount = metadata?.EntryCount ?? 0;
            CacheSizeBytes = cacheSizeBytes;
            IsCurrentSource = isCurrentSource;
            Warning = warning;
        }

        public string CacheRoot { get; }
        public uint VolumeSerialNumber { get; }
        public string VolumeSerialNumberHex => VolumeSerialNumber.ToString("X8", CultureInfo.InvariantCulture);
        public string DisplayName { get; }
        public string VolumeLabel { get; }
        public string FileSystemName { get; }
        public DriveType? DriveType { get; }
        public ulong? TotalBytes { get; }
        public DateTime? FirstUsedUtcDate { get; }
        public DateTime? LastUsedUtcDate { get; }
        public int EntryCount { get; }
        public long CacheSizeBytes { get; }
        public bool IsCurrentSource { get; }
        public string Warning { get; }
    }

    public sealed class ExifCacheRootInfo
    {
        internal ExifCacheRootInfo(
            string rootPath,
            bool isCurrent,
            bool exists,
            long cacheSizeBytes,
            IReadOnlyList<ExifCacheCardInfo> cards,
            string warning)
        {
            RootPath = rootPath;
            IsCurrent = isCurrent;
            Exists = exists;
            CacheSizeBytes = cacheSizeBytes;
            Cards = cards;
            Warning = warning;
        }

        public string RootPath { get; }
        public bool IsCurrent { get; }
        public bool Exists { get; }
        public long CacheSizeBytes { get; }
        public IReadOnlyList<ExifCacheCardInfo> Cards { get; }
        public string Warning { get; }
    }

    public sealed class ExifCacheManager
    {
        public const int MaximumDisplayNameLength = 100;
        private readonly TimeSpan _lockTimeout;

        public ExifCacheManager(TimeSpan? lockTimeout = null)
        {
            var timeout = lockTimeout ?? TimeSpan.FromSeconds(2);
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(lockTimeout));
            _lockTimeout = timeout;
        }

        public IReadOnlyList<ExifCacheRootInfo> Inspect(
            string currentRoot,
            IEnumerable<string> previousRoots,
            uint? currentSourceVolumeSerialNumber = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(currentRoot))
                throw new ArgumentException("The current cache root is required.", nameof(currentRoot));

            var normalizedCurrent = Path.GetFullPath(currentRoot);
            var roots = new List<string> { normalizedCurrent };
            if (previousRoots != null)
            {
                foreach (var path in previousRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
                {
                    var fullPath = Path.GetFullPath(path);
                    if (!roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) roots.Add(fullPath);
                }
            }

            return roots.Select((root, index) => InspectRoot(
                    root,
                    index == 0,
                    currentSourceVolumeSerialNumber,
                    cancellationToken))
                .ToList();
        }

        public void RenameCard(
            string cacheRoot,
            uint volumeSerialNumber,
            string displayName,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var normalized = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            if (normalized != null && normalized.Length > MaximumDisplayNameLength)
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, "名前は{0}文字以内で入力してください。", MaximumDisplayNameLength),
                    nameof(displayName));

            using (var session = OpenExisting(cacheRoot, volumeSerialNumber, cancellationToken))
                session.SetDisplayName(normalized);
        }

        public int RemoveEntriesLastUsedBefore(
            string cacheRoot,
            uint volumeSerialNumber,
            DateTime cutoffUtcDate,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (cutoffUtcDate.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The cutoff date must be UTC.", nameof(cutoffUtcDate));
            using (var session = OpenExisting(cacheRoot, volumeSerialNumber, cancellationToken))
                return session.RemoveEntriesLastUsedBefore(cutoffUtcDate.Date);
        }

        public void DeleteCard(
            string cacheRoot,
            uint volumeSerialNumber,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var session = OpenExisting(cacheRoot, volumeSerialNumber, cancellationToken))
                session.DeleteVolumeFolder();
        }

        private ExifCacheRootInfo InspectRoot(
            string root,
            bool isCurrent,
            uint? currentSourceVolumeSerialNumber,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
                return new ExifCacheRootInfo(root, isCurrent, false, 0, new ExifCacheCardInfo[0], null);

            List<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new ExifCacheRootInfo(root, isCurrent, true, 0, new ExifCacheCardInfo[0], ex.Message);
            }

            var cards = new List<ExifCacheCardInfo>();
            foreach (var folder in folders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint serial;
                if (!TryParseVolumeFolder(folder, out serial)) continue;

                ExifCacheMetadataSnapshot metadata = null;
                string warning = null;
                try
                {
                    using (var session = OpenExisting(root, serial, cancellationToken))
                    {
                        metadata = session.GetMetadataSnapshot();
                        if (session.RecoveredFromInvalidFile)
                            warning = "破損または互換性のないエントリを破棄して再生成しました。";
                    }
                }
                catch (Exception ex) when (IsManagementFailure(ex))
                {
                    warning = ex.Message;
                }

                long size = 0;
                try { size = CalculateDirectorySize(folder); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warning = AppendWarning(warning, "容量を取得できません: " + ex.Message);
                }
                cards.Add(new ExifCacheCardInfo(
                    root,
                    serial,
                    metadata,
                    size,
                    currentSourceVolumeSerialNumber == serial,
                    warning));
            }

            long rootSize = 0;
            string rootWarning = null;
            try { rootSize = CalculateDirectorySize(root); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                rootWarning = "保存先の容量を取得できません: " + ex.Message;
            }
            return new ExifCacheRootInfo(root, isCurrent, true, rootSize, cards, rootWarning);
        }

        private ExifCacheSession OpenExisting(
            string cacheRoot,
            uint volumeSerialNumber,
            CancellationToken cancellationToken)
        {
            var store = new ExifCacheStore(cacheRoot, _lockTimeout);
            ExifCacheSession session;
            string warning;
            if (!store.TryOpenExisting(volumeSerialNumber, out session, out warning, cancellationToken))
                throw new InvalidOperationException(warning ?? "Exif キャッシュを開けませんでした。");
            return session;
        }

        private static bool TryParseVolumeFolder(string path, out uint serial)
        {
            serial = 0;
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return name.Length == 8 && uint.TryParse(
                name,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out serial);
        }

        private static long CalculateDirectorySize(string root)
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    total = checked(total + new FileInfo(path).Length);
                foreach (var path in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) pending.Push(path);
                }
            }
            return total;
        }

        private static string AppendWarning(string existing, string addition) =>
            string.IsNullOrWhiteSpace(existing) ? addition : existing + " " + addition;

        private static bool IsManagementFailure(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException ||
            ex is SerializationException || ex is FormatException || ex is ArgumentException;
    }
}
