using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoImporter.Core.Metadata
{
    public enum TakenDateOffsetState
    {
        Missing,
        Valid,
        Invalid
    }

    public sealed class PhotoMetadata
    {
        public static readonly PhotoMetadata Empty = new PhotoMetadata(null, null, TakenDateOffsetState.Missing, null, null, null);

        public PhotoMetadata(
            DateTime? takenDate,
            TimeSpan? takenDateOffset,
            TakenDateOffsetState offsetState,
            string cameraMake,
            string cameraModel,
            string lens)
        {
            if (takenDate.HasValue)
                takenDate = DateTime.SpecifyKind(takenDate.Value, DateTimeKind.Unspecified);
            if (offsetState == TakenDateOffsetState.Valid && !takenDateOffset.HasValue)
                throw new ArgumentException("A valid offset requires a value.", nameof(takenDateOffset));
            TakenDate = takenDate;
            TakenDateOffset = takenDateOffset;
            OffsetState = offsetState;
            CameraMake = cameraMake;
            CameraModel = cameraModel;
            Lens = lens;
        }

        public DateTime? TakenDate { get; }
        public TimeSpan? TakenDateOffset { get; }
        public TakenDateOffsetState OffsetState { get; }
        public string CameraMake { get; }
        public string CameraModel { get; }
        public string Lens { get; }

        public bool HasValues => TakenDate.HasValue || TakenDateOffset.HasValue ||
                                 CameraMake != null || CameraModel != null || Lens != null;
    }

    public interface IPhotoMetadataReader
    {
        PhotoMetadataReadResult Read(string path);
    }

    public sealed class PhotoMetadataReader : IPhotoMetadataReader
    {
        private static readonly Regex OffsetPattern = new Regex(
            @"^(?<sign>[+-])(?<hour>[0-9]{2}):(?<minute>[0-9]{2})$",
            RegexOptions.CultureInvariant);

        public PhotoMetadataReadResult Read(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            try
            {
                var directories = ImageMetadataReader.ReadMetadata(path).ToList();
                var metadata = Extract(directories);
                return metadata.HasValues
                    ? PhotoMetadataReadResult.Success(metadata)
                    : PhotoMetadataReadResult.NoMetadata();
            }
            catch (ImageProcessingException)
            {
                return PhotoMetadataReadResult.Unsupported();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return PhotoMetadataReadResult.ReadError(ex);
            }
        }

        internal static PhotoMetadata Extract(IEnumerable<MetadataExtractor.Directory> directories)
        {
            if (directories == null) throw new ArgumentNullException(nameof(directories));

            var directoryList = directories.ToList();
            var subIfds = directoryList.OfType<ExifSubIfdDirectory>().ToList();
            var ifd0 = directoryList.OfType<ExifIfd0Directory>().FirstOrDefault();

            DateTime? takenDate = null;
            ExifSubIfdDirectory takenDateSubIfd = null;
            foreach (var subIfd in subIfds)
            {
                DateTime takenDateValue;
                if (!subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out takenDateValue))
                    continue;

                takenDate = DateTime.SpecifyKind(takenDateValue, DateTimeKind.Unspecified);
                takenDateSubIfd = subIfd;
                break;
            }

            var offsetText = takenDateSubIfd?.GetString(ExifDirectoryBase.TagTimeZoneOriginal);
            TimeSpan? offset;
            var offsetState = ParseOffset(offsetText, out offset);

            var lens = subIfds
                .Select(subIfd => Clean(subIfd.GetString(ExifDirectoryBase.TagLensModel)))
                .FirstOrDefault(value => value != null);

            return new PhotoMetadata(
                takenDate,
                offset,
                offsetState,
                Clean(ifd0?.GetString(ExifDirectoryBase.TagMake)),
                Clean(ifd0?.GetString(ExifDirectoryBase.TagModel)),
                lens);
        }

        private static TakenDateOffsetState ParseOffset(string value, out TimeSpan? offset)
        {
            offset = null;
            if (string.IsNullOrWhiteSpace(value)) return TakenDateOffsetState.Missing;

            var match = OffsetPattern.Match(value.Trim());
            int hours;
            int minutes;
            if (!match.Success ||
                !int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
                !int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
                hours > 14 || minutes > 59 || (hours == 14 && minutes != 0))
                return TakenDateOffsetState.Invalid;

            var parsed = new TimeSpan(hours, minutes, 0);
            offset = match.Groups["sign"].Value == "-" ? -parsed : parsed;
            return TakenDateOffsetState.Valid;
        }

        private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
