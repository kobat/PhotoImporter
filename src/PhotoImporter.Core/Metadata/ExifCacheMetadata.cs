using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PhotoImporter.Core.Metadata
{
    internal sealed class ExifCacheMetadataSnapshot
    {
        public string DisplayName { get; set; }
        public string VolumeLabel { get; set; }
        public string FileSystemName { get; set; }
        public DriveType? DriveType { get; set; }
        public ulong? TotalBytes { get; set; }
        public DateTime? FirstUsedUtcDate { get; set; }
        public DateTime? LastUsedUtcDate { get; set; }
        public int EntryCount { get; set; }
    }

    [DataContract]
    internal sealed class ExifCacheMetadataData
    {
        internal const int CurrentSchemaVersion = 1;
        private const string DateFormat = "yyyy-MM-dd";

        [DataMember(Order = 1)] public int SchemaVersion { get; set; }
        [DataMember(Order = 2, EmitDefaultValue = false)] public string DisplayName { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public string VolumeLabel { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public string FileSystemName { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public int? DriveTypeValue { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public ulong? TotalBytes { get; set; }
        [DataMember(Order = 7, EmitDefaultValue = false)] public string FirstUsedUtcDate { get; set; }
        [DataMember(Order = 8, EmitDefaultValue = false)] public string LastUsedUtcDate { get; set; }
        [DataMember(Order = 9)] public int EntryCount { get; set; }

        public static ExifCacheMetadataData CreateEmpty() => new ExifCacheMetadataData
        {
            SchemaVersion = CurrentSchemaVersion
        };

        public bool IsValid()
        {
            if (SchemaVersion != CurrentSchemaVersion || EntryCount < 0) return false;
            if (DriveTypeValue.HasValue && !Enum.IsDefined(typeof(DriveType), DriveTypeValue.Value)) return false;
            DateTime? first;
            DateTime? last;
            if (!TryParseOptionalUtcDate(FirstUsedUtcDate, out first) ||
                !TryParseOptionalUtcDate(LastUsedUtcDate, out last)) return false;
            return !first.HasValue || !last.HasValue || first.Value <= last.Value;
        }

        public bool UpdateVolumeInfo(VolumeInfo volume)
        {
            if (volume == null) throw new ArgumentNullException(nameof(volume));
            var changed = false;
            var volumeLabel = EmptyToNull(volume.Label);
            if (!string.Equals(VolumeLabel, volumeLabel, StringComparison.Ordinal))
            {
                VolumeLabel = volumeLabel;
                changed = true;
            }
            var fileSystemName = EmptyToNull(volume.FileSystemName);
            if (!string.Equals(FileSystemName, fileSystemName, StringComparison.Ordinal))
            {
                FileSystemName = fileSystemName;
                changed = true;
            }
            var driveType = (int)volume.DriveType;
            if (DriveTypeValue != driveType)
            {
                DriveTypeValue = driveType;
                changed = true;
            }
            if (TotalBytes != volume.TotalBytes)
            {
                TotalBytes = volume.TotalBytes;
                changed = true;
            }
            return changed;
        }

        public bool Touch(long utcDateTicks)
        {
            var changed = false;
            var utcDate = FormatUtcDate(utcDateTicks);
            if (FirstUsedUtcDate == null)
            {
                FirstUsedUtcDate = utcDate;
                changed = true;
            }
            if (!string.Equals(LastUsedUtcDate, utcDate, StringComparison.Ordinal))
            {
                LastUsedUtcDate = utcDate;
                changed = true;
            }
            return changed;
        }

        public bool UpdateDerived(int entryCount, long? earliestEntryDateTicks, long? latestEntryDateTicks)
        {
            var changed = false;
            if (EntryCount != entryCount)
            {
                EntryCount = entryCount;
                changed = true;
            }
            if (FirstUsedUtcDate == null && earliestEntryDateTicks.HasValue)
            {
                FirstUsedUtcDate = FormatUtcDate(earliestEntryDateTicks.Value);
                changed = true;
            }
            var latestDate = latestEntryDateTicks.HasValue
                ? FormatUtcDate(latestEntryDateTicks.Value)
                : null;
            if (latestDate != null && !string.Equals(LastUsedUtcDate, latestDate, StringComparison.Ordinal))
            {
                LastUsedUtcDate = latestDate;
                changed = true;
            }
            return changed;
        }

        public ExifCacheMetadataSnapshot ToSnapshot() => new ExifCacheMetadataSnapshot
        {
            DisplayName = DisplayName,
            VolumeLabel = VolumeLabel,
            FileSystemName = FileSystemName,
            DriveType = DriveTypeValue.HasValue ? (DriveType?)((DriveType)DriveTypeValue.Value) : null,
            TotalBytes = TotalBytes,
            FirstUsedUtcDate = ParseUtcDate(FirstUsedUtcDate),
            LastUsedUtcDate = ParseUtcDate(LastUsedUtcDate),
            EntryCount = EntryCount
        };

        private static string EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string FormatUtcDate(long ticks) =>
            new DateTime(ticks, DateTimeKind.Utc).ToString(DateFormat, CultureInfo.InvariantCulture);

        private static bool TryParseOptionalUtcDate(string value, out DateTime? date)
        {
            date = null;
            if (string.IsNullOrEmpty(value)) return true;
            DateTime parsed;
            if (!DateTime.TryParseExact(
                value,
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed)) return false;
            date = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
            return true;
        }

        private static DateTime? ParseUtcDate(string value)
        {
            DateTime? date;
            return TryParseOptionalUtcDate(value, out date) ? date : null;
        }
    }

    internal static class ExifCacheMetadataFile
    {
        public static ExifCacheMetadataData Load(string path, out bool needsSave)
        {
            needsSave = false;
            if (!File.Exists(path))
            {
                needsSave = true;
                return ExifCacheMetadataData.CreateEmpty();
            }

            try
            {
                ExifCacheMetadataData metadata;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    metadata = (ExifCacheMetadataData)CreateSerializer().ReadObject(stream);
                if (metadata != null && metadata.IsValid()) return metadata;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                           ex is SerializationException || ex is ArgumentException)
            {
            }

            needsSave = true;
            return ExifCacheMetadataData.CreateEmpty();
        }

        public static void WriteAtomically(string destinationPath, ExifCacheMetadataData metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            var temporaryPath = destinationPath + "." + System.Diagnostics.Process.GetCurrentProcess().Id +
                                "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CreateSerializer().WriteObject(stream, metadata);
                    stream.Flush(true);
                }
                if (File.Exists(destinationPath))
                    File.Replace(temporaryPath, destinationPath, null);
                else
                    File.Move(temporaryPath, destinationPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static DataContractJsonSerializer CreateSerializer() =>
            new DataContractJsonSerializer(typeof(ExifCacheMetadataData));
    }
}
