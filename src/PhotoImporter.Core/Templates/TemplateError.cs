using System;

namespace PhotoImporter.Core.Templates
{
    public enum TemplateErrorCode
    {
        TemplateEmpty,
        UnclosedToken,
        UnexpectedClosingBrace,
        TokenNameEmpty,
        UnknownToken,
        FormatNotSupported,
        DuplicateSequenceToken,
        InvalidSequenceWidth,
        InvalidSourceRelativeDirectoryDepth,
        InvalidDateFormat,
        InvalidNumberFormat,
        TimeZoneArgumentMissing,
        InvalidTimeZoneCode,
        InvalidUtcOffset,
        InvalidLiteralCharacter,
        InvalidPathStructure,
        ReservedDeviceName,
        PathTooLong,
        SequenceExhausted,
        DestinationConflict
    }

    public sealed class TemplateError
    {
        public TemplateError(TemplateErrorCode code, int position, int length, string tokenName = null)
        {
            Code = code;
            Position = position;
            Length = length;
            TokenName = tokenName;
        }

        public TemplateErrorCode Code { get; }
        public int Position { get; }
        public int Length { get; }
        public string TokenName { get; }
    }

    public sealed class TemplateException : Exception
    {
        public TemplateException(TemplateError error)
            : base(error.Code.ToString())
        {
            Error = error;
        }

        public TemplateError Error { get; }
    }
}
