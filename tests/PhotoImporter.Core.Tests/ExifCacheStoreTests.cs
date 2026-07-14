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

            Assert.True(File.Exists(Path.Combine(store.CacheRoot, volume.SerialNumberHex, "entries.json")));
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

            Assert.False(File.Exists(Path.Combine(store.CacheRoot, volume.SerialNumberHex, "entries.json")));
        }

        [Fact]
        public void CorruptDocumentIsDiscardedAndRegenerated()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var volumeFolder = Path.Combine(store.CacheRoot, volume.SerialNumberHex);
            Directory.CreateDirectory(volumeFolder);
            File.WriteAllText(Path.Combine(volumeFolder, "entries.json"), "{not-json");
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
        public void RemovesStalePartialFilesAfterTakingLock()
        {
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var volumeFolder = Path.Combine(store.CacheRoot, volume.SerialNumberHex);
            Directory.CreateDirectory(volumeFolder);
            var partial = Path.Combine(volumeFolder, "entries.json.123.deadbeef.partial");
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
