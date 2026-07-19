using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
                ["Protected"] = TemplateTokenKind.Protected,
                ["Sequence"] = TemplateTokenKind.Sequence,
                ["TakenDate"] = TemplateTokenKind.TakenDate,
                ["TakenDateLocal"] = TemplateTokenKind.TakenDateLocal,
                ["TakenDateInTimeZone"] = TemplateTokenKind.TakenDateInTimeZone,
                ["CameraMake"] = TemplateTokenKind.CameraMake,
                ["CameraModel"] = TemplateTokenKind.CameraModel,
                ["CameraSerial"] = TemplateTokenKind.CameraSerial,
                ["Lens"] = TemplateTokenKind.Lens,
                ["Width"] = TemplateTokenKind.Width,
                ["Height"] = TemplateTokenKind.Height,
                ["ExifWidth"] = TemplateTokenKind.ExifWidth,
                ["ExifHeight"] = TemplateTokenKind.ExifHeight,
                ["Orientation"] = TemplateTokenKind.Orientation,
                ["Aperture"] = TemplateTokenKind.Aperture,
                ["ShutterSpeed"] = TemplateTokenKind.ShutterSpeed,
                ["ExposureTime"] = TemplateTokenKind.ExposureTime,
                ["Iso"] = TemplateTokenKind.Iso,
                ["FocalLength"] = TemplateTokenKind.FocalLength,
                ["FocalLength35mm"] = TemplateTokenKind.FocalLength35mm,
                ["Rating"] = TemplateTokenKind.Rating,
                ["HasGps"] = TemplateTokenKind.HasGps,
                ["GpsLatitude"] = TemplateTokenKind.GpsLatitude,
                ["GpsLongitude"] = TemplateTokenKind.GpsLongitude,
                ["GpsAltitude"] = TemplateTokenKind.GpsAltitude
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

                if (IsExifToken(token))
                    requiresExif = true;

                parts.Add(TemplatePart.ForToken(token, format, index));
                index = close;
                literalStart = close + 1;
            }

            FlushLiteral(parts, literal, literalStart);
            TemplateError structureError;
            if (!ValidateStaticPathStructure(parts, source.Length, out structureError))
                return new TemplateParseResult(null, structureError);

            return new TemplateParseResult(new ParsedTemplate(source, parts, requiresExif, sequenceWidth), null);
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
                TemplateTimeZone zone;
                string dateFormat;
                TemplateErrorCode errorCode;
                if (!TemplateTimeZone.TryParseFormat(format, out zone, out dateFormat, out errorCode))
                {
                    error = new TemplateError(errorCode, position, length, token.ToString());
                    return false;
                }
                return ValidateDateFormat(dateFormat, token, position, length, out error);
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

            if (token == TemplateTokenKind.ShutterSpeed)
            {
                if (format != null && format != "1-250s" && format != "1-250" &&
                    format != "1_250s" && format != "1_250")
                {
                    error = new TemplateError(TemplateErrorCode.InvalidNumberFormat, position, length, token.ToString());
                    return false;
                }
                return true;
            }

            if (token == TemplateTokenKind.GpsLatitude || token == TemplateTokenKind.GpsLongitude)
            {
                if (format != null && format != "dms" && format != "dm")
                {
                    error = new TemplateError(TemplateErrorCode.InvalidNumberFormat, position, length, token.ToString());
                    return false;
                }
                return true;
            }

            var isDate = token == TemplateTokenKind.ModifiedDate ||
                         token == TemplateTokenKind.TakenDate ||
                         token == TemplateTokenKind.TakenDateLocal;
            if (format != null && IsNumberToken(token))
            {
                try
                {
                    IFormattable sample = IsIntegerNumberToken(token) ? (IFormattable)123 : 1.25m;
                    var formatted = sample.ToString(format, CultureInfo.InvariantCulture);
                    if (formatted.Any(IsInvalidLiteral)) throw new FormatException();
                }
                catch (FormatException)
                {
                    error = new TemplateError(TemplateErrorCode.InvalidNumberFormat, position, length, token.ToString());
                    return false;
                }
                return true;
            }
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

            if (isDate && !ValidateDateFormat(format, token, position, length, out error))
                return false;

            return true;
        }

        private static bool ValidateDateFormat(
            string format,
            TemplateTokenKind token,
            int position,
            int length,
            out TemplateError error)
        {
            error = null;
            try
            {
                var formatted = new DateTime(2001, 2, 3, 4, 5, 6).ToString(
                    format ?? "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                if (formatted.Length == 0 || formatted.Any(IsInvalidLiteral))
                    throw new FormatException();
            }
            catch (FormatException)
            {
                error = new TemplateError(TemplateErrorCode.InvalidDateFormat, position, length, token.ToString());
                return false;
            }
            return true;
        }

        private static bool ValidateStaticPathStructure(
            IEnumerable<TemplatePart> parts,
            int sourceLength,
            out TemplateError error)
        {
            var sample = new StringBuilder();
            foreach (var part in parts)
            {
                if (!part.Token.HasValue)
                {
                    sample.Append(part.Literal);
                    continue;
                }

                switch (part.Token.Value)
                {
                    case TemplateTokenKind.Sequence:
                        break;
                    case TemplateTokenKind.ModifiedDate:
                    case TemplateTokenKind.TakenDate:
                    case TemplateTokenKind.TakenDateLocal:
                        sample.Append(new DateTime(2001, 2, 3, 4, 5, 6).ToString(
                            part.Format ?? "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
                        break;
                    case TemplateTokenKind.TakenDateInTimeZone:
                        TemplateTimeZone zone;
                        string dateFormat;
                        TemplateErrorCode errorCode;
                        TemplateTimeZone.TryParseFormat(part.Format, out zone, out dateFormat, out errorCode);
                        sample.Append(new DateTime(2001, 2, 3, 4, 5, 6).ToString(
                            dateFormat, CultureInfo.InvariantCulture));
                        break;
                    default:
                        sample.Append('x');
                        break;
                }
            }

            var value = sample.ToString();
            var elements = value.Split('\\');
            if (value.Length == 0 || value.StartsWith("\\", StringComparison.Ordinal) ||
                value.EndsWith("\\", StringComparison.Ordinal) ||
                elements.Any(item => item.Length == 0 || item == "." || item == ".."))
            {
                error = new TemplateError(TemplateErrorCode.InvalidPathStructure, 0, sourceLength);
                return false;
            }

            error = null;
            return true;
        }

        private static bool IsExifToken(TemplateTokenKind token)
        {
            switch (token)
            {
                case TemplateTokenKind.TakenDate:
                case TemplateTokenKind.TakenDateLocal:
                case TemplateTokenKind.TakenDateInTimeZone:
                case TemplateTokenKind.CameraMake:
                case TemplateTokenKind.CameraModel:
                case TemplateTokenKind.CameraSerial:
                case TemplateTokenKind.Lens:
                case TemplateTokenKind.Width:
                case TemplateTokenKind.Height:
                case TemplateTokenKind.ExifWidth:
                case TemplateTokenKind.ExifHeight:
                case TemplateTokenKind.Orientation:
                case TemplateTokenKind.Aperture:
                case TemplateTokenKind.ShutterSpeed:
                case TemplateTokenKind.ExposureTime:
                case TemplateTokenKind.Iso:
                case TemplateTokenKind.FocalLength:
                case TemplateTokenKind.FocalLength35mm:
                case TemplateTokenKind.Rating:
                case TemplateTokenKind.HasGps:
                case TemplateTokenKind.GpsLatitude:
                case TemplateTokenKind.GpsLongitude:
                case TemplateTokenKind.GpsAltitude:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsNumberToken(TemplateTokenKind token) =>
            token == TemplateTokenKind.Width || token == TemplateTokenKind.Height ||
            token == TemplateTokenKind.ExifWidth || token == TemplateTokenKind.ExifHeight ||
            token == TemplateTokenKind.Orientation || token == TemplateTokenKind.Aperture ||
            token == TemplateTokenKind.ExposureTime || token == TemplateTokenKind.Iso ||
            token == TemplateTokenKind.FocalLength || token == TemplateTokenKind.FocalLength35mm ||
            token == TemplateTokenKind.Rating || token == TemplateTokenKind.GpsAltitude;

        private static bool IsIntegerNumberToken(TemplateTokenKind token) =>
            token == TemplateTokenKind.Width || token == TemplateTokenKind.Height ||
            token == TemplateTokenKind.ExifWidth || token == TemplateTokenKind.ExifHeight ||
            token == TemplateTokenKind.Orientation || token == TemplateTokenKind.Iso ||
            token == TemplateTokenKind.FocalLength35mm || token == TemplateTokenKind.Rating;

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
