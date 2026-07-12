using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoImporter.Core.Templates
{
    public sealed class FileTemplateContext
    {
        public FileTemplateContext(
            string originalName,
            DateTime modifiedDate,
            long fileSize,
            string sourceRelativeDirectory = "")
        {
            if (originalName == null) throw new ArgumentNullException(nameof(originalName));
            if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
            if (sourceRelativeDirectory == null) throw new ArgumentNullException(nameof(sourceRelativeDirectory));
            OriginalName = originalName;
            ModifiedDate = modifiedDate;
            FileSize = fileSize;
            SourceRelativeDirectory = sourceRelativeDirectory;
        }

        public string OriginalName { get; }
        public DateTime ModifiedDate { get; }
        public long FileSize { get; }
        public string SourceRelativeDirectory { get; }
    }

    public static class TemplateEvaluator
    {
        private const string DefaultDateFormat = "yyyyMMdd_HHmmss";
        private static readonly Regex ReservedDeviceName = new Regex(
            @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string Evaluate(
            ParsedTemplate template,
            FileTemplateContext context,
            int? sequenceNumber = null,
            int maximumFullPathLength = 259,
            string destinationRoot = null)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (template.RequiresExif)
                throw new NotSupportedException("Exif token evaluation is implemented by the metadata stage.");
            if (sequenceNumber.HasValue && !template.HasSequence)
                throw new ArgumentException("The template has no Sequence token.", nameof(sequenceNumber));

            var extension = Path.GetExtension(context.OriginalName);
            var fileName = Path.GetFileNameWithoutExtension(context.OriginalName);
            var output = new StringBuilder();
            var skipLeadingSeparator = false;

            for (var partIndex = 0; partIndex < template.Parts.Count; partIndex++)
            {
                var part = template.Parts[partIndex];
                if (!part.Token.HasValue)
                {
                    if (skipLeadingSeparator && part.Literal.StartsWith("\\", StringComparison.Ordinal))
                        output.Append(part.Literal.Substring(1));
                    else
                        output.Append(part.Literal);
                    skipLeadingSeparator = false;
                    continue;
                }

                skipLeadingSeparator = false;
                switch (part.Token.Value)
                {
                    case TemplateTokenKind.OriginalName:
                        output.Append(context.OriginalName);
                        break;
                    case TemplateTokenKind.FileName:
                        output.Append(fileName);
                        break;
                    case TemplateTokenKind.Extension:
                        output.Append(extension);
                        break;
                    case TemplateTokenKind.SourceRelativeDirectory:
                        output.Append(context.SourceRelativeDirectory);
                        skipLeadingSeparator = context.SourceRelativeDirectory.Length == 0;
                        break;
                    case TemplateTokenKind.ModifiedDate:
                        output.Append(FormatDate(context.ModifiedDate, part.Format, part));
                        break;
                    case TemplateTokenKind.FileSize:
                        output.Append(context.FileSize.ToString(CultureInfo.InvariantCulture));
                        break;
                    case TemplateTokenKind.Sequence:
                        if (sequenceNumber.HasValue)
                        {
                            var width = template.SequenceWidth.Value;
                            var maximum = (long)Math.Pow(10, width) - 1;
                            if (sequenceNumber.Value < 1 || sequenceNumber.Value > maximum)
                                Throw(TemplateErrorCode.SequenceExhausted, part);
                            output.Append('_');
                            output.Append(sequenceNumber.Value.ToString(new string('0', width), CultureInfo.InvariantCulture));
                        }
                        break;
                    default:
                        throw new NotSupportedException("Exif token evaluation is implemented by the metadata stage.");
                }
            }

            var relativePath = output.ToString();
            ValidateRelativePath(relativePath, template);
            if (destinationRoot != null &&
                Path.Combine(destinationRoot, relativePath).Length > maximumFullPathLength)
            {
                throw new TemplateException(new TemplateError(TemplateErrorCode.PathTooLong, 0, template.Source.Length));
            }
            return relativePath;
        }

        private static string FormatDate(DateTime value, string format, TemplatePart part)
        {
            try
            {
                return value.ToString(format ?? DefaultDateFormat, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                Throw(TemplateErrorCode.InvalidDateFormat, part);
                return null;
            }
        }

        private static void ValidateRelativePath(string relativePath, ParsedTemplate template)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath.StartsWith("\\", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                throw new TemplateException(new TemplateError(TemplateErrorCode.InvalidPathStructure, 0, template.Source.Length));
            }

            var elements = relativePath.Split('\\');
            if (elements.Any(element => string.IsNullOrEmpty(element) || element == "." || element == ".."))
                throw new TemplateException(new TemplateError(TemplateErrorCode.InvalidPathStructure, 0, template.Source.Length));

            foreach (var element in elements)
            {
                if (element.EndsWith(" ", StringComparison.Ordinal) || element.EndsWith(".", StringComparison.Ordinal))
                    throw new TemplateException(new TemplateError(TemplateErrorCode.InvalidPathStructure, 0, template.Source.Length));
                if (ReservedDeviceName.IsMatch(element))
                    throw new TemplateException(new TemplateError(TemplateErrorCode.ReservedDeviceName, 0, template.Source.Length));
                if (element.Any(character => character < 32 || "<>:\"/|?*".IndexOf(character) >= 0))
                    throw new TemplateException(new TemplateError(TemplateErrorCode.InvalidLiteralCharacter, 0, template.Source.Length));
            }
        }

        private static void Throw(TemplateErrorCode code, TemplatePart part)
        {
            throw new TemplateException(new TemplateError(code, part.Position, 1, part.Token?.ToString()));
        }
    }
}
