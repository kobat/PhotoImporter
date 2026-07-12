using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PhotoImporter.Core.Templates
{
    public static class TemplateParser
    {
        private static readonly IDictionary<string, TemplateTokenKind> Tokens =
            new Dictionary<string, TemplateTokenKind>(StringComparer.OrdinalIgnoreCase)
            {
                ["OriginalName"] = TemplateTokenKind.OriginalName,
                ["FileName"] = TemplateTokenKind.FileName,
                ["Extension"] = TemplateTokenKind.Extension,
                ["SourceRelativeDirectory"] = TemplateTokenKind.SourceRelativeDirectory,
                ["ModifiedDate"] = TemplateTokenKind.ModifiedDate,
                ["FileSize"] = TemplateTokenKind.FileSize,
                ["Sequence"] = TemplateTokenKind.Sequence,
                ["TakenDate"] = TemplateTokenKind.TakenDate,
                ["TakenDateLocal"] = TemplateTokenKind.TakenDateLocal,
                ["TakenDateInTimeZone"] = TemplateTokenKind.TakenDateInTimeZone,
                ["CameraMake"] = TemplateTokenKind.CameraMake,
                ["CameraModel"] = TemplateTokenKind.CameraModel,
                ["Lens"] = TemplateTokenKind.Lens
            };

        public static TemplateParseResult Parse(string source)
        {
            if (string.IsNullOrEmpty(source))
                return Failure(TemplateErrorCode.TemplateEmpty, 0, 0);

            var parts = new List<TemplatePart>();
            var literal = new StringBuilder();
            var literalStart = 0;
            var sequenceSeen = false;
            int? sequenceWidth = null;
            var requiresExif = false;

            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                if (character == '{' && index + 1 < source.Length && source[index + 1] == '{')
                {
                    if (literal.Length == 0) literalStart = index;
                    literal.Append('{');
                    index++;
                    continue;
                }

                if (character == '}' && index + 1 < source.Length && source[index + 1] == '}')
                {
                    if (literal.Length == 0) literalStart = index;
                    literal.Append('}');
                    index++;
                    continue;
                }

                if (character == '}')
                    return Failure(TemplateErrorCode.UnexpectedClosingBrace, index, 1);

                if (character != '{')
                {
                    if (IsInvalidLiteral(character))
                        return Failure(TemplateErrorCode.InvalidLiteralCharacter, index, 1);
                    if (literal.Length == 0) literalStart = index;
                    literal.Append(character);
                    continue;
                }

                FlushLiteral(parts, literal, literalStart);
                var close = source.IndexOf('}', index + 1);
                if (close < 0)
                    return Failure(TemplateErrorCode.UnclosedToken, index, source.Length - index);

                var nestedOpen = source.IndexOf('{', index + 1, close - index - 1);
                if (nestedOpen >= 0)
                    return Failure(TemplateErrorCode.UnclosedToken, index, close - index + 1);

                var content = source.Substring(index + 1, close - index - 1);
                var colon = content.IndexOf(':');
                var name = colon < 0 ? content : content.Substring(0, colon);
                var format = colon < 0 ? null : content.Substring(colon + 1);
                if (name.Length == 0)
                    return Failure(TemplateErrorCode.TokenNameEmpty, index, close - index + 1);

                TemplateTokenKind token;
                if (!Tokens.TryGetValue(name, out token))
                    return Failure(TemplateErrorCode.UnknownToken, index, close - index + 1, name);

                TemplateError validationError;
                if (!ValidateFormat(token, format, index, close - index + 1, out validationError))
                    return new TemplateParseResult(null, validationError);

                if (token == TemplateTokenKind.Sequence)
                {
                    if (sequenceSeen)
                        return Failure(TemplateErrorCode.DuplicateSequenceToken, index, close - index + 1, name);
                    sequenceSeen = true;
                    sequenceWidth = format == null ? 3 : int.Parse(format, CultureInfo.InvariantCulture);
                }

                if (token >= TemplateTokenKind.TakenDate)
                    requiresExif = true;

                parts.Add(TemplatePart.ForToken(token, format, index));
                index = close;
                literalStart = close + 1;
            }

            FlushLiteral(parts, literal, literalStart);
            return new TemplateParseResult(
                new ParsedTemplate(source, parts, requiresExif, sequenceWidth), null);
        }

        private static bool ValidateFormat(
            TemplateTokenKind token,
            string format,
            int position,
            int length,
            out TemplateError error)
        {
            error = null;
            if (token == TemplateTokenKind.TakenDateInTimeZone)
            {
                if (string.IsNullOrEmpty(format))
                {
                    error = new TemplateError(TemplateErrorCode.TimeZoneArgumentMissing, position, length, token.ToString());
                    return false;
                }
                return true;
            }

            if (token == TemplateTokenKind.Sequence)
            {
                int width;
                if (format != null &&
                    (!int.TryParse(format, NumberStyles.None, CultureInfo.InvariantCulture, out width) ||
                     width < 1 || width > 9 || width.ToString(CultureInfo.InvariantCulture) != format))
                {
                    error = new TemplateError(TemplateErrorCode.InvalidSequenceWidth, position, length, token.ToString());
                    return false;
                }
                return true;
            }

            if (token == TemplateTokenKind.SourceRelativeDirectory)
            {
                int depth;
                if (format != null &&
                    (!int.TryParse(format, NumberStyles.None, CultureInfo.InvariantCulture, out depth) ||
                     depth < 1 || depth.ToString(CultureInfo.InvariantCulture) != format))
                {
                    error = new TemplateError(
                        TemplateErrorCode.InvalidSourceRelativeDirectoryDepth,
                        position,
                        length,
                        token.ToString());
                    return false;
                }
                return true;
            }

            var isDate = token == TemplateTokenKind.ModifiedDate ||
                         token == TemplateTokenKind.TakenDate ||
                         token == TemplateTokenKind.TakenDateLocal;
            if (format != null && !isDate)
            {
                error = new TemplateError(TemplateErrorCode.FormatNotSupported, position, length, token.ToString());
                return false;
            }

            if (isDate && format != null && format.Length == 0)
            {
                error = new TemplateError(TemplateErrorCode.InvalidDateFormat, position, length, token.ToString());
                return false;
            }

            return true;
        }

        private static bool IsInvalidLiteral(char value) =>
            value < 32 || value == '<' || value == '>' || value == ':' || value == '"' ||
            value == '/' || value == '|' || value == '?' || value == '*';

        private static void FlushLiteral(ICollection<TemplatePart> parts, StringBuilder literal, int position)
        {
            if (literal.Length == 0) return;
            parts.Add(TemplatePart.ForLiteral(literal.ToString(), position));
            literal.Clear();
        }

        private static TemplateParseResult Failure(
            TemplateErrorCode code,
            int position,
            int length,
            string tokenName = null) =>
            new TemplateParseResult(null, new TemplateError(code, position, length, tokenName));
    }
}
