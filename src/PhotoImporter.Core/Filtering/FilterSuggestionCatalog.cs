using PhotoImporter.Core.Templates;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace PhotoImporter.Core.Filtering
{
    public enum FilterSpecialValue { NoSequence }

    public sealed class FilterSuggestion
    {
        internal FilterSuggestion(object value, int count) { Value = value; Count = count; }
        public object Value { get; }
        public int Count { get; }
    }

    public sealed class FilterSuggestions
    {
        internal FilterSuggestions(IList<FilterSuggestion> values, int unread, int unknown)
        {
            Values = new ReadOnlyCollection<FilterSuggestion>(values);
            UnreadCount = unread;
            UnknownCount = unknown;
        }
        public IReadOnlyList<FilterSuggestion> Values { get; }
        public int UnreadCount { get; }
        public int UnknownCount { get; }
    }

    // Owns one scan snapshot. No filesystem or Exif reads are performed here.
    public sealed class FilterSuggestionCatalog
    {
        private readonly FilterCandidate[] _candidates;
        private readonly Dictionary<string, FilterSuggestions> _cache = new Dictionary<string, FilterSuggestions>();

        public FilterSuggestionCatalog(IEnumerable<FilterCandidate> candidates)
        {
            _candidates = candidates.ToArray();
        }

        public FilterSuggestions Get(FilterField field, string timeZone = null, bool exactTimes = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var key = field + "|" + timeZone + "|" + exactTimes;
            lock (_cache)
            {
                FilterSuggestions cached;
                if (_cache.TryGetValue(key, out cached)) return cached;
            }
            TemplateTimeZone zone = null;
            string format;
            TemplateErrorCode error;
            if (field == FilterField.TakenDateInTimeZone &&
                (timeZone == null || timeZone.IndexOf('|') >= 0 ||
                 !TemplateTimeZone.TryParseFormat(timeZone, out zone, out format, out error)))
                throw new ArgumentException("Invalid time zone.", nameof(timeZone));
            var counts = new Dictionary<object, int>();
            var unread = 0;
            var unknown = 0;
            foreach (var candidate in _candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = candidate.GetValue(field, zone);
                if (value.State == FilterValueState.ExifUnread) { unread++; continue; }
                if (value.State == FilterValueState.Unknown) { unknown++; continue; }
                object item = value.Value;
                if (item is SequenceFilterValue)
                {
                    var sequence = ((SequenceFilterValue)item).Number;
                    item = sequence.HasValue ? (object)(decimal)sequence.Value : FilterSpecialValue.NoSequence;
                }
                if (item is DateTime && !exactTimes) item = ((DateTime)item).Date;
                int count;
                counts.TryGetValue(item, out count);
                counts[item] = count + 1;
            }
            var values = counts.Select(pair => new FilterSuggestion(pair.Key, pair.Value)).ToList();
            values.Sort((a, b) => Compare(a.Value, b.Value));
            if (FilterFieldDefinition.Get(field).ValueType == FilterValueType.DateTime) values.Reverse();
            var result = new FilterSuggestions(values, unread, unknown);
            lock (_cache)
            {
                // Bound the number of potentially large, high-cardinality lists retained.
                if (_cache.Count >= 8) _cache.Clear();
                _cache[key] = result;
            }
            return result;
        }

        private static int Compare(object a, object b)
        {
            if (a.GetType() != b.GetType()) return a is FilterSpecialValue ? -1 : 1;
            if (a is string) return StringComparer.Ordinal.Compare((string)a, (string)b);
            return ((IComparable)a).CompareTo(b);
        }
    }
}
