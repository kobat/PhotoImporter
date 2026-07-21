using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Templates;
using System;
using System.IO;

namespace PhotoImporter.Core.Filtering
{
    public sealed class FilterCandidate
    {
        public FilterCandidate(
            string originalName,
            DateTime? modifiedDate,
            long? fileSize,
            string sourceRelativeDirectory,
            bool? isProtected,
            int? sequenceNumber,
            FilterCopyStatus copyStatus,
            PhotoMetadataReadResult metadataResult = null,
            bool sequenceIsKnown = true)
        {
            if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
            OriginalName = originalName;
            ModifiedDate = modifiedDate;
            FileSize = fileSize;
            SourceRelativeDirectory = sourceRelativeDirectory;
            IsProtected = isProtected;
            SequenceNumber = sequenceNumber;
            CopyStatus = copyStatus;
            MetadataResult = metadataResult;
            SequenceIsKnown = sequenceIsKnown;
        }

        public string OriginalName { get; }
        public DateTime? ModifiedDate { get; }
        public long? FileSize { get; }
        public string SourceRelativeDirectory { get; }
        public bool? IsProtected { get; }
        public int? SequenceNumber { get; }
        public bool SequenceIsKnown { get; }
        public FilterCopyStatus CopyStatus { get; }
        public PhotoMetadataReadResult MetadataResult { get; }

        public FilterExifReadStatus ExifReadStatus
        {
            get
            {
                if (MetadataResult == null) return FilterExifReadStatus.Unread;
                switch (MetadataResult.Status)
                {
                    case PhotoMetadataReadStatus.Success: return FilterExifReadStatus.Read;
                    case PhotoMetadataReadStatus.NoMetadata: return FilterExifReadStatus.NoMetadata;
                    case PhotoMetadataReadStatus.Unsupported: return FilterExifReadStatus.Unsupported;
                    default: return FilterExifReadStatus.ReadError;
                }
            }
        }

        internal FilterValue GetValue(FilterField field, TemplateTimeZone timeZone = null)
        {
            switch (field)
            {
                case FilterField.FileType:
                    return OriginalName == null
                        ? UnknownFileValue()
                        : FilterValue.Known(PhotoFileClassifier.Classify(OriginalName));
                case FilterField.Extension:
                    return OriginalName == null
                        ? UnknownFileValue()
                        : FilterValue.Known(PhotoFileClassifier.NormalizeExtension(OriginalName));
                case FilterField.CopyStatus:
                    return FilterValue.Known(CopyStatus);
                case FilterField.ExifReadStatus:
                    return MetadataResult == null
                        ? FilterValue.ExifUnread()
                        : FilterValue.Known(ExifReadStatus);
                case FilterField.OriginalName:
                    return StringOrUnknown(OriginalName, false);
                case FilterField.FileName:
                    return StringOrUnknown(OriginalName == null ? null : Path.GetFileNameWithoutExtension(OriginalName), false);
                case FilterField.SourceRelativeDirectory:
                    return StringOrUnknown(SourceRelativeDirectory, false);
                case FilterField.ModifiedDate:
                    return ModifiedDate.HasValue ? FilterValue.Known(ModifiedDate.Value) : UnknownFileValue();
                case FilterField.FileSize:
                    return FileSize.HasValue ? FilterValue.Known((decimal)FileSize.Value) : UnknownFileValue();
                case FilterField.Protected:
                    return IsProtected.HasValue ? FilterValue.Known(IsProtected.Value) : UnknownFileValue();
                case FilterField.Sequence:
                    return !SequenceIsKnown
                        ? UnknownFileValue()
                        : FilterValue.Known(new SequenceFilterValue(SequenceNumber));
            }

            if (MetadataResult == null) return FilterValue.ExifUnread();
            var metadata = MetadataResult.Metadata;
            switch (field)
            {
                case FilterField.TakenDate:
                    return NullableDate(metadata.TakenDate);
                case FilterField.TakenDateLocal:
                    return NullableDate(ConvertTakenDate(metadata, null, true));
                case FilterField.TakenDateInTimeZone:
                    if (timeZone == null) throw new ArgumentNullException(nameof(timeZone));
                    return NullableDate(ConvertTakenDate(metadata, timeZone, false));
                case FilterField.CameraMake: return StringOrUnknown(metadata.CameraMake, true);
                case FilterField.CameraModel: return StringOrUnknown(metadata.CameraModel, true);
                case FilterField.CameraSerial: return StringOrUnknown(metadata.CameraSerial, true);
                case FilterField.Lens: return StringOrUnknown(metadata.Lens, true);
                case FilterField.Width:
                case FilterField.Height:
                    int? width;
                    int? height;
                    PhotoMetadataValues.GetOrientedDimensions(metadata, out width, out height);
                    return NullableNumber(field == FilterField.Width ? width : height);
                case FilterField.ExifWidth: return NullableNumber(metadata.ExifWidth);
                case FilterField.ExifHeight: return NullableNumber(metadata.ExifHeight);
                case FilterField.Orientation: return NullableNumber(metadata.Orientation);
                case FilterField.Aperture: return NullableNumber(metadata.FNumber);
                case FilterField.ShutterSpeed:
                case FilterField.ExposureTime:
                    return metadata.ExposureTime.HasValue
                        ? FilterValue.Known(metadata.ExposureTime.Value.ToDecimal())
                        : UnknownExifValue();
                case FilterField.Iso: return NullableNumber(metadata.Iso);
                case FilterField.FocalLength: return NullableNumber(metadata.FocalLength);
                case FilterField.FocalLength35mm: return NullableNumber(metadata.FocalLength35mm);
                case FilterField.Rating: return NullableNumber(metadata.Rating);
                case FilterField.HasGps: return FilterValue.Known(metadata.HasGps);
                case FilterField.GpsLatitude: return NullableNumber(metadata.GpsLatitude);
                case FilterField.GpsLongitude: return NullableNumber(metadata.GpsLongitude);
                case FilterField.GpsAltitude: return NullableNumber(metadata.GpsAltitude);
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }

        private FilterValue UnknownFileValue() => FilterValue.Unknown(FilterUnknownReason.ScanError);

        private FilterValue UnknownExifValue()
        {
            switch (MetadataResult.Status)
            {
                case PhotoMetadataReadStatus.NoMetadata: return FilterValue.Unknown(FilterUnknownReason.NoMetadata);
                case PhotoMetadataReadStatus.Unsupported: return FilterValue.Unknown(FilterUnknownReason.Unsupported);
                case PhotoMetadataReadStatus.ReadError: return FilterValue.Unknown(FilterUnknownReason.ReadError);
                default: return FilterValue.Unknown(FilterUnknownReason.Missing);
            }
        }

        private FilterValue StringOrUnknown(string value, bool exif) =>
            value == null ? (exif ? UnknownExifValue() : UnknownFileValue()) : FilterValue.Known(value);

        private FilterValue NullableDate(DateTime? value) =>
            value.HasValue ? FilterValue.Known(value.Value) : UnknownExifValue();

        private FilterValue NullableNumber(int? value) =>
            value.HasValue ? FilterValue.Known((decimal)value.Value) : UnknownExifValue();

        private FilterValue NullableNumber(decimal? value) =>
            value.HasValue ? FilterValue.Known(value.Value) : UnknownExifValue();

        private static DateTime? ConvertTakenDate(PhotoMetadata metadata, TemplateTimeZone zone, bool local)
        {
            if (!metadata.TakenDate.HasValue) return null;
            if (metadata.OffsetState != TakenDateOffsetState.Valid) return metadata.TakenDate.Value;
            var instant = new DateTimeOffset(metadata.TakenDate.Value, metadata.TakenDateOffset.Value);
            return local ? TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime : zone.Convert(instant);
        }

    }
}
