using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using PhotoImporter.Core.Metadata;

namespace PhotoImporter.Core.Copying
{
    public sealed class CopyEngine
    {
        private const uint MoveFileReplaceExisting = 0x00000001;
        private const uint MoveFileWriteThrough = 0x00000008;
        private static readonly Regex PartialName = new Regex(
            @"^PI_[0-9A-Fa-f]{32}\.partial$",
            RegexOptions.CultureInvariant);
        private readonly CopyFile2Native _native = new CopyFile2Native();

        public CopyBatchResult Execute(
            IEnumerable<CopyPlanItem> plan,
            IProgress<CopyProgress> progress,
            CancellationToken cancellationToken)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var items = plan.ToList();
            var results = new List<CopyItemResult>();
            var totalBytes = items.Sum(item => item.SourceSnapshot.FileSize);
            long completedBytes = 0;
            var cancelled = false;

            Report(progress, 0, items.Count, 0, totalBytes, null);
            for (var index = 0; index < items.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var item = items[index];
                var currentTransferred = 0L;
                try
                {
                    ExecuteOne(item, cancellationToken, transferred =>
                    {
                        currentTransferred = Math.Min(item.SourceSnapshot.FileSize, Math.Max(0, transferred));
                        Report(progress, index, items.Count, completedBytes + currentTransferred, totalBytes, item.SourcePath);
                    });
                    completedBytes += item.SourceSnapshot.FileSize;
                    results.Add(new CopyItemResult(item, CopyItemStatus.Copied, null, null));
                    Report(progress, index + 1, items.Count, completedBytes, totalBytes, item.SourcePath);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    results.Add(new CopyItemResult(item, CopyItemStatus.Cancelled, "コピーをキャンセルしました。", null));
                    break;
                }
                catch (CopyRecoveryException ex)
                {
                    results.Add(new CopyItemResult(item, CopyItemStatus.Failed, ex.Message, ex.RecoveryPath));
                    Report(progress, index + 1, items.Count, completedBytes, totalBytes, item.SourcePath);
                }
                catch (Exception ex)
                {
                    results.Add(new CopyItemResult(item, CopyItemStatus.Failed, ex.Message, null));
                    Report(progress, index + 1, items.Count, completedBytes, totalBytes, item.SourcePath);
                }
            }

            return new CopyBatchResult(results, cancelled);
        }

        private void ExecuteOne(CopyPlanItem item, CancellationToken token, Action<long> progress)
        {
            ValidateSourceSnapshot(item.SourcePath, item.SourceSnapshot, "コピー元");
            ValidateDestination(item);
            token.ThrowIfCancellationRequested();

            var destinationDirectory = Path.GetDirectoryName(item.DestinationPath);
            if (string.IsNullOrEmpty(destinationDirectory))
                throw new InvalidOperationException("コピー先フォルダーを特定できません。");
            Directory.CreateDirectory(destinationDirectory);

            var partialPath = Path.Combine(
                destinationDirectory,
                "PI_" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                try
                {
                    _native.Copy(item.SourcePath, partialPath, token, progress);
                }
                catch (COMException) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }

                ValidateDestinationSnapshot(
                    partialPath, item.SourceSnapshot, item.DestinationTimestampPolicy, "一時ファイル");
                ValidateDestination(item);
                token.ThrowIfCancellationRequested();

                var flags = MoveFileWriteThrough | (item.Overwrite ? MoveFileReplaceExisting : 0u);
                if (!MoveFileEx(partialPath, item.DestinationPath, flags))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (DestinationMatchesExpected(item))
                    {
                        TryDeleteSafePartial(partialPath, destinationDirectory, item.DestinationRoot);
                        throw new Win32Exception(error, "正式なファイル名を確定できませんでした。");
                    }

                    throw new CopyRecoveryException(
                        "正式なファイル名の確定状態を判定できません。一時ファイルを保全しました。",
                        partialPath,
                        error);
                }

                if (File.Exists(partialPath))
                    throw new CopyRecoveryException("確定後も一時ファイルが残っています。", partialPath);
                ValidateDestinationSnapshot(
                    item.DestinationPath, item.SourceSnapshot, item.DestinationTimestampPolicy, "コピー先");
            }
            catch (CopyRecoveryException)
            {
                throw;
            }
            catch
            {
                TryDeleteSafePartial(partialPath, destinationDirectory, item.DestinationRoot);
                throw;
            }
        }

        private static void ValidateDestination(CopyPlanItem item)
        {
            if (!DestinationMatchesExpected(item))
                throw new InvalidOperationException("コピー先がスキャン時から変更されています。再スキャンしてください。");
        }

        private static bool DestinationMatchesExpected(CopyPlanItem item)
        {
            if (item.DestinationSnapshot == null)
                return !File.Exists(item.DestinationPath) && !Directory.Exists(item.DestinationPath);

            if (!File.Exists(item.DestinationPath)) return false;
            try
            {
                var info = new FileInfo(item.DestinationPath);
                return info.Length == item.DestinationSnapshot.FileSize &&
                       item.DestinationTimestampPolicy.Matches(
                           item.DestinationSnapshot.LastWriteTimeUtc, info.LastWriteTimeUtc);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void ValidateSourceSnapshot(string path, FileSnapshot expected, string label)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(label + "が見つかりません。再スキャンしてください。", path);
            var info = new FileInfo(path);
            if (info.Length != expected.FileSize || info.LastWriteTimeUtc != expected.LastWriteTimeUtc)
                throw new InvalidOperationException(label + "がスキャン時から変更されています。再スキャンしてください。");
        }

        private static void ValidateDestinationSnapshot(
            string path,
            FileSnapshot expected,
            FileSystemTimestampPolicy timestampPolicy,
            string label)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(label + "が見つかりません。再スキャンしてください。", path);
            var info = new FileInfo(path);
            if (!SnapshotMatches(expected, info.Length, info.LastWriteTimeUtc, timestampPolicy))
                throw new InvalidOperationException(label + "がスキャン時から変更されています。再スキャンしてください。");
        }

        internal static bool SnapshotMatches(
            FileSnapshot expected,
            long actualFileSize,
            DateTime actualLastWriteTimeUtc,
            FileSystemTimestampPolicy timestampPolicy)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (timestampPolicy == null) throw new ArgumentNullException(nameof(timestampPolicy));
            return actualFileSize == expected.FileSize &&
                   timestampPolicy.Matches(expected.LastWriteTimeUtc, actualLastWriteTimeUtc);
        }

        private static void TryDeleteSafePartial(string partialPath, string expectedDirectory, string destinationRoot)
        {
            try
            {
                var fullPartialPath = Path.GetFullPath(partialPath);
                var rootPrefix = EnsureTrailingSeparator(Path.GetFullPath(destinationRoot));
                if (!fullPartialPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return;
                if (!string.Equals(
                        Path.GetFullPath(Path.GetDirectoryName(fullPartialPath)),
                        Path.GetFullPath(expectedDirectory),
                        StringComparison.OrdinalIgnoreCase)) return;
                if (!PartialName.IsMatch(Path.GetFileName(fullPartialPath))) return;
                if (!File.Exists(fullPartialPath)) return;
                var attributes = File.GetAttributes(fullPartialPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) != 0) return;
                File.Delete(fullPartialPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;

        private static void Report(
            IProgress<CopyProgress> progress,
            int completedFiles,
            int totalFiles,
            long transferredBytes,
            long totalBytes,
            string currentSourcePath)
        {
            if (progress != null)
                progress.Report(new CopyProgress(
                    completedFiles, totalFiles, transferredBytes, totalBytes, currentSourcePath));
        }

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
    }

    public sealed class CopyRecoveryException : IOException
    {
        internal CopyRecoveryException(string message, string recoveryPath, int win32Error = 0)
            : base(win32Error == 0 ? message : message + " (Win32 " + win32Error + ")")
        {
            RecoveryPath = recoveryPath;
        }

        public string RecoveryPath { get; }
    }
}
