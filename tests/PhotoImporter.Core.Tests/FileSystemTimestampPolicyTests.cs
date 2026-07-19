using System;
using PhotoImporter.Core.Copying;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class FileSystemTimestampPolicyTests
    {
        private static readonly DateTime Timestamp =
            new DateTime(2026, 7, 12, 3, 0, 0, DateTimeKind.Utc).AddTicks(123456);

        [Theory]
        [InlineData("NTFS")]
        [InlineData("ReFS")]
        public void ExactFileSystemsRejectAnyTimestampDifference(string fileSystemName)
        {
            var policy = FileSystemTimestampPolicy.Create(fileSystemName);

            Assert.True(policy.Matches(Timestamp, Timestamp));
            Assert.False(policy.Matches(Timestamp, Timestamp.AddTicks(1)));
        }

        [Fact]
        public void ExFatMatchesAtTenMillisecondPrecision()
        {
            var policy = FileSystemTimestampPolicy.Create("exFAT");
            var rounded = Truncate(Timestamp, TimeSpan.TicksPerMillisecond * 10);

            Assert.True(policy.Matches(Timestamp, rounded));
            Assert.False(policy.Matches(Timestamp, rounded.AddMilliseconds(-10)));
        }

        [Theory]
        [InlineData("FAT")]
        [InlineData("FAT32")]
        public void FatMatchesAtTwoSecondLocalTimePrecision(string fileSystemName)
        {
            var policy = FileSystemTimestampPolicy.Create(fileSystemName, TimeZoneInfo.Utc);
            var rounded = Truncate(Timestamp, TimeSpan.TicksPerSecond * 2);

            Assert.True(policy.Matches(Timestamp, rounded));
            Assert.False(policy.Matches(Timestamp, rounded.AddSeconds(-2)));
        }

        [Fact]
        public void FatComparesAmbiguousDstTimesAsTheSameLocalWallClock()
        {
            var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
            var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 12, 31),
                TimeSpan.FromHours(1),
                daylightStart,
                daylightEnd);
            var timeZone = TimeZoneInfo.CreateCustomTimeZone(
                "PhotoImporterTestZone",
                TimeSpan.FromHours(-8),
                "PhotoImporter Test Zone",
                "Test Standard Time",
                "Test Daylight Time",
                new[] { rule });
            var policy = FileSystemTimestampPolicy.Create("FAT32", timeZone);
            var daylightOccurrence = new DateTime(2026, 11, 1, 8, 30, 0, DateTimeKind.Utc);
            var standardOccurrence = daylightOccurrence.AddHours(1);

            Assert.True(policy.Matches(daylightOccurrence, standardOccurrence));
        }

        [Fact]
        public void UnknownFileSystemIsUnsupportedAndNeverMatches()
        {
            var policy = FileSystemTimestampPolicy.Create("FutureFS");

            Assert.False(policy.IsSupported);
            Assert.False(policy.Matches(Timestamp, Timestamp));
        }

        [Fact]
        public void CopyValidationUsesDestinationPrecisionAndStillRequiresSize()
        {
            var expected = new FileSnapshot(100, Timestamp);
            var rounded = Truncate(Timestamp, TimeSpan.TicksPerMillisecond * 10);
            var policy = FileSystemTimestampPolicy.Create("exFAT");

            Assert.True(CopyEngine.SnapshotMatches(expected, 100, rounded, policy));
            Assert.False(CopyEngine.SnapshotMatches(expected, 101, rounded, policy));
        }

        [Theory]
        [InlineData("FAT")]
        [InlineData("FAT32")]
        public void CopyValidationUsesFatTwoSecondPrecision(string fileSystemName)
        {
            var expected = new FileSnapshot(100, Timestamp);
            var rounded = Truncate(Timestamp, TimeSpan.TicksPerSecond * 2);
            var policy = FileSystemTimestampPolicy.Create(fileSystemName, TimeZoneInfo.Utc);

            Assert.True(CopyEngine.SnapshotMatches(expected, 100, rounded, policy));
            Assert.False(CopyEngine.SnapshotMatches(
                expected, 100, rounded.AddSeconds(-2), policy));
        }

        private static DateTime Truncate(DateTime value, long resolutionTicks) =>
            new DateTime(value.Ticks - value.Ticks % resolutionTicks, value.Kind);
    }
}
