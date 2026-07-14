using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace PhotoImporter.Core.Metadata
{
    public sealed class ExifCacheKey : IEquatable<ExifCacheKey>
    {
        private ExifCacheKey(
            uint volumeSerialNumber,
            string volumeRelativePath,
            string comparisonPath,
            long fileSize,
            long lastWriteTimeUtcTicks)
        {
            VolumeSerialNumber = volumeSerialNumber;
            VolumeRelativePath = volumeRelativePath;
            ComparisonPath = comparisonPath;
            FileSize = fileSize;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        }

        public uint VolumeSerialNumber { get; }
        public string VolumeRelativePath { get; }
        public string ComparisonPath { get; }
        public long FileSize { get; }
        public long LastWriteTimeUtcTicks { get; }

        public static ExifCacheKey Create(
            VolumeInfo volume,
            string filePath,
            long fileSize,
            DateTime lastWriteTimeUtc)
        {
            if (volume == null) throw new ArgumentNullException(nameof(volume));
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
            if (lastWriteTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The last-write time must be UTC.", nameof(lastWriteTimeUtc));

            var fullPath = Path.GetFullPath(filePath);
            if (!fullPath.StartsWith(volume.RootPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The file is not located on the supplied volume.", nameof(filePath));

            var relativePath = fullPath.Substring(volume.RootPath.Length)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            if (relativePath.Length == 0)
                throw new ArgumentException("The file path must not be the volume root.", nameof(filePath));

            return new ExifCacheKey(
                volume.SerialNumber,
                relativePath,
                relativePath.ToUpperInvariant(),
                fileSize,
                lastWriteTimeUtc.Ticks);
        }

        public bool Equals(ExifCacheKey other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return VolumeSerialNumber == other.VolumeSerialNumber &&
                   FileSize == other.FileSize &&
                   LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks &&
                   string.Equals(ComparisonPath, other.ComparisonPath, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as ExifCacheKey);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)VolumeSerialNumber;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ComparisonPath);
                hash = (hash * 397) ^ FileSize.GetHashCode();
                hash = (hash * 397) ^ LastWriteTimeUtcTicks.GetHashCode();
                return hash;
            }
        }
    }

    public sealed class ExifFileSnapshot
    {
        public ExifFileSnapshot(string fullPath, long fileSize, DateTime lastWriteTimeUtc)
        {
            if (fullPath == null) throw new ArgumentNullException(nameof(fullPath));
            if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
            if (lastWriteTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The last-write time must be UTC.", nameof(lastWriteTimeUtc));

            FullPath = Path.GetFullPath(fullPath);
            FileSize = fileSize;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public string FullPath { get; }
        public long FileSize { get; }
        public DateTime LastWriteTimeUtc { get; }
    }

    public sealed class ExifCacheKeyPlan
    {
        private readonly IReadOnlyDictionary<string, ExifCacheKey> _keyByTarget;

        private ExifCacheKeyPlan(IDictionary<string, ExifCacheKey> keyByTarget)
        {
            _keyByTarget = new ReadOnlyDictionary<string, ExifCacheKey>(
                new Dictionary<string, ExifCacheKey>(keyByTarget, StringComparer.OrdinalIgnoreCase));
        }

        public ExifCacheKey GetKeyForTarget(string targetPath)
        {
            if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));
            ExifCacheKey key;
            if (!_keyByTarget.TryGetValue(targetPath, out key))
                throw new ArgumentException("The target path is not part of this cache-key plan.", nameof(targetPath));
            return key;
        }

        public static ExifCacheKeyPlan Create(
            RawJpegAnalysisPlan analysisPlan,
            VolumeInfo volume,
            IEnumerable<ExifFileSnapshot> snapshots)
        {
            if (analysisPlan == null) throw new ArgumentNullException(nameof(analysisPlan));
            if (volume == null) throw new ArgumentNullException(nameof(volume));
            if (snapshots == null) throw new ArgumentNullException(nameof(snapshots));

            var snapshotByPath = snapshots.ToDictionary(
                snapshot => snapshot.FullPath,
                snapshot => snapshot,
                StringComparer.OrdinalIgnoreCase);
            var keyBySource = new Dictionary<string, ExifCacheKey>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in analysisPlan.AnalysisSources)
            {
                ExifFileSnapshot snapshot;
                if (!snapshotByPath.TryGetValue(source, out snapshot))
                    throw new ArgumentException("A snapshot is missing for an analysis source.", nameof(snapshots));
                keyBySource.Add(source, ExifCacheKey.Create(
                    volume,
                    snapshot.FullPath,
                    snapshot.FileSize,
                    snapshot.LastWriteTimeUtc));
            }

            var keyByTarget = new Dictionary<string, ExifCacheKey>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in analysisPlan.TargetPaths)
                keyByTarget.Add(target, keyBySource[analysisPlan.GetAnalysisSource(target)]);
            return new ExifCacheKeyPlan(keyByTarget);
        }
    }
}
