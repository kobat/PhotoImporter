using System;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PhotoMetadataReaderTests
    {
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

            var metadata = PhotoMetadataReader.Extract(new Directory[]
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
    }
}
