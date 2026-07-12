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

        [Theory]
        [InlineData("", TemplateErrorCode.TemplateEmpty)]
        [InlineData("{Unknown}", TemplateErrorCode.UnknownToken)]
        [InlineData("{FileName", TemplateErrorCode.UnclosedToken)]
        [InlineData("FileName}", TemplateErrorCode.UnexpectedClosingBrace)]
        [InlineData("{}", TemplateErrorCode.TokenNameEmpty)]
        [InlineData("{FileName:x}", TemplateErrorCode.FormatNotSupported)]
        [InlineData("{Sequence}{Sequence}", TemplateErrorCode.DuplicateSequenceToken)]
        [InlineData("{Sequence:0}", TemplateErrorCode.InvalidSequenceWidth)]
        [InlineData("{Sequence:10}", TemplateErrorCode.InvalidSequenceWidth)]
        [InlineData("{Sequence: 2}", TemplateErrorCode.InvalidSequenceWidth)]
        [InlineData("{TakenDateInTimeZone}", TemplateErrorCode.TimeZoneArgumentMissing)]
        [InlineData("folder/file.jpg", TemplateErrorCode.InvalidLiteralCharacter)]
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
