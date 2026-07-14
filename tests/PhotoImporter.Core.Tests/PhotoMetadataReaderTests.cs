using System;
using System.IO;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoImporter.Core.Metadata;
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
    }
}
