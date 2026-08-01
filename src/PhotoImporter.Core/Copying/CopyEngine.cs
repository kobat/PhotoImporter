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
        private readonly ICopyFileOperation _copyFile;
        private readonly MoveFileOperation _moveFile;
        private readonly IFileAttributeOperations _fileAttributes;
        private readonly IRootAvailability _rootAvailability;

        public CopyEngine()
            : this(new CopyFile2Native(), TryMoveFile, new FileAttributeOperations(), new RootAvailability())
        {
        }

        internal CopyEngine(ICopyFileOperation copyFile)
            : this(copyFile, TryMoveFile, new FileAttributeOperations(), new RootAvailability())
        {
        }

        internal CopyEngine(
            ICopyFileOperation copyFile,
            MoveFileOperation moveFile,
            IFileAttributeOperations fileAttributes,
            IRootAvailability rootAvailability = null)
        {
            _copyFile = copyFile ?? throw new ArgumentNullException(nameof(copyFile));
            _moveFile = moveFile ?? throw new ArgumentNullException(nameof(moveFile));
            _fileAttributes = fileAttributes ?? throw new ArgumentNullException(nameof(fileAttributes));
            _rootAvailability = rootAvailability ?? new RootAvailability();
        }

        public CopyBatchResult Execute(
            IEnumerable<CopyPlanItem> plan,
            IProgress<CopyProgress> progress,
            CancellationToken cancellationToken)
        {
            return Execute(plan, null, progress, cancellationToken, null);
        }

        public CopyBatchResult Execute(
            IEnumerable<CopyPlanItem> plan,
            string sourceRoot,
            IProgress<CopyProgress> progress,
            CancellationToken cancellationToken)
        {
            return Execute(plan, sourceRoot, progress, cancellationToken, null);
        }

        public CopyBatchResult Execute(
            IEnumerable<CopyPlanItem> plan,
            string sourceRoot,
            IProgress<CopyProgress> progress,
            CancellationToken cancellationToken,
            CopyPauseController pauseController)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var items = plan.ToList();
            var results = new List<CopyItemResult>();
            var totalBytes = items.Sum(item => item.SourceSnapshot.FileSize);
            long completedBytes = 0;
            var cancelled = false;
            string batchError = null;
            var resultsBySource = new Dictionary<string, CopyItemResult>(StringComparer.OrdinalIgnoreCase);

            Report(progress, 0, items.Count, 0, totalBytes, null);
            for (var index = 0; index < items.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                try
                {
                    if (pauseController != null)
                        pauseController.WaitAtFileBoundary(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }

                var item = items[index];
                if (item.DependsOnSourcePath != null)
                {
                    CopyItemResult dependency;
                    if (!resultsBySource.TryGetValue(item.DependsOnSourcePath, out dependency) ||
                        dependency.Status != CopyItemStatus.Copied)
                    {
                        var skipped = new CopyItemResult(
                            item,
                            CopyItemStatus.Failed,
                            "関連先画像をコピーできなかったため、サイドカーをコピーしませんでした。",
                            null);
                        results.Add(skipped);
                        resultsBySource[item.SourcePath] = skipped;
                        Report(progress, index + 1, items.Count, completedBytes, totalBytes, item.SourcePath);
                        continue;
                    }
                }
                batchError = GetUnavailableRootError(sourceRoot, item.DestinationRoot);
                if (batchError != null) break;
                var currentTransferred = 0L;
                try
                {
                    ExecuteOne(item, cancellationToken, pauseController, transferred =>
                    {
                        var reported = Math.Min(item.SourceSnapshot.FileSize, Math.Max(0, transferred));
                        currentTransferred = Math.Max(currentTransferred, reported);
                        Report(progress, index, items.Count, completedBytes + currentTransferred, totalBytes, item.SourcePath);
                    });
                    completedBytes += item.SourceSnapshot.FileSize;
                    var copied = new CopyItemResult(item, CopyItemStatus.Copied, null, null);
                    results.Add(copied);
                    resultsBySource[item.SourcePath] = copied;
                    Report(progress, index + 1, items.Count, completedBytes, totalBytes, item.SourcePath);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    var cancelledItem = new CopyItemResult(
                        item, CopyItemStatus.Cancelled, "コピーをキャンセルしました。", null);
                    results.Add(cancelledItem);
                    resultsBySource[item.SourcePath] = cancelledItem;
                    break;
                }
                catch (CopyRecoveryException ex)
                {
                    batchError = GetUnavailableRootError(sourceRoot, item.DestinationRoot);
                    if (batchError != null) break;
                    var failed = new CopyItemResult(item, CopyItemStatus.Failed, ex.Message, ex.RecoveryPath);
                    results.Add(failed);
                    resultsBySource[item.SourcePath] = failed;
                    Report(progress, index + 1, items.Count, completedBytes, totalBytes, item.SourcePath);
                }
                catch (Exception ex)
                {
                    batchError = GetUnavailableRootError(sourceRoot, item.DestinationRoot);
                    if (batchError != null) break;
                    var failed = new CopyItemResult(item, CopyItemStatus.Failed, ex.Message, null);
                    results.Add(failed);
                    resultsBySource[item.SourcePath] = failed;
                    Report(progress, index + 1, items.Count, completedBytes, totalBytes, item.SourcePath);
                }
            }

            return new CopyBatchResult(results, cancelled, batchError);
        }

        private string GetUnavailableRootError(string sourceRoot, string destinationRoot)
        {
            if (!string.IsNullOrWhiteSpace(sourceRoot) && !_rootAvailability.IsAvailable(sourceRoot))
                return "コピー元全体を利用できなくなったため、コピーを中止しました: " + sourceRoot;
            if (!_rootAvailability.IsAvailable(destinationRoot))
                return "コピー先全体を利用できなくなったため、コピーを中止しました: " + destinationRoot;
            return null;
        }

        private void ExecuteOne(
            CopyPlanItem item,
            CancellationToken token,
            CopyPauseController pauseController,
            Action<long> progress)
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
                    _copyFile.Copy(item.SourcePath, partialPath, token, pauseController, progress);
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
                var destinationReadOnlyCleared = TryClearReadOnlyDestination(item);
                bool moved;
                int error;
                try
                {
                    moved = _moveFile(partialPath, item.DestinationPath, flags, out error);
                }
                catch (Exception ex)
                {
                    if (destinationReadOnlyCleared && !TryRestoreDestinationReadOnly(item))
                        throw CreateReadOnlyRecoveryException(item, partialPath, 0, ex);
                    throw;
                }

                if (!moved)
                {
                    if (destinationReadOnlyCleared && !TryRestoreDestinationReadOnly(item))
                        throw CreateReadOnlyRecoveryException(item, partialPath, error, null);

                    if (DestinationMatchesExpected(item))
                    {
                        if (!TryDeleteSafePartial(
                                partialPath, destinationDirectory, item.DestinationRoot, _fileAttributes))
                            throw new CopyRecoveryException(
                                "正式なファイル名を確定できず、一時ファイルも削除できませんでした。",
                                partialPath,
                                error);
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
            catch (Exception ex)
            {
                if (!TryDeleteSafePartial(
                        partialPath, destinationDirectory, item.DestinationRoot, _fileAttributes))
                    throw new CopyRecoveryException(
                        "コピーに失敗し、一時ファイルを安全に削除できませんでした。",
                        partialPath,
                        0,
                        ex);
                throw;
            }
        }

        private bool TryClearReadOnlyDestination(CopyPlanItem item)
        {
            if (!item.Overwrite || item.DestinationSnapshot == null) return false;

            var attributes = _fileAttributes.GetAttributes(item.DestinationPath);
            if ((attributes & FileAttributes.ReadOnly) == 0) return false;
            _fileAttributes.SetAttributes(item.DestinationPath, RemoveReadOnly(attributes));
            return true;
        }

        private bool TryRestoreDestinationReadOnly(CopyPlanItem item)
        {
            try
            {
                if (!DestinationMatchesExpected(item)) return false;
                var attributes = _fileAttributes.GetAttributes(item.DestinationPath);
                _fileAttributes.SetAttributes(item.DestinationPath, AddReadOnly(attributes));
                return DestinationMatchesExpected(item) &&
                       (_fileAttributes.GetAttributes(item.DestinationPath) & FileAttributes.ReadOnly) != 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private static CopyRecoveryException CreateReadOnlyRecoveryException(
            CopyPlanItem item,
            string partialPath,
            int win32Error,
            Exception innerException)
        {
            var message =
                "正式なファイル名を確定できず、旧コピー先の読み取り専用属性を復元できませんでした。" +
                "コピー先を確認してください: " + item.DestinationPath;
            return new CopyRecoveryException(message, partialPath, win32Error, innerException);
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

        internal static bool TryDeleteSafePartial(
            string partialPath,
            string expectedDirectory,
            string destinationRoot)
        {
            return TryDeleteSafePartial(
                partialPath, expectedDirectory, destinationRoot, new FileAttributeOperations());
        }

        private static bool TryDeleteSafePartial(
            string partialPath,
            string expectedDirectory,
            string destinationRoot,
            IFileAttributeOperations fileAttributes)
        {
            try
            {
                var fullPartialPath = Path.GetFullPath(partialPath);
                var rootPrefix = EnsureTrailingSeparator(Path.GetFullPath(destinationRoot));
                if (!fullPartialPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(
                        Path.GetFullPath(Path.GetDirectoryName(fullPartialPath)),
                        Path.GetFullPath(expectedDirectory),
                        StringComparison.OrdinalIgnoreCase)) return false;
                if (!PartialName.IsMatch(Path.GetFileName(fullPartialPath))) return false;
                if (!File.Exists(fullPartialPath)) return true;
                var attributes = fileAttributes.GetAttributes(fullPartialPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) != 0) return false;

                var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
                if (readOnly)
                    fileAttributes.SetAttributes(fullPartialPath, RemoveReadOnly(attributes));
                try
                {
                    File.Delete(fullPartialPath);
                    return !File.Exists(fullPartialPath);
                }
                catch
                {
                    if (readOnly && File.Exists(fullPartialPath))
                    {
                        try
                        {
                            var current = fileAttributes.GetAttributes(fullPartialPath);
                            fileAttributes.SetAttributes(fullPartialPath, AddReadOnly(current));
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (ArgumentException) { }
                        catch (NotSupportedException) { }
                        catch (System.Security.SecurityException) { }
                    }
                    throw;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private static FileAttributes RemoveReadOnly(FileAttributes attributes)
        {
            var result = attributes & ~FileAttributes.ReadOnly;
            return result == 0 ? FileAttributes.Normal : result;
        }

        private static FileAttributes AddReadOnly(FileAttributes attributes) =>
            (attributes & ~FileAttributes.Normal) | FileAttributes.ReadOnly;

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

        private static bool TryMoveFile(
            string existingFileName,
            string newFileName,
            uint flags,
            out int error)
        {
            var moved = MoveFileEx(existingFileName, newFileName, flags);
            error = moved ? 0 : Marshal.GetLastWin32Error();
            return moved;
        }

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
    }

    internal delegate bool MoveFileOperation(
        string existingFileName,
        string newFileName,
        uint flags,
        out int error);

    internal interface IFileAttributeOperations
    {
        FileAttributes GetAttributes(string path);
        void SetAttributes(string path, FileAttributes attributes);
    }

    internal interface IRootAvailability
    {
        bool IsAvailable(string path);
    }

    internal sealed class RootAvailability : IRootAvailability
    {
        public bool IsAvailable(string path)
        {
            try { return Directory.Exists(path); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal sealed class FileAttributeOperations : IFileAttributeOperations
    {
        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

        public void SetAttributes(string path, FileAttributes attributes) =>
            File.SetAttributes(path, attributes);
    }

    public sealed class CopyRecoveryException : IOException
    {
        internal CopyRecoveryException(
            string message,
            string recoveryPath,
            int win32Error = 0,
            Exception innerException = null)
            : base(
                win32Error == 0 ? message : message + " (Win32 " + win32Error + ")",
                innerException)
        {
            RecoveryPath = recoveryPath;
        }

        public string RecoveryPath { get; }
    }
}
