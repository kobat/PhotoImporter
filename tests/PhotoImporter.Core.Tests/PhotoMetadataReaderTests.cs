using System;
using System.IO;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Xmp;
using PhotoImporter.Core.Metadata;
using XmpCore;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PhotoMetadataReaderTests
    {
        [Fact]
        public void StructuredResultsDistinguishCacheableAndTransientOutcomes()
        {
            var metadata = new PhotoMetadata(
                new DateTime(2026, 7, 14),
                null,
                TakenDateOffsetState.Missing,
                null,
                null,
                null);

            Assert.Equal(PhotoMetadataReadStatus.Success, PhotoMetadataReadResult.Success(metadata).Status);
            Assert.True(PhotoMetadataReadResult.NoMetadata().IsCacheable);
            Assert.True(PhotoMetadataReadResult.Unsupported().IsCacheable);

            var error = new IOException("read failed");
            var result = PhotoMetadataReadResult.ReadError(error);
            Assert.Equal(PhotoMetadataReadStatus.ReadError, result.Status);
            Assert.Same(error, result.Error);
            Assert.False(result.IsCacheable);
        }

        [Fact]
        public void MissingFileReturnsStructuredReadError()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");

            var result = new PhotoMetadataReader().Read(path);

            Assert.Equal(PhotoMetadataReadStatus.ReadError, result.Status);
            Assert.IsAssignableFrom<IOException>(result.Error);
            Assert.Same(PhotoMetadata.Empty, result.Metadata);
        }

        [Fact]
        public void ExtractsPhotoMetadataFromLaterSonyArwSubIfd()
        {
            var ifd0 = new ExifIfd0Directory();
            ifd0.Set(ExifDirectoryBase.TagMake, "SONY");
            ifd0.Set(ExifDirectoryBase.TagModel, "ILCE-1");

            var rawImageSubIfd = new ExifSubIfdDirectory();
            rawImageSubIfd.Set(ExifDirectoryBase.TagImageWidth, 6144);
            rawImageSubIfd.Set(ExifDirectoryBase.TagImageHeight, 4096);
            rawImageSubIfd.Set(ExifDirectoryBase.TagTimeZoneOriginal, "+01:00");

            var photoSubIfd = new ExifSubIfdDirectory();
            photoSubIfd.Set(ExifDirectoryBase.TagDateTimeOriginal, "2026:06:06 14:50:50");
            photoSubIfd.Set(ExifDirectoryBase.TagTimeZoneOriginal, "+09:00");
            photoSubIfd.Set(
                ExifDirectoryBase.TagLensModel,
                "60-600mm F4.5-6.3 DG DN OS | Sports 023");

            var metadata = PhotoMetadataReader.Extract(new MetadataExtractor.Directory[]
            {
                ifd0,
                rawImageSubIfd,
                photoSubIfd
            });

            Assert.Equal(new DateTime(2026, 6, 6, 14, 50, 50), metadata.TakenDate);
            Assert.Equal(DateTimeKind.Unspecified, metadata.TakenDate.Value.Kind);
            Assert.Equal(TimeSpan.FromHours(9), metadata.TakenDateOffset);
            Assert.Equal(TakenDateOffsetState.Valid, metadata.OffsetState);
            Assert.Equal("SONY", metadata.CameraMake);
            Assert.Equal("ILCE-1", metadata.CameraModel);
            Assert.Equal("60-600mm F4.5-6.3 DG DN OS | Sports 023", metadata.Lens);
        }

        [Fact]
        public void ExtractsExtendedExifValuesAndSubseconds()
        {
            var jpeg = new JpegDirectory();
            jpeg.Set(JpegDirectory.TagImageWidth, 6000);
            jpeg.Set(JpegDirectory.TagImageHeight, 4000);
            var ifd0 = new ExifIfd0Directory();
            ifd0.Set(ExifDirectoryBase.TagOrientation, 6);
            ifd0.Set(ExifDirectoryBase.TagRating, 4);
            var subIfd = new ExifSubIfdDirectory();
            subIfd.Set(ExifDirectoryBase.TagDateTimeOriginal, "2026:07:14 12:34:56");
            subIfd.Set(ExifDirectoryBase.TagSubsecondTimeOriginal, "123456789");
            subIfd.Set(ExifDirectoryBase.TagBodySerialNumber, " BODY-1 ");
            subIfd.Set(ExifDirectoryBase.TagExifImageWidth, 6048);
            subIfd.Set(ExifDirectoryBase.TagExifImageHeight, 4024);
            subIfd.Set(ExifDirectoryBase.TagFNumber, new Rational(28, 10));
            subIfd.Set(ExifDirectoryBase.TagExposureTime, new Rational(2, 500));
            subIfd.Set(ExifDirectoryBase.TagIsoEquivalent, 100);
            subIfd.Set(ExifDirectoryBase.TagFocalLength, new Rational(355, 10));
            subIfd.Set(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, 35);
            var gps = new GpsDirectory();
            gps.Set(GpsDirectory.TagLatitudeRef, "N");
            gps.Set(GpsDirectory.TagLatitude, new[] { new Rational(35, 1), new Rational(40, 1), new Rational(524, 10) });
            gps.Set(GpsDirectory.TagLongitudeRef, "E");
            gps.Set(GpsDirectory.TagLongitude, new[] { new Rational(139, 1), new Rational(46, 1), new Rational(165, 10) });
            gps.Set(GpsDirectory.TagAltitude, new Rational(125, 10));
            gps.Set(GpsDirectory.TagAltitudeRef, (byte)1);

            var metadata = PhotoMetadataReader.Extract(new MetadataExtractor.Directory[]
            {
                jpeg, ifd0, subIfd, gps
            });

            Assert.Equal(new DateTime(2026, 7, 14, 12, 34, 56).AddTicks(1234567), metadata.TakenDate);
            Assert.Equal("BODY-1", metadata.CameraSerial);
            Assert.Equal(6000, metadata.DecodedWidth);
            Assert.Equal(4000, metadata.DecodedHeight);
            Assert.Equal(6048, metadata.ExifWidth);
            Assert.Equal(4024, metadata.ExifHeight);
            Assert.Equal(6, metadata.Orientation);
            Assert.Equal(2.8m, metadata.FNumber);
            Assert.Equal(new ExifRational(1, 250), metadata.ExposureTime);
            Assert.Equal(100, metadata.Iso);
            Assert.Equal(35.5m, metadata.FocalLength);
            Assert.Equal(35, metadata.FocalLength35mm);
            Assert.Equal(4, metadata.Rating);
            Assert.True(metadata.HasGps);
            Assert.Equal(-12.5m, metadata.GpsAltitude);
        }

        [Fact]
        public void InvalidGpsPairAlsoDiscardsAltitude()
        {
            var metadata = new PhotoMetadata(
                null, null, TakenDateOffsetState.Missing, null, null, null,
                gpsLatitude: 0m, gpsLongitude: 0m, gpsAltitude: 10m);

            Assert.False(metadata.HasGps);
            Assert.Null(metadata.GpsAltitude);
        }

        [Fact]
        public void FallsBackToMakerNoteCameraSerial()
        {
            var makerNote = new FujifilmMakernoteDirectory();
            makerNote.Set(FujifilmMakernoteDirectory.TagSerialNumber, "FUJI-123");

            var metadata = PhotoMetadataReader.Extract(new MetadataExtractor.Directory[] { makerNote });

            Assert.Equal("FUJI-123", metadata.CameraSerial);
        }

        [Fact]
        public void RatingUsesExifThenXmpThenRatingPercent()
        {
            var ifd0 = new ExifIfd0Directory();
            ifd0.Set(ExifDirectoryBase.TagRating, 0);
            ifd0.Set(ExifDirectoryBase.TagRatingPercent, 50);
            var xmp = new XmpDirectory();
            var xmpMeta = XmpMetaFactory.Create();
            xmpMeta.SetPropertyInteger(XmpConstants.NsXmp, "Rating", -1);
            xmp.SetXmpMeta(xmpMeta);

            var rejected = PhotoMetadataReader.Extract(new MetadataExtractor.Directory[] { ifd0, xmp });
            Assert.Equal(-1, rejected.Rating);

            ifd0.Set(ExifDirectoryBase.TagRating, 5);
            var exifWins = PhotoMetadataReader.Extract(new MetadataExtractor.Directory[] { ifd0, xmp });
            Assert.Equal(5, exifWins.Rating);

            xmpMeta.SetPropertyInteger(XmpConstants.NsXmp, "Rating", 0);
            ifd0.Set(ExifDirectoryBase.TagRating, 0);
            var percentFallback = PhotoMetadataReader.Extract(new MetadataExtractor.Directory[] { ifd0, xmp });
            Assert.Equal(3, percentFallback.Rating);
        }
    }
}
