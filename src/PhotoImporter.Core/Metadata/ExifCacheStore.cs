using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PhotoImporter.Core.Metadata
{
    public sealed class ExifCacheStore
    {
        public const int EntriesSchemaVersion = 1;
        public const int ExtractionVersion = 1;
        private readonly string _cacheRoot;
        private readonly TimeSpan _lockTimeout;

        public ExifCacheStore(string cacheRoot, TimeSpan? lockTimeout = null)
        {
            if (string.IsNullOrWhiteSpace(cacheRoot))
                throw new ArgumentException("A cache root is required.", nameof(cacheRoot));
            var timeout = lockTimeout ?? TimeSpan.FromSeconds(10);
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(lockTimeout));

            _cacheRoot = Path.GetFullPath(cacheRoot);
            _lockTimeout = timeout;
        }

        public string CacheRoot => _cacheRoot;

        public bool TryOpen(VolumeInfo volume, out ExifCacheSession session, out string warning)
        {
            if (volume == null) throw new ArgumentNullException(nameof(volume));
            session = null;
            warning = null;

            Mutex mutex = null;
            var ownsMutex = false;
            try
            {
                mutex = new Mutex(false, CreateMutexName(_cacheRoot, volume.SerialNumber));
                try
                {
                    ownsMutex = mutex.WaitOne(_lockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    warning = "同じボリュームの Exif キャッシュを別の PhotoImporter が使用しています。キャッシュなしで続行します。";
                    return false;
                }

                var volumeFolder = Path.Combine(_cacheRoot, volume.SerialNumberHex);
                Directory.CreateDirectory(volumeFolder);
                CleanupPartialFiles(volumeFolder);
                session = ExifCacheSession.Load(volumeFolder, mutex);
                mutex = null;
                ownsMutex = false;
                return true;
            }
            catch (Exception ex) when (IsCacheFailure(ex))
            {
                warning = string.Format(
                    CultureInfo.CurrentCulture,
                    "Exif キャッシュを利用できません ({0}): {1} キャッシュなしで続行します。",
                    _cacheRoot,
                    ex.Message);
                return false;
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
                mutex?.Dispose();
            }
        }

        internal static string CreateMutexName(string cacheRoot, uint volumeSerialNumber)
        {
            var normalizedRoot = Path.GetFullPath(cacheRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            byte[] hash;
            using (var sha256 = SHA256.Create())
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedRoot));
            var rootHash = string.Concat(hash.Take(12).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            return string.Format(
                CultureInfo.InvariantCulture,
                "PhotoImporter.ExifCache.{0}.{1:X8}",
                rootHash,
                volumeSerialNumber);
        }

        private static void CleanupPartialFiles(string volumeFolder)
        {
            foreach (var path in Directory.EnumerateFiles(volumeFolder, "entries.json.*.partial"))
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private static bool IsCacheFailure(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException ||
            ex is SerializationException || ex is CryptographicException;
    }

    public sealed class ExifCacheSession : IDisposable
    {
        private readonly string _entriesPath;
        private readonly Mutex _mutex;
        private readonly Dictionary<CacheIdentity, CacheEntryData> _entries;
        private bool _dirty;
        private bool _disposed;

        private ExifCacheSession(
            string volumeFolder,
            Mutex mutex,
            Dictionary<CacheIdentity, CacheEntryData> entries,
            bool recoveredFromInvalidFile)
        {
            _entriesPath = Path.Combine(volumeFolder, "entries.json");
            _mutex = mutex;
            _entries = entries;
            RecoveredFromInvalidFile = recoveredFromInvalidFile;
            _dirty = recoveredFromInvalidFile;
        }

        public bool RecoveredFromInvalidFile { get; }
        public int Count => _entries.Count;

        public bool TryGet(ExifCacheKey key, DateTime utcNow, out PhotoMetadataReadResult result)
        {
            EnsureUsable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            ValidateUtc(utcNow, nameof(utcNow));

            CacheEntryData entry;
            if (!_entries.TryGetValue(CacheIdentity.From(key), out entry))
            {
                result = null;
                return false;
            }

            var todayTicks = utcNow.Date.Ticks;
            if (entry.LastUsedUtcDateTicks != todayTicks)
            {
                entry.LastUsedUtcDateTicks = todayTicks;
                _dirty = true;
            }
            result = entry.ToResult();
            return true;
        }

        public void Put(ExifCacheKey key, PhotoMetadataReadResult result, DateTime utcNow)
        {
            EnsureUsable();
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (result == null) throw new ArgumentNullException(nameof(result));
            ValidateUtc(utcNow, nameof(utcNow));
            if (!result.IsCacheable) return;

            _entries[CacheIdentity.From(key)] = CacheEntryData.From(key, result, utcNow.Date.Ticks);
            _dirty = true;
        }

        public void Save()
        {
            EnsureUsable();
            if (!_dirty) return;

            var document = new EntriesDocument
            {
                SchemaVersion = ExifCacheStore.EntriesSchemaVersion,
                ExtractionVersion = ExifCacheStore.ExtractionVersion,
                Entries = _entries.Values.OrderBy(entry => entry.ComparisonPath, StringComparer.Ordinal)
                    .ThenBy(entry => entry.FileSize)
                    .ThenBy(entry => entry.LastWriteTimeUtcTicks)
                    .ToList()
            };
            WriteAtomically(_entriesPath, document);
            _dirty = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                Save();
            }
            finally
            {
                _disposed = true;
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }

        internal static ExifCacheSession Load(string volumeFolder, Mutex mutex)
        {
            var entriesPath = Path.Combine(volumeFolder, "entries.json");
            var recovered = false;
            var entries = new Dictionary<CacheIdentity, CacheEntryData>();
            if (File.Exists(entriesPath))
            {
                try
                {
                    EntriesDocument document;
                    using (var stream = new FileStream(entriesPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        document = (EntriesDocument)CreateSerializer().ReadObject(stream);
                    if (document == null ||
                        document.SchemaVersion != ExifCacheStore.EntriesSchemaVersion ||
                        document.ExtractionVersion != ExifCacheStore.ExtractionVersion ||
                        document.Entries == null)
                    {
                        recovered = true;
                    }
                    else
                    {
                        foreach (var entry in document.Entries)
                        {
                            if (!entry.IsValid()) continue;
                            entries[entry.Identity] = entry;
                        }
                    }
                }
                catch (Exception ex) when (ex is SerializationException || ex is IOException ||
                                               ex is ArgumentException || ex is FormatException)
                {
                    recovered = true;
                    entries.Clear();
                }
            }
            return new ExifCacheSession(volumeFolder, mutex, entries, recovered);
        }

        private static void WriteAtomically(string destinationPath, EntriesDocument document)
        {
            var temporaryPath = destinationPath + "." + System.Diagnostics.Process.GetCurrentProcess().Id +
                                "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CreateSerializer().WriteObject(stream, document);
                    stream.Flush(true);
                }

                if (File.Exists(destinationPath))
                    File.Replace(temporaryPath, destinationPath, null);
                else
                    File.Move(temporaryPath, destinationPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private static DataContractJsonSerializer CreateSerializer() =>
            new DataContractJsonSerializer(typeof(EntriesDocument));

        private void EnsureUsable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ExifCacheSession));
        }

        private static void ValidateUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The time must be UTC.", parameterName);
        }
    }

    internal struct CacheIdentity : IEquatable<CacheIdentity>
    {
        public string ComparisonPath;
        public long FileSize;
        public long LastWriteTimeUtcTicks;

        public static CacheIdentity From(ExifCacheKey key) => new CacheIdentity
        {
            ComparisonPath = key.ComparisonPath,
            FileSize = key.FileSize,
            LastWriteTimeUtcTicks = key.LastWriteTimeUtcTicks
        };

        public bool Equals(CacheIdentity other) =>
            FileSize == other.FileSize &&
            LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks &&
            string.Equals(ComparisonPath, other.ComparisonPath, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is CacheIdentity && Equals((CacheIdentity)obj);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(ComparisonPath);
                hash = (hash * 397) ^ FileSize.GetHashCode();
                return (hash * 397) ^ LastWriteTimeUtcTicks.GetHashCode();
            }
        }
    }

    [DataContract]
    internal sealed class EntriesDocument
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; }
        [DataMember(Order = 2)] public int ExtractionVersion { get; set; }
        [DataMember(Order = 3)] public List<CacheEntryData> Entries { get; set; }
    }

    [DataContract]
    internal sealed class CacheEntryData
    {
        [DataMember(Order = 1)] public string RelativePath { get; set; }
        [DataMember(Order = 2)] public string ComparisonPath { get; set; }
        [DataMember(Order = 3)] public long FileSize { get; set; }
        [DataMember(Order = 4)] public long LastWriteTimeUtcTicks { get; set; }
        [DataMember(Order = 5)] public long LastUsedUtcDateTicks { get; set; }
        [DataMember(Order = 6)] public int Status { get; set; }
        [DataMember(Order = 7, EmitDefaultValue = false)] public long? TakenDateTicks { get; set; }
        [DataMember(Order = 8, EmitDefaultValue = false)] public long? OffsetTicks { get; set; }
        [DataMember(Order = 9)] public int OffsetState { get; set; }
        [DataMember(Order = 10, EmitDefaultValue = false)] public string CameraMake { get; set; }
        [DataMember(Order = 11, EmitDefaultValue = false)] public string CameraModel { get; set; }
        [DataMember(Order = 12, EmitDefaultValue = false)] public string Lens { get; set; }

        public CacheIdentity Identity => new CacheIdentity
        {
            ComparisonPath = ComparisonPath,
            FileSize = FileSize,
            LastWriteTimeUtcTicks = LastWriteTimeUtcTicks
        };

        public static CacheEntryData From(
            ExifCacheKey key,
            PhotoMetadataReadResult result,
            long lastUsedUtcDateTicks)
        {
            var metadata = result.Metadata;
            return new CacheEntryData
            {
                RelativePath = key.VolumeRelativePath,
                ComparisonPath = key.ComparisonPath,
                FileSize = key.FileSize,
                LastWriteTimeUtcTicks = key.LastWriteTimeUtcTicks,
                LastUsedUtcDateTicks = lastUsedUtcDateTicks,
                Status = (int)result.Status,
                TakenDateTicks = metadata.TakenDate?.Ticks,
                OffsetTicks = metadata.TakenDateOffset?.Ticks,
                OffsetState = (int)metadata.OffsetState,
                CameraMake = metadata.CameraMake,
                CameraModel = metadata.CameraModel,
                Lens = metadata.Lens
            };
        }

        public bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(RelativePath) || string.IsNullOrWhiteSpace(ComparisonPath) ||
                FileSize < 0 || LastWriteTimeUtcTicks < DateTime.MinValue.Ticks ||
                LastWriteTimeUtcTicks > DateTime.MaxValue.Ticks ||
                LastUsedUtcDateTicks < DateTime.MinValue.Ticks || LastUsedUtcDateTicks > DateTime.MaxValue.Ticks ||
                !Enum.IsDefined(typeof(PhotoMetadataReadStatus), Status) ||
                Status == (int)PhotoMetadataReadStatus.ReadError ||
                !Enum.IsDefined(typeof(TakenDateOffsetState), OffsetState)) return false;
            if (TakenDateTicks.HasValue &&
                (TakenDateTicks.Value < DateTime.MinValue.Ticks || TakenDateTicks.Value > DateTime.MaxValue.Ticks)) return false;
            if (OffsetTicks.HasValue && (OffsetTicks.Value < TimeSpan.MinValue.Ticks || OffsetTicks.Value > TimeSpan.MaxValue.Ticks)) return false;
            if ((TakenDateOffsetState)OffsetState == TakenDateOffsetState.Valid && !OffsetTicks.HasValue) return false;

            var metadata = CreateMetadata();
            return Status == (int)PhotoMetadataReadStatus.Success ? metadata.HasValues : !metadata.HasValues;
        }

        public PhotoMetadataReadResult ToResult()
        {
            var status = (PhotoMetadataReadStatus)Status;
            if (status == PhotoMetadataReadStatus.Success) return PhotoMetadataReadResult.Success(CreateMetadata());
            if (status == PhotoMetadataReadStatus.NoMetadata) return PhotoMetadataReadResult.NoMetadata();
            return PhotoMetadataReadResult.Unsupported();
        }

        private PhotoMetadata CreateMetadata() => new PhotoMetadata(
            TakenDateTicks.HasValue ? new DateTime(TakenDateTicks.Value, DateTimeKind.Unspecified) : (DateTime?)null,
            OffsetTicks.HasValue ? TimeSpan.FromTicks(OffsetTicks.Value) : (TimeSpan?)null,
            (TakenDateOffsetState)OffsetState,
            CameraMake,
            CameraModel,
            Lens);
    }
}
