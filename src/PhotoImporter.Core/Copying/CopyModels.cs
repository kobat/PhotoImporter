using System;
using System.Collections.Generic;
using System.IO;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Templates;

namespace PhotoImporter.Core.Copying
{
    public sealed class FileSnapshot
    {
        public FileSnapshot(long fileSize, DateTime lastWriteTimeUtc)
        {
            if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
            if (lastWriteTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The timestamp must be UTC.", nameof(lastWriteTimeUtc));
            FileSize = fileSize;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public long FileSize { get; }
        public DateTime LastWriteTimeUtc { get; }
    }

    public sealed class CopyPlanItem
    {
        public CopyPlanItem(
            string sourcePath,
            string destinationRoot,
            string destinationPath,
            FileSnapshot sourceSnapshot,
            DestinationFileSnapshot destinationSnapshot,
            FileSystemTimestampPolicy destinationTimestampPolicy,
            bool overwrite)
        {
            SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
            DestinationRoot = destinationRoot ?? throw new ArgumentNullException(nameof(destinationRoot));
            DestinationPath = destinationPath ?? throw new ArgumentNullException(nameof(destinationPath));
            SourceSnapshot = sourceSnapshot ?? throw new ArgumentNullException(nameof(sourceSnapshot));
            DestinationSnapshot = destinationSnapshot;
            DestinationTimestampPolicy = destinationTimestampPolicy ??
                throw new ArgumentNullException(nameof(destinationTimestampPolicy));
            if (!DestinationTimestampPolicy.IsSupported)
                throw new NotSupportedException(
                    "Unsupported destination file system: " + DestinationTimestampPolicy.FileSystemName);
            Overwrite = overwrite;

            var root = NormalizeRoot(DestinationRoot);
            var destination = Path.GetFullPath(DestinationPath);
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The destination must be under the destination root.", nameof(destinationPath));
        }

        public string SourcePath { get; }
        public string DestinationRoot { get; }
        public string DestinationPath { get; }
        public FileSnapshot SourceSnapshot { get; }
        public DestinationFileSnapshot DestinationSnapshot { get; }
        public FileSystemTimestampPolicy DestinationTimestampPolicy { get; }
        public bool Overwrite { get; }

        private static string NormalizeRoot(string path)
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath + Path.DirectorySeparatorChar;
        }
    }

    public enum CopyItemStatus
    {
        Copied,
        Failed,
        Cancelled
    }

    public sealed class CopyItemResult
    {
        internal CopyItemResult(CopyPlanItem item, CopyItemStatus status, string error, string recoveryPath)
        {
            Item = item;
            Status = status;
            Error = error;
            RecoveryPath = recoveryPath;
        }

        public CopyPlanItem Item { get; }
        public CopyItemStatus Status { get; }
        public string Error { get; }
        public string RecoveryPath { get; }
    }

    public sealed class CopyBatchResult
    {
        internal CopyBatchResult(IList<CopyItemResult> items, bool cancelled)
        {
            Items = items;
            Cancelled = cancelled;
        }

        public IList<CopyItemResult> Items { get; }
        public bool Cancelled { get; }
    }

    public sealed class CopyProgress
    {
        internal CopyProgress(
            int completedFiles,
            int totalFiles,
            long transferredBytes,
            long totalBytes,
            string currentSourcePath)
        {
            CompletedFiles = completedFiles;
            TotalFiles = totalFiles;
            TransferredBytes = transferredBytes;
            TotalBytes = totalBytes;
            CurrentSourcePath = currentSourcePath;
        }

        public int CompletedFiles { get; }
        public int TotalFiles { get; }
        public long TransferredBytes { get; }
        public long TotalBytes { get; }
        public string CurrentSourcePath { get; }
    }
}
