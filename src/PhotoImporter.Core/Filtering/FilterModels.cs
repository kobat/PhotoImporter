using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PhotoImporter.Core.Filtering
{
    public enum FilterField
    {
        FileType,
        Extension,
        CopyStatus,
        ExifReadStatus,
        OriginalName,
        FileName,
        SourceRelativeDirectory,
        ModifiedDate,
        FileSize,
        Protected,
        Sequence,
        TakenDate,
        TakenDateLocal,
        TakenDateInTimeZone,
        CameraMake,
        CameraModel,
        CameraSerial,
        Lens,
        Width,
        Height,
        ExifWidth,
        ExifHeight,
        Orientation,
        Aperture,
        ShutterSpeed,
        ExposureTime,
        Iso,
        FocalLength,
        FocalLength35mm,
        Rating,
        HasGps,
        GpsLatitude,
        GpsLongitude,
        GpsAltitude
    }

    public enum FilterValueType
    {
        String,
        Number,
        DateTime,
        Boolean,
        Choice
    }

    public sealed class FilterFieldDefinition
    {
        private static readonly IReadOnlyDictionary<FilterField, FilterFieldDefinition> Definitions =
            CreateDefinitions().ToDictionary(item => item.Field);

        private FilterFieldDefinition(
            FilterField field,
            FilterValueType valueType,
            Type valueClrType,
            bool requiresExif,
            bool canBeUnknown)
        {
            Field = field;
            ValueType = valueType;
            ValueClrType = valueClrType;
            RequiresExif = requiresExif;
            CanBeUnknown = canBeUnknown;
        }

        public FilterField Field { get; }
        public FilterValueType ValueType { get; }
        public Type ValueClrType { get; }
        public bool RequiresExif { get; }
        public bool CanBeUnknown { get; }

        public static IReadOnlyCollection<FilterFieldDefinition> All =>
            new ReadOnlyCollection<FilterFieldDefinition>(Definitions.Values.OrderBy(item => item.Field).ToList());

        public static FilterFieldDefinition Get(FilterField field)
        {
            FilterFieldDefinition definition;
            if (!Definitions.TryGetValue(field, out definition))
                throw new ArgumentOutOfRangeException(nameof(field));
            return definition;
        }

        private static IEnumerable<FilterFieldDefinition> CreateDefinitions()
        {
            yield return Choice(FilterField.FileType, typeof(Metadata.PhotoFileType), false, true);
            yield return String(FilterField.Extension, false, false);
            yield return Choice(FilterField.CopyStatus, typeof(FilterCopyStatus), false, false);
            yield return Choice(FilterField.ExifReadStatus, typeof(FilterExifReadStatus), true, false);
            yield return String(FilterField.OriginalName, false, true);
            yield return String(FilterField.FileName, false, true);
            yield return String(FilterField.SourceRelativeDirectory, false, true);
            yield return Date(FilterField.ModifiedDate, false, true);
            yield return Number(FilterField.FileSize, false, true);
            yield return Boolean(FilterField.Protected, false, false);
            yield return Number(FilterField.Sequence, false, true);
            yield return Date(FilterField.TakenDate, true, true);
            yield return Date(FilterField.TakenDateLocal, true, true);
            yield return Date(FilterField.TakenDateInTimeZone, true, true);
            yield return String(FilterField.CameraMake, true, true);
            yield return String(FilterField.CameraModel, true, true);
            yield return String(FilterField.CameraSerial, true, true);
            yield return String(FilterField.Lens, true, true);
            yield return Number(FilterField.Width, true, true);
            yield return Number(FilterField.Height, true, true);
            yield return Number(FilterField.ExifWidth, true, true);
            yield return Number(FilterField.ExifHeight, true, true);
            yield return Number(FilterField.Orientation, true, true);
            yield return Number(FilterField.Aperture, true, true);
            yield return Number(FilterField.ShutterSpeed, true, true);
            yield return Number(FilterField.ExposureTime, true, true);
            yield return Number(FilterField.Iso, true, true);
            yield return Number(FilterField.FocalLength, true, true);
            yield return Number(FilterField.FocalLength35mm, true, true);
            yield return Number(FilterField.Rating, true, true);
            yield return Boolean(FilterField.HasGps, true, false);
            yield return Number(FilterField.GpsLatitude, true, true);
            yield return Number(FilterField.GpsLongitude, true, true);
            yield return Number(FilterField.GpsAltitude, true, true);
        }

        private static FilterFieldDefinition String(FilterField field, bool exif, bool unknown) =>
            new FilterFieldDefinition(field, FilterValueType.String, typeof(string), exif, unknown);
        private static FilterFieldDefinition Number(FilterField field, bool exif, bool unknown) =>
            new FilterFieldDefinition(field, FilterValueType.Number, typeof(decimal), exif, unknown);
        private static FilterFieldDefinition Date(FilterField field, bool exif, bool unknown) =>
            new FilterFieldDefinition(field, FilterValueType.DateTime, typeof(DateTime), exif, unknown);
        private static FilterFieldDefinition Boolean(FilterField field, bool exif, bool unknown) =>
            new FilterFieldDefinition(field, FilterValueType.Boolean, typeof(bool), exif, unknown);
        private static FilterFieldDefinition Choice(FilterField field, Type type, bool exif, bool unknown) =>
            new FilterFieldDefinition(field, FilterValueType.Choice, type, exif, unknown);
    }

    public enum FilterCopyStatus
    {
        NotImported,
        Overwrite,
        Imported,
        Conflict,
        ScanError,
        CopyError
    }

    public enum FilterExifReadStatus
    {
        Unread,
        Read,
        NoMetadata,
        Unsupported,
        ReadError
    }

    public enum FilterUnknownReason
    {
        Missing,
        NoMetadata,
        Unsupported,
        ReadError,
        ScanError
    }

    internal enum FilterValueState
    {
        Known,
        Unknown,
        ExifUnread
    }

    internal struct FilterValue
    {
        private FilterValue(FilterValueState state, object value, FilterUnknownReason? reason)
        {
            State = state;
            Value = value;
            UnknownReason = reason;
        }

        public FilterValueState State { get; }
        public object Value { get; }
        public FilterUnknownReason? UnknownReason { get; }

        public static FilterValue Known(object value) => new FilterValue(FilterValueState.Known, value, null);
        public static FilterValue Unknown(FilterUnknownReason reason) => new FilterValue(FilterValueState.Unknown, null, reason);
        public static FilterValue ExifUnread() => new FilterValue(FilterValueState.ExifUnread, null, null);
    }

    internal struct SequenceFilterValue
    {
        public SequenceFilterValue(int? number) { Number = number; }
        public int? Number { get; }
    }
}
