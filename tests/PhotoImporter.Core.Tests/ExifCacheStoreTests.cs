using System;
using System.IO;
using System.Threading;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class ExifCacheStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "PhotoImporter.Tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void SavesAndLoadsAllCacheableResultKinds()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var successKey = CreateKey(volume, "success.jpg", 10);
            var emptyKey = CreateKey(volume, "empty.jpg", 20);
            var unsupportedKey = CreateKey(volume, "unsupported.raw", 30);
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                session.Put(successKey, PhotoMetadataReadResult.Success(new PhotoMetadata(
                    new DateTime(2026, 7, 14, 12, 34, 56),
                    TimeSpan.FromHours(9),
                    TakenDateOffsetState.Valid,
                    "Sony", "ILCE-7M4", "FE 35mm F1.4 GM")), Utc(2026, 7, 14));
                session.Put(emptyKey, PhotoMetadataReadResult.NoMetadata(), Utc(2026, 7, 14));
                session.Put(unsupportedKey, PhotoMetadataReadResult.Unsupported(), Utc(2026, 7, 14));
            }

            var entriesPath = Path.Combine(store.CacheRoot, volume.SerialNumberHex, "entries.tsv");
            Assert.True(File.Exists(entriesPath));
            var tsv = File.ReadAllText(entriesPath);
            Assert.Contains("# PhotoImporter Exif Cache\tSchemaVersion=3\tExtractionVersion=2", tsv);
            Assert.DoesNotContain("ComparisonPath", tsv);
            Assert.Contains("2026-07-14T12:34:56.0000000", tsv);
            Assert.Contains("\tSuccess\t", tsv);
            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                Assert.Equal(3, session.Count);
                PhotoMetadataReadResult result;
                Assert.True(session.TryGet(successKey, Utc(2026, 7, 15), out result));
                Assert.Equal(PhotoMetadataReadStatus.Success, result.Status);
                Assert.Equal(new DateTime(2026, 7, 14, 12, 34, 56), result.Metadata.TakenDate);
                Assert.Equal(TimeSpan.FromHours(9), result.Metadata.TakenDateOffset);
                Assert.Equal("Sony", result.Metadata.CameraMake);
                Assert.Equal("ILCE-7M4", result.Metadata.CameraModel);
                Assert.Equal("FE 35mm F1.4 GM", result.Metadata.Lens);
                Assert.True(session.TryGet(emptyKey, Utc(2026, 7, 15), out result));
                Assert.Equal(PhotoMetadataReadStatus.NoMetadata, result.Status);
                Assert.True(session.TryGet(unsupportedKey, Utc(2026, 7, 15), out result));
                Assert.Equal(PhotoMetadataReadStatus.Unsupported, result.Status);
            }
        }

        [Fact]
        public void ExtendedExifValuesRoundTripThroughSchemaThreeTsv()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var key = CreateKey(volume, "extended.jpg", 10);
            var metadata = new PhotoMetadata(
                new DateTime(2026, 7, 14, 12, 34, 56, 789),
                TimeSpan.FromHours(9),
                TakenDateOffsetState.Valid,
                "Sony", "ILCE-1", "FE 35mm", "SERIAL-1",
                6000, 4000, 6048, 4024, 6, 2.8m,
                new ExifRational(2, 500), 100, 35.5m, 35, -1,
                35.681236m, 139.767125m, -12.5m);
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
                session.Put(key, PhotoMetadataReadResult.Success(metadata), Utc(2026, 7, 14));

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                PhotoMetadataReadResult result;
                Assert.True(session.TryGet(key, Utc(2026, 7, 15), out result));
                var actual = result.Metadata;
                Assert.Equal("SERIAL-1", actual.CameraSerial);
                Assert.Equal(6000, actual.DecodedWidth);
                Assert.Equal(4000, actual.DecodedHeight);
                Assert.Equal(6048, actual.ExifWidth);
                Assert.Equal(4024, actual.ExifHeight);
                Assert.Equal(6, actual.Orientation);
                Assert.Equal(2.8m, actual.FNumber);
                Assert.Equal(new ExifRational(1, 250), actual.ExposureTime);
                Assert.Equal(100, actual.Iso);
                Assert.Equal(35.5m, actual.FocalLength);
                Assert.Equal(35, actual.FocalLength35mm);
                Assert.Equal(-1, actual.Rating);
                Assert.Equal(35.681236m, actual.GpsLatitude);
                Assert.Equal(139.767125m, actual.GpsLongitude);
                Assert.Equal(-12.5m, actual.GpsAltitude);
            }
        }

        [Fact]
        public void DoesNotStoreReadErrors()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var key = CreateKey(volume, "unreadable.jpg", 10);
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
                session.Put(key, PhotoMetadataReadResult.ReadError(new IOException("removed")), Utc(2026, 7, 14));

            Assert.False(File.Exists(Path.Combine(store.CacheRoot, volume.SerialNumberHex, "entries.tsv")));
        }

        [Fact]
        public void CorruptDocumentIsDiscardedAndRegenerated()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var volumeFolder = Path.Combine(store.CacheRoot, volume.SerialNumberHex);
            Directory.CreateDirectory(volumeFolder);
            File.WriteAllText(Path.Combine(volumeFolder, "entries.tsv"), "not-tsv");
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                Assert.True(session.RecoveredFromInvalidFile);
                Assert.Equal(0, session.Count);
                session.Put(CreateKey(volume, "photo.jpg", 10), PhotoMetadataReadResult.NoMetadata(), Utc(2026, 7, 14));
            }

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                Assert.False(session.RecoveredFromInvalidFile);
                Assert.Equal(1, session.Count);
            }
        }

        [Fact]
        public void OldTsvSchemaAndExtractionVersionAreDiscarded()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var volumeFolder = Path.Combine(store.CacheRoot, volume.SerialNumberHex);
            Directory.CreateDirectory(volumeFolder);
            File.WriteAllText(Path.Combine(volumeFolder, "entries.tsv"),
                "# PhotoImporter Exif Cache\tSchemaVersion=2\tExtractionVersion=1\r\n" +
                "RelativePath\tFileSize\tLastWriteTimeUtc\tLastUsedUtcDate\tStatus\tTakenDate\tOffset\tOffsetState\tCameraMake\tCameraModel\tLens\r\n");
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                Assert.True(session.RecoveredFromInvalidFile);
                Assert.Equal(0, session.Count);
            }

            Assert.Contains("SchemaVersion=3\tExtractionVersion=2",
                File.ReadAllText(Path.Combine(volumeFolder, "entries.tsv")));
        }

        [Fact]
        public void QuotedTsvFieldsRoundTripTabsNewLinesAndQuotes()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var key = CreateKey(volume, "quoted.jpg", 10);
            var make = "Camera\tMaker";
            var model = "Line 1\r\nLine \"2\"";
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
                session.Put(key, PhotoMetadataReadResult.Success(new PhotoMetadata(
                    new DateTime(2026, 7, 14, 12, 34, 56, 789),
                    TimeSpan.FromTicks(324000000001),
                    TakenDateOffsetState.Valid,
                    make, model, "Lens")), Utc(2026, 7, 14));

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                PhotoMetadataReadResult result;
                Assert.True(session.TryGet(key, Utc(2026, 7, 14), out result));
                Assert.Equal(make, result.Metadata.CameraMake);
                Assert.Equal(model, result.Metadata.CameraModel);
                Assert.Equal(TimeSpan.FromTicks(324000000001), result.Metadata.TakenDateOffset);
            }
        }

        [Fact]
        public void InvalidEntryRowIsSkippedWithoutDiscardingValidRows()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var key = CreateKey(volume, "valid.jpg", 10);
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
                session.Put(key, PhotoMetadataReadResult.NoMetadata(), Utc(2026, 7, 14));

            var entriesPath = Path.Combine(store.CacheRoot, volume.SerialNumberHex, "entries.tsv");
            File.AppendAllText(entriesPath,
                "broken.jpg\tnot-a-size\t2026-07-14T00:00:00.0000000Z\t2026-07-14\tNoMetadata\t\t\tMissing\t\t\t\r\n");

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                Assert.False(session.RecoveredFromInvalidFile);
                Assert.Equal(1, session.Count);
            }
        }

        [Fact]
        public void LegacyJsonFromOldExtractionVersionIsDiscarded()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var volumeFolder = Path.Combine(store.CacheRoot, volume.SerialNumberHex);
            var legacyPath = Path.Combine(volumeFolder, "entries.json");
            Directory.CreateDirectory(volumeFolder);
            File.WriteAllText(legacyPath, string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{{\"SchemaVersion\":1,\"ExtractionVersion\":1,\"Entries\":[{{\"RelativePath\":\"legacy.jpg\",\"ComparisonPath\":\"LEGACY.JPG\",\"FileSize\":10,\"LastWriteTimeUtcTicks\":{0},\"LastUsedUtcDateTicks\":{1},\"Status\":1,\"OffsetState\":0}}]}}",
                Utc(2026, 7, 14).Ticks,
                Utc(2026, 7, 14).Ticks));
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                Assert.True(session.RecoveredFromInvalidFile);
                Assert.Equal(0, session.Count);
                PhotoMetadataReadResult result;
                Assert.False(session.TryGet(CreateKey(volume, "legacy.jpg", 10), Utc(2026, 7, 14), out result));
            }

            Assert.True(File.Exists(Path.Combine(volumeFolder, "entries.tsv")));
            Assert.False(File.Exists(legacyPath));
        }

        [Fact]
        public void RemovesStalePartialFilesAfterTakingLock()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var volumeFolder = Path.Combine(store.CacheRoot, volume.SerialNumberHex);
            Directory.CreateDirectory(volumeFolder);
            var partial = Path.Combine(volumeFolder, "entries.tsv.123.deadbeef.partial");
            File.WriteAllText(partial, "incomplete");
            ExifCacheSession session;
            string warning;

            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session) { }

            Assert.False(File.Exists(partial));
        }

        [Fact]
        public void SameVolumeAndRootAreSerializedByNamedMutex()
        {
            var volume = CreateVolume();
            var cacheRoot = Path.Combine(_root, "cache");
            var ready = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                using (var mutex = new Mutex(false, ExifCacheStore.CreateMutexName(cacheRoot, volume.SerialNumber)))
                {
                    mutex.WaitOne();
                    ready.Set();
                    release.Wait();
                    mutex.ReleaseMutex();
                }
            });
            thread.Start();
            ready.Wait();
            try
            {
                var store = new ExifCacheStore(cacheRoot, TimeSpan.FromMilliseconds(50));
                ExifCacheSession session;
                string warning;

                Assert.False(store.TryOpen(volume, out session, out warning));
                Assert.Null(session);
                Assert.Contains("別の PhotoImporter", warning);
            }
            finally
            {
                release.Set();
                thread.Join();
                ready.Dispose();
                release.Dispose();
            }
        }

        [Fact]
        public void WaitingForNamedMutexCanBeCancelled()
        {
            var volume = CreateVolume();
            var cacheRoot = Path.Combine(_root, "cache");
            var ready = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                using (var mutex = new Mutex(false, ExifCacheStore.CreateMutexName(cacheRoot, volume.SerialNumber)))
                {
                    mutex.WaitOne();
                    ready.Set();
                    release.Wait();
                    mutex.ReleaseMutex();
                }
            });
            thread.Start();
            ready.Wait();
            try
            {
                var store = new ExifCacheStore(cacheRoot, Timeout.InfiniteTimeSpan);
                ExifCacheSession session;
                string warning;

                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                    Assert.Throws<OperationCanceledException>(() =>
                        store.TryOpen(volume, out session, out warning, cancellation.Token));
                }
            }
            finally
            {
                release.Set();
                thread.Join();
                ready.Dispose();
                release.Dispose();
            }
        }

        [Fact]
        public void MutexNameSeparatesRootsAndVolumes()
        {
            var first = ExifCacheStore.CreateMutexName(Path.Combine(_root, "a"), 1);

            Assert.Equal(first, ExifCacheStore.CreateMutexName(Path.Combine(_root, "a"), 1));
            Assert.NotEqual(first, ExifCacheStore.CreateMutexName(Path.Combine(_root, "b"), 1));
            Assert.NotEqual(first, ExifCacheStore.CreateMutexName(Path.Combine(_root, "a"), 2));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private VolumeInfo CreateVolume() =>
            new VolumeInfo(_root, 0xA1B2C3D4, "CARD", "exFAT", DriveType.Removable, 64UL * 1024 * 1024 * 1024);

        private static ExifCacheKey CreateKey(VolumeInfo volume, string name, long size) =>
            ExifCacheKey.Create(volume, Path.Combine(volume.RootPath, name), size, Utc(2026, 7, 14));

        private static DateTime Utc(int year, int month, int day) =>
            new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }
}
