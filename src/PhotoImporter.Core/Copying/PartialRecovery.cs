using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;

namespace PhotoImporter.Core.Copying
{
    public enum PartialRecoveryDestinationState
    {
        Missing,
        MatchesExpectedSource,
        MatchesPreviousSnapshot,
        RequiresComparison
    }

    public static class PartialRecoveryGuidance
    {
        public static string Describe(PartialRecoveryDestinationState state)
        {
            switch (state)
            {
                case PartialRecoveryDestinationState.Missing:
                    return "正式ファイルがない場合は、一時ファイルが唯一の残存データである可能性があります。削除や名前変更をせず保全してください。";
                case PartialRecoveryDestinationState.MatchesExpectedSource:
                    return "正式ファイルがコピー元のサイズと更新日時に一致する場合はコピー完了と判断できます。一時ファイルは内容を確認するまで自動削除しません。";
                case PartialRecoveryDestinationState.MatchesPreviousSnapshot:
                    return "正式ファイルがスキャン時の旧状態に一致する場合は、元写真から再スキャンし、必要ならコピーをやり直してください。";
                default:
                    return "正式ファイルがあるものの状態を確認できない場合は、正式ファイルを元写真と比較してから再スキャンしてください。";
            }
        }
    }

    public sealed class PartialRecoveryCandidate
    {
        internal PartialRecoveryCandidate(string path, long fileSize, DateTime lastWriteTimeUtc)
        {
            Path = path;
            FileSize = fileSize;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public string Path { get; }
        public long FileSize { get; }
        public DateTime LastWriteTimeUtc { get; }
    }

    public sealed class PartialRecoveryScanResult
    {
        internal PartialRecoveryScanResult(
            IList<PartialRecoveryCandidate> candidates,
            IList<string> warnings)
        {
            Candidates = new ReadOnlyCollection<PartialRecoveryCandidate>(candidates);
            Warnings = new ReadOnlyCollection<string>(warnings);
        }

        public IReadOnlyList<PartialRecoveryCandidate> Candidates { get; }
        public IReadOnlyList<string> Warnings { get; }
    }

    public sealed class PartialRecoveryDetector
    {
        private static readonly Regex PartialName = new Regex(
            @"\API_[0-9A-Fa-f]{32}\.partial\z",
            RegexOptions.CultureInvariant);

        public PartialRecoveryScanResult Scan(string destinationRoot)
        {
            var candidates = new List<PartialRecoveryCandidate>();
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(destinationRoot))
                return new PartialRecoveryScanResult(candidates, warnings);

            string root;
            try
            {
                root = Path.GetFullPath(destinationRoot);
                if (!Directory.Exists(root))
                    return new PartialRecoveryScanResult(candidates, warnings);
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add("コピー先ルートがリパースポイントのため、一時ファイルを検査しませんでした: " + root);
                    return new PartialRecoveryScanResult(candidates, warnings);
                }
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                warnings.Add("コピー先を検査できませんでした: " + ex.Message);
                return new PartialRecoveryScanResult(candidates, warnings);
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(directory);
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    warnings.Add("フォルダーを検査できませんでした: " + directory + " / " + ex.Message);
                    continue;
                }

                foreach (var entry in entries)
                {
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                                pending.Push(entry);
                            continue;
                        }

                        if (!IsRecoveryCandidate(entry, root, attributes)) continue;
                        var info = new FileInfo(entry);
                        candidates.Add(new PartialRecoveryCandidate(
                            info.FullName, info.Length, info.LastWriteTimeUtc));
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        warnings.Add("項目を検査できませんでした: " + entry + " / " + ex.Message);
                    }
                }
            }

            candidates.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
            return new PartialRecoveryScanResult(candidates, warnings);
        }

        internal static bool IsRecoveryCandidate(
            string path,
            string destinationRoot,
            FileAttributes attributes)
        {
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;

            try
            {
                var fullPath = Path.GetFullPath(path);
                var root = Path.GetFullPath(destinationRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                       PartialName.IsMatch(Path.GetFileName(fullPath));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return false;
            }
        }

        private static bool IsFileSystemException(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException ||
            ex is ArgumentException || ex is NotSupportedException ||
            ex is System.Security.SecurityException;
    }
}
