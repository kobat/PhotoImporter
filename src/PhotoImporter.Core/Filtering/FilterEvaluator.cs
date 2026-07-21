using PhotoImporter.Core.Templates;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PhotoImporter.Core.Filtering
{
    public enum StringFilterMatchMode
    {
        Exact,
        Contains,
        Wildcard,
        RegularExpression
    }

    public enum FilterValidationCode
    {
        FieldTypeMismatch,
        NoChoices,
        RangeIsEmpty,
        MinimumExceedsMaximum,
        InvalidRegularExpression,
        TimeZoneRequired,
        InvalidTimeZone,
        OptionNotSupported
    }

    public sealed class FilterValidationError
    {
        internal FilterValidationError(int conditionIndex, FilterValidationCode code, string message)
        {
            ConditionIndex = conditionIndex;
            Code = code;
            Message = message;
        }

        public int ConditionIndex { get; }
        public FilterValidationCode Code { get; }
        public string Message { get; }
    }

    public sealed class FilterPreparationResult
    {
        internal FilterPreparationResult(PreparedFilter filter, IList<FilterValidationError> errors)
        {
            Filter = filter;
            Errors = new ReadOnlyCollection<FilterValidationError>(errors);
        }

        public PreparedFilter Filter { get; }
        public IReadOnlyList<FilterValidationError> Errors { get; }
        public bool IsValid => Filter != null;
    }

    public sealed class FilterEvaluationException : Exception
    {
        internal FilterEvaluationException(FilterField field, string message, Exception innerException = null)
            : base(message, innerException)
        {
            Field = field;
        }

        public FilterField Field { get; }
    }

    public sealed class FilterSet
    {
        public FilterSet(IEnumerable<FilterCondition> conditions)
        {
            if (conditions == null) throw new ArgumentNullException(nameof(conditions));
            Conditions = new ReadOnlyCollection<FilterCondition>(conditions.ToList());
            if (Conditions.Any(item => item == null))
                throw new ArgumentException("Conditions cannot contain null.", nameof(conditions));
        }

        public IReadOnlyList<FilterCondition> Conditions { get; }
        public bool RequiresExif => Conditions.Any(item => FilterFieldDefinition.Get(item.Field).RequiresExif);

        public FilterPreparationResult Prepare()
        {
            var prepared = new List<PreparedFilterCondition>();
            var errors = new List<FilterValidationError>();
            for (var index = 0; index < Conditions.Count; index++)
            {
                if (Conditions[index].IncludeUnknown &&
                    !FilterFieldDefinition.Get(Conditions[index].Field).CanBeUnknown)
                {
                    errors.Add(new FilterValidationError(
                        index,
                        FilterValidationCode.OptionNotSupported,
                        "Unknown is not supported for " + Conditions[index].Field + "."));
                    continue;
                }
                PreparedFilterCondition condition;
                FilterValidationCode code;
                string message;
                if (Conditions[index].TryPrepare(out condition, out code, out message))
                    prepared.Add(condition);
                else
                    errors.Add(new FilterValidationError(index, code, message));
            }
            return errors.Count == 0
                ? new FilterPreparationResult(new PreparedFilter(prepared, RequiresExif), errors)
                : new FilterPreparationResult(null, errors);
        }
    }

    public sealed class PreparedFilter
    {
        private readonly IReadOnlyList<PreparedFilterCondition> _conditions;

        internal PreparedFilter(IList<PreparedFilterCondition> conditions, bool requiresExif)
        {
            _conditions = new ReadOnlyCollection<PreparedFilterCondition>(conditions);
            RequiresExif = requiresExif;
        }

        public bool RequiresExif { get; }
        public int ConditionCount => _conditions.Count;

        public bool Matches(FilterCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            foreach (var condition in _conditions)
                if (!condition.Matches(candidate)) return false;
            return true;
        }
    }

    public abstract class FilterCondition
    {
        protected FilterCondition(FilterField field, bool includeMatches, bool includeUnknown)
        {
            Field = field;
            IncludeMatches = includeMatches;
            IncludeUnknown = includeUnknown;
        }

        public FilterField Field { get; }
        public bool IncludeMatches { get; }
        public bool IncludeUnknown { get; }

        internal abstract bool TryPrepare(
            out PreparedFilterCondition condition,
            out FilterValidationCode code,
            out string message);

        protected bool TryValidateType(FilterValueType expected, out FilterValidationCode code, out string message)
        {
            if (FilterFieldDefinition.Get(Field).ValueType == expected)
            {
                code = default(FilterValidationCode);
                message = null;
                return true;
            }
            code = FilterValidationCode.FieldTypeMismatch;
            message = "The condition type is not valid for " + Field + ".";
            return false;
        }
    }

    public sealed class StringFilterCondition : FilterCondition
    {
        public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);

        public StringFilterCondition(
            FilterField field,
            string pattern,
            StringFilterMatchMode matchMode,
            bool caseSensitive = false,
            bool includeMatches = true,
            bool includeUnknown = false,
            TimeSpan? regexTimeout = null)
            : base(field, includeMatches, includeUnknown)
        {
            Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            MatchMode = matchMode;
            CaseSensitive = caseSensitive;
            RegexTimeout = regexTimeout ?? DefaultRegexTimeout;
        }

        public string Pattern { get; }
        public StringFilterMatchMode MatchMode { get; }
        public bool CaseSensitive { get; }
        public TimeSpan RegexTimeout { get; }

        internal override bool TryPrepare(
            out PreparedFilterCondition condition,
            out FilterValidationCode code,
            out string message)
        {
            condition = null;
            if (!TryValidateType(FilterValueType.String, out code, out message)) return false;
            var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Regex regex = null;
            if (MatchMode == StringFilterMatchMode.Wildcard || MatchMode == StringFilterMatchMode.RegularExpression)
            {
                var expression = MatchMode == StringFilterMatchMode.Wildcard
                    ? "^" + Regex.Escape(Pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$"
                    : Pattern;
                var options = RegexOptions.CultureInvariant |
                              (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                try
                {
                    regex = new Regex(expression, options, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    code = FilterValidationCode.InvalidRegularExpression;
                    message = ex.Message;
                    return false;
                }
            }

            condition = new PreparedFilterCondition(Field, IncludeMatches, IncludeUnknown, (candidate, value) =>
            {
                var text = (string)value;
                try
                {
                    switch (MatchMode)
                    {
                        case StringFilterMatchMode.Exact: return string.Equals(text, Pattern, comparison);
                        case StringFilterMatchMode.Contains: return text.IndexOf(Pattern, comparison) >= 0;
                        default: return regex.IsMatch(text);
                    }
                }
                catch (RegexMatchTimeoutException ex)
                {
                    throw new FilterEvaluationException(Field, "Regular expression evaluation timed out.", ex);
                }
            });
            code = default(FilterValidationCode);
            message = null;
            return true;
        }
    }

    public sealed class ChoiceFilterCondition<T> : FilterCondition
    {
        public ChoiceFilterCondition(
            FilterField field,
            IEnumerable<T> choices,
            bool includeMatches = true,
            bool includeUnknown = false)
            : base(field, includeMatches, includeUnknown)
        {
            if (choices == null) throw new ArgumentNullException(nameof(choices));
            Choices = new ReadOnlyCollection<T>(choices.Distinct().ToList());
        }

        public IReadOnlyList<T> Choices { get; }

        internal override bool TryPrepare(
            out PreparedFilterCondition condition,
            out FilterValidationCode code,
            out string message)
        {
            condition = null;
            var definition = FilterFieldDefinition.Get(Field);
            var compatible = definition.ValueClrType == typeof(T) &&
                             (definition.ValueType == FilterValueType.Choice ||
                              definition.ValueType == FilterValueType.Boolean ||
                              definition.ValueType == FilterValueType.String);
            if (!compatible)
            {
                code = FilterValidationCode.FieldTypeMismatch;
                message = "The choice type is not valid for " + Field + ".";
                return false;
            }
            if (Choices.Count == 0)
            {
                code = FilterValidationCode.NoChoices;
                message = "At least one choice is required.";
                return false;
            }
            IEqualityComparer<object> comparer;
            if (typeof(T) == typeof(string)) comparer = new ObjectStringComparer();
            else comparer = EqualityComparer<object>.Default;
            var selected = new HashSet<object>(Choices.Cast<object>(), comparer);
            condition = new PreparedFilterCondition(
                Field, IncludeMatches, IncludeUnknown, (candidate, value) => selected.Contains(value));
            code = default(FilterValidationCode);
            message = null;
            return true;
        }

        private sealed class ObjectStringComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) =>
                string.Equals((string)x, (string)y, StringComparison.OrdinalIgnoreCase);
            public int GetHashCode(object obj) => StringComparer.OrdinalIgnoreCase.GetHashCode((string)obj);
        }
    }

    public sealed class NumberFilterCondition : FilterCondition
    {
        public NumberFilterCondition(
            FilterField field,
            decimal? minimum,
            decimal? maximum,
            bool includeMatches = true,
            bool includeUnknown = false,
            bool includeNoSequence = false,
            bool includeRejectedRating = false)
            : base(field, includeMatches, includeUnknown)
        {
            Minimum = minimum;
            Maximum = maximum;
            IncludeNoSequence = includeNoSequence;
            IncludeRejectedRating = includeRejectedRating;
        }

        public decimal? Minimum { get; }
        public decimal? Maximum { get; }
        public bool IncludeNoSequence { get; }
        public bool IncludeRejectedRating { get; }

        internal override bool TryPrepare(
            out PreparedFilterCondition condition,
            out FilterValidationCode code,
            out string message)
        {
            condition = null;
            if (!TryValidateType(FilterValueType.Number, out code, out message)) return false;
            if (IncludeNoSequence && Field != FilterField.Sequence ||
                IncludeRejectedRating && Field != FilterField.Rating)
            {
                code = FilterValidationCode.OptionNotSupported;
                message = "The selected numeric option is not valid for " + Field + ".";
                return false;
            }
            if (!Minimum.HasValue && !Maximum.HasValue && !IncludeNoSequence && !IncludeRejectedRating)
            {
                code = FilterValidationCode.RangeIsEmpty;
                message = "A minimum, maximum, or special value is required.";
                return false;
            }
            if (Minimum > Maximum)
            {
                code = FilterValidationCode.MinimumExceedsMaximum;
                message = "The minimum cannot exceed the maximum.";
                return false;
            }

            condition = new PreparedFilterCondition(Field, IncludeMatches, IncludeUnknown, (candidate, value) =>
            {
                var sequence = value is SequenceFilterValue ? (SequenceFilterValue?)value : null;
                if (sequence.HasValue && !sequence.Value.Number.HasValue) return IncludeNoSequence;
                var number = sequence.HasValue
                    ? (decimal)sequence.Value.Number.Value
                    : (decimal)value;
                if (Field == FilterField.Rating && number == -1) return IncludeRejectedRating;
                return (!Minimum.HasValue || number >= Minimum.Value) &&
                       (!Maximum.HasValue || number <= Maximum.Value);
            });
            code = default(FilterValidationCode);
            message = null;
            return true;
        }
    }

    public sealed class DateTimeFilterCondition : FilterCondition
    {
        public DateTimeFilterCondition(
            FilterField field,
            DateTime? minimum,
            DateTime? maximum,
            bool maximumIsExclusive = false,
            string timeZoneSpecifier = null,
            bool includeMatches = true,
            bool includeUnknown = false)
            : base(field, includeMatches, includeUnknown)
        {
            Minimum = minimum;
            Maximum = maximum;
            MaximumIsExclusive = maximumIsExclusive;
            TimeZoneSpecifier = timeZoneSpecifier;
        }

        public DateTime? Minimum { get; }
        public DateTime? Maximum { get; }
        public bool MaximumIsExclusive { get; }
        public string TimeZoneSpecifier { get; }

        public static DateTimeFilterCondition ForDateRange(
            FilterField field,
            DateTime? firstDate,
            DateTime? lastDate,
            string timeZoneSpecifier = null,
            bool includeMatches = true,
            bool includeUnknown = false)
        {
            var start = firstDate?.Date;
            var exclusiveEnd = lastDate?.Date.AddDays(1);
            return new DateTimeFilterCondition(
                field, start, exclusiveEnd, true, timeZoneSpecifier, includeMatches, includeUnknown);
        }

        internal override bool TryPrepare(
            out PreparedFilterCondition condition,
            out FilterValidationCode code,
            out string message)
        {
            condition = null;
            if (!TryValidateType(FilterValueType.DateTime, out code, out message)) return false;
            if (!Minimum.HasValue && !Maximum.HasValue)
            {
                code = FilterValidationCode.RangeIsEmpty;
                message = "A start or end value is required.";
                return false;
            }
            if (Minimum > Maximum || Minimum == Maximum && MaximumIsExclusive)
            {
                code = FilterValidationCode.MinimumExceedsMaximum;
                message = "The start cannot exceed the end.";
                return false;
            }

            TemplateTimeZone zone = null;
            if (Field == FilterField.TakenDateInTimeZone)
            {
                if (string.IsNullOrEmpty(TimeZoneSpecifier))
                {
                    code = FilterValidationCode.TimeZoneRequired;
                    message = "A time zone is required.";
                    return false;
                }
                string dateFormat;
                TemplateErrorCode templateError;
                if (TimeZoneSpecifier.IndexOf('|') >= 0 ||
                    !TemplateTimeZone.TryParseFormat(TimeZoneSpecifier, out zone, out dateFormat, out templateError))
                {
                    code = FilterValidationCode.InvalidTimeZone;
                    message = "The time zone is invalid.";
                    return false;
                }
            }

            condition = new PreparedFilterCondition(Field, IncludeMatches, IncludeUnknown, (candidate, value) =>
            {
                var date = (DateTime)value;
                return (!Minimum.HasValue || date >= Minimum.Value) &&
                       (!Maximum.HasValue || (MaximumIsExclusive ? date < Maximum.Value : date <= Maximum.Value));
            }, zone);
            code = default(FilterValidationCode);
            message = null;
            return true;
        }
    }

    internal sealed class PreparedFilterCondition
    {
        private readonly Func<FilterCandidate, object, bool> _knownMatcher;
        private readonly TemplateTimeZone _timeZone;

        public PreparedFilterCondition(
            FilterField field,
            bool includeMatches,
            bool includeUnknown,
            Func<FilterCandidate, object, bool> knownMatcher,
            TemplateTimeZone timeZone = null)
        {
            Field = field;
            IncludeMatches = includeMatches;
            IncludeUnknown = includeUnknown;
            _knownMatcher = knownMatcher;
            _timeZone = timeZone;
        }

        public FilterField Field { get; }
        public bool IncludeMatches { get; }
        public bool IncludeUnknown { get; }

        public bool Matches(FilterCandidate candidate)
        {
            var value = candidate.GetValue(Field, _timeZone);
            if (value.State == FilterValueState.ExifUnread)
                throw new FilterEvaluationException(Field, "Exif information must be read before applying this condition.");
            if (value.State == FilterValueState.Unknown) return IncludeUnknown;
            var matchesKnownValue = _knownMatcher(candidate, value.Value);
            return IncludeMatches ? matchesKnownValue : !matchesKnownValue;
        }
    }

    public static class FileSizeFilterParser
    {
        private static readonly Regex Pattern = new Regex(
            @"^\s*(?<number>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>B|KiB|MiB|GiB)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool TryParseBytes(string input, out long bytes)
        {
            bytes = 0;
            if (input == null) return false;
            var match = Pattern.Match(input);
            decimal number;
            if (!match.Success ||
                !decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out number)) return false;
            decimal multiplier;
            switch (match.Groups["unit"].Value.ToUpperInvariant())
            {
                case "":
                case "B": multiplier = 1m; break;
                case "KIB": multiplier = 1024m; break;
                case "MIB": multiplier = 1024m * 1024m; break;
                case "GIB": multiplier = 1024m * 1024m * 1024m; break;
                default: return false;
            }
            decimal converted;
            try
            {
                converted = number * multiplier;
            }
            catch (OverflowException)
            {
                return false;
            }
            if (converted > long.MaxValue || converted != decimal.Truncate(converted)) return false;
            bytes = (long)converted;
            return true;
        }
    }
}
