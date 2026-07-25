using PhotoImporter.App;
using PhotoImporter.Core.Copying;
using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Templates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class LazyExifPreviewLoadTests : IDisposable
    {
        private readonly string _root;
        private readonly string _source;
        private readonly string _destination;

        public LazyExifPreviewLoadTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "PhotoImporter-LazyExif-" + Guid.NewGuid().ToString("N"));
            _source = Path.Combine(_root, "source");
            _destination = Path.Combine(_root, "destination");
            Directory.CreateDirectory(_source);
            Directory.CreateDirectory(_destination);
        }

        [Fact]
        public void Load_UsesFixedTargetSetAndCommitPreservesCopyPlanAndSelection()
        {
            var path = CreateFile("original.jpg", "original");
            var item = CreateItem(path, 7);
            item.IsSelected = false;
            var copyPlan = item.CopyPlan;
            var destinationPath = item.DestinationPath;
            var sourceSnapshot = copyPlan.SourceSnapshot;
            var destinationSnapshot = copyPlan.DestinationSnapshot;
            var reader = new CallbackMetadataReader(current =>
            {
                CreateFile("added-during-exif.jpg", "new");
                return PhotoMetadataReadResult.Success(CreateMetadata("FixedSet"));
            });
            var plan = LazyExifPreviewLoadPlan.Capture(
                _source,
                _destination,
                new[] { item },
                RawJpegAnalysisMode.JpegOnlyForPair);

            var result = plan.Load(
                false,
                Path.Combine(_root, "cache"),
                null,
                CancellationToken.None,
                new CachedPhotoMetadataScanner(reader));
            var commit = result.PrepareCommit(new[] { item }, MatchAll());
            commit.Apply();

            Assert.Single(reader.Paths);
            Assert.Equal(path, reader.Paths[0], StringComparer.OrdinalIgnoreCase);
            Assert.Single(result.Attachments);
            Assert.Same(copyPlan, item.CopyPlan);
            Assert.Same(sourceSnapshot, item.CopyPlan.SourceSnapshot);
            Assert.Same(destinationSnapshot, item.CopyPlan.DestinationSnapshot);
            Assert.Equal(destinationPath, item.DestinationPath);
            Assert.Equal(7, item.SequenceNumber);
            Assert.False(item.IsSelected);
            Assert.Equal("FixedSet", item.MetadataResult.Metadata.CameraMake);
            Assert.Equal("FixedSet", item.TemplateContext.Metadata.CameraMake);
        }

        [Fact]
        public void Load_UsesSameRawJpegSharingRuleAsManualScan()
        {
            var raw = CreateItem(CreateFile("pair.arw", "raw"), 1);
            var jpeg = CreateItem(CreateFile("pair.jpg", "jpeg"), 2);
            var reader = new CallbackMetadataReader(current =>
                PhotoMetadataReadResult.Success(CreateMetadata(Path.GetExtension(current))));
            var plan = LazyExifPreviewLoadPlan.Capture(
                _source,
                _destination,
                new[] { raw, jpeg },
                RawJpegAnalysisMode.JpegOnlyForPair);

            var result = plan.Load(
                false,
                Path.Combine(_root, "cache"),
                null,
                CancellationToken.None,
                new CachedPhotoMetadataScanner(reader));
            result.PrepareCommit(new[] { raw, jpeg }, MatchAll()).Apply();

            Assert.Single(reader.Paths);
            Assert.EndsWith("pair.jpg", reader.Paths[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("pair.jpg", raw.MetadataSourcePath);
            Assert.Equal("pair.jpg", jpeg.MetadataSourcePath);
            Assert.Same(raw.MetadataResult, jpeg.MetadataResult);
            Assert.Equal(jpeg.TemplateContext.ModifiedDateUtc, raw.TemplateContext.ExifSourceModifiedDateUtc);
        }

        [Fact]
        public void Load_WhenExistingTargetChangedBeforeRead_LeavesPreviewUntouched()
        {
            var path = CreateFile("changed.jpg", "before");
            var item = CreateItem(path, 1);
            var plan = LazyExifPreviewLoadPlan.Capture(
                _source,
                _destination,
                new[] { item },
                RawJpegAnalysisMode.AnalyzeBoth);
            File.AppendAllText(path, "after");

            var error = Assert.Throws<IOException>(() => plan.Load(
                false,
                Path.Combine(_root, "cache"),
                null,
                CancellationToken.None,
                new CachedPhotoMetadataScanner(new CallbackMetadataReader(
                    current => PhotoMetadataReadResult.NoMetadata()))));

            Assert.Contains("手動で再スキャン", error.Message);
            Assert.Null(item.MetadataResult);
            Assert.True(item.IsSelected);
        }

        [Fact]
        public void Load_WhenAnalysisSourceChangesDuringRead_LeavesPreviewUntouched()
        {
            var path = CreateFile("changing.jpg", "before");
            var item = CreateItem(path, 1);
            var plan = LazyExifPreviewLoadPlan.Capture(
                _source,
                _destination,
                new[] { item },
                RawJpegAnalysisMode.AnalyzeBoth);
            var reader = new CallbackMetadataReader(current =>
            {
                File.AppendAllText(current, "changed");
                return PhotoMetadataReadResult.NoMetadata();
            });

            Assert.Throws<IOException>(() => plan.Load(
                false,
                Path.Combine(_root, "cache"),
                null,
                CancellationToken.None,
                new CachedPhotoMetadataScanner(reader)));

            Assert.Null(item.MetadataResult);
            Assert.True(item.IsSelected);
        }

        [Fact]
        public void Load_WhenCancelled_LeavesPreviewUntouched()
        {
            var item = CreateItem(CreateFile("cancelled.jpg", "data"), 1);
            var plan = LazyExifPreviewLoadPlan.Capture(
                _source,
                _destination,
                new[] { item },
                RawJpegAnalysisMode.AnalyzeBoth);
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => plan.Load(
                false,
                Path.Combine(_root, "cache"),
                null,
                cancellation.Token));

            Assert.Null(item.MetadataResult);
            Assert.True(item.IsSelected);
        }

        [Fact]
        public void Load_WhenMetadataReaderFailsGlobally_LeavesPreviewUntouched()
        {
            var item = CreateItem(CreateFile("reader-error.jpg", "data"), 1);
            item.IsSelected = false;
            var plan = LazyExifPreviewLoadPlan.Capture(
                _source,
                _destination,
                new[] { item },
                RawJpegAnalysisMode.AnalyzeBoth);

            Assert.Throws<InvalidOperationException>(() => plan.Load(
                false,
                Path.Combine(_root, "cache"),
                null,
                CancellationToken.None,
                new CachedPhotoMetadataScanner(new CallbackMetadataReader(
                    current => throw new InvalidOperationException("reader failed")))));

            Assert.Null(item.MetadataResult);
            Assert.False(item.IsSelected);
        }

        [Fact]
        public void PrepareCommit_WhenFilterEvaluationFails_LeavesPreviewUntouched()
        {
            var item = CreateItem(CreateFile("filter-error.jpg", "data"), 1);
            var loadResult = new LazyExifPreviewLoadResult(
                new[] { item },
                new[]
                {
                    new LazyExifAttachment(
                        item,
                        null,
                        "filter-error.jpg",
                        DateTime.Now,
                        DateTime.UtcNow)
                },
                new string[0],
                0);
            var filter = new FilterSet(new FilterCondition[]
            {
                new StringFilterCondition(
                    FilterField.CameraMake,
                    "camera",
                    StringFilterMatchMode.Exact)
            }).Prepare().Filter;

            Assert.Throws<FilterEvaluationException>(() =>
                loadResult.PrepareCommit(new[] { item }, filter));

            Assert.Null(item.MetadataResult);
            Assert.True(item.IsSelected);
        }

        [Fact]
        public void PrepareCommit_WhenRowsChanged_LeavesOriginalPreviewUntouched()
        {
            var item = CreateItem(CreateFile("original.jpg", "data"), 1);
            var replacement = CreateItem(CreateFile("replacement.jpg", "data"), 2);
            var loadResult = new LazyExifPreviewLoadResult(
                new[] { item },
                new[]
                {
                    new LazyExifAttachment(
                        item,
                        PhotoMetadataReadResult.NoMetadata(),
                        "original.jpg",
                        DateTime.Now,
                        DateTime.UtcNow)
                },
                new string[0],
                0);

            Assert.Throws<InvalidOperationException>(() =>
                loadResult.PrepareCommit(new[] { replacement }, MatchAll()));

            Assert.Null(item.MetadataResult);
            Assert.True(item.IsSelected);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string CreateFile(string name, string contents)
        {
            var path = Path.Combine(_source, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private PreviewItem CreateItem(string fullPath, int sequence)
        {
            var info = new FileInfo(fullPath);
            var relativePath = Path.GetFileName(fullPath);
            var destinationPath = Path.Combine(_destination, relativePath);
            var context = new FileTemplateContext(
                info.Name,
                info.LastWriteTime,
                info.Length,
                string.Empty,
                PhotoMetadata.Empty,
                info.LastWriteTimeUtc);
            var copyPlan = new CopyPlanItem(
                info.FullName,
                _destination,
                destinationPath,
                new FileSnapshot(info.Length, info.LastWriteTimeUtc),
                null,
                FileSystemTimestampPolicy.Create("NTFS"),
                false);
            return new PreviewItem(
                relativePath,
                relativePath,
                DestinationStatus.NotImported,
                copyPlan,
                null,
                context,
                null,
                null,
                sequence);
        }

        private static PreparedFilter MatchAll() =>
            new FilterSet(new FilterCondition[0]).Prepare().Filter;

        private static PhotoMetadata CreateMetadata(string cameraMake) =>
            new PhotoMetadata(
                null,
                null,
                TakenDateOffsetState.Missing,
                cameraMake,
                null,
                null);

        private sealed class CallbackMetadataReader : IPhotoMetadataReader
        {
            private readonly Func<string, PhotoMetadataReadResult> _read;

            public CallbackMetadataReader(Func<string, PhotoMetadataReadResult> read)
            {
                _read = read;
            }

            public List<string> Paths { get; } = new List<string>();

            public PhotoMetadataReadResult Read(string path)
            {
                Paths.Add(path);
                return _read(path);
            }
        }
    }
}
