using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoImporter.Core.Templates
{
    internal sealed class TemplateTimeZone
    {
        private static readonly IDictionary<string, string> WindowsIds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["UTC"] = "UTC",
                ["JST"] = "Tokyo Standard Time",
                ["PST"] = "Pacific Standard Time",
                ["MST"] = "Mountain Standard Time",
                ["CST"] = "Central Standard Time",
                ["EST"] = "Eastern Standard Time",
                ["GMT"] = "GMT Standard Time",
                ["CET"] = "W. Europe Standard Time"
            };

        private static readonly Regex FixedOffsetPattern = new Regex(
            @"^UTC(?<sign>[+-])(?<hour>[0-9]{1,2})(?::(?<minute>[0-9]{2}))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private TemplateTimeZone(string normalizedSpecifier, string windowsId, TimeSpan? fixedOffset)
        {
            NormalizedSpecifier = normalizedSpecifier;
            WindowsId = windowsId;
            FixedOffset = fixedOffset;
        }

        public string NormalizedSpecifier { get; }
        public string WindowsId { get; }
        public TimeSpan? FixedOffset { get; }

        public DateTime Convert(DateTimeOffset instant)
        {
            if (FixedOffset.HasValue) return instant.ToOffset(FixedOffset.Value).DateTime;
            return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(WindowsId)).DateTime;
        }

        public DateTime ConvertFromUtc(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            if (FixedOffset.HasValue) return new DateTimeOffset(utc).ToOffset(FixedOffset.Value).DateTime;
            return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(WindowsId));
        }

        public static bool TryParseFormat(
            string format,
            out TemplateTimeZone zone,
            out string dateFormat,
            out TemplateErrorCode error)
        {
            zone = null;
            dateFormat = null;
            error = TemplateErrorCode.InvalidTimeZoneCode;
            if (string.IsNullOrEmpty(format))
            {
                error = TemplateErrorCode.TimeZoneArgumentMissing;
                return false;
            }

            var separator = format.IndexOf('|');
            var specifier = separator < 0 ? format : format.Substring(0, separator);
            dateFormat = separator < 0 ? null : format.Substring(separator + 1);
            if (specifier.Length == 0)
            {
                error = TemplateErrorCode.TimeZoneArgumentMissing;
                return false;
            }
            if (separator >= 0 && dateFormat.Length == 0)
            {
                error = TemplateErrorCode.InvalidDateFormat;
                return false;
            }

            string windowsId;
            if (WindowsIds.TryGetValue(specifier, out windowsId))
            {
                zone = new TemplateTimeZone(specifier.ToUpperInvariant(), windowsId, null);
                return true;
            }

            var match = FixedOffsetPattern.Match(specifier);
            int hours;
            int minutes;
            if (!match.Success ||
                !int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
                !int.TryParse(match.Groups["minute"].Success ? match.Groups["minute"].Value : "0", NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
                hours > 14 || minutes > 59 || (hours == 14 && minutes != 0))
            {
                error = specifier.StartsWith("UTC", StringComparison.OrdinalIgnoreCase)
                    ? TemplateErrorCode.InvalidUtcOffset
                    : TemplateErrorCode.InvalidTimeZoneCode;
                return false;
            }

            var offset = new TimeSpan(hours, minutes, 0);
            if (match.Groups["sign"].Value == "-") offset = -offset;
            var normalized = offset == TimeSpan.Zero
                ? "UTC"
                : string.Format(CultureInfo.InvariantCulture, "UTC{0}{1:00}:{2:00}", offset < TimeSpan.Zero ? "-" : "+", Math.Abs(offset.Hours), Math.Abs(offset.Minutes));
            zone = new TemplateTimeZone(normalized, null, offset);
            return true;
        }
    }
}
