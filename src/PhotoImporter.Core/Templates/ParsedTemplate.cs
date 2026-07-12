using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PhotoImporter.Core.Templates
{
    public enum TemplateTokenKind
    {
        OriginalName,
        FileName,
        Extension,
        ModifiedDate,
        FileSize,
        Sequence,
        TakenDate,
        TakenDateLocal,
        TakenDateInTimeZone,
        CameraMake,
        CameraModel,
        Lens
    }

    public sealed class TemplatePart
    {
        private TemplatePart(string literal, TemplateTokenKind? token, string format, int position)
        {
            Literal = literal;
            Token = token;
            Format = format;
            Position = position;
        }

        public string Literal { get; }
        public TemplateTokenKind? Token { get; }
        public string Format { get; }
        public int Position { get; }

        public static TemplatePart ForLiteral(string value, int position) =>
            new TemplatePart(value, null, null, position);

        public static TemplatePart ForToken(TemplateTokenKind token, string format, int position) =>
            new TemplatePart(null, token, format, position);
    }

    public sealed class ParsedTemplate
    {
        internal ParsedTemplate(string source, IList<TemplatePart> parts, bool requiresExif, int? sequenceWidth)
        {
            Source = source;
            Parts = new ReadOnlyCollection<TemplatePart>(parts);
            RequiresExif = requiresExif;
            SequenceWidth = sequenceWidth;
        }

        public string Source { get; }
        public IReadOnlyList<TemplatePart> Parts { get; }
        public bool RequiresExif { get; }
        public int? SequenceWidth { get; }
        public bool HasSequence => SequenceWidth.HasValue;
    }

    public sealed class TemplateParseResult
    {
        internal TemplateParseResult(ParsedTemplate template, TemplateError error)
        {
            Template = template;
            Error = error;
        }

        public ParsedTemplate Template { get; }
        public TemplateError Error { get; }
        public bool IsValid => Template != null;
    }
}
