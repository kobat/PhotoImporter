using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace PhotoImporter.Core.Metadata
{
    public sealed class SidecarPolicy
    {
        private readonly HashSet<string> _extensions;

        private SidecarPolicy(bool enabled, IEnumerable<string> extensions)
        {
            Enabled = enabled;
            var normalized = NormalizeExtensions(extensions);
            Extensions = new ReadOnlyCollection<string>(normalized);
            _extensions = new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);
        }

        public bool Enabled { get; }
        public IReadOnlyList<string> Extensions { get; }

        public static SidecarPolicy Default { get; } =
            new SidecarPolicy(true, new[] { ".xmp" });

        public static SidecarPolicy Disabled { get; } =
            new SidecarPolicy(false, new[] { ".xmp" });

        public static SidecarPolicy Create(bool enabled, IEnumerable<string> extensions) =>
            new SidecarPolicy(enabled, extensions);

        public bool IsCandidate(string path) =>
            Enabled && path != null && _extensions.Contains(Path.GetExtension(path));

        private static List<string> NormalizeExtensions(IEnumerable<string> extensions)
        {
            if (extensions == null) throw new ArgumentNullException(nameof(extensions));
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in extensions)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var extension = value.Trim();
                if (!extension.StartsWith(".", StringComparison.Ordinal))
                    extension = "." + extension;
                ValidateExtension(extension);
                extension = extension.ToLowerInvariant();
                if (seen.Add(extension)) normalized.Add(extension);
            }

            if (normalized.Count == 0)
                throw new ArgumentException(
                    "サイドカー拡張子を1件以上指定してください。",
                    nameof(extensions));
            return normalized;
        }

        private static void ValidateExtension(string extension)
        {
            var name = extension.Substring(1);
            if (name.Length == 0 ||
                name.IndexOf('.') >= 0 ||
                name.Any(char.IsWhiteSpace) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "サイドカー拡張子が正しくありません: " + extension,
                    nameof(extension));
            }

            if (PhotoFileClassifier.IsSupported("file" + extension))
                throw new ArgumentException(
                    "画像・RAW・動画の拡張子はサイドカーに指定できません: " + extension,
                    nameof(extension));
        }
    }
}
