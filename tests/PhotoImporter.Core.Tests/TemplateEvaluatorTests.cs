using System;
using PhotoImporter.Core.Metadata;
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

        [Fact]
        public void PreservesNestedSourceRelativeDirectory()
        {
            var template = Parse(@"{SourceRelativeDirectory}\{OriginalName}");
            var context = new FileTemplateContext(
                "DSC00001.ARW", new DateTime(2026, 7, 12), 100, @"DCIM\100MSDCF");

            Assert.Equal(@"DCIM\100MSDCF\DSC00001.ARW", TemplateEvaluator.Evaluate(template, context));
        }

        [Theory]
        [InlineData(1, @"100MSDCF\DSC00001.ARW")]
        [InlineData(2, @"DCIM\100MSDCF\DSC00001.ARW")]
        [InlineData(3, @"DCIM\100MSDCF\DSC00001.ARW")]
        public void SelectsTrailingSourceDirectoryDepth(int depth, string expected)
        {
            var template = Parse($@"{{SourceRelativeDirectory:{depth}}}\{{OriginalName}}");
            var context = new FileTemplateContext(
                "DSC00001.ARW", new DateTime(2026, 7, 12), 100, @"DCIM\100MSDCF");

            Assert.Equal(expected, TemplateEvaluator.Evaluate(template, context));
        }

        [Fact]
        public void OmitsSeparatorAfterEmptySourceRelativeDirectory()
        {
            var template = Parse(@"{SourceRelativeDirectory}\{OriginalName}");
            var context = new FileTemplateContext(
                "DSC00001.ARW", new DateTime(2026, 7, 12), 100, string.Empty);

            Assert.Equal("DSC00001.ARW", TemplateEvaluator.Evaluate(template, context));
        }

        [Theory]
        [InlineData(@"..\outside")]
        [InlineData(@"folder\\child")]
        [InlineData(@"\rooted")]
        public void RejectsUnsafeSourceRelativeDirectory(string relativeDirectory)
        {
            var template = Parse(@"{SourceRelativeDirectory}\{OriginalName}");
            var context = new FileTemplateContext(
                "DSC00001.ARW", new DateTime(2026, 7, 12), 100, relativeDirectory);

            var exception = Assert.Throws<TemplateException>(() => TemplateEvaluator.Evaluate(template, context));

            Assert.Equal(TemplateErrorCode.InvalidPathStructure, exception.Error.Code);
        }

        [Fact]
        public void RejectsUnsafeSourceRelativeDirectoryBeforeSelectingDepth()
        {
            var template = Parse(@"{SourceRelativeDirectory:1}\{OriginalName}");
            var context = new FileTemplateContext(
                "DSC00001.ARW", new DateTime(2026, 7, 12), 100, @"..\outside");

            var exception = Assert.Throws<TemplateException>(() =>
                TemplateEvaluator.Evaluate(template, context));

            Assert.Equal(TemplateErrorCode.InvalidPathStructure, exception.Error.Code);
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

        [Fact]
        public void EvaluatesExifValuesAndSanitizesMetadata()
        {
            var metadata = new PhotoMetadata(
                new DateTime(2026, 7, 12, 14, 25, 30),
                TimeSpan.FromHours(9),
                TakenDateOffsetState.Valid,
                "ACME/Camera",
                "Model:1",
                null);
            var context = new FileTemplateContext("photo.jpg", Context.ModifiedDate, 10, "", metadata);

            var path = TemplateEvaluator.Evaluate(
                Parse(@"{TakenDate:yyyy-MM-dd}\{CameraMake}_{CameraModel}_{Lens}{Extension}"), context);

            Assert.Equal(@"2026-07-12\ACME_Camera_Model_1_Unknown.jpg", path);
        }

        [Fact]
        public void ConvertsExifInstantToFixedTimeZone()
        {
            var metadata = new PhotoMetadata(
                new DateTime(2026, 7, 12, 12, 0, 0),
                TimeSpan.FromHours(-5),
                TakenDateOffsetState.Valid,
                null, null, null);
            var context = new FileTemplateContext("photo.jpg", Context.ModifiedDate, 10, "", metadata);

            var result = TemplateEvaluator.EvaluateDetailed(
                Parse("{TakenDateInTimeZone:UTC+9|yyyy-MM-dd_HH-mm}"), context);

            Assert.Equal("2026-07-13_02-00", result.RelativePath);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void KeepsWallClockAndWarnsWhenExifOffsetIsMissing()
        {
            var metadata = new PhotoMetadata(
                new DateTime(2026, 7, 12, 12, 0, 0),
                null,
                TakenDateOffsetState.Missing,
                null, null, null);
            var context = new FileTemplateContext("photo.jpg", Context.ModifiedDate, 10, "", metadata);

            var result = TemplateEvaluator.EvaluateDetailed(
                Parse("{TakenDateInTimeZone:JST|yyyy-MM-dd_HH-mm}"), context);

            Assert.Equal("2026-07-12_12-00", result.RelativePath);
            Assert.Equal(new[] { TemplateWarningCode.TakenDateOffsetMissing }, result.Warnings);
        }

        [Fact]
        public void FallsBackToUtcModifiedDateForSpecifiedTimeZone()
        {
            var context = new FileTemplateContext(
                "photo.jpg",
                new DateTime(2026, 7, 12, 0, 0, 0),
                10,
                "",
                PhotoMetadata.Empty,
                new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc));

            var result = TemplateEvaluator.EvaluateDetailed(
                Parse("{TakenDateInTimeZone:UTC+9|yyyy-MM-dd_HH-mm}"), context);

            Assert.Equal("2026-07-12_09-00", result.RelativePath);
            Assert.Equal(new[] { TemplateWarningCode.TakenDateFallbackToModifiedDate }, result.Warnings);
        }

        private static ParsedTemplate Parse(string source)
        {
            var result = TemplateParser.Parse(source);
            Assert.True(result.IsValid);
            return result.Template;
        }
    }
}
