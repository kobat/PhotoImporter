using System;
using System.IO;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class ExifCacheKeyTests
    {
        private static readonly DateTime LastWriteTimeUtc =
            new DateTime(2026, 7, 14, 12, 34, 56, DateTimeKind.Utc);

        [Fact]
        public void UsesVolumeRelativePathAndFourIdentityElements()
        {
            var volume = CreateVolume(0xA1B2C3D4);

            var key = ExifCacheKey.Create(
                volume,
                Path.Combine(volume.RootPath, "DCIM", "100MSDCF", "DSC00001.ARW"),
                123456,
                LastWriteTimeUtc);

            Assert.Equal(0xA1B2C3D4u, key.VolumeSerialNumber);
            Assert.Equal(Path.Combine("DCIM", "100MSDCF", "DSC00001.ARW"), key.VolumeRelativePath);
            Assert.Equal(Path.Combine("DCIM", "100MSDCF", "DSC00001.ARW").ToUpperInvariant(), key.ComparisonPath);
            Assert.Equal(123456, key.FileSize);
            Assert.Equal(LastWriteTimeUtc.Ticks, key.LastWriteTimeUtcTicks);
        }

        [Fact]
        public void PathComparisonIsCaseInsensitiveButPreservesDisplayPath()
        {
            var volume = CreateVolume(1);
            var first = ExifCacheKey.Create(
                volume,
                Path.Combine(volume.RootPath, "DCIM", "photo.jpg"),
                10,
                LastWriteTimeUtc);
            var second = ExifCacheKey.Create(
                volume,
                Path.Combine(volume.RootPath, "dcim", "PHOTO.JPG"),
                10,
                LastWriteTimeUtc);

            Assert.Equal(first, second);
            Assert.NotEqual(first.VolumeRelativePath, second.VolumeRelativePath);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
        }

        [Theory]
        [InlineData(2u, 10L, 638881796960000000L)]
        [InlineData(1u, 11L, 638881796960000000L)]
        [InlineData(1u, 10L, 638881796960000001L)]
        public void AnyNonPathIdentityDifferenceCausesCacheMiss(
            uint serialNumber,
            long fileSize,
            long lastWriteTicks)
        {
            var path = Path.Combine(CreateVolume(1).RootPath, "DCIM", "photo.jpg");
            var baseline = ExifCacheKey.Create(CreateVolume(1), path, 10, new DateTime(638881796960000000L, DateTimeKind.Utc));
            var candidate = ExifCacheKey.Create(CreateVolume(serialNumber), path, fileSize, new DateTime(lastWriteTicks, DateTimeKind.Utc));

            Assert.NotEqual(baseline, candidate);
        }

        [Fact]
        public void RejectsFileOutsideVolumeAndNonUtcTimestamp()
        {
            var volume = new VolumeInfo(Path.Combine(Path.GetTempPath(), "volume-a"), 1, null, null, DriveType.Removable, 0);
            var outside = Path.Combine(Path.GetTempPath(), "volume-b", "photo.jpg");

            Assert.Throws<ArgumentException>(() =>
                ExifCacheKey.Create(volume, outside, 10, LastWriteTimeUtc));
            Assert.Throws<ArgumentException>(() =>
                ExifCacheKey.Create(volume, Path.Combine(volume.RootPath, "photo.jpg"), 10, LastWriteTimeUtc.ToLocalTime()));
        }

        [Fact]
        public void CacheKeyPlanUsesJpegKeyForBothMembersOfPair()
        {
            var volume = CreateVolume(1);
            var jpeg = Path.Combine(volume.RootPath, "DCIM", "photo.jpg");
            var raw = Path.Combine(volume.RootPath, "DCIM", "photo.arw");
            var analysisPlan = RawJpegAnalysisPlan.Create(new[] { raw, jpeg });
            var keyPlan = ExifCacheKeyPlan.Create(
                analysisPlan,
                volume,
                new[]
                {
                    new ExifFileSnapshot(raw, 100, LastWriteTimeUtc.AddSeconds(-1)),
                    new ExifFileSnapshot(jpeg, 20, LastWriteTimeUtc)
                });

            Assert.Same(keyPlan.GetKeyForTarget(jpeg), keyPlan.GetKeyForTarget(raw));
            Assert.Equal(20, keyPlan.GetKeyForTarget(raw).FileSize);
            Assert.EndsWith("PHOTO.JPG", keyPlan.GetKeyForTarget(raw).ComparisonPath);
        }

        [Fact]
        public void CacheKeyPlanUsesSeparateKeysInAnalyzeBothMode()
        {
            var volume = CreateVolume(1);
            var jpeg = Path.Combine(volume.RootPath, "DCIM", "photo.jpg");
            var raw = Path.Combine(volume.RootPath, "DCIM", "photo.arw");
            var analysisPlan = RawJpegAnalysisPlan.Create(new[] { raw, jpeg }, RawJpegAnalysisMode.AnalyzeBoth);
            var keyPlan = ExifCacheKeyPlan.Create(
                analysisPlan,
                volume,
                new[]
                {
                    new ExifFileSnapshot(raw, 100, LastWriteTimeUtc),
                    new ExifFileSnapshot(jpeg, 20, LastWriteTimeUtc)
                });

            Assert.NotEqual(keyPlan.GetKeyForTarget(jpeg), keyPlan.GetKeyForTarget(raw));
        }

        [Fact]
        public void WindowsReaderReturnsCurrentVolumeInformation()
        {
            var volume = new WindowsVolumeInfoReader().Read(Path.GetTempPath());

            Assert.False(string.IsNullOrWhiteSpace(volume.RootPath));
            Assert.Equal(8, volume.SerialNumberHex.Length);
            Assert.True(volume.TotalBytes > 0);
        }

        private static VolumeInfo CreateVolume(uint serialNumber) =>
            new VolumeInfo(Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())), serialNumber, "CARD", "exFAT", DriveType.Removable, 64UL * 1024 * 1024 * 1024);
    }
}
