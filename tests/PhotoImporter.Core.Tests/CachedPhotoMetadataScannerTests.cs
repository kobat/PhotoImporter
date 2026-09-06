using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class CachedPhotoMetadataScannerTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "PhotoImporter.Tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void ConfirmationCountsOnlyMissesAndRunsBeforeAnyPhysicalRead()
        {
            var cached = CreateFile("cached.jpg", "cached");
            var raw = CreateFile("pair.arw", "raw");
            var jpeg = CreateFile("pair.jpg", "jpeg");
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var volume = CreateVolume();
            var reader = new StubReader(_ => PhotoMetadataReadResult.NoMetadata());
            var scanner = new CachedPhotoMetadataScanner(reader);
            scanner.Scan(RawJpegAnalysisPlan.Create(new[] { cached }), volume, store, UtcNow());
            var confirmations = 0;
            var plan = RawJpegAnalysisPlan.Create(new[] { cached, raw, jpeg });
            var result = scanner.Scan(plan, volume, store, UtcNow(), confirmFileReads: count =>
            {
                confirmations++;
                Assert.Equal(1, count);
                Assert.Equal(1, reader.ReadCount);
                return true;
            });
            Assert.Equal(1, confirmations);
            Assert.Equal(2, reader.ReadCount);
            Assert.Equal(1, result.CacheHits);
            scanner.Scan(plan, volume, store, UtcNow(), confirmFileReads: _ =>
                throw new Exception("All cache hits must skip confirmation."));
            Assert.Equal(2, reader.ReadCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DecliningConfirmationDoesNotReadAnyFile(bool useCache)
        {
            var first = CreateFile("first.jpg", "first");
            var second = CreateFile("second.jpg", "second");
            var reader = new StubReader(_ => PhotoMetadataReadResult.NoMetadata());
            var confirmations = 0;
            Assert.Throws<OperationCanceledException>(() => new CachedPhotoMetadataScanner(reader).Scan(
                RawJpegAnalysisPlan.Create(new[] { first, second }), CreateVolume(),
                useCache ? new ExifCacheStore(Path.Combine(_root, "cache")) : null, UtcNow(),
                confirmFileReads: count => { confirmations++; Assert.Equal(2, count); return false; }));
            Assert.Equal(1, confirmations);
            Assert.Equal(0, reader.ReadCount);
        }

        [Fact]
        public void ConfirmationReleasesCacheLockAndRetainsHitsIfCacheBecomesUnavailable()
        {
            var cached = CreateFile("cached.jpg", "cached");
            var missing = CreateFile("missing.jpg", "missing");
            var store = new ExifCacheStore(Path.Combine(_root, "cache"), TimeSpan.Zero);
            var volume = CreateVolume();
            var reader = new StubReader(_ => PhotoMetadataReadResult.NoMetadata());
            var scanner = new CachedPhotoMetadataScanner(reader);
            scanner.Scan(RawJpegAnalysisPlan.Create(new[] { cached }), volume, store, UtcNow());
            var result = scanner.Scan(RawJpegAnalysisPlan.Create(new[] { cached, missing }), volume, store, UtcNow(),
                confirmFileReads: count =>
                {
                    Assert.Equal(1, count);
                    Task.Run(() =>
                    {
                        ExifCacheSession session;
                        string warning;
                        Assert.True(store.TryOpen(volume, out session, out warning), warning);
                        session.Dispose();
                    }).GetAwaiter().GetResult();
                    // Only this test's private cache is removed to simulate cache loss while waiting.
                    Directory.Delete(store.CacheRoot, true);
                    File.WriteAllText(store.CacheRoot, "unavailable");
                    return true;
                });
            Assert.Equal(1, result.CacheHits);
            Assert.Equal(2, reader.ReadCount);
            Assert.Equal(2, result.Results.Count);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void UnsupportedExtensionDoesNotRequireConfirmationOrPhysicalReading()
        {
            var file = CreateFile("notes.txt", "notes");
            var reader = new StubReader(_ => throw new Exception("File contents must not be read."));
            var result = new CachedPhotoMetadataScanner(reader).Scan(
                RawJpegAnalysisPlan.Create(new[] { file }), null, null, UtcNow(),
                confirmFileReads: _ => throw new Exception("No physical reads require confirmation."));
            Assert.Equal(PhotoMetadataReadStatus.Unsupported, result.Results[file].Status);
            Assert.Equal(0, reader.ReadCount);
        }

        [Fact]
        public void CancellationDuringConfirmationDoesNotReadAnyFile()
        {
            var photo = CreateFile("photo.jpg", "data");
            var reader = new StubReader(_ => PhotoMetadataReadResult.NoMetadata());
            using (var cancellation = new CancellationTokenSource())
                Assert.Throws<OperationCanceledException>(() => new CachedPhotoMetadataScanner(reader).Scan(
                    RawJpegAnalysisPlan.Create(new[] { photo }), null, null, UtcNow(),
                    cancellationToken: cancellation.Token,
                    confirmFileReads: _ => { cancellation.Cancel(); return true; }));
            Assert.Equal(0, reader.ReadCount);
        }

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
            var progress = new CapturingProgress<PhotoMetadataScanProgress>();

            Assert.Throws<OperationCanceledException>(() => scanner.Scan(
                plan, volume, store, UtcNow(), progress, cancellation.Token));

            var resumed = scanner.Scan(plan, volume, store, UtcNow());

            Assert.Equal(3, reader.ReadCount);
            Assert.Equal(2, resumed.CacheHits);
            Assert.Equal(3, resumed.Results.Count);
            Assert.Contains(progress.Values, item => item.Phase == PhotoMetadataScanPhase.SavingCache);
            Assert.DoesNotContain(progress.Values, item => item.Phase == PhotoMetadataScanPhase.Completed);
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

        [Fact]
        public void ProgressReportsPhasesAndMonotonicReadingCounts()
        {
            var files = new[]
            {
                CreateFile("DCIM/001.jpg", "first"),
                CreateFile("DCIM/002.jpg", "second"),
                CreateFile("DCIM/003.jpg", "third")
            };
            var progress = new CapturingProgress<PhotoMetadataScanProgress>();
            var scanner = new CachedPhotoMetadataScanner(
                new StubReader(_ => PhotoMetadataReadResult.Success(CreateMetadata())));

            scanner.Scan(
                RawJpegAnalysisPlan.Create(files),
                null,
                null,
                UtcNow(),
                progress);

            Assert.Equal(PhotoMetadataScanPhase.Preparing, progress.Values.First().Phase);
            Assert.Equal(PhotoMetadataScanPhase.Completed, progress.Values.Last().Phase);
            var reading = progress.Values
                .Where(item => item.Phase == PhotoMetadataScanPhase.Reading)
                .ToList();
            Assert.Equal(0, reading.First().CompletedFiles);
            Assert.Equal(files.Length, reading.Last().CompletedFiles);
            Assert.All(reading, item => Assert.Equal(files.Length, item.TotalFiles));
            Assert.True(reading.Zip(reading.Skip(1),
                (first, second) => first.CompletedFiles <= second.CompletedFiles).All(value => value));
        }

        [Fact]
        public void CachedProgressIncludesSavingPhaseAndCacheHits()
        {
            var photo = CreateFile("DCIM/photo.jpg", "jpeg-data");
            var plan = RawJpegAnalysisPlan.Create(new[] { photo });
            var volume = CreateVolume();
            var store = new ExifCacheStore(Path.Combine(_root, "cache"));
            var scanner = new CachedPhotoMetadataScanner(
                new StubReader(_ => PhotoMetadataReadResult.Success(CreateMetadata())));
            scanner.Scan(plan, volume, store, UtcNow());
            var progress = new CapturingProgress<PhotoMetadataScanProgress>();

            scanner.Scan(plan, volume, store, UtcNow().AddDays(1), progress);

            Assert.Contains(progress.Values, item =>
                item.Phase == PhotoMetadataScanPhase.Reading &&
                item.CompletedFiles == 1 &&
                item.CacheHits == 1);
            Assert.Contains(progress.Values, item => item.Phase == PhotoMetadataScanPhase.SavingCache);
            Assert.Equal(PhotoMetadataScanPhase.Completed, progress.Values.Last().Phase);
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

        private sealed class CapturingProgress<T> : IProgress<T>
        {
            public List<T> Values { get; } = new List<T>();

            public void Report(T value) => Values.Add(value);
        }
    }
}
