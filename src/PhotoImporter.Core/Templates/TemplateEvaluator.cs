using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PhotoImporter.Core.Metadata;

namespace PhotoImporter.Core.Templates
{
    public sealed class FileTemplateContext
    {
        public FileTemplateContext(
            string originalName,
            DateTime modifiedDate,
            long fileSize,
            string sourceRelativeDirectory = "",
            PhotoMetadata metadata = null,
            DateTime? modifiedDateUtc = null)
        {
            if (originalName == null) throw new ArgumentNullException(nameof(originalName));
            if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
            if (sourceRelativeDirectory == null) throw new ArgumentNullException(nameof(sourceRelativeDirectory));
            OriginalName = originalName;
            ModifiedDate = modifiedDate;
            FileSize = fileSize;
            SourceRelativeDirectory = sourceRelativeDirectory;
            Metadata = metadata ?? PhotoMetadata.Empty;
            ModifiedDateUtc = modifiedDateUtc ?? (modifiedDate.Kind == DateTimeKind.Utc
                ? modifiedDate
                : modifiedDate.ToUniversalTime());
            if (ModifiedDateUtc.Kind != DateTimeKind.Utc)
                ModifiedDateUtc = DateTime.SpecifyKind(ModifiedDateUtc, DateTimeKind.Utc);
        }

        public string OriginalName { get; }
        public DateTime ModifiedDate { get; }
        public long FileSize { get; }
        public string SourceRelativeDirectory { get; }
        public PhotoMetadata Metadata { get; }
        public DateTime ModifiedDateUtc { get; }
    }

    public enum TemplateWarningCode
    {
        TakenDateOffsetMissing,
        TakenDateFallbackToModifiedDate
    }

    public sealed class TemplateEvaluation
    {
        internal TemplateEvaluation(string relativePath, IList<TemplateWarningCode> warnings)
        {
            RelativePath = relativePath;
            Warnings = new System.Collections.ObjectModel.ReadOnlyCollection<TemplateWarningCode>(warnings);
        }

        public string RelativePath { get; }
        public IReadOnlyList<TemplateWarningCode> Warnings { get; }
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
            => EvaluateDetailed(template, context, sequenceNumber, maximumFullPathLength, destinationRoot).RelativePath;

        public static TemplateEvaluation EvaluateDetailed(
            ParsedTemplate template,
            FileTemplateContext context,
            int? sequenceNumber = null,
            int maximumFullPathLength = 259,
            string destinationRoot = null)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (sequenceNumber.HasValue && !template.HasSequence)
                throw new ArgumentException("The template has no Sequence token.", nameof(sequenceNumber));

            var extension = Path.GetExtension(context.OriginalName);
            var fileName = Path.GetFileNameWithoutExtension(context.OriginalName);
            var output = new StringBuilder();
            var warnings = new List<TemplateWarningCode>();
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
                        if (context.SourceRelativeDirectory.Length > 0)
                            ValidateRelativePath(context.SourceRelativeDirectory + @"\_", template);
                        var sourceRelativeDirectory = SelectSourceRelativeDirectory(
                            context.SourceRelativeDirectory,
                            part.Format);
                        output.Append(sourceRelativeDirectory);
                        skipLeadingSeparator = sourceRelativeDirectory.Length == 0;
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
                    case TemplateTokenKind.TakenDate:
                        output.Append(FormatDate(GetTakenDate(context, warnings), part.Format, part));
                        break;
                    case TemplateTokenKind.TakenDateLocal:
                        output.Append(FormatDate(GetTakenDateLocal(context, warnings), part.Format, part));
                        break;
                    case TemplateTokenKind.TakenDateInTimeZone:
                        string dateFormat;
                        output.Append(FormatDate(GetTakenDateInTimeZone(context, part, warnings, out dateFormat), dateFormat, part));
                        break;
                    case TemplateTokenKind.CameraMake:
                        output.Append(SanitizePathElement(context.Metadata.CameraMake));
                        break;
                    case TemplateTokenKind.CameraModel:
                        output.Append(SanitizePathElement(context.Metadata.CameraModel));
                        break;
                    case TemplateTokenKind.Lens:
                        output.Append(SanitizePathElement(context.Metadata.Lens));
                        break;
                }
            }

            var relativePath = output.ToString();
            ValidateRelativePath(relativePath, template);
            if (destinationRoot != null &&
                Path.Combine(destinationRoot, relativePath).Length > maximumFullPathLength)
            {
                throw new TemplateException(new TemplateError(TemplateErrorCode.PathTooLong, 0, template.Source.Length));
            }
            return new TemplateEvaluation(relativePath, warnings);
        }

        private static DateTime GetTakenDate(FileTemplateContext context, ICollection<TemplateWarningCode> warnings)
        {
            if (context.Metadata.TakenDate.HasValue) return context.Metadata.TakenDate.Value;
            AddWarning(warnings, TemplateWarningCode.TakenDateFallbackToModifiedDate);
            return context.ModifiedDate;
        }

        private static DateTime GetTakenDateLocal(FileTemplateContext context, ICollection<TemplateWarningCode> warnings)
        {
            if (!context.Metadata.TakenDate.HasValue)
            {
                AddWarning(warnings, TemplateWarningCode.TakenDateFallbackToModifiedDate);
                return TimeZoneInfo.ConvertTimeFromUtc(context.ModifiedDateUtc, TimeZoneInfo.Local);
            }
            if (context.Metadata.OffsetState != TakenDateOffsetState.Valid)
            {
                AddWarning(warnings, TemplateWarningCode.TakenDateOffsetMissing);
                return context.Metadata.TakenDate.Value;
            }
            var instant = new DateTimeOffset(context.Metadata.TakenDate.Value, context.Metadata.TakenDateOffset.Value);
            return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime;
        }

        private static DateTime GetTakenDateInTimeZone(
            FileTemplateContext context,
            TemplatePart part,
            ICollection<TemplateWarningCode> warnings,
            out string dateFormat)
        {
            TemplateTimeZone zone;
            TemplateErrorCode error;
            if (!TemplateTimeZone.TryParseFormat(part.Format, out zone, out dateFormat, out error))
                Throw(error, part);
            if (!context.Metadata.TakenDate.HasValue)
            {
                AddWarning(warnings, TemplateWarningCode.TakenDateFallbackToModifiedDate);
                return zone.ConvertFromUtc(context.ModifiedDateUtc);
            }
            if (context.Metadata.OffsetState != TakenDateOffsetState.Valid)
            {
                AddWarning(warnings, TemplateWarningCode.TakenDateOffsetMissing);
                return context.Metadata.TakenDate.Value;
            }
            var instant = new DateTimeOffset(context.Metadata.TakenDate.Value, context.Metadata.TakenDateOffset.Value);
            return zone.Convert(instant);
        }

        private static void AddWarning(ICollection<TemplateWarningCode> warnings, TemplateWarningCode warning)
        {
            if (!warnings.Contains(warning)) warnings.Add(warning);
        }

        private static string SanitizePathElement(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Unknown";
            var output = new StringBuilder();
            var previousWasGeneratedUnderscore = false;
            foreach (var character in value)
            {
                var invalid = character < 32 || "<>:\"/\\|?*".IndexOf(character) >= 0;
                var next = invalid ? '_' : character;
                if (invalid && previousWasGeneratedUnderscore) continue;
                output.Append(next);
                previousWasGeneratedUnderscore = invalid;
            }
            var sanitized = output.ToString().TrimEnd(' ', '.');
            if (sanitized.Length == 0) sanitized = "Unknown";
            if (ReservedDeviceName.IsMatch(sanitized)) sanitized = "_" + sanitized;
            return sanitized;
        }

        private static string SelectSourceRelativeDirectory(string relativeDirectory, string depthFormat)
        {
            if (depthFormat == null || relativeDirectory.Length == 0)
                return relativeDirectory;

            var depth = int.Parse(depthFormat, CultureInfo.InvariantCulture);
            var elements = relativeDirectory.Split('\\');
            if (depth >= elements.Length)
                return relativeDirectory;

            return string.Join("\\", elements.Skip(elements.Length - depth));
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
