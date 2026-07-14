using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class CachedPhotoMetadataScannerTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "PhotoImporter.Tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void SecondScanUsesCachedMetadataWithoutReadingFileAgain()
        {
            var photo = CreateFile("DCIM/photo.jpg", "jpeg-data");
            var plan = RawJpegAnalysisPlan.Create(new[] { photo });
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var reader = new StubReader(_ => PhotoMetadataReadResult.Success(CreateMetadata()));
            var scanner = new CachedPhotoMetadataScanner(reader);

            var first = scanner.Scan(plan, volume, store, UtcNow());
            var second = scanner.Scan(plan, volume, store, UtcNow().AddDays(1));

            Assert.Equal(1, reader.ReadCount);
            Assert.Equal(0, first.CacheHits);
            Assert.Equal(1, second.CacheHits);
            Assert.Equal("Camera", second.Results[photo].Metadata.CameraMake);
        }

        [Fact]
        public void JpegPairSharesOneCachedPhysicalResult()
        {
            var raw = CreateFile("DCIM/photo.arw", "raw-data");
            var jpeg = CreateFile("DCIM/photo.jpg", "jpeg-data");
            var plan = RawJpegAnalysisPlan.Create(new[] { raw, jpeg });
            var reader = new StubReader(_ => PhotoMetadataReadResult.Success(CreateMetadata()));
            var scanner = new CachedPhotoMetadataScanner(reader);
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));

            scanner.Scan(plan, CreateVolume(), store, UtcNow());
            var cached = scanner.Scan(plan, CreateVolume(), store, UtcNow());

            Assert.Equal(1, reader.ReadCount);
            Assert.Equal(1, cached.CacheHits);
            Assert.Single(cached.Results);
            Assert.True(cached.Results.ContainsKey(jpeg));
        }

        [Fact]
        public void FileChangedWhileReadingBecomesErrorAndIsNotCached()
        {
            var photo = CreateFile("photo.jpg", "before");
            var plan = RawJpegAnalysisPlan.Create(new[] { photo });
            var reader = new StubReader(path =>
            {
                File.AppendAllText(path, "-changed");
                return PhotoMetadataReadResult.Success(CreateMetadata());
            });
            var scanner = new CachedPhotoMetadataScanner(reader);
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));

            var result = scanner.Scan(plan, CreateVolume(), store, UtcNow());

            Assert.Equal(PhotoMetadataReadStatus.ReadError, result.Results[photo].Status);
            Assert.Contains("変更", result.Results[photo].Error.Message);
            Assert.False(File.Exists(Path.Combine(store.CacheRoot, CreateVolume().SerialNumberHex, "entries.tsv")));
        }

        [Fact]
        public void CacheSaveFailureKeepsMetadataAndReturnsWarning()
        {
            var photo = CreateFile("photo.jpg", "data");
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var entriesPath = Path.Combine(store.CacheRoot, volume.SerialNumberHex, "entries.tsv");
            var reader = new StubReader(_ =>
            {
                Directory.CreateDirectory(entriesPath);
                return PhotoMetadataReadResult.Success(CreateMetadata());
            });

            var result = new CachedPhotoMetadataScanner(reader).Scan(
                RawJpegAnalysisPlan.Create(new[] { photo }), volume, store, UtcNow());

            Assert.Equal(PhotoMetadataReadStatus.Success, result.Results[photo].Status);
            Assert.Contains(result.Warnings, warning => warning.Contains("保存できませんでした"));
        }

        [Fact]
        public void CancellationAfterReadCachesCompletedFilesForNextScan()
        {
            var firstPhoto = CreateFile("DCIM/001.jpg", "first");
            var secondPhoto = CreateFile("DCIM/002.jpg", "second");
            var thirdPhoto = CreateFile("DCIM/003.jpg", "third");
            var plan = RawJpegAnalysisPlan.Create(new[] { firstPhoto, secondPhoto, thirdPhoto });
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var cancellation = new CancellationTokenSource();
            var reads = 0;
            var reader = new StubReader(_ =>
            {
                reads++;
                if (reads == 2) cancellation.Cancel();
                return PhotoMetadataReadResult.Success(CreateMetadata());
            });
            var scanner = new CachedPhotoMetadataScanner(reader);

            Assert.Throws<OperationCanceledException>(() => scanner.Scan(
                plan, volume, store, UtcNow(), null, cancellation.Token));

            var resumed = scanner.Scan(plan, volume, store, UtcNow());

            Assert.Equal(3, reader.ReadCount);
            Assert.Equal(2, resumed.CacheHits);
            Assert.Equal(3, resumed.Results.Count);
        }

        [Fact]
        public void AlreadyCancelledScanDoesNotReadMetadata()
        {
            var photo = CreateFile("photo.jpg", "data");
            var reader = new StubReader(_ => PhotoMetadataReadResult.Success(CreateMetadata()));
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => new CachedPhotoMetadataScanner(reader).Scan(
                RawJpegAnalysisPlan.Create(new[] { photo }),
                CreateVolume(),
                new ExifCacheStore(Path.Combine(_root, "cache")),
                UtcNow(),
                null,
                cancellation.Token));

            Assert.Equal(0, reader.ReadCount);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string CreateFile(string relativePath, string contents)
        {
            var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
            return path;
        }

        private VolumeInfo CreateVolume() =>
            new VolumeInfo(_root, 0x1234ABCD, "CARD", "exFAT", DriveType.Removable, 1024);

        private static PhotoMetadata CreateMetadata() =>
            new PhotoMetadata(new DateTime(2026, 7, 14, 12, 0, 0), null,
                TakenDateOffsetState.Missing, "Camera", "Model", null);

        private static DateTime UtcNow() =>
            new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);

        private sealed class StubReader : IPhotoMetadataReader
        {
            private readonly Func<string, PhotoMetadataReadResult> _read;

            public StubReader(Func<string, PhotoMetadataReadResult> read)
            {
                _read = read;
            }

            public int ReadCount { get; private set; }

            public PhotoMetadataReadResult Read(string path)
            {
                ReadCount++;
                return _read(path);
            }
        }
    }
}
