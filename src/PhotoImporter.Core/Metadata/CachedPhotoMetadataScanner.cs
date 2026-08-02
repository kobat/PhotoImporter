using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PhotoImporter.Core.Metadata
{
    public enum PhotoMetadataScanPhase
    {
        Preparing,
        Reading,
        SavingCache,
        Completed
    }

    public sealed class PhotoMetadataScanProgress
    {
        public PhotoMetadataScanProgress(
            PhotoMetadataScanPhase phase,
            int completedFiles,
            int totalFiles,
            int cacheHits)
        {
            Phase = phase;
            CompletedFiles = completedFiles;
            TotalFiles = totalFiles;
            CacheHits = cacheHits;
        }

        public PhotoMetadataScanPhase Phase { get; }
        public int CompletedFiles { get; }
        public int TotalFiles { get; }
        public int CacheHits { get; }
    }

    public sealed class PhotoMetadataScanResult
    {
        internal PhotoMetadataScanResult(
            IDictionary<string, PhotoMetadataReadResult> results,
            IList<string> warnings,
            int cacheHits)
        {
            Results = new ReadOnlyDictionary<string, PhotoMetadataReadResult>(
                new Dictionary<string, PhotoMetadataReadResult>(results, StringComparer.OrdinalIgnoreCase));
            Warnings = new ReadOnlyCollection<string>(warnings);
            CacheHits = cacheHits;
        }

        public IReadOnlyDictionary<string, PhotoMetadataReadResult> Results { get; }
        public IReadOnlyList<string> Warnings { get; }
        public int CacheHits { get; }
    }

    public sealed class CachedPhotoMetadataScanner
    {
        private readonly IPhotoMetadataReader _reader;

        public CachedPhotoMetadataScanner(IPhotoMetadataReader reader = null)
        {
            _reader = reader ?? new PhotoMetadataReader();
        }

        public PhotoMetadataScanResult Scan(
            RawJpegAnalysisPlan analysisPlan,
            VolumeInfo volume,
            ExifCacheStore cacheStore,
            DateTime utcNow,
            IProgress<PhotoMetadataScanProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (analysisPlan == null) throw new ArgumentNullException(nameof(analysisPlan));
            if (cacheStore != null && volume == null) throw new ArgumentNullException(nameof(volume));
            if (utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The current time must be UTC.", nameof(utcNow));

            var results = new Dictionary<string, PhotoMetadataReadResult>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            var snapshots = new Dictionary<string, ExifFileSnapshot>(StringComparer.OrdinalIgnoreCase);
            var progressReporter = new MetadataProgressReporter(
                progress,
                analysisPlan.AnalysisSources.Count);
            progressReporter.ReportPhase(PhotoMetadataScanPhase.Preparing, 0, 0);
            foreach (var source in analysisPlan.AnalysisSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    snapshots.Add(source, TakeSnapshot(source));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    results.Add(source, PhotoMetadataReadResult.ReadError(ex));
                }
            }

            ExifCacheSession cacheSession = null;
            if (cacheStore != null)
            {
                string warning;
                if (!cacheStore.TryOpen(volume, out cacheSession, out warning, cancellationToken))
                {
                    if (!string.IsNullOrWhiteSpace(warning)) warnings.Add(warning);
                }
                else if (cacheSession.RecoveredFromInvalidFile)
                {
                    warnings.Add("破損または互換性のない Exif キャッシュを破棄して再生成しました。");
                }
            }

            var cacheHits = 0;
            var completed = 0;
            var scanCompleted = false;
            progressReporter.ReportReading(0, 0, true);
            try
            {
                foreach (var source in analysisPlan.AnalysisSources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (results.ContainsKey(source))
                    {
                        completed++;
                        progressReporter.ReportReading(completed, cacheHits);
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }

                    var before = snapshots[source];
                    var key = cacheSession == null
                        ? null
                        : ExifCacheKey.Create(volume, source, before.FileSize, before.LastWriteTimeUtc);
                    PhotoMetadataReadResult result;
                    if (cacheSession != null && cacheSession.TryGet(key, utcNow, out result))
                    {
                        cacheHits++;
                    }
                    else
                    {
                        result = _reader.Read(source);
                        try
                        {
                            var after = TakeSnapshot(source);
                            if (!SnapshotsMatch(before, after))
                            {
                                result = PhotoMetadataReadResult.ReadError(new IOException(
                                    "Exif の読み取り中にファイルが変更されました。もう一度スキャンしてください。"));
                            }
                            else
                            {
                                if (cacheSession != null) cacheSession.Put(key, result, utcNow);
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            result = PhotoMetadataReadResult.ReadError(new IOException(
                                "Exif の読み取り中にファイルの状態を再確認できませんでした。", ex));
                        }
                    }

                    results.Add(source, result);
                    completed++;
                    progressReporter.ReportReading(completed, cacheHits);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                scanCompleted = true;
            }
            finally
            {
                if (cacheSession != null)
                {
                    progressReporter.ReportPhase(
                        PhotoMetadataScanPhase.SavingCache,
                        completed,
                        cacheHits);
                    try
                    {
                        cacheSession.Dispose();
                    }
                    catch (Exception ex) when (ExifCacheStore.IsCacheFailure(ex))
                    {
                        warnings.Add(string.Format(
                            "Exif キャッシュを保存できませんでした ({0}): {1} 次回は再解析します。",
                            cacheStore.CacheRoot,
                            ex.Message));
                    }
                }
            }

            if (scanCompleted)
                progressReporter.ReportPhase(
                    PhotoMetadataScanPhase.Completed,
                    completed,
                    cacheHits);

            return new PhotoMetadataScanResult(results, warnings, cacheHits);
        }

        private sealed class MetadataProgressReporter
        {
            private static readonly TimeSpan MinimumReportInterval = TimeSpan.FromMilliseconds(75);
            private readonly IProgress<PhotoMetadataScanProgress> _progress;
            private readonly int _totalFiles;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private TimeSpan _lastReadingReport = TimeSpan.MinValue;

            public MetadataProgressReporter(
                IProgress<PhotoMetadataScanProgress> progress,
                int totalFiles)
            {
                _progress = progress;
                _totalFiles = totalFiles;
            }

            public void ReportReading(int completedFiles, int cacheHits, bool force = false)
            {
                if (_progress == null) return;
                var now = _stopwatch.Elapsed;
                if (!force && completedFiles != _totalFiles &&
                    _lastReadingReport != TimeSpan.MinValue &&
                    now - _lastReadingReport < MinimumReportInterval)
                    return;

                _lastReadingReport = now;
                ReportPhase(PhotoMetadataScanPhase.Reading, completedFiles, cacheHits);
            }

            public void ReportPhase(
                PhotoMetadataScanPhase phase,
                int completedFiles,
                int cacheHits)
            {
                _progress?.Report(new PhotoMetadataScanProgress(
                    phase,
                    completedFiles,
                    _totalFiles,
                    cacheHits));
            }
        }

        private static ExifFileSnapshot TakeSnapshot(string path)
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists) throw new FileNotFoundException("ファイルが見つかりません。", path);
            return new ExifFileSnapshot(info.FullName, info.Length, info.LastWriteTimeUtc);
        }

        private static bool SnapshotsMatch(ExifFileSnapshot first, ExifFileSnapshot second) =>
            first.FileSize == second.FileSize && first.LastWriteTimeUtc == second.LastWriteTimeUtc;
    }
}
