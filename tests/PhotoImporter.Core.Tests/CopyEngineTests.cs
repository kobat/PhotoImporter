using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PhotoImporter.Core.Copying;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Templates;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class CopyEngineTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoImporterTests_" + Guid.NewGuid().ToString("N"));

        public CopyEngineTests() => Directory.CreateDirectory(_root);

        [Fact]
        public void CopiesThroughPartialAndFinalizesExpectedMetadata()
        {
            var source = CreateFile("source.jpg", new byte[] { 1, 2, 3, 4 });
            var destination = Path.Combine(_root, "out", "photo.jpg");
            var sourceInfo = new FileInfo(source);
            var plan = new CopyPlanItem(
                source,
                _root,
                destination,
                new FileSnapshot(sourceInfo.Length, sourceInfo.LastWriteTimeUtc),
                null,
                FileSystemTimestampPolicy.Create("NTFS"),
                false);

            var result = new CopyEngine().Execute(new[] { plan }, null, CancellationToken.None);

            Assert.Equal(CopyItemStatus.Copied, Assert.Single(result.Items).Status);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination), "PI_*.partial"));
        }

        [Fact]
        public void RejectsChangedSourceWithoutCreatingDestination()
        {
            var source = CreateFile("changed.jpg", new byte[] { 1 });
            var info = new FileInfo(source);
            var plan = new CopyPlanItem(
                source,
                _root,
                Path.Combine(_root, "out", "changed.jpg"),
                new FileSnapshot(info.Length, info.LastWriteTimeUtc),
                null,
                FileSystemTimestampPolicy.Create("NTFS"),
                false);
            File.AppendAllText(source, "changed");

            var result = new CopyEngine().Execute(new[] { plan }, null, CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal(CopyItemStatus.Failed, item.Status);
            Assert.Contains("再スキャン", item.Error);
            Assert.False(File.Exists(plan.DestinationPath));
        }

        [Fact]
        public void OverwritesOnlyWhenDestinationMatchesSnapshot()
        {
            var source = CreateFile("new.jpg", new byte[] { 9, 8, 7 });
            var destination = CreateFile("out\\old.jpg", new byte[] { 1 });
            var sourceInfo = new FileInfo(source);
            var destinationInfo = new FileInfo(destination);
            var plan = new CopyPlanItem(
                source,
                _root,
                destination,
                new FileSnapshot(sourceInfo.Length, sourceInfo.LastWriteTimeUtc),
                new DestinationFileSnapshot(destinationInfo.Length, destinationInfo.LastWriteTimeUtc),
                FileSystemTimestampPolicy.Create("NTFS"),
                true);

            var result = new CopyEngine().Execute(new[] { plan }, null, CancellationToken.None);

            Assert.Equal(CopyItemStatus.Copied, Assert.Single(result.Items).Status);
            Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination));
        }

        [Fact]
        public void ContinuesAfterAFileError()
        {
            var missing = Path.Combine(_root, "missing.jpg");
            var good = CreateFile("good.jpg", new byte[] { 4, 2 });
            var goodInfo = new FileInfo(good);
            var items = new List<CopyPlanItem>
            {
                new CopyPlanItem(missing, _root, Path.Combine(_root, "out", "missing.jpg"),
                    new FileSnapshot(1, DateTime.UtcNow), null,
                    FileSystemTimestampPolicy.Create("NTFS"), false),
                new CopyPlanItem(good, _root, Path.Combine(_root, "out", "good.jpg"),
                    new FileSnapshot(goodInfo.Length, goodInfo.LastWriteTimeUtc), null,
                    FileSystemTimestampPolicy.Create("NTFS"), false)
            };

            var result = new CopyEngine().Execute(items, null, CancellationToken.None);

            Assert.Equal(new[] { CopyItemStatus.Failed, CopyItemStatus.Copied },
                result.Items.Select(item => item.Status).ToArray());
        }

        private string CreateFile(string relativePath, byte[] bytes)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
