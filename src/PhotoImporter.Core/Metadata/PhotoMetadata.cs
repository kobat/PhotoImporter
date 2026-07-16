using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Gif;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Xmp;

namespace PhotoImporter.Core.Metadata
{
    public struct ExifRational : IEquatable<ExifRational>
    {
        public ExifRational(long numerator, long denominator)
        {
            if (denominator == 0) throw new ArgumentOutOfRangeException(nameof(denominator));
            if (denominator == long.MinValue || (denominator < 0 && numerator == long.MinValue))
                throw new ArgumentOutOfRangeException(nameof(denominator));
            if (denominator < 0)
            {
                numerator = -numerator;
                denominator = -denominator;
            }
            var divisor = GreatestCommonDivisor(numerator, denominator);
            Numerator = numerator / divisor;
            Denominator = denominator / divisor;
        }

        public long Numerator { get; }
        public long Denominator { get; }
        public decimal ToDecimal() => (decimal)Numerator / Denominator;

        public bool Equals(ExifRational other) =>
            Numerator == other.Numerator && Denominator == other.Denominator;
        public override bool Equals(object obj) => obj is ExifRational && Equals((ExifRational)obj);
        public override int GetHashCode() => unchecked((Numerator.GetHashCode() * 397) ^ Denominator.GetHashCode());
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture, "{0}/{1}", Numerator, Denominator);

        private static long GreatestCommonDivisor(long left, long right)
        {
            var unsignedLeft = Magnitude(left);
            var unsignedRight = Magnitude(right);
            while (unsignedRight != 0)
            {
                var remainder = unsignedLeft % unsignedRight;
                unsignedLeft = unsignedRight;
                unsignedRight = remainder;
            }
            return unsignedLeft == 0 ? 1 : (long)unsignedLeft;
        }

        private static ulong Magnitude(long value) => value < 0
            ? (ulong)(-(value + 1)) + 1
            : (ulong)value;
    }

    public enum TakenDateOffsetState
    {
        Missing,
        Valid,
        Invalid
    }

    public sealed class PhotoMetadata
    {
        public static readonly PhotoMetadata Empty = new PhotoMetadata(
            null, null, TakenDateOffsetState.Missing, null, null, null);

        public PhotoMetadata(
            DateTime? takenDate,
            TimeSpan? takenDateOffset,
            TakenDateOffsetState offsetState,
            string cameraMake,
            string cameraModel,
            string lens,
            string cameraSerial = null,
            int? decodedWidth = null,
            int? decodedHeight = null,
            int? exifWidth = null,
            int? exifHeight = null,
            int? orientation = null,
            decimal? fNumber = null,
            ExifRational? exposureTime = null,
            int? iso = null,
            decimal? focalLength = null,
            int? focalLength35mm = null,
            int? rating = null,
            decimal? gpsLatitude = null,
            decimal? gpsLongitude = null,
            decimal? gpsAltitude = null)
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
            CameraSerial = cameraSerial;
            DecodedWidth = PositiveOrNull(decodedWidth);
            DecodedHeight = PositiveOrNull(decodedHeight);
            ExifWidth = PositiveOrNull(exifWidth);
            ExifHeight = PositiveOrNull(exifHeight);
            Orientation = orientation >= 1 && orientation <= 8 ? orientation : null;
            FNumber = PositiveOrNull(fNumber);
            ExposureTime = exposureTime.HasValue && exposureTime.Value.Numerator > 0
                ? exposureTime
                : null;
            Iso = PositiveOrNull(iso);
            FocalLength = PositiveOrNull(focalLength);
            FocalLength35mm = PositiveOrNull(focalLength35mm);
            Rating = rating >= -1 && rating <= 5 && rating != 0 ? rating : null;

            var validGps = gpsLatitude >= -90m && gpsLatitude <= 90m &&
                           gpsLongitude >= -180m && gpsLongitude <= 180m &&
                           !(gpsLatitude == 0m && gpsLongitude == 0m);
            GpsLatitude = validGps ? gpsLatitude : null;
            GpsLongitude = validGps ? gpsLongitude : null;
            GpsAltitude = validGps ? gpsAltitude : null;
        }

        public DateTime? TakenDate { get; }
        public TimeSpan? TakenDateOffset { get; }
        public TakenDateOffsetState OffsetState { get; }
        public string CameraMake { get; }
        public string CameraModel { get; }
        public string Lens { get; }
        public string CameraSerial { get; }
        public int? DecodedWidth { get; }
        public int? DecodedHeight { get; }
        public int? ExifWidth { get; }
        public int? ExifHeight { get; }
        public int? Orientation { get; }
        public decimal? FNumber { get; }
        public ExifRational? ExposureTime { get; }
        public int? Iso { get; }
        public decimal? FocalLength { get; }
        public int? FocalLength35mm { get; }
        public int? Rating { get; }
        public decimal? GpsLatitude { get; }
        public decimal? GpsLongitude { get; }
        public decimal? GpsAltitude { get; }
        public bool HasGps => GpsLatitude.HasValue && GpsLongitude.HasValue;

        public bool HasValues => TakenDate.HasValue || TakenDateOffset.HasValue ||
                                 CameraMake != null || CameraModel != null || Lens != null || CameraSerial != null ||
                                 DecodedWidth.HasValue || DecodedHeight.HasValue || ExifWidth.HasValue ||
                                 ExifHeight.HasValue || Orientation.HasValue || FNumber.HasValue ||
                                 ExposureTime.HasValue || Iso.HasValue || FocalLength.HasValue ||
                                 FocalLength35mm.HasValue || Rating.HasValue || HasGps;

        private static int? PositiveOrNull(int? value) => value > 0 ? value : null;
        private static decimal? PositiveOrNull(decimal? value) => value > 0 ? value : null;
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

            if (takenDate.HasValue && takenDateSubIfd != null)
                takenDate = AddSubseconds(takenDate.Value,
                    takenDateSubIfd.GetString(ExifDirectoryBase.TagSubsecondTimeOriginal));

            var lens = subIfds
                .Select(subIfd => Clean(subIfd.GetString(ExifDirectoryBase.TagLensModel)))
                .FirstOrDefault(value => value != null);

            int? decodedWidth;
            int? decodedHeight;
            ExtractDecodedDimensions(directoryList, out decodedWidth, out decodedHeight);
            int? exifWidth;
            int? exifHeight;
            ExtractExifDimensions(subIfds, out exifWidth, out exifHeight);

            var gps = directoryList.OfType<GpsDirectory>().FirstOrDefault();
            decimal? latitude;
            decimal? longitude;
            decimal? altitude;
            ExtractGps(gps, out latitude, out longitude, out altitude);

            return new PhotoMetadata(
                takenDate,
                offset,
                offsetState,
                Clean(ifd0?.GetString(ExifDirectoryBase.TagMake)),
                Clean(ifd0?.GetString(ExifDirectoryBase.TagModel)),
                lens,
                ExtractCameraSerial(subIfds, directoryList),
                decodedWidth,
                decodedHeight,
                exifWidth,
                exifHeight,
                GetPositiveInt(ifd0, ExifDirectoryBase.TagOrientation),
                GetPositiveDecimal(subIfds, ExifDirectoryBase.TagFNumber),
                GetPositiveRational(subIfds, ExifDirectoryBase.TagExposureTime),
                GetPositiveInt(subIfds, ExifDirectoryBase.TagIsoEquivalent),
                GetPositiveDecimal(subIfds, ExifDirectoryBase.TagFocalLength),
                GetPositiveInt(subIfds, ExifDirectoryBase.Tag35MMFilmEquivFocalLength),
                ExtractRating(ifd0, directoryList.OfType<XmpDirectory>()),
                latitude,
                longitude,
                altitude);
        }

        private static DateTime AddSubseconds(DateTime value, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return value;
            var digits = new string(text.Trim().TakeWhile(char.IsDigit).Take(7).ToArray());
            if (digits.Length == 0) return value;
            int ticks;
            if (!int.TryParse(digits.PadRight(7, '0'), NumberStyles.None, CultureInfo.InvariantCulture, out ticks))
                return value;
            try { return value.AddTicks(ticks); }
            catch (ArgumentOutOfRangeException) { return value; }
        }

        private static void ExtractDecodedDimensions(
            IEnumerable<MetadataExtractor.Directory> directories, out int? width, out int? height)
        {
            width = null;
            height = null;
            foreach (var jpeg in directories.OfType<JpegDirectory>())
            {
                var candidateWidth = GetPositiveInt(jpeg, JpegDirectory.TagImageWidth);
                var candidateHeight = GetPositiveInt(jpeg, JpegDirectory.TagImageHeight);
                if (!candidateWidth.HasValue || !candidateHeight.HasValue) continue;
                width = candidateWidth;
                height = candidateHeight;
                return;
            }
            foreach (var png in directories.OfType<PngDirectory>())
            {
                var candidateWidth = GetPositiveInt(png, PngDirectory.TagImageWidth);
                var candidateHeight = GetPositiveInt(png, PngDirectory.TagImageHeight);
                if (!candidateWidth.HasValue || !candidateHeight.HasValue) continue;
                width = candidateWidth;
                height = candidateHeight;
                return;
            }
            foreach (var heic in directories.OfType<HeicImagePropertiesDirectory>())
            {
                var candidateWidth = GetPositiveInt(heic, HeicImagePropertiesDirectory.TagImageWidth);
                var candidateHeight = GetPositiveInt(heic, HeicImagePropertiesDirectory.TagImageHeight);
                if (!candidateWidth.HasValue || !candidateHeight.HasValue) continue;
                width = candidateWidth;
                height = candidateHeight;
                return;
            }
            foreach (var webp in directories.OfType<WebPDirectory>())
            {
                var candidateWidth = GetPositiveInt(webp, WebPDirectory.TagImageWidth);
                var candidateHeight = GetPositiveInt(webp, WebPDirectory.TagImageHeight);
                if (!candidateWidth.HasValue || !candidateHeight.HasValue) continue;
                width = candidateWidth;
                height = candidateHeight;
                return;
            }
            foreach (var gif in directories.OfType<GifHeaderDirectory>())
            {
                var candidateWidth = GetPositiveInt(gif, GifHeaderDirectory.TagImageWidth);
                var candidateHeight = GetPositiveInt(gif, GifHeaderDirectory.TagImageHeight);
                if (!candidateWidth.HasValue || !candidateHeight.HasValue) continue;
                width = candidateWidth;
                height = candidateHeight;
                return;
            }
        }

        private static void ExtractExifDimensions(
            IEnumerable<ExifSubIfdDirectory> directories, out int? width, out int? height)
        {
            width = null;
            height = null;
            foreach (var directory in directories)
            {
                var candidateWidth = GetPositiveInt(directory, ExifDirectoryBase.TagExifImageWidth);
                var candidateHeight = GetPositiveInt(directory, ExifDirectoryBase.TagExifImageHeight);
                if (!candidateWidth.HasValue || !candidateHeight.HasValue) continue;
                width = candidateWidth;
                height = candidateHeight;
                return;
            }
        }

        private static void ExtractGps(GpsDirectory directory, out decimal? latitude, out decimal? longitude, out decimal? altitude)
        {
            latitude = null;
            longitude = null;
            altitude = null;
            if (directory == null) return;
            var latitudeRef = Clean(directory.GetString(GpsDirectory.TagLatitudeRef));
            var longitudeRef = Clean(directory.GetString(GpsDirectory.TagLongitudeRef));
            if ((!string.Equals(latitudeRef, "N", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(latitudeRef, "S", StringComparison.OrdinalIgnoreCase)) ||
                (!string.Equals(longitudeRef, "E", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(longitudeRef, "W", StringComparison.OrdinalIgnoreCase))) return;
            GeoLocation location;
            if (!directory.TryGetGeoLocation(out location) || location == null ||
                double.IsNaN(location.Latitude) || double.IsNaN(location.Longitude) ||
                location.Latitude < -90 || location.Latitude > 90 ||
                location.Longitude < -180 || location.Longitude > 180 ||
                (location.Latitude == 0 && location.Longitude == 0)) return;
            latitude = (decimal)location.Latitude;
            longitude = (decimal)location.Longitude;

            Rational rational;
            if (!directory.TryGetRational(GpsDirectory.TagAltitude, out rational)) return;
            var value = rational.ToDecimal();
            try
            {
                if (directory.ContainsTag(GpsDirectory.TagAltitudeRef) &&
                    directory.GetByte(GpsDirectory.TagAltitudeRef) == 1) value = -value;
                altitude = value;
            }
            catch (MetadataException) { }
        }

        private static int? ExtractRating(ExifIfd0Directory ifd0, IEnumerable<XmpDirectory> xmpDirectories)
        {
            var rating = GetInt(ifd0, ExifDirectoryBase.TagRating);
            if (rating >= 1 && rating <= 5) return rating;

            foreach (var directory in xmpDirectories)
            {
                foreach (var pair in directory.GetXmpProperties())
                {
                    if (!pair.Key.EndsWith(":Rating", StringComparison.OrdinalIgnoreCase) &&
                        !pair.Key.EndsWith("/Rating", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(pair.Key, "xmp:Rating", StringComparison.OrdinalIgnoreCase)) continue;
                    int xmpRating;
                    if (!int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out xmpRating))
                        continue;
                    if (xmpRating == -1 || (xmpRating >= 1 && xmpRating <= 5)) return xmpRating;
                }
            }

            var percent = GetInt(ifd0, ExifDirectoryBase.TagRatingPercent);
            if (!percent.HasValue || percent <= 0) return null;
            if (percent <= 24) return 1;
            if (percent <= 49) return 2;
            if (percent <= 74) return 3;
            if (percent <= 98) return 4;
            return 5;
        }

        private static string FirstCleanString(IEnumerable<ExifSubIfdDirectory> directories, int tag) =>
            directories.Select(directory => Clean(directory.GetString(tag))).FirstOrDefault(value => value != null);

        private static string ExtractCameraSerial(
            IEnumerable<ExifSubIfdDirectory> subIfds,
            IEnumerable<MetadataExtractor.Directory> directories)
        {
            var bodySerial = FirstCleanString(subIfds, ExifDirectoryBase.TagBodySerialNumber);
            if (bodySerial != null) return bodySerial;

            foreach (var directory in directories.Where(item =>
                         item.GetType().Namespace != null &&
                         item.GetType().Namespace.IndexOf(".Makernotes", StringComparison.Ordinal) >= 0))
            {
                foreach (var tag in directory.Tags)
                {
                    if (!tag.HasName ||
                        (!string.Equals(tag.Name, "Serial Number", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(tag.Name, "Camera Serial Number", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(tag.Name, "Canon Serial Number", StringComparison.OrdinalIgnoreCase))) continue;
                    var value = Clean(directory.GetString(tag.Type));
                    if (value != null) return value;
                }
            }
            return null;
        }

        private static int? GetPositiveInt(IEnumerable<ExifSubIfdDirectory> directories, int tag) =>
            directories.Select(directory => GetPositiveInt(directory, tag)).FirstOrDefault(value => value.HasValue);

        private static decimal? GetPositiveDecimal(IEnumerable<ExifSubIfdDirectory> directories, int tag)
        {
            foreach (var directory in directories)
            {
                Rational rational;
                if (directory.TryGetRational(tag, out rational) && rational.Numerator > 0 && rational.Denominator > 0)
                    return rational.ToDecimal();
            }
            return null;
        }

        private static ExifRational? GetPositiveRational(IEnumerable<ExifSubIfdDirectory> directories, int tag)
        {
            foreach (var directory in directories)
            {
                Rational rational;
                if (directory.TryGetRational(tag, out rational) && rational.Numerator > 0 && rational.Denominator > 0)
                    return new ExifRational(rational.Numerator, rational.Denominator);
            }
            return null;
        }

        private static int? GetPositiveInt(MetadataExtractor.Directory directory, int tag)
        {
            var value = GetInt(directory, tag);
            return value > 0 ? value : null;
        }

        private static int? GetInt(MetadataExtractor.Directory directory, int tag)
        {
            if (directory == null || !directory.ContainsTag(tag)) return null;
            try { return directory.GetInt32(tag); }
            catch (MetadataException) { return null; }
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
