using System;
using System.IO;
using System.Linq;
using System.Threading;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class ExifCacheManagerTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "PhotoImporter.Tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void InspectReturnsVolumeMetadataAndCreatesMetaJson()
        {
            var cacheRoot = Path.Combine(_root, "cache");
            var volume = CreateVolume(0xA1B2C3D4, "CAMERA CARD");
            Put(cacheRoot, volume, "photo.jpg", 10, Utc(2026, 7, 14));

            var roots = new ExifCacheManager().Inspect(
                cacheRoot,
                new string[0],
                volume.SerialNumber);

            var root = Assert.Single(roots);
            Assert.True(root.IsCurrent);
            Assert.True(root.Exists);
            var card = Assert.Single(root.Cards);
            Assert.Equal(volume.SerialNumber, card.VolumeSerialNumber);
            Assert.Equal("CAMERA CARD", card.VolumeLabel);
            Assert.Equal("exFAT", card.FileSystemName);
            Assert.Equal(DriveType.Removable, card.DriveType);
            Assert.Equal(volume.TotalBytes, card.TotalBytes);
            Assert.Equal(Utc(2026, 7, 14), card.FirstUsedUtcDate);
            Assert.Equal(Utc(2026, 7, 14), card.LastUsedUtcDate);
            Assert.Equal(1, card.EntryCount);
            Assert.True(card.CacheSizeBytes > 0);
            Assert.True(card.IsCurrentSource);
            var metadataPath = Path.Combine(cacheRoot, volume.SerialNumberHex, "meta.json");
            Assert.True(File.Exists(metadataPath));
            Assert.Contains("\"FirstUsedUtcDate\":\"2026-07-14\"", File.ReadAllText(metadataPath));
        }

        [Fact]
        public void RenameCardPersistsAndCanBeCleared()
        {
            var cacheRoot = Path.Combine(_root, "cache");
            var volume = CreateVolume(0xA1B2C3D4, "CARD");
            Put(cacheRoot, volume, "photo.jpg", 10, Utc(2026, 7, 14));
            var manager = new ExifCacheManager();

            manager.RenameCard(cacheRoot, volume.SerialNumber, "  夏の旅行  ");
            var renamed = Assert.Single(Assert.Single(manager.Inspect(cacheRoot, null)).Cards);
            Assert.Equal("夏の旅行", renamed.DisplayName);

            manager.RenameCard(cacheRoot, volume.SerialNumber, " ");
            var cleared = Assert.Single(Assert.Single(manager.Inspect(cacheRoot, null)).Cards);
            Assert.Null(cleared.DisplayName);
        }

        [Fact]
        public void RenameRejectsNamesLongerThanLimit()
        {
            var manager = new ExifCacheManager();

            Assert.Throws<ArgumentException>(() => manager.RenameCard(
                Path.Combine(_root, "cache"),
                1,
                new string('x', ExifCacheManager.MaximumDisplayNameLength + 1)));
        }

        [Fact]
        public void RemoveEntriesUsesExclusiveUtcDateBoundary()
        {
            var cacheRoot = Path.Combine(_root, "cache");
            var volume = CreateVolume(0xA1B2C3D4, "CARD");
            var oldKey = Put(cacheRoot, volume, "old.jpg", 10, Utc(2026, 6, 1));
            var boundaryKey = Put(cacheRoot, volume, "boundary.jpg", 20, Utc(2026, 7, 1));
            var newKey = Put(cacheRoot, volume, "new.jpg", 30, Utc(2026, 7, 2));
            var manager = new ExifCacheManager();

            var removed = manager.RemoveEntriesLastUsedBefore(
                cacheRoot,
                volume.SerialNumber,
                Utc(2026, 7, 1));

            Assert.Equal(1, removed);
            var store = new ExifCacheStore(cacheRoot);
            ExifCacheSession session;
            string warning;
            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                PhotoMetadataReadResult result;
                Assert.False(session.TryGet(oldKey, Utc(2026, 7, 3), out result));
                Assert.True(session.TryGet(boundaryKey, Utc(2026, 7, 3), out result));
                Assert.True(session.TryGet(newKey, Utc(2026, 7, 3), out result));
                Assert.Equal(2, session.Count);
            }
        }

        [Fact]
        public void DeleteCardRemovesOnlySelectedVolumeFolder()
        {
            var cacheRoot = Path.Combine(_root, "cache");
            var first = CreateVolume(0xA1B2C3D4, "FIRST");
            var second = CreateVolume(0x01020304, "SECOND");
            Put(cacheRoot, first, "first.jpg", 10, Utc(2026, 7, 14));
            Put(cacheRoot, second, "second.jpg", 10, Utc(2026, 7, 14));
            File.WriteAllText(Path.Combine(cacheRoot, "unrelated.txt"), "keep");

            new ExifCacheManager().DeleteCard(cacheRoot, first.SerialNumber);

            Assert.False(Directory.Exists(Path.Combine(cacheRoot, first.SerialNumberHex)));
            Assert.True(Directory.Exists(Path.Combine(cacheRoot, second.SerialNumberHex)));
            Assert.True(File.Exists(Path.Combine(cacheRoot, "unrelated.txt")));
        }

        [Fact]
        public void InspectDeduplicatesRootsAndKeepsMissingPreviousRootVisible()
        {
            var current = Path.Combine(_root, "current");
            var missing = Path.Combine(_root, "missing");
            Directory.CreateDirectory(current);

            var roots = new ExifCacheManager().Inspect(
                current,
                new[] { current.ToUpperInvariant(), missing, missing });

            Assert.Equal(2, roots.Count);
            Assert.True(roots[0].IsCurrent);
            Assert.False(roots[1].IsCurrent);
            Assert.False(roots[1].Exists);
            Assert.Equal(Path.GetFullPath(missing), roots[1].RootPath, ignoreCase: true);
        }

        [Fact]
        public void CorruptMetadataIsRebuiltWithoutDiscardingEntries()
        {
            var cacheRoot = Path.Combine(_root, "cache");
            var volume = CreateVolume(0xA1B2C3D4, "CARD");
            Put(cacheRoot, volume, "photo.jpg", 10, Utc(2026, 7, 14));
            var metadataPath = Path.Combine(cacheRoot, volume.SerialNumberHex, "meta.json");
            File.WriteAllText(metadataPath, "not-json");

            var card = Assert.Single(Assert.Single(new ExifCacheManager().Inspect(cacheRoot, null)).Cards);

            Assert.Equal(1, card.EntryCount);
            Assert.Equal(Utc(2026, 7, 14), card.FirstUsedUtcDate);
            Assert.Contains("SchemaVersion", File.ReadAllText(metadataPath));
        }

        [Fact]
        public void InspectRepairsLastUsedDateWhenEntriesWereSavedAfterMetadata()
        {
            var cacheRoot = Path.Combine(_root, "cache");
            var volume = CreateVolume(0xA1B2C3D4, "CARD");
            var key = Put(cacheRoot, volume, "photo.jpg", 10, Utc(2026, 7, 14));
            var metadataPath = Path.Combine(cacheRoot, volume.SerialNumberHex, "meta.json");
            var oldMetadata = File.ReadAllBytes(metadataPath);
            var store = new ExifCacheStore(cacheRoot);
            ExifCacheSession session;
            string warning;
            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
            {
                PhotoMetadataReadResult result;
                Assert.True(session.TryGet(key, Utc(2026, 7, 20), out result));
            }
            File.WriteAllBytes(metadataPath, oldMetadata);

            var card = Assert.Single(Assert.Single(new ExifCacheManager().Inspect(cacheRoot, null)).Cards);

            Assert.Equal(Utc(2026, 7, 20), card.LastUsedUtcDate);
        }

        [Fact]
        public void ManagementOperationDoesNotRunWhenVolumeLockIsBusy()
        {
            var cacheRoot = Path.Combine(_root, "cache");
            var volume = CreateVolume(0xA1B2C3D4, "CARD");
            Put(cacheRoot, volume, "photo.jpg", 10, Utc(2026, 7, 14));
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
                var manager = new ExifCacheManager(TimeSpan.FromMilliseconds(50));

                Assert.Throws<InvalidOperationException>(() =>
                    manager.DeleteCard(cacheRoot, volume.SerialNumber));
                Assert.True(Directory.Exists(Path.Combine(cacheRoot, volume.SerialNumberHex)));
            }
            finally
            {
                release.Set();
                thread.Join();
                ready.Dispose();
                release.Dispose();
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private VolumeInfo CreateVolume(uint serial, string label) =>
            new VolumeInfo(_root, serial, label, "exFAT", DriveType.Removable, 64UL * 1024 * 1024 * 1024);

        private static ExifCacheKey Put(
            string cacheRoot,
            VolumeInfo volume,
            string name,
            long size,
            DateTime utcDate)
        {
            var key = ExifCacheKey.Create(
                volume,
                Path.Combine(volume.RootPath, name),
                size,
                Utc(2026, 7, 14));
            var store = new ExifCacheStore(cacheRoot);
            ExifCacheSession session;
            string warning;
            Assert.True(store.TryOpen(volume, out session, out warning), warning);
            using (session)
                session.Put(key, PhotoMetadataReadResult.NoMetadata(), utcDate);
            return key;
        }

        private static DateTime Utc(int year, int month, int day) =>
            new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }
}
