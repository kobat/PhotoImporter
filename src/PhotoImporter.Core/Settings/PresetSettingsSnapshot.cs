using PhotoImporter.Core.Metadata;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PhotoImporter.Core.Settings
{
    public sealed class PresetSettingsSnapshot
    {
        private readonly string[] _sidecarExtensions;

        public PresetSettingsSnapshot(
            string sourceFolder,
            string destinationFolder,
            string templateText,
            bool overwriteExisting,
            SourceFileSelectionMode sourceFileSelectionMode,
            bool associateSidecars,
            IEnumerable<string> sidecarExtensions,
            bool analyzeJpegOnlyForRawJpegPair,
            bool readExifInformation)
        {
            SourceFolder = sourceFolder;
            DestinationFolder = destinationFolder;
            TemplateText = templateText ?? string.Empty;
            OverwriteExisting = overwriteExisting;
            SourceFileSelectionMode = sourceFileSelectionMode;
            AssociateSidecars = associateSidecars;
            _sidecarExtensions = (sidecarExtensions ?? Enumerable.Empty<string>()).ToArray();
            AnalyzeJpegOnlyForRawJpegPair = analyzeJpegOnlyForRawJpegPair;
            ReadExifInformation = readExifInformation;
        }

        public string SourceFolder { get; }
        public string DestinationFolder { get; }
        public string TemplateText { get; }
        public bool OverwriteExisting { get; }
        public SourceFileSelectionMode SourceFileSelectionMode { get; }
        public bool AssociateSidecars { get; }
        public IReadOnlyList<string> SidecarExtensions => _sidecarExtensions;
        public bool AnalyzeJpegOnlyForRawJpegPair { get; }
        public bool ReadExifInformation { get; }

        public static PresetSettingsSnapshot FromSettings(PhotoImporterSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new PresetSettingsSnapshot(
                settings.SourceFolder,
                settings.DestinationFolder,
                settings.TemplateText,
                settings.OverwriteExisting,
                settings.SourceFileSelectionMode,
                settings.AssociateSidecars,
                settings.SidecarExtensions,
                settings.AnalyzeJpegOnlyForRawJpegPair,
                settings.ReadExifInformation);
        }

        public static PresetSettingsSnapshot FromPreset(PhotoImporterPreset preset)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            return new PresetSettingsSnapshot(
                preset.SourceFolder,
                preset.DestinationFolder,
                preset.TemplateText,
                preset.OverwriteExisting,
                preset.SourceFileSelectionMode,
                preset.AssociateSidecars,
                preset.SidecarExtensions,
                preset.AnalyzeJpegOnlyForRawJpegPair,
                preset.ReadExifInformation);
        }

        public bool Matches(PhotoImporterPreset preset)
        {
            if (preset == null) return false;
            return (!preset.SaveSourceFolder || PathsEqual(SourceFolder, preset.SourceFolder)) &&
                   PathsEqual(DestinationFolder, preset.DestinationFolder) &&
                   string.Equals(TemplateText, preset.TemplateText ?? string.Empty, StringComparison.Ordinal) &&
                   OverwriteExisting == preset.OverwriteExisting &&
                   SourceFileSelectionMode == preset.SourceFileSelectionMode &&
                   AssociateSidecars == preset.AssociateSidecars &&
                   SidecarSetsEqual(_sidecarExtensions, preset.SidecarExtensions) &&
                   AnalyzeJpegOnlyForRawJpegPair == preset.AnalyzeJpegOnlyForRawJpegPair &&
                   ReadExifInformation == preset.ReadExifInformation;
        }

        public bool EquivalentTo(PresetSettingsSnapshot other)
        {
            if (other == null) return false;
            return PathsEqual(SourceFolder, other.SourceFolder) &&
                   PathsEqual(DestinationFolder, other.DestinationFolder) &&
                   string.Equals(TemplateText, other.TemplateText, StringComparison.Ordinal) &&
                   OverwriteExisting == other.OverwriteExisting &&
                   SourceFileSelectionMode == other.SourceFileSelectionMode &&
                   AssociateSidecars == other.AssociateSidecars &&
                   SidecarSetsEqual(_sidecarExtensions, other._sidecarExtensions) &&
                   AnalyzeJpegOnlyForRawJpegPair == other.AnalyzeJpegOnlyForRawJpegPair &&
                   ReadExifInformation == other.ReadExifInformation;
        }

        private static bool PathsEqual(string first, string second) =>
            string.Equals(NormalizePathForComparison(first), NormalizePathForComparison(second),
                StringComparison.OrdinalIgnoreCase);

        private static string NormalizePathForComparison(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                var fullPath = Path.GetFullPath(path);
                var root = Path.GetPathRoot(fullPath);
                while (fullPath.Length > (root == null ? 0 : root.Length) &&
                       (fullPath.EndsWith("\\", StringComparison.Ordinal) ||
                        fullPath.EndsWith("/", StringComparison.Ordinal)))
                    fullPath = fullPath.Substring(0, fullPath.Length - 1);
                return fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return (path ?? string.Empty).Trim();
            }
        }

        private static bool SidecarSetsEqual(IEnumerable<string> first, IEnumerable<string> second)
        {
            try
            {
                var firstPolicy = SidecarPolicy.Create(true, first ?? Enumerable.Empty<string>());
                var secondPolicy = SidecarPolicy.Create(true, second ?? Enumerable.Empty<string>());
                return new HashSet<string>(firstPolicy.Extensions, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(secondPolicy.Extensions);
            }
            catch (ArgumentException)
            {
                return new HashSet<string>(first ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase)
                    .SetEquals(second ?? Enumerable.Empty<string>());
            }
        }
    }
}
