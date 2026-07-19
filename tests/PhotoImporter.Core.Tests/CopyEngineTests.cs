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
        public void CopiesReadOnlySourceAndPreservesReadOnlyAttribute()
        {
            var source = CreateFile("protected.jpg", new byte[] { 1, 2, 3 });
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
            var destination = Path.Combine(_root, "out", "protected.jpg");
            var plan = CreatePlan(source, destination, null, false);

            var result = new CopyEngine().Execute(new[] { plan }, null, CancellationToken.None);

            Assert.Equal(CopyItemStatus.Copied, Assert.Single(result.Items).Status);
            Assert.True((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
        }

        [Fact]
        public void CancellationDeletesReadOnlyPartial()
        {
            var source = CreateFile("cancel.jpg", new byte[] { 4, 5, 6 });
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
            var destination = Path.Combine(_root, "out", "cancel.jpg");
            var cancellation = new CancellationTokenSource();
            var engine = new CopyEngine(new ManagedCopyOperation(() => cancellation.Cancel()));

            var result = engine.Execute(
                new[] { CreatePlan(source, destination, null, false) },
                null,
                cancellation.Token);

            Assert.True(result.Cancelled);
            Assert.Equal(CopyItemStatus.Cancelled, Assert.Single(result.Items).Status);
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination), "PI_*.partial"));
        }

        [Fact]
        public void MoveFailureDeletesSafeReadOnlyPartial()
        {
            var source = CreateFile("move-failure.jpg", new byte[] { 7, 8, 9 });
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
            var destination = Path.Combine(_root, "out", "move-failure.jpg");
            var engine = CreateEngineWithMove(FailMove);

            var result = engine.Execute(
                new[] { CreatePlan(source, destination, null, false) },
                null,
                CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal(CopyItemStatus.Failed, item.Status);
            Assert.Null(item.RecoveryPath);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination), "PI_*.partial"));
        }

        [Fact]
        public void UnsafePartialIsNotModifiedOrDeleted()
        {
            var directory = Path.Combine(_root, "out");
            var path = CreateFile("out\\not-owned.partial", new byte[] { 1 });
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

            var deleted = CopyEngine.TryDeleteSafePartial(path, directory, _root);

            Assert.False(deleted);
            Assert.True(File.Exists(path));
            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
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
        public void OverwritesReadOnlyDestination()
        {
            var source = CreateFile("replacement.jpg", new byte[] { 9, 8, 7 });
            var destination = CreateFile("out\\read-only.jpg", new byte[] { 1 });
            File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
            var destinationInfo = new FileInfo(destination);
            var snapshot = new DestinationFileSnapshot(
                destinationInfo.Length, destinationInfo.LastWriteTimeUtc);

            var result = new CopyEngine().Execute(
                new[] { CreatePlan(source, destination, snapshot, true) },
                null,
                CancellationToken.None);

            Assert.Equal(CopyItemStatus.Copied, Assert.Single(result.Items).Status);
            Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination));
            Assert.True((File.GetAttributes(destination) & FileAttributes.ReadOnly) == 0);
        }

        [Fact]
        public void MoveFailureRestoresReadOnlyDestination()
        {
            var source = CreateFile("restore-source.jpg", new byte[] { 2, 3 });
            var destination = CreateFile("out\\restore-destination.jpg", new byte[] { 1 });
            File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
            var destinationInfo = new FileInfo(destination);
            var snapshot = new DestinationFileSnapshot(
                destinationInfo.Length, destinationInfo.LastWriteTimeUtc);

            var result = CreateEngineWithMove(FailMove).Execute(
                new[] { CreatePlan(source, destination, snapshot, true) },
                null,
                CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal(CopyItemStatus.Failed, item.Status);
            Assert.Null(item.RecoveryPath);
            Assert.True((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination), "PI_*.partial"));
        }

        [Fact]
        public void UnconfirmedReadOnlyRestoreReturnsRecoveryPaths()
        {
            var source = CreateFile("unrestored-source.jpg", new byte[] { 2, 3 });
            var destination = CreateFile("out\\unrestored-destination.jpg", new byte[] { 1 });
            File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
            var destinationInfo = new FileInfo(destination);
            var snapshot = new DestinationFileSnapshot(
                destinationInfo.Length, destinationInfo.LastWriteTimeUtc);
            var engine = new CopyEngine(
                new ManagedCopyOperation(),
                FailMove,
                new FailingReadOnlyRestore(destination));

            var result = engine.Execute(
                new[] { CreatePlan(source, destination, snapshot, true) },
                null,
                CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal(CopyItemStatus.Failed, item.Status);
            Assert.NotNull(item.RecoveryPath);
            Assert.True(File.Exists(item.RecoveryPath));
            Assert.Contains(destination, item.Error);
            Assert.True((File.GetAttributes(destination) & FileAttributes.ReadOnly) == 0);
        }

        [Fact]
        public void RejectsDestinationCreatedWhileCopying()
        {
            var source = CreateFile("appeared-source.jpg", new byte[] { 1, 2 });
            var destination = Path.Combine(_root, "out", "appeared.jpg");
            var plan = CreatePlan(source, destination, null, false);
            var engine = new CopyEngine(new ManagedCopyOperation(
                () => File.WriteAllBytes(destination, new byte[] { 9 })));

            var result = engine.Execute(new[] { plan }, null, CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal(CopyItemStatus.Failed, item.Status);
            Assert.Contains("再スキャン", item.Error);
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination), "PI_*.partial"));
        }

        [Fact]
        public void RejectsDestinationChangedWhileCopying()
        {
            var source = CreateFile("changed-destination-source.jpg", new byte[] { 1, 2 });
            var destination = CreateFile("out\\changed-destination.jpg", new byte[] { 3 });
            var destinationInfo = new FileInfo(destination);
            var snapshot = new DestinationFileSnapshot(
                destinationInfo.Length, destinationInfo.LastWriteTimeUtc);
            var plan = CreatePlan(source, destination, snapshot, true);
            var engine = new CopyEngine(new ManagedCopyOperation(
                () => File.AppendAllText(destination, "changed")));

            var result = engine.Execute(new[] { plan }, null, CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal(CopyItemStatus.Failed, item.Status);
            Assert.Contains("再スキャン", item.Error);
            Assert.EndsWith("changed", File.ReadAllText(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination), "PI_*.partial"));
        }

        [Fact]
        public void MoveFailurePreservesPartialWhenDestinationStateIsUnknown()
        {
            var source = CreateFile("unknown-source.jpg", new byte[] { 1, 2 });
            var destination = Path.Combine(_root, "out", "unknown.jpg");
            MoveFileOperation failAfterDestinationAppears =
                (string partial, string final, uint flags, out int error) =>
                {
                    File.WriteAllBytes(final, new byte[] { 9 });
                    error = 5;
                    return false;
                };

            var result = CreateEngineWithMove(failAfterDestinationAppears).Execute(
                new[] { CreatePlan(source, destination, null, false) },
                null,
                CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal(CopyItemStatus.Failed, item.Status);
            Assert.NotNull(item.RecoveryPath);
            Assert.True(File.Exists(item.RecoveryPath));
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(destination));
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

        private CopyPlanItem CreatePlan(
            string source,
            string destination,
            DestinationFileSnapshot destinationSnapshot,
            bool overwrite)
        {
            var sourceInfo = new FileInfo(source);
            return new CopyPlanItem(
                source,
                _root,
                destination,
                new FileSnapshot(sourceInfo.Length, sourceInfo.LastWriteTimeUtc),
                destinationSnapshot,
                FileSystemTimestampPolicy.Create("NTFS"),
                overwrite);
        }

        private static CopyEngine CreateEngineWithMove(MoveFileOperation moveFile) =>
            new CopyEngine(new ManagedCopyOperation(), moveFile, new FileAttributeOperations());

        private static bool FailMove(
            string partial,
            string destination,
            uint flags,
            out int error)
        {
            error = 5;
            return false;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class ManagedCopyOperation : ICopyFileOperation
        {
            private readonly Action _afterCopy;

            internal ManagedCopyOperation(Action afterCopy = null) => _afterCopy = afterCopy;

            public void Copy(
                string sourcePath,
                string destinationPath,
                CancellationToken cancellationToken,
                Action<long> progress)
            {
                var source = new FileInfo(sourcePath);
                var sourceAttributes = source.Attributes;
                File.Copy(sourcePath, destinationPath, false);
                File.SetAttributes(destinationPath, sourceAttributes & ~FileAttributes.ReadOnly);
                File.SetLastWriteTimeUtc(destinationPath, source.LastWriteTimeUtc);
                File.SetAttributes(destinationPath, sourceAttributes);
                if (progress != null) progress(source.Length);
                if (_afterCopy != null) _afterCopy();
            }
        }

        private sealed class FailingReadOnlyRestore : IFileAttributeOperations
        {
            private readonly string _destination;

            internal FailingReadOnlyRestore(string destination) => _destination = destination;

            public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

            public void SetAttributes(string path, FileAttributes attributes)
            {
                if (string.Equals(path, _destination, StringComparison.OrdinalIgnoreCase) &&
                    (attributes & FileAttributes.ReadOnly) != 0)
                    throw new IOException("Simulated attribute restoration failure.");
                File.SetAttributes(path, attributes);
            }
        }
    }
}
