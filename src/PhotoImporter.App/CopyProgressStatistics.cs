using PhotoImporter.Core.Copying;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PhotoImporter.App
{
    internal sealed class CopyProgressStatistics : IProgress<CopyProgress>
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MinimumRecentDuration = TimeSpan.FromSeconds(10);

        private readonly object _sync = new object();
        private readonly Func<long> _getTimestamp;
        private readonly long _timestampFrequency;
        private readonly long _startedTimestamp;
        private readonly List<Sample> _samples = new List<Sample>();

        private CopyProgress _latestProgress;
        private bool _isPaused;
        private long _pauseStartedTimestamp;
        private long _accumulatedPausedTicks;

        internal CopyProgressStatistics()
            : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
        {
        }

        internal CopyProgressStatistics(Func<long> getTimestamp, long timestampFrequency)
        {
            _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
            if (timestampFrequency <= 0)
                throw new ArgumentOutOfRangeException(nameof(timestampFrequency));

            _timestampFrequency = timestampFrequency;
            _startedTimestamp = _getTimestamp();
        }

        public void Report(CopyProgress value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));

            lock (_sync)
            {
                _latestProgress = value;
                if (_samples.Count == 0)
                    AddSample(GetActiveElapsedTicks(_getTimestamp()), value);
            }
        }

        internal void SetPaused(bool paused)
        {
            lock (_sync)
            {
                if (_isPaused == paused) return;

                var timestamp = _getTimestamp();
                if (paused)
                {
                    _pauseStartedTimestamp = timestamp;
                    _isPaused = true;
                }
                else
                {
                    _accumulatedPausedTicks += Math.Max(0, timestamp - _pauseStartedTimestamp);
                    _isPaused = false;
                }
            }
        }

        internal CopyProgressStatisticsSnapshot Capture()
        {
            lock (_sync)
            {
                var activeElapsedTicks = GetActiveElapsedTicks(_getTimestamp());
                if (_latestProgress == null)
                    return CopyProgressStatisticsSnapshot.Empty(
                        ToTimeSpan(activeElapsedTicks),
                        _isPaused);

                if (_samples.Count == 0 ||
                    activeElapsedTicks - _samples[_samples.Count - 1].ActiveElapsedTicks >=
                    ToTimestampTicks(SampleInterval))
                {
                    AddSample(activeElapsedTicks, _latestProgress);
                }

                TrimOldSamples(activeElapsedTicks);

                var activeElapsed = ToTimeSpan(activeElapsedTicks);
                double? overallFilesPerSecond = null;
                double? overallBytesPerSecond = null;
                if (activeElapsed.TotalSeconds >= 1)
                {
                    overallFilesPerSecond = _latestProgress.CompletedFiles / activeElapsed.TotalSeconds;
                    overallBytesPerSecond =
                        _latestProgress.CumulativeTransferredBytes / activeElapsed.TotalSeconds;
                }

                double? recentFilesPerSecond = null;
                double? recentBytesPerSecond = null;
                if (_samples.Count >= 2)
                {
                    var oldest = _samples[0];
                    var newest = _samples[_samples.Count - 1];
                    var recentDuration = ToTimeSpan(
                        newest.ActiveElapsedTicks - oldest.ActiveElapsedTicks);
                    if (recentDuration >= MinimumRecentDuration)
                    {
                        recentFilesPerSecond = Math.Max(
                            0,
                            newest.CompletedFiles - oldest.CompletedFiles) /
                            recentDuration.TotalSeconds;
                        recentBytesPerSecond = Math.Max(
                            0,
                            newest.CumulativeTransferredBytes - oldest.CumulativeTransferredBytes) /
                            recentDuration.TotalSeconds;
                    }
                }

                TimeSpan? estimatedRemaining = null;
                if (recentBytesPerSecond.HasValue && recentBytesPerSecond.Value > 0 &&
                    _latestProgress.RemainingWorkBytes > 0)
                {
                    var seconds = _latestProgress.RemainingWorkBytes / recentBytesPerSecond.Value;
                    if (seconds <= TimeSpan.MaxValue.TotalSeconds)
                        estimatedRemaining = TimeSpan.FromSeconds(Math.Ceiling(seconds));
                }
                else if (_latestProgress.RemainingWorkBytes == 0)
                {
                    estimatedRemaining = TimeSpan.Zero;
                }

                return new CopyProgressStatisticsSnapshot(
                    _latestProgress,
                    activeElapsed,
                    _isPaused,
                    overallFilesPerSecond,
                    overallBytesPerSecond,
                    recentFilesPerSecond,
                    recentBytesPerSecond,
                    estimatedRemaining);
            }
        }

        private void AddSample(long activeElapsedTicks, CopyProgress progress)
        {
            _samples.Add(new Sample(
                activeElapsedTicks,
                progress.CompletedFiles,
                progress.CumulativeTransferredBytes));
        }

        private void TrimOldSamples(long activeElapsedTicks)
        {
            var cutoff = activeElapsedTicks - ToTimestampTicks(RecentWindow);
            while (_samples.Count > 1 && _samples[0].ActiveElapsedTicks < cutoff)
                _samples.RemoveAt(0);
        }

        private long GetActiveElapsedTicks(long timestamp)
        {
            var effectiveTimestamp = _isPaused ? _pauseStartedTimestamp : timestamp;
            return Math.Max(0, effectiveTimestamp - _startedTimestamp - _accumulatedPausedTicks);
        }

        private long ToTimestampTicks(TimeSpan value) =>
            (long)Math.Round(value.TotalSeconds * _timestampFrequency);

        private TimeSpan ToTimeSpan(long timestampTicks) =>
            TimeSpan.FromSeconds(timestampTicks / (double)_timestampFrequency);

        private sealed class Sample
        {
            internal Sample(
                long activeElapsedTicks,
                int completedFiles,
                long cumulativeTransferredBytes)
            {
                ActiveElapsedTicks = activeElapsedTicks;
                CompletedFiles = completedFiles;
                CumulativeTransferredBytes = cumulativeTransferredBytes;
            }

            internal long ActiveElapsedTicks { get; }
            internal int CompletedFiles { get; }
            internal long CumulativeTransferredBytes { get; }
        }
    }

    internal sealed class CopyProgressStatisticsSnapshot
    {
        internal CopyProgressStatisticsSnapshot(
            CopyProgress progress,
            TimeSpan activeElapsed,
            bool isPaused,
            double? overallFilesPerSecond,
            double? overallBytesPerSecond,
            double? recentFilesPerSecond,
            double? recentBytesPerSecond,
            TimeSpan? estimatedRemaining)
        {
            Progress = progress;
            ActiveElapsed = activeElapsed;
            IsPaused = isPaused;
            OverallFilesPerSecond = overallFilesPerSecond;
            OverallBytesPerSecond = overallBytesPerSecond;
            RecentFilesPerSecond = recentFilesPerSecond;
            RecentBytesPerSecond = recentBytesPerSecond;
            EstimatedRemaining = estimatedRemaining;
        }

        internal CopyProgress Progress { get; }
        internal TimeSpan ActiveElapsed { get; }
        internal bool IsPaused { get; }
        internal double? OverallFilesPerSecond { get; }
        internal double? OverallBytesPerSecond { get; }
        internal double? RecentFilesPerSecond { get; }
        internal double? RecentBytesPerSecond { get; }
        internal TimeSpan? EstimatedRemaining { get; }

        internal static CopyProgressStatisticsSnapshot Empty(
            TimeSpan activeElapsed,
            bool isPaused) =>
            new CopyProgressStatisticsSnapshot(
                null,
                activeElapsed,
                isPaused,
                null,
                null,
                null,
                null,
                null);
    }
}
