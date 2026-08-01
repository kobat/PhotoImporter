using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoImporter.Core.Settings
{
    public sealed class RecentInputHistory
    {
        private readonly string[] _sourceFolders;
        private readonly string[] _destinationFolders;
        private readonly string[] _templates;

        public RecentInputHistory(
            IEnumerable<string> sourceFolders,
            IEnumerable<string> destinationFolders,
            IEnumerable<string> templates)
        {
            _sourceFolders = (sourceFolders ?? Enumerable.Empty<string>()).ToArray();
            _destinationFolders = (destinationFolders ?? Enumerable.Empty<string>()).ToArray();
            _templates = (templates ?? Enumerable.Empty<string>()).ToArray();
        }

        public IReadOnlyList<string> SourceFolders => _sourceFolders;
        public IReadOnlyList<string> DestinationFolders => _destinationFolders;
        public IReadOnlyList<string> Templates => _templates;

        public static RecentInputHistory Empty { get; } = new RecentInputHistory(null, null, null);

        internal RecentInputHistory Record(
            string sourceFolder,
            string destinationFolder,
            string templateText,
            int limit)
        {
            if (limit < 0 || limit > PhotoImporterSettings.MaximumInputHistoryLimit)
                throw new ArgumentOutOfRangeException(nameof(limit));

            return new RecentInputHistory(
                Promote(_sourceFolders, sourceFolder, limit, StringComparer.OrdinalIgnoreCase),
                Promote(_destinationFolders, destinationFolder, limit, StringComparer.OrdinalIgnoreCase),
                Promote(_templates, templateText, limit, StringComparer.Ordinal));
        }

        private static IEnumerable<string> Promote(
            IEnumerable<string> existing,
            string value,
            int limit,
            StringComparer comparer)
        {
            if (limit == 0) return Enumerable.Empty<string>();

            return new[] { value }
                .Concat(existing ?? Enumerable.Empty<string>())
                .Where(item => !string.IsNullOrEmpty(item))
                .Distinct(comparer)
                .Take(limit);
        }
    }
}
