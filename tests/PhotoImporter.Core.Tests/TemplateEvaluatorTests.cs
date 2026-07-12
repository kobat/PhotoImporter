using System;
using PhotoImporter.Core.Templates;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class TemplateEvaluatorTests
    {
        private static readonly FileTemplateContext Context =
            new FileTemplateContext("photo.backup.JPG", new DateTime(2026, 7, 12, 14, 25, 30), 48201931);

        [Fact]
        public void EvaluatesFileSystemTokens()
        {
            var template = Parse(@"{ModifiedDate:yyyy-MM-dd}\{FileName}_{FileSize}{Extension}");

            var path = TemplateEvaluator.Evaluate(template, Context);

            Assert.Equal(@"2026-07-12\photo.backup_48201931.JPG", path);
        }

        [Fact]
        public void SequenceIsEmptyUntilASequenceNumberIsProvided()
        {
            var template = Parse("{FileName}{Sequence:4}{Extension}");

            Assert.Equal("photo.backup.JPG", TemplateEvaluator.Evaluate(template, Context));
            Assert.Equal("photo.backup_0002.JPG", TemplateEvaluator.Evaluate(template, Context, 2));
        }

        [Theory]
        [InlineData(@"\{OriginalName}", TemplateErrorCode.InvalidPathStructure)]
        [InlineData(@"folder\\{OriginalName}", TemplateErrorCode.InvalidPathStructure)]
        [InlineData(@"{Sequence}\{OriginalName}", TemplateErrorCode.InvalidPathStructure)]
        [InlineData("CON.jpg", TemplateErrorCode.ReservedDeviceName)]
        public void RejectsUnsafeGeneratedPaths(string source, TemplateErrorCode expected)
        {
            var exception = Assert.Throws<TemplateException>(() =>
                TemplateEvaluator.Evaluate(Parse(source), Context));

            Assert.Equal(expected, exception.Error.Code);
        }

        private static ParsedTemplate Parse(string source)
        {
            var result = TemplateParser.Parse(source);
            Assert.True(result.IsValid);
            return result.Template;
        }
    }
}
