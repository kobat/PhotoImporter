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
        public const int EntriesSchemaVersion = 2;
        public const int ExtractionVersion = 1;
        internal const int LegacyEntriesSchemaVersion = 1;
        internal const string EntriesFileName = "entries.tsv";
        internal const string LegacyEntriesFileName = "entries.json";
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

        public bool TryOpen(
            VolumeInfo volume,
            out ExifCacheSession session,
            out string warning,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (volume == null) throw new ArgumentNullException(nameof(volume));
            cancellationToken.ThrowIfCancellationRequested();
            session = null;
            warning = null;

            Mutex mutex = null;
            var ownsMutex = false;
            try
            {
                mutex = new Mutex(false, CreateMutexName(_cacheRoot, volume.SerialNumber));
                try
                {
                    if (cancellationToken.CanBeCanceled)
                    {
                        var waitResult = WaitHandle.WaitAny(
                            new WaitHandle[] { mutex, cancellationToken.WaitHandle },
                            _lockTimeout);
                        ownsMutex = waitResult == 0;
                        if (waitResult == 1) throw new OperationCanceledException(cancellationToken);
                    }
                    else
                    {
                        ownsMutex = mutex.WaitOne(_lockTimeout);
                    }
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

                cancellationToken.ThrowIfCancellationRequested();

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
            foreach (var pattern in new[] { "entries.tsv.*.partial", "entries.json.*.partial" })
            {
                foreach (var path in Directory.EnumerateFiles(volumeFolder, pattern))
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
        }

        internal static bool IsCacheFailure(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException ||
            ex is SerializationException || ex is CryptographicException ||
            ex is DecoderFallbackException;
    }

    public sealed class ExifCacheSession : IDisposable
    {
        private readonly string _entriesPath;
        private readonly string _legacyEntriesPath;
        private readonly Mutex _mutex;
        private readonly Dictionary<CacheIdentity, CacheEntryData> _entries;
        private bool _dirty;
        private bool _disposed;

        private ExifCacheSession(
            string volumeFolder,
            Mutex mutex,
            Dictionary<CacheIdentity, CacheEntryData> entries,
            bool recoveredFromInvalidFile,
            bool needsSave)
        {
            _entriesPath = Path.Combine(volumeFolder, ExifCacheStore.EntriesFileName);
            _legacyEntriesPath = Path.Combine(volumeFolder, ExifCacheStore.LegacyEntriesFileName);
            _mutex = mutex;
            _entries = entries;
            RecoveredFromInvalidFile = recoveredFromInvalidFile;
            _dirty = needsSave;
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

            var entries = _entries.Values.OrderBy(entry => entry.ComparisonPath, StringComparer.Ordinal)
                .ThenBy(entry => entry.FileSize)
                .ThenBy(entry => entry.LastWriteTimeUtcTicks)
                .ToList();
            WriteAtomically(_entriesPath, entries);
            if (File.Exists(_legacyEntriesPath)) File.Delete(_legacyEntriesPath);
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
            var entriesPath = Path.Combine(volumeFolder, ExifCacheStore.EntriesFileName);
            var legacyEntriesPath = Path.Combine(volumeFolder, ExifCacheStore.LegacyEntriesFileName);
            var recovered = false;
            var needsSave = false;
            var entries = new Dictionary<CacheIdentity, CacheEntryData>();
            if (File.Exists(entriesPath))
            {
                try
                {
                    LoadTsv(entriesPath, entries);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                               ex is ArgumentException || ex is FormatException ||
                                               ex is DecoderFallbackException)
                {
                    recovered = true;
                    needsSave = true;
                    entries.Clear();
                }
            }
            else if (File.Exists(legacyEntriesPath))
            {
                try
                {
                    LoadLegacyJson(legacyEntriesPath, entries);
                    needsSave = true;
                }
                catch (Exception ex) when (ex is SerializationException || ex is IOException ||
                                               ex is UnauthorizedAccessException || ex is ArgumentException ||
                                               ex is FormatException || ex is DecoderFallbackException)
                {
                    recovered = true;
                    needsSave = true;
                    entries.Clear();
                }
            }

            if (File.Exists(entriesPath) && File.Exists(legacyEntriesPath)) needsSave = true;
            return new ExifCacheSession(volumeFolder, mutex, entries, recovered, needsSave);
        }

        private static readonly string[] ColumnNames =
        {
            "RelativePath", "FileSize", "LastWriteTimeUtc", "LastUsedUtcDate", "Status",
            "TakenDate", "Offset", "OffsetState", "CameraMake", "CameraModel", "Lens"
        };

        private const string DocumentMarker = "# PhotoImporter Exif Cache";
        private const string UtcDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        private const string UnspecifiedDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
        private const string DateFormat = "yyyy-MM-dd";

        private static void LoadTsv(string path, IDictionary<CacheIdentity, CacheEntryData> entries)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
            {
                string[] record;
                if (!TryReadTsvRecord(reader, out record) || record.Length != 3 ||
                    record[0] != DocumentMarker ||
                    record[1] != "SchemaVersion=" + ExifCacheStore.EntriesSchemaVersion.ToString(CultureInfo.InvariantCulture) ||
                    record[2] != "ExtractionVersion=" + ExifCacheStore.ExtractionVersion.ToString(CultureInfo.InvariantCulture))
                    throw new FormatException("The Exif cache header is invalid or incompatible.");

                if (!TryReadTsvRecord(reader, out record) || !record.SequenceEqual(ColumnNames, StringComparer.Ordinal))
                    throw new FormatException("The Exif cache column header is invalid.");

                while (TryReadTsvRecord(reader, out record))
                {
                    CacheEntryData entry;
                    if (!TryParseEntry(record, out entry) || !entry.IsValid()) continue;
                    entries[entry.Identity] = entry;
                }
            }
        }

        private static void LoadLegacyJson(string path, IDictionary<CacheIdentity, CacheEntryData> entries)
        {
            EntriesDocument document;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                document = (EntriesDocument)CreateLegacySerializer().ReadObject(stream);
            if (document == null ||
                document.SchemaVersion != ExifCacheStore.LegacyEntriesSchemaVersion ||
                document.ExtractionVersion != ExifCacheStore.ExtractionVersion ||
                document.Entries == null)
                throw new FormatException("The legacy Exif cache is invalid or incompatible.");

            foreach (var entry in document.Entries)
            {
                if (entry == null || !entry.IsValid()) continue;
                entry.ComparisonPath = entry.RelativePath.ToUpperInvariant();
                entries[entry.Identity] = entry;
            }
        }

        private static bool TryParseEntry(string[] fields, out CacheEntryData entry)
        {
            entry = null;
            if (fields.Length != ColumnNames.Length) return false;

            long fileSize;
            DateTime lastWriteTimeUtc;
            DateTime lastUsedUtcDate;
            PhotoMetadataReadStatus status;
            DateTime takenDate;
            TimeSpan offset;
            TakenDateOffsetState offsetState;
            if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fileSize) ||
                !DateTime.TryParseExact(fields[2], UtcDateTimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out lastWriteTimeUtc) ||
                !DateTime.TryParseExact(fields[3], DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out lastUsedUtcDate) ||
                !Enum.TryParse(fields[4], false, out status) ||
                !Enum.TryParse(fields[7], false, out offsetState)) return false;

            long? takenDateTicks = null;
            if (fields[5].Length != 0)
            {
                if (!DateTime.TryParseExact(fields[5], UnspecifiedDateTimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out takenDate)) return false;
                takenDateTicks = DateTime.SpecifyKind(takenDate, DateTimeKind.Unspecified).Ticks;
            }

            long? offsetTicks = null;
            if (fields[6].Length != 0)
            {
                if (!TimeSpan.TryParseExact(fields[6], "c", CultureInfo.InvariantCulture, out offset)) return false;
                offsetTicks = offset.Ticks;
            }

            entry = new CacheEntryData
            {
                RelativePath = fields[0],
                ComparisonPath = fields[0].ToUpperInvariant(),
                FileSize = fileSize,
                LastWriteTimeUtcTicks = lastWriteTimeUtc.Ticks,
                LastUsedUtcDateTicks = lastUsedUtcDate.Date.Ticks,
                Status = (int)status,
                TakenDateTicks = takenDateTicks,
                OffsetTicks = offsetTicks,
                OffsetState = (int)offsetState,
                CameraMake = EmptyToNull(fields[8]),
                CameraModel = EmptyToNull(fields[9]),
                Lens = EmptyToNull(fields[10])
            };
            return true;
        }

        private static void WriteAtomically(string destinationPath, IEnumerable<CacheEntryData> entries)
        {
            var temporaryPath = destinationPath + "." + System.Diagnostics.Process.GetCurrentProcess().Id +
                                "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 4096, true))
                    {
                        writer.NewLine = "\r\n";
                        writer.WriteLine(string.Join("\t", new[]
                        {
                            DocumentMarker,
                            "SchemaVersion=" + ExifCacheStore.EntriesSchemaVersion.ToString(CultureInfo.InvariantCulture),
                            "ExtractionVersion=" + ExifCacheStore.ExtractionVersion.ToString(CultureInfo.InvariantCulture)
                        }));
                        writer.WriteLine(string.Join("\t", ColumnNames));
                        foreach (var entry in entries) WriteEntry(writer, entry);
                        writer.Flush();
                    }
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

        private static void WriteEntry(TextWriter writer, CacheEntryData entry)
        {
            var fields = new[]
            {
                entry.RelativePath,
                entry.FileSize.ToString(CultureInfo.InvariantCulture),
                new DateTime(entry.LastWriteTimeUtcTicks, DateTimeKind.Utc).ToString(UtcDateTimeFormat, CultureInfo.InvariantCulture),
                new DateTime(entry.LastUsedUtcDateTicks, DateTimeKind.Utc).ToString(DateFormat, CultureInfo.InvariantCulture),
                ((PhotoMetadataReadStatus)entry.Status).ToString(),
                entry.TakenDateTicks.HasValue
                    ? new DateTime(entry.TakenDateTicks.Value, DateTimeKind.Unspecified).ToString(UnspecifiedDateTimeFormat, CultureInfo.InvariantCulture)
                    : string.Empty,
                entry.OffsetTicks.HasValue
                    ? TimeSpan.FromTicks(entry.OffsetTicks.Value).ToString("c", CultureInfo.InvariantCulture)
                    : string.Empty,
                ((TakenDateOffsetState)entry.OffsetState).ToString(),
                entry.CameraMake ?? string.Empty,
                entry.CameraModel ?? string.Empty,
                entry.Lens ?? string.Empty
            };
            writer.WriteLine(string.Join("\t", fields.Select(EscapeTsvField)));
        }

        private static string EscapeTsvField(string value)
        {
            if (value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static bool TryReadTsvRecord(TextReader reader, out string[] record)
        {
            var fields = new List<string>();
            var value = new StringBuilder();
            var anyCharacters = false;
            var atFieldStart = true;
            var inQuotes = false;
            var afterQuote = false;

            while (true)
            {
                var next = reader.Read();
                if (next < 0)
                {
                    if (inQuotes) throw new FormatException("The Exif cache contains an unterminated quoted field.");
                    if (!anyCharacters && fields.Count == 0 && value.Length == 0)
                    {
                        record = null;
                        return false;
                    }
                    fields.Add(value.ToString());
                    record = fields.ToArray();
                    return true;
                }

                anyCharacters = true;
                var ch = (char)next;
                if (inQuotes)
                {
                    if (ch != '"')
                    {
                        value.Append(ch);
                        continue;
                    }
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        value.Append('"');
                        continue;
                    }
                    inQuotes = false;
                    afterQuote = true;
                    continue;
                }

                if (afterQuote)
                {
                    if (ch == '\t')
                    {
                        fields.Add(value.ToString());
                        value.Clear();
                        atFieldStart = true;
                        afterQuote = false;
                        continue;
                    }
                    if (ch == '\r' || ch == '\n')
                    {
                        if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                        fields.Add(value.ToString());
                        record = fields.ToArray();
                        return true;
                    }
                    throw new FormatException("The Exif cache contains characters after a quoted field.");
                }

                if (atFieldStart && ch == '"')
                {
                    inQuotes = true;
                    atFieldStart = false;
                }
                else if (ch == '"')
                {
                    throw new FormatException("The Exif cache contains an unexpected quote.");
                }
                else if (ch == '\t')
                {
                    fields.Add(value.ToString());
                    value.Clear();
                    atFieldStart = true;
                }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                    fields.Add(value.ToString());
                    record = fields.ToArray();
                    return true;
                }
                else
                {
                    value.Append(ch);
                    atFieldStart = false;
                }
            }
        }

        private static string EmptyToNull(string value) => value.Length == 0 ? null : value;

        private static DataContractJsonSerializer CreateLegacySerializer() =>
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
