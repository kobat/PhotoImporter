using System;
using System.Collections.Generic;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Templates;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class DestinationAllocatorTests
    {
        private static readonly DateTime SourceTime = new DateTime(2026, 7, 12, 3, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void UsesBaseCandidateWhenItDoesNotExist()
        {
            var result = CreateAllocator(new Dictionary<string, DestinationFileSnapshot>())
                .Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal("A.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.NotImported, result.Status);
            Assert.Null(result.DestinationSnapshot);
            Assert.Null(result.SequenceNumber);
        }

        [Fact]
        public void MarksSameSizeAndTimestampAsImported()
        {
            var files = new Dictionary<string, DestinationFileSnapshot>
            {
                ["A.jpg"] = Snapshot(100, SourceTime)
            };

            var result = CreateAllocator(files).Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal(DestinationStatus.Imported, result.Status);
            Assert.Equal(100, result.DestinationSnapshot.FileSize);
        }

        [Fact]
        public void DifferentSizeIsNotImportedEvenWhenDestinationIsNewer()
        {
            var files = new Dictionary<string, DestinationFileSnapshot>
            {
                ["A.jpg"] = Snapshot(999, SourceTime.AddHours(1))
            };

            var result = CreateAllocator(files).Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal("A_001.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.NotImported, result.Status);
        }

        [Fact]
        public void SameSizeButNewerTimestampIsNotImported()
        {
            var files = new Dictionary<string, DestinationFileSnapshot>
            {
                ["A.jpg"] = Snapshot(100, SourceTime.AddTicks(1))
            };

            var result = CreateAllocator(files).Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal("A_001.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.NotImported, result.Status);
        }

        [Fact]
        public void ExFatRoundedTimestampIsImportedOnRescan()
        {
            var sourceTime = SourceTime.AddTicks(123456);
            var roundedTime = new DateTime(
                sourceTime.Ticks - sourceTime.Ticks % (TimeSpan.TicksPerMillisecond * 10),
                DateTimeKind.Utc);
            var files = new Dictionary<string, DestinationFileSnapshot>
            {
                ["A.jpg"] = Snapshot(100, roundedTime)
            };
            var allocator = new DestinationAllocator(
                Parse("{FileName}{Sequence}{Extension}"),
                new DictionaryLookup(files),
                FileSystemTimestampPolicy.Create("exFAT"));

            var result = allocator.Allocate(Context("A.jpg", 100), sourceTime);

            Assert.Equal("A.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.Imported, result.Status);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SequenceUsesNextNameForOlderDestinationRegardlessOfOverwrite(bool overwrite)
        {
            var files = new Dictionary<string, DestinationFileSnapshot>
            {
                ["A.jpg"] = Snapshot(100, SourceTime.AddMinutes(-1)),
                ["A_001.jpg"] = Snapshot(100, SourceTime.AddMinutes(-1))
            };

            var result = CreateAllocator(files, overwrite).Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal("A_002.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.NotImported, result.Status);
            Assert.Equal(2, result.SequenceNumber);
        }

        [Fact]
        public void WithoutSequenceOlderDestinationIsOverwriteWhenEnabled()
        {
            var allocator = new DestinationAllocator(
                Parse("{OriginalName}"),
                new DictionaryLookup(new Dictionary<string, DestinationFileSnapshot>
                {
                    ["A.jpg"] = Snapshot(90, SourceTime.AddSeconds(-1))
                }),
                FileSystemTimestampPolicy.Create("NTFS"),
                true);

            var result = allocator.Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal("A.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.Overwrite, result.Status);
        }

        [Fact]
        public void WithoutSequenceOlderDestinationIsConflictWhenOverwriteDisabled()
        {
            var allocator = new DestinationAllocator(
                Parse("{OriginalName}"),
                new DictionaryLookup(new Dictionary<string, DestinationFileSnapshot>
                {
                    ["A.jpg"] = Snapshot(90, SourceTime.AddSeconds(-1))
                }),
                FileSystemTimestampPolicy.Create("NTFS"));

            var result = allocator.Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal(DestinationStatus.Conflict, result.Status);
        }

        [Fact]
        public void DoesNotReuseAReservedPathWithinTheSameScan()
        {
            var allocator = CreateAllocator(new Dictionary<string, DestinationFileSnapshot>());

            var first = allocator.Allocate(Context("A.jpg", 100), SourceTime);
            var second = allocator.Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal("A.jpg", first.RelativePath);
            Assert.Equal("A_001.jpg", second.RelativePath);
            Assert.Null(first.SequenceNumber);
            Assert.Equal(1, second.SequenceNumber);
        }

        [Fact]
        public void DuplicateWithoutSequenceIsConflict()
        {
            var allocator = new DestinationAllocator(
                Parse("{OriginalName}"),
                new DictionaryLookup(new Dictionary<string, DestinationFileSnapshot>()),
                FileSystemTimestampPolicy.Create("NTFS"),
                true);

            allocator.Allocate(Context("A.jpg", 100), SourceTime);
            var second = allocator.Allocate(Context("A.jpg", 100), SourceTime);

            Assert.Equal(DestinationStatus.Conflict, second.Status);
        }

        [Fact]
        public void AcceptsDestinationPathAtConfiguredLimit()
        {
            Assert.Equal(32767, TemplateEvaluator.MaximumFullPathLength);
            const string destinationRoot = @"C:\photos";
            const string fileName = "A.jpg";
            var maximum = destinationRoot.Length + 1 + fileName.Length;
            var allocator = new DestinationAllocator(
                Parse("{OriginalName}"),
                new DictionaryLookup(new Dictionary<string, DestinationFileSnapshot>()),
                FileSystemTimestampPolicy.Create("NTFS"),
                destinationRoot: destinationRoot,
                maximumFullPathLength: maximum);

            var result = allocator.Allocate(Context(fileName, 100), SourceTime);

            Assert.Equal(fileName, result.RelativePath);
        }

        [Fact]
        public void RejectsDestinationPathOverConfiguredLimitIncludingRoot()
        {
            const string destinationRoot = @"C:\photos";
            const string fileName = "A.jpg";
            var maximum = destinationRoot.Length + fileName.Length;
            var allocator = new DestinationAllocator(
                Parse("{OriginalName}"),
                new DictionaryLookup(new Dictionary<string, DestinationFileSnapshot>()),
                FileSystemTimestampPolicy.Create("NTFS"),
                destinationRoot: destinationRoot,
                maximumFullPathLength: maximum);

            var exception = Assert.Throws<TemplateException>(() =>
                allocator.Allocate(Context(fileName, 100), SourceTime));

            Assert.Equal(TemplateErrorCode.PathTooLong, exception.Error.Code);
        }

        [Fact]
        public void IncludesLongDestinationRootForShortRelativePath()
        {
            var destinationRoot = @"C:\" + new string('r', 80);
            var allocator = new DestinationAllocator(
                Parse("{OriginalName}"),
                new DictionaryLookup(new Dictionary<string, DestinationFileSnapshot>()),
                FileSystemTimestampPolicy.Create("NTFS"),
                destinationRoot: destinationRoot,
                maximumFullPathLength: 80);

            var exception = Assert.Throws<TemplateException>(() =>
                allocator.Allocate(Context("A.jpg", 100), SourceTime));

            Assert.Equal(TemplateErrorCode.PathTooLong, exception.Error.Code);
        }

        private static DestinationAllocator CreateAllocator(
            IDictionary<string, DestinationFileSnapshot> files,
            bool overwrite = false) =>
            new DestinationAllocator(
                Parse("{FileName}{Sequence}{Extension}"),
                new DictionaryLookup(files),
                FileSystemTimestampPolicy.Create("NTFS"),
                overwrite);

        private static ParsedTemplate Parse(string source)
        {
            var result = TemplateParser.Parse(source);
            Assert.True(result.IsValid);
            return result.Template;
        }

        private static FileTemplateContext Context(string name, long size) =>
            new FileTemplateContext(name, SourceTime.ToLocalTime(), size);

        private static DestinationFileSnapshot Snapshot(long size, DateTime timeUtc) =>
            new DestinationFileSnapshot(size, timeUtc);

        private sealed class DictionaryLookup : IDestinationFileLookup
        {
            private readonly IDictionary<string, DestinationFileSnapshot> _files;

            public DictionaryLookup(IDictionary<string, DestinationFileSnapshot> files) => _files = files;

            public bool TryGetFile(string relativePath, out DestinationFileSnapshot snapshot) =>
                _files.TryGetValue(relativePath, out snapshot);
        }
    }
}
