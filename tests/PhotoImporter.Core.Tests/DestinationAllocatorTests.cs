using System;
using System.Collections.Generic;
using PhotoImporter.Core.Templates;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class DestinationAllocatorTests
    {
        [Fact]
        public void UsesBaseCandidateWhenItDoesNotExist()
        {
            var allocator = CreateAllocator(new Dictionary<string, long>());

            var result = allocator.Allocate(Context("A.jpg", 100));

            Assert.Equal("A.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.NotImported, result.Status);
        }

        [Fact]
        public void MarksSameSizeExistingCandidateAsImported()
        {
            var allocator = CreateAllocator(new Dictionary<string, long> { ["A.jpg"] = 100 });

            var result = allocator.Allocate(Context("A.jpg", 100));

            Assert.Equal("A.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.Imported, result.Status);
        }

        [Fact]
        public void ChoosesFirstAvailableSequenceForDifferentSizeConflict()
        {
            var allocator = CreateAllocator(new Dictionary<string, long>
            {
                ["A.jpg"] = 90,
                ["A_001.jpg"] = 80
            });

            var result = allocator.Allocate(Context("A.jpg", 100));

            Assert.Equal("A_002.jpg", result.RelativePath);
            Assert.Equal(DestinationStatus.NotImported, result.Status);
        }

        [Fact]
        public void DoesNotReuseAReservedPathWithinTheSameScan()
        {
            var allocator = CreateAllocator(new Dictionary<string, long>());

            var first = allocator.Allocate(Context("A.jpg", 100));
            var second = allocator.Allocate(Context("A.jpg", 100));

            Assert.Equal("A.jpg", first.RelativePath);
            Assert.Equal("A_001.jpg", second.RelativePath);
        }

        [Fact]
        public void ConflictingTemplateWithoutSequenceFails()
        {
            var template = Parse("{FileName}{Extension}");
            var allocator = new DestinationAllocator(template, new DictionaryLookup(
                new Dictionary<string, long> { ["A.jpg"] = 99 }));

            var exception = Assert.Throws<TemplateException>(() => allocator.Allocate(Context("A.jpg", 100)));

            Assert.Equal(TemplateErrorCode.DestinationConflict, exception.Error.Code);
        }

        private static DestinationAllocator CreateAllocator(IDictionary<string, long> files) =>
            new DestinationAllocator(Parse("{FileName}{Sequence}{Extension}"), new DictionaryLookup(files));

        private static ParsedTemplate Parse(string source)
        {
            var result = TemplateParser.Parse(source);
            Assert.True(result.IsValid);
            return result.Template;
        }

        private static FileTemplateContext Context(string name, long size) =>
            new FileTemplateContext(name, new DateTime(2026, 7, 12), size);

        private sealed class DictionaryLookup : IDestinationFileLookup
        {
            private readonly IDictionary<string, long> _files;

            public DictionaryLookup(IDictionary<string, long> files) => _files = files;

            public bool TryGetFileSize(string relativePath, out long fileSize) =>
                _files.TryGetValue(relativePath, out fileSize);
        }
    }
}
