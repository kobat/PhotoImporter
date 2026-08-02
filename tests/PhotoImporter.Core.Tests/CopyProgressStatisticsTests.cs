using PhotoImporter.App;
using PhotoImporter.Core.Copying;
using System;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class CopyProgressStatisticsTests
    {
        [Fact]
        public void PauseDurationIsExcludedFromRecentAndOverallRates()
        {
            var clock = new FakeClock();
            var statistics = new CopyProgressStatistics(clock.GetTimestamp, clock.Frequency);
            statistics.Report(CreateProgress(0, 10, 0, 0, 1000));

            clock.Advance(TimeSpan.FromSeconds(5));
            statistics.Report(CreateProgress(5, 10, 500, 500, 1000));
            statistics.Capture();

            statistics.SetPaused(true);
            clock.Advance(TimeSpan.FromMinutes(2));
            var paused = statistics.Capture();
            Assert.Equal(TimeSpan.FromSeconds(5), paused.ActiveElapsed);
            Assert.True(paused.IsPaused);

            statistics.SetPaused(false);
            clock.Advance(TimeSpan.FromSeconds(5));
            statistics.Report(CreateProgress(10, 10, 1000, 1000, 1000));
            var resumed = statistics.Capture();

            Assert.Equal(TimeSpan.FromSeconds(10), resumed.ActiveElapsed);
            Assert.False(resumed.IsPaused);
            Assert.Equal(1, resumed.OverallFilesPerSecond.Value, 6);
            Assert.Equal(100, resumed.OverallBytesPerSecond.Value, 6);
            Assert.Equal(1, resumed.RecentFilesPerSecond.Value, 6);
            Assert.Equal(100, resumed.RecentBytesPerSecond.Value, 6);
        }

        [Fact]
        public void RecentRatesUseOnlyTheLatestMinuteOfActiveTime()
        {
            var clock = new FakeClock();
            var statistics = new CopyProgressStatistics(clock.GetTimestamp, clock.Frequency);
            statistics.Report(CreateProgress(0, 100, 0, 0, 100000));

            for (var seconds = 5; seconds <= 65; seconds += 5)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                statistics.Report(CreateProgress(
                    seconds / 5,
                    100,
                    seconds * 100,
                    seconds * 100,
                    100000));
                statistics.Capture();
            }

            var snapshot = statistics.Capture();
            Assert.Equal(0.2, snapshot.RecentFilesPerSecond.Value, 6);
            Assert.Equal(100, snapshot.RecentBytesPerSecond.Value, 6);
        }

        [Fact]
        public void RemainingTimeUsesRecentTransferredBytesAndRemainingWork()
        {
            var clock = new FakeClock();
            var statistics = new CopyProgressStatistics(clock.GetTimestamp, clock.Frequency);
            statistics.Report(CreateProgress(0, 20, 0, 0, 2000));

            clock.Advance(TimeSpan.FromSeconds(10));
            statistics.Report(CreateProgress(10, 20, 1000, 1000, 2000));
            var snapshot = statistics.Capture();

            Assert.Equal(100, snapshot.RecentBytesPerSecond.Value, 6);
            Assert.Equal(TimeSpan.FromSeconds(10), snapshot.EstimatedRemaining.Value);
        }

        [Fact]
        public void RecentRatesRemainUnavailableUntilTenSecondsAreSampled()
        {
            var clock = new FakeClock();
            var statistics = new CopyProgressStatistics(clock.GetTimestamp, clock.Frequency);
            statistics.Report(CreateProgress(0, 10, 0, 0, 1000));

            clock.Advance(TimeSpan.FromSeconds(5));
            statistics.Report(CreateProgress(5, 10, 500, 500, 1000));
            var snapshot = statistics.Capture();

            Assert.False(snapshot.RecentFilesPerSecond.HasValue);
            Assert.False(snapshot.RecentBytesPerSecond.HasValue);
            Assert.False(snapshot.EstimatedRemaining.HasValue);
        }

        private static CopyProgress CreateProgress(
            int completedFiles,
            int totalFiles,
            long completedWorkBytes,
            long cumulativeTransferredBytes,
            long totalBytes) =>
            new CopyProgress(
                completedFiles,
                totalFiles,
                completedWorkBytes,
                completedWorkBytes,
                cumulativeTransferredBytes,
                totalBytes,
                null);

        private sealed class FakeClock
        {
            internal long Frequency => 1000;
            internal long Timestamp { get; private set; }

            internal long GetTimestamp() => Timestamp;

            internal void Advance(TimeSpan duration) =>
                Timestamp += (long)(duration.TotalSeconds * Frequency);
        }
    }
}
