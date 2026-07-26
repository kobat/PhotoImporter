using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Settings;

namespace PhotoImporter.App
{
    internal sealed class SourceFileEnumerator
    {
        private static readonly HashSet<string> ExcludedFileNames = new HashSet<string>(
            new[] { "Thumbs.db", "desktop.ini" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ExcludedDirectoryNames = new HashSet<string>(
            new[] { "System Volume Information", "$RECYCLE.BIN", "RECYCLER", "RECYCLED" },
            StringComparer.OrdinalIgnoreCase);
        private readonly ISourceFileSystem _fileSystem;

        public SourceFileEnumerator()
            : this(new PhysicalSourceFileSystem())
        {
        }

        internal SourceFileEnumerator(ISourceFileSystem fileSystem)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        }

        public SourceScanResult Enumerate(
            string sourceRoot,
            System.Threading.CancellationToken cancellationToken)
        {
            return Enumerate(
                sourceRoot,
                SourceFileSelectionMode.MediaOnly,
                cancellationToken);
        }

        public SourceScanResult Enumerate(
            string sourceRoot,
            SourceFileSelectionMode selectionMode,
            System.Threading.CancellationToken cancellationToken)
        {
            return Enumerate(sourceRoot, selectionMode, false, cancellationToken);
        }

        public SourceScanResult Enumerate(
            string sourceRoot,
            SourceFileSelectionMode selectionMode,
            bool includeSidecarCandidates,
            System.Threading.CancellationToken cancellationToken)
        {
            if (sourceRoot == null) throw new ArgumentNullException(nameof(sourceRoot));
            if (!Enum.IsDefined(typeof(SourceFileSelectionMode), selectionMode))
                throw new ArgumentOutOfRangeException(nameof(selectionMode));
            var result = new SourceScanResult();
            var pending = new Stack<string>();
            pending.Push(sourceRoot);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSourceRootAvailable(sourceRoot);
                var directory = pending.Pop();
                IReadOnlyList<SourceEntry> entries;
                try
                {
                    entries = _fileSystem.GetEntries(directory);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    EnsureSourceRootAvailable(sourceRoot);
                    result.Issues.Add(new ScanIssue(MakeRelative(sourceRoot, directory), ex.Message));
                    continue;
                }

                foreach (var entry in entries.OrderByDescending(
                    item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (entry.IsDirectory)
                        {
                            if (!ExcludedDirectoryNames.Contains(entry.Name))
                                pending.Push(entry.FullPath);
                        }
                        else if (!ExcludedFileNames.Contains(entry.Name) &&
                                 ShouldInclude(entry.FullPath, selectionMode, includeSidecarCandidates))
                        {
                            result.Files.Add(entry.FullPath);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                    {
                        EnsureSourceRootAvailable(sourceRoot);
                        result.Issues.Add(new ScanIssue(
                            MakeRelative(sourceRoot, entry.FullPath), ex.Message));
                    }
                }
            }

            return result;
        }

        private static bool ShouldInclude(
            string path,
            SourceFileSelectionMode selectionMode,
            bool includeSidecarCandidates)
        {
            if (includeSidecarCandidates && SidecarAssociationPlan.IsSidecarCandidate(path))
                return true;
            switch (selectionMode)
            {
                case SourceFileSelectionMode.MediaOnly:
                    return PhotoFileClassifier.IsSupported(path);
                case SourceFileSelectionMode.AllFiles:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(selectionMode));
            }
        }

        private void EnsureSourceRootAvailable(string sourceRoot)
        {
            if (!_fileSystem.DirectoryExists(sourceRoot))
                throw new IOException("コピー元全体を利用できなくなったため、スキャンを中止しました: " + sourceRoot);
        }

        private static string MakeRelative(string root, string path)
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(normalizedRoot.Length)
                : fullPath;
        }
    }

    internal interface ISourceFileSystem
    {
        bool DirectoryExists(string path);
        IReadOnlyList<SourceEntry> GetEntries(string directory);
    }

    internal sealed class PhysicalSourceFileSystem : ISourceFileSystem
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public IReadOnlyList<SourceEntry> GetEntries(string directory) =>
            new DirectoryInfo(directory).GetFileSystemInfos()
                .Select(entry => new SourceEntry(
                    entry.FullName,
                    entry.Name,
                    entry is DirectoryInfo,
                    () => entry.Attributes))
                .ToList();
    }

    internal sealed class SourceEntry
    {
        public SourceEntry(string fullPath, string name, bool isDirectory, FileAttributes attributes)
            : this(fullPath, name, isDirectory, () => attributes)
        {
        }

        internal SourceEntry(
            string fullPath,
            string name,
            bool isDirectory,
            Func<FileAttributes> getAttributes)
        {
            FullPath = fullPath;
            Name = name;
            IsDirectory = isDirectory;
            _getAttributes = getAttributes ?? throw new ArgumentNullException(nameof(getAttributes));
        }

        private readonly Func<FileAttributes> _getAttributes;
        public string FullPath { get; }
        public string Name { get; }
        public bool IsDirectory { get; }
        public FileAttributes Attributes => _getAttributes();
    }

    internal sealed class SourceScanResult
    {
        public List<string> Files { get; } = new List<string>();
        public List<ScanIssue> Issues { get; } = new List<ScanIssue>();
    }

    internal sealed class ScanIssue
    {
        public ScanIssue(string path, string message) { Path = path; Message = message; }
        public string Path { get; }
        public string Message { get; }
    }
}
