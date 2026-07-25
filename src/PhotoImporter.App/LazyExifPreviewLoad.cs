using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;

namespace PhotoImporter.App
{
    internal sealed class LazyExifPreviewLoadPlan
    {
        private readonly string _sourceRoot;
        private readonly string _destinationRoot;
        private readonly IReadOnlyList<PreviewItem> _allItems;
        private readonly IReadOnlyList<LazyExifTarget> _targets;
        private readonly RawJpegAnalysisPlan _analysisPlan;

        private LazyExifPreviewLoadPlan(
            string sourceRoot,
            string destinationRoot,
            IReadOnlyList<PreviewItem> allItems,
            IReadOnlyList<LazyExifTarget> targets,
            RawJpegAnalysisPlan analysisPlan)
        {
            _sourceRoot = sourceRoot;
            _destinationRoot = destinationRoot;
            _allItems = allItems;
            _targets = targets;
            _analysisPlan = analysisPlan;
        }

        public static LazyExifPreviewLoadPlan Capture(
            string sourceRoot,
            string destinationRoot,
            IEnumerable<PreviewItem> items,
            RawJpegAnalysisMode rawJpegAnalysisMode)
        {
            if (sourceRoot == null) throw new ArgumentNullException(nameof(sourceRoot));
            if (destinationRoot == null) throw new ArgumentNullException(nameof(destinationRoot));
            if (items == null) throw new ArgumentNullException(nameof(items));

            var normalizedSourceRoot = Path.GetFullPath(sourceRoot);
            var allItems = items.ToList().AsReadOnly();
            var targets = new List<LazyExifTarget>();
            foreach (var item in allItems.Where(item => !item.IsScanError))
            {
                if (item.TemplateContext == null)
                    throw new InvalidOperationException(
                        "Exif 情報を関連付けるためのスキャン時情報がありません。手動で再スキャンしてください。");

                var fullPath = Path.GetFullPath(Path.Combine(normalizedSourceRoot, item.SourcePath));
                targets.Add(new LazyExifTarget(
                    item,
                    fullPath,
                    item.TemplateContext.FileSize,
                    item.TemplateContext.ModifiedDate,
                    item.TemplateContext.ModifiedDateUtc));
            }

            var duplicate = targets.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException(
                    "同じコピー元ファイルが一覧に複数あります。手動で再スキャンしてください: " + duplicate.Key);

            return new LazyExifPreviewLoadPlan(
                normalizedSourceRoot,
                Path.GetFullPath(destinationRoot),
                allItems,
                targets.AsReadOnly(),
                RawJpegAnalysisPlan.Create(
                    targets.Select(item => item.FullPath),
                    rawJpegAnalysisMode));
        }

        public LazyExifPreviewLoadResult Load(
            bool useExifCache,
            string exifCacheRoot,
            IProgress<PhotoMetadataScanProgress> progress,
            CancellationToken cancellationToken,
            CachedPhotoMetadataScanner scanner = null)
        {
            ValidateTargets(cancellationToken);

            var warnings = new List<string>();
            ExifCacheStore cacheStore = null;
            VolumeInfo volume = null;
            if (useExifCache &&
                (IsSameOrUnder(exifCacheRoot, _sourceRoot) || IsSameOrUnder(_sourceRoot, exifCacheRoot) ||
                 IsSameOrUnder(exifCacheRoot, _destinationRoot) || IsSameOrUnder(_destinationRoot, exifCacheRoot)))
            {
                warnings.Add(string.Format(
                    "Exif キャッシュの保存先 ({0}) がコピー元またはコピー先と重なるため、キャッシュなしで続行しました。",
                    exifCacheRoot));
            }
            else if (useExifCache)
            {
                cacheStore = new ExifCacheStore(exifCacheRoot);
                try
                {
                    volume = new WindowsVolumeInfoReader().Read(_sourceRoot);
                }
                catch (Exception ex) when (ex is Win32Exception || ex is IOException ||
                                               ex is UnauthorizedAccessException)
                {
                    warnings.Add(
                        "コピー元のボリューム情報を取得できないため、Exif キャッシュなしで続行しました: " +
                        ex.Message);
                    cacheStore = null;
                }
            }

            var scan = (scanner ?? new CachedPhotoMetadataScanner()).Scan(
                _analysisPlan,
                volume,
                cacheStore,
                DateTime.UtcNow,
                progress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTargets(cancellationToken);
            warnings.AddRange(scan.Warnings);

            var targetByPath = _targets.ToDictionary(
                item => item.FullPath,
                StringComparer.OrdinalIgnoreCase);
            var attachments = _targets.Select(target =>
            {
                var analysisSource = _analysisPlan.GetAnalysisSource(target.FullPath);
                var analysisTarget = targetByPath[analysisSource];
                return new LazyExifAttachment(
                    target.Item,
                    scan.Results[analysisSource],
                    MakeRelative(_sourceRoot, analysisSource),
                    analysisTarget.ModifiedDate,
                    analysisTarget.LastWriteTimeUtc);
            }).ToList();
            return new LazyExifPreviewLoadResult(
                _allItems,
                attachments.AsReadOnly(),
                new ReadOnlyCollection<string>(warnings),
                scan.CacheHits);
        }

        private void ValidateTargets(CancellationToken cancellationToken)
        {
            foreach (var target in _targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(target.FullPath);
                    info.Refresh();
                    if (!info.Exists ||
                        info.Length != target.FileSize ||
                        info.LastWriteTimeUtc != target.LastWriteTimeUtc)
                        throw new IOException(
                            "Exif 読込の対象ファイルが手動スキャン後に変更または削除されました。");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new IOException(
                        "一覧へ安全に Exif 情報を関連付けられません。手動で再スキャンしてください: " +
                        target.Item.SourcePath,
                        ex);
                }
            }
        }

        private static bool IsSameOrUnder(string candidate, string root)
        {
            var fullCandidate = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullCandidate.StartsWith(
                       fullRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeRelative(string root, string path)
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var rootUri = new Uri(normalizedRoot);
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }
    }

    internal sealed class LazyExifPreviewLoadResult
    {
        private readonly IReadOnlyList<PreviewItem> _originalItems;

        public LazyExifPreviewLoadResult(
            IReadOnlyList<PreviewItem> originalItems,
            IReadOnlyList<LazyExifAttachment> attachments,
            IReadOnlyList<string> warnings,
            int cacheHits)
        {
            _originalItems = originalItems;
            Attachments = attachments;
            Warnings = warnings;
            CacheHits = cacheHits;
        }

        public IReadOnlyList<LazyExifAttachment> Attachments { get; }
        public IReadOnlyList<string> Warnings { get; }
        public int CacheHits { get; }

        public LazyExifPreviewCommit PrepareCommit(
            IEnumerable<PreviewItem> currentItems,
            PreparedFilter filter)
        {
            if (currentItems == null) throw new ArgumentNullException(nameof(currentItems));
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            if (!currentItems.SequenceEqual(_originalItems))
                throw new InvalidOperationException(
                    "Exif 読込中に一覧が変更されました。手動で再スキャンしてください。");

            var byItem = Attachments.ToDictionary(item => item.Item);
            foreach (var item in _originalItems)
            {
                LazyExifAttachment attachment;
                filter.Matches(byItem.TryGetValue(item, out attachment)
                    ? item.CreateFilterCandidate(attachment.MetadataResult)
                    : item.CreateFilterCandidate());
            }
            return new LazyExifPreviewCommit(Attachments);
        }
    }

    internal sealed class LazyExifPreviewCommit
    {
        private readonly IReadOnlyList<LazyExifAttachment> _attachments;

        public LazyExifPreviewCommit(IReadOnlyList<LazyExifAttachment> attachments)
        {
            _attachments = attachments;
        }

        public void Apply()
        {
            foreach (var attachment in _attachments)
                attachment.Item.AttachMetadata(
                    attachment.MetadataResult,
                    attachment.MetadataSourcePath,
                    attachment.MetadataSourceModifiedDate,
                    attachment.MetadataSourceModifiedDateUtc);
        }
    }

    internal sealed class LazyExifAttachment
    {
        public LazyExifAttachment(
            PreviewItem item,
            PhotoMetadataReadResult metadataResult,
            string metadataSourcePath,
            DateTime metadataSourceModifiedDate,
            DateTime metadataSourceModifiedDateUtc)
        {
            Item = item;
            MetadataResult = metadataResult;
            MetadataSourcePath = metadataSourcePath;
            MetadataSourceModifiedDate = metadataSourceModifiedDate;
            MetadataSourceModifiedDateUtc = metadataSourceModifiedDateUtc;
        }

        public PreviewItem Item { get; }
        public PhotoMetadataReadResult MetadataResult { get; }
        public string MetadataSourcePath { get; }
        public DateTime MetadataSourceModifiedDate { get; }
        public DateTime MetadataSourceModifiedDateUtc { get; }
    }

    internal sealed class LazyExifTarget
    {
        public LazyExifTarget(
            PreviewItem item,
            string fullPath,
            long fileSize,
            DateTime modifiedDate,
            DateTime lastWriteTimeUtc)
        {
            Item = item;
            FullPath = fullPath;
            FileSize = fileSize;
            ModifiedDate = modifiedDate;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public PreviewItem Item { get; }
        public string FullPath { get; }
        public long FileSize { get; }
        public DateTime ModifiedDate { get; }
        public DateTime LastWriteTimeUtc { get; }
    }
}
