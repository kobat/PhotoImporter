using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Xml.Linq;

namespace PhotoImporter.Core.Settings
{
    public sealed class PhotoImporterPreset
    {
        private readonly List<string> _sidecarExtensions = new List<string>();
        private readonly List<XAttribute> _extensionAttributes = new List<XAttribute>();
        private readonly List<XElement> _extensionElements = new List<XElement>();

        public Guid Id { get; set; }
        public string Name { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }
        public bool SaveSourceFolder { get; set; }
        public string SourceFolder { get; set; }
        public string DestinationFolder { get; set; }
        public string TemplateText { get; set; }
        public bool OverwriteExisting { get; set; }
        public SourceFileSelectionMode SourceFileSelectionMode { get; set; }
        public bool AssociateSidecars { get; set; }
        public bool AnalyzeJpegOnlyForRawJpegPair { get; set; }
        public bool ReadExifInformation { get; set; }
        public IList<string> SidecarExtensions => _sidecarExtensions;

        internal IList<XAttribute> ExtensionAttributes => _extensionAttributes;
        internal IList<XElement> ExtensionElements => _extensionElements;

        public PhotoImporterPreset Clone()
        {
            var clone = new PhotoImporterPreset
            {
                Id = Id,
                Name = Name,
                CreatedUtc = CreatedUtc,
                UpdatedUtc = UpdatedUtc,
                LastUsedUtc = LastUsedUtc,
                SaveSourceFolder = SaveSourceFolder,
                SourceFolder = SourceFolder,
                DestinationFolder = DestinationFolder,
                TemplateText = TemplateText,
                OverwriteExisting = OverwriteExisting,
                SourceFileSelectionMode = SourceFileSelectionMode,
                AssociateSidecars = AssociateSidecars,
                AnalyzeJpegOnlyForRawJpegPair = AnalyzeJpegOnlyForRawJpegPair,
                ReadExifInformation = ReadExifInformation
            };
            clone._sidecarExtensions.AddRange(_sidecarExtensions);
            clone._extensionAttributes.AddRange(_extensionAttributes.Select(item => new XAttribute(item)));
            clone._extensionElements.AddRange(_extensionElements.Select(item => new XElement(item)));
            return clone;
        }
    }

    public sealed class PresetLoadResult
    {
        internal PresetLoadResult(IEnumerable<PhotoImporterPreset> presets, string warning, bool unsupportedVersion)
        {
            Presets = new ReadOnlyCollection<PhotoImporterPreset>(
                (presets ?? Enumerable.Empty<PhotoImporterPreset>()).Select(item => item.Clone()).ToList());
            Warning = warning;
            UnsupportedVersion = unsupportedVersion;
        }

        public IReadOnlyList<PhotoImporterPreset> Presets { get; }
        public string Warning { get; }
        public bool UnsupportedVersion { get; }
    }
}
