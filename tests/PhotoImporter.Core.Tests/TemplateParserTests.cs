using PhotoImporter.Core.Templates;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class TemplateParserTests
    {
        [Fact]
        public void ParsesFileSystemTemplateWithoutExif()
        {
            var result = TemplateParser.Parse(@"{ModifiedDate:yyyy-MM-dd}\{FileName}{Sequence:4}{Extension}");

            Assert.True(result.IsValid);
            Assert.False(result.Template.RequiresExif);
            Assert.Equal(4, result.Template.SequenceWidth);
        }

        [Fact]
        public void RecognizesExifRequirementCaseInsensitively()
        {
            var result = TemplateParser.Parse(@"{takendate:yyyy}\{CAMERAMODEL}{extension}");

            Assert.True(result.IsValid);
            Assert.True(result.Template.RequiresExif);
        }

        [Fact]
        public void ParsesSourceRelativeDirectoryWithoutExif()
        {
            var result = TemplateParser.Parse(@"{SourceRelativeDirectory}\{OriginalName}");

            Assert.True(result.IsValid);
            Assert.False(result.Template.RequiresExif);
            Assert.Equal(TemplateTokenKind.SourceRelativeDirectory, result.Template.Parts[0].Token);
        }

        [Fact]
        public void ParsesSourceRelativeDirectoryDepth()
        {
            var result = TemplateParser.Parse(@"{SourceRelativeDirectory:2}\{OriginalName}");

            Assert.True(result.IsValid);
            Assert.Equal("2", result.Template.Parts[0].Format);
        }

        [Fact]
        public void ProtectedDoesNotRequireExifButExtendedTokensDo()
        {
            Assert.False(TemplateParser.Parse("{Protected}").Template.RequiresExif);
            Assert.True(TemplateParser.Parse("{Width}_{HasGps}_{CameraSerial}").Template.RequiresExif);
        }

        [Theory]
        [InlineData("{Width:D5}")]
        [InlineData("{Aperture:0.0}")]
        [InlineData("{ExposureTime:0.000000}")]
        [InlineData("{ShutterSpeed:1_250}")]
        [InlineData("{GpsLatitude:dms}")]
        [InlineData(@"{ModifiedDate:yyyy-MM-dd}\{TakenDate:yyyy}\{OriginalName}")]
        public void AcceptsExtendedFormats(string source)
        {
            Assert.True(TemplateParser.Parse(source).IsValid);
        }

        [Theory]
        [InlineData("", TemplateErrorCode.TemplateEmpty)]
        [InlineData("{Unknown}", TemplateErrorCode.UnknownToken)]
        [InlineData("{FileName", TemplateErrorCode.UnclosedToken)]
        [InlineData("FileName}", TemplateErrorCode.UnexpectedClosingBrace)]
        [InlineData("{}", TemplateErrorCode.TokenNameEmpty)]
        [InlineData("{FileName:x}", TemplateErrorCode.FormatNotSupported)]
        [InlineData("{SourceRelativeDirectory:x}", TemplateErrorCode.InvalidSourceRelativeDirectoryDepth)]
        [InlineData("{SourceRelativeDirectory:0}", TemplateErrorCode.InvalidSourceRelativeDirectoryDepth)]
        [InlineData("{SourceRelativeDirectory:01}", TemplateErrorCode.InvalidSourceRelativeDirectoryDepth)]
        [InlineData("{SourceRelativeDirectory:-1}", TemplateErrorCode.InvalidSourceRelativeDirectoryDepth)]
        [InlineData("{Sequence}{Sequence}", TemplateErrorCode.DuplicateSequenceToken)]
        [InlineData("{Sequence:0}", TemplateErrorCode.InvalidSequenceWidth)]
        [InlineData("{Sequence:10}", TemplateErrorCode.InvalidSequenceWidth)]
        [InlineData("{Sequence: 2}", TemplateErrorCode.InvalidSequenceWidth)]
        [InlineData("{TakenDateInTimeZone}", TemplateErrorCode.TimeZoneArgumentMissing)]
        [InlineData("{TakenDateInTimeZone:XYZ}", TemplateErrorCode.InvalidTimeZoneCode)]
        [InlineData("{TakenDateInTimeZone:UTC+15}", TemplateErrorCode.InvalidUtcOffset)]
        [InlineData("{TakenDateInTimeZone:UTC+9|}", TemplateErrorCode.InvalidDateFormat)]
        [InlineData("{ModifiedDate:%}", TemplateErrorCode.InvalidDateFormat)]
        [InlineData("{ModifiedDate:HH:mm}", TemplateErrorCode.InvalidDateFormat)]
        [InlineData("{Protected:x}", TemplateErrorCode.FormatNotSupported)]
        [InlineData("{Aperture:D5}", TemplateErrorCode.InvalidNumberFormat)]
        [InlineData("{ShutterSpeed:1/250}", TemplateErrorCode.InvalidNumberFormat)]
        [InlineData("{GpsLatitude:D6}", TemplateErrorCode.InvalidNumberFormat)]
        [InlineData("folder/file.jpg", TemplateErrorCode.InvalidLiteralCharacter)]
        [InlineData(@"\{OriginalName}", TemplateErrorCode.InvalidPathStructure)]
        [InlineData(@"folder\", TemplateErrorCode.InvalidPathStructure)]
        [InlineData(@"folder\\{OriginalName}", TemplateErrorCode.InvalidPathStructure)]
        [InlineData(@"folder\.\{OriginalName}", TemplateErrorCode.InvalidPathStructure)]
        [InlineData(@"folder\..\{OriginalName}", TemplateErrorCode.InvalidPathStructure)]
        [InlineData(@"{Sequence}\{OriginalName}", TemplateErrorCode.InvalidPathStructure)]
        public void ReturnsStructuredErrors(string source, TemplateErrorCode expected)
        {
            var result = TemplateParser.Parse(source);

            Assert.False(result.IsValid);
            Assert.Equal(expected, result.Error.Code);
        }

        [Fact]
        public void DecodesEscapedBraces()
        {
            var result = TemplateParser.Parse("{{{FileName}}}{Extension}");

            Assert.True(result.IsValid);
            Assert.Equal("{", result.Template.Parts[0].Literal);
            Assert.Equal("}", result.Template.Parts[2].Literal);
        }
    }
}
