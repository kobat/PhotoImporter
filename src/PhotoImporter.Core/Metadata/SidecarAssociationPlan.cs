using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace PhotoImporter.Core.Metadata
{
    public enum SidecarNamingStyle
    {
        ImageStem,
        FullImageFileName
    }

    public sealed class SidecarAssociation
    {
        internal SidecarAssociation(
            string sidecarPath,
            string imagePath,
            SidecarNamingStyle namingStyle)
        {
            SidecarPath = sidecarPath;
            ImagePath = imagePath;
            NamingStyle = namingStyle;
        }

        public string SidecarPath { get; }
        public string ImagePath { get; }
        public SidecarNamingStyle NamingStyle { get; }
    }

    public sealed class SidecarAssociationPlan
    {
        private static readonly HashSet<string> SidecarExtensions = new HashSet<string>(
            new[] { ".xmp" },
            StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyDictionary<string, SidecarAssociation> _bySidecar;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<SidecarAssociation>> _byImage;

        private SidecarAssociationPlan(
            IDictionary<string, SidecarAssociation> bySidecar,
            IDictionary<string, IReadOnlyList<SidecarAssociation>> byImage,
            IList<string> warnings)
        {
            _bySidecar = new ReadOnlyDictionary<string, SidecarAssociation>(
                new Dictionary<string, SidecarAssociation>(bySidecar, StringComparer.OrdinalIgnoreCase));
            _byImage = new ReadOnlyDictionary<string, IReadOnlyList<SidecarAssociation>>(
                new Dictionary<string, IReadOnlyList<SidecarAssociation>>(byImage, StringComparer.OrdinalIgnoreCase));
            Warnings = new ReadOnlyCollection<string>(warnings);
        }

        public IReadOnlyList<string> Warnings { get; }

        public static bool IsSidecarCandidate(string path) =>
            path != null && SidecarExtensions.Contains(Path.GetExtension(path));

        public bool TryGetAssociation(string sidecarPath, out SidecarAssociation association) =>
            _bySidecar.TryGetValue(sidecarPath, out association);

        public IReadOnlyList<SidecarAssociation> GetSidecars(string imagePath)
        {
            IReadOnlyList<SidecarAssociation> associations;
            return _byImage.TryGetValue(imagePath, out associations)
                ? associations
                : new SidecarAssociation[0];
        }

        public static SidecarAssociationPlan Create(IEnumerable<string> paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            var allPaths = paths.ToList();
            if (allPaths.Any(path => path == null))
                throw new ArgumentException("Paths cannot contain null.", nameof(paths));

            var images = allPaths.Where(IsImage).ToList();
            var imagesByDirectory = images.GroupBy(
                    path => Path.GetDirectoryName(path) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var bySidecar = new Dictionary<string, SidecarAssociation>(StringComparer.OrdinalIgnoreCase);
            var mutableByImage = new Dictionary<string, List<SidecarAssociation>>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();

            foreach (var sidecar in allPaths.Where(IsSidecarCandidate))
            {
                var directory = Path.GetDirectoryName(sidecar) ?? string.Empty;
                List<string> directoryImages;
                if (!imagesByDirectory.TryGetValue(directory, out directoryImages)) continue;

                var nameWithoutSidecarExtension = Path.GetFileNameWithoutExtension(sidecar);
                var exact = directoryImages.Where(image => string.Equals(
                        Path.GetFileName(image),
                        nameWithoutSidecarExtension,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                string imagePath;
                SidecarNamingStyle style;
                if (exact.Count == 1)
                {
                    imagePath = exact[0];
                    style = SidecarNamingStyle.FullImageFileName;
                }
                else
                {
                    var stemMatches = directoryImages.Where(image => string.Equals(
                            Path.GetFileNameWithoutExtension(image),
                            nameWithoutSidecarExtension,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (stemMatches.Count == 1)
                    {
                        imagePath = stemMatches[0];
                        style = SidecarNamingStyle.ImageStem;
                    }
                    else if (IsUnambiguousRawJpegPair(stemMatches, out imagePath))
                    {
                        style = SidecarNamingStyle.ImageStem;
                    }
                    else
                    {
                        if (exact.Count > 1 || stemMatches.Count > 1)
                            warnings.Add("同名サイドカーの関連先が曖昧なため、独立ファイルとして扱います: " + sidecar);
                        continue;
                    }
                }

                var association = new SidecarAssociation(sidecar, imagePath, style);
                bySidecar.Add(sidecar, association);
                List<SidecarAssociation> imageAssociations;
                if (!mutableByImage.TryGetValue(imagePath, out imageAssociations))
                {
                    imageAssociations = new List<SidecarAssociation>();
                    mutableByImage.Add(imagePath, imageAssociations);
                }
                imageAssociations.Add(association);
            }

            var byImage = mutableByImage.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<SidecarAssociation>)new ReadOnlyCollection<SidecarAssociation>(
                    pair.Value.OrderBy(item => item.SidecarPath, StringComparer.OrdinalIgnoreCase).ToList()),
                StringComparer.OrdinalIgnoreCase);
            return new SidecarAssociationPlan(bySidecar, byImage, warnings);
        }

        private static bool IsImage(string path)
        {
            var type = PhotoFileClassifier.Classify(path);
            return type == PhotoFileType.Jpeg ||
                   type == PhotoFileType.Raw ||
                   type == PhotoFileType.OtherImage;
        }

        private static bool IsUnambiguousRawJpegPair(
            IReadOnlyList<string> candidates,
            out string rawPath)
        {
            rawPath = null;
            if (candidates.Count != 2) return false;
            var raw = candidates.Where(PhotoFileClassifier.IsRaw).ToList();
            var jpeg = candidates.Where(PhotoFileClassifier.IsJpeg).ToList();
            if (raw.Count != 1 || jpeg.Count != 1) return false;
            rawPath = raw[0];
            return true;
        }
    }

    public static class SidecarDestinationPath
    {
        public static string Derive(
            string imageRelativePath,
            string sidecarSourcePath,
            SidecarNamingStyle namingStyle)
        {
            if (string.IsNullOrWhiteSpace(imageRelativePath))
                throw new ArgumentException("The image destination path is required.", nameof(imageRelativePath));
            if (string.IsNullOrWhiteSpace(sidecarSourcePath))
                throw new ArgumentException("The sidecar source path is required.", nameof(sidecarSourcePath));
            if (!Enum.IsDefined(typeof(SidecarNamingStyle), namingStyle))
                throw new ArgumentOutOfRangeException(nameof(namingStyle));

            var directory = Path.GetDirectoryName(imageRelativePath) ?? string.Empty;
            var imageName = Path.GetFileName(imageRelativePath);
            var baseName = namingStyle == SidecarNamingStyle.FullImageFileName
                ? imageName
                : Path.GetFileNameWithoutExtension(imageName);
            var sidecarExtension = Path.GetExtension(sidecarSourcePath);
            return Path.Combine(directory, baseName + sidecarExtension);
        }

        public static IReadOnlyList<string> GetPotentialXmpPaths(string imageRelativePath)
        {
            if (string.IsNullOrWhiteSpace(imageRelativePath))
                throw new ArgumentException("The image destination path is required.", nameof(imageRelativePath));
            var directory = Path.GetDirectoryName(imageRelativePath) ?? string.Empty;
            var imageName = Path.GetFileName(imageRelativePath);
            var stem = Path.GetFileNameWithoutExtension(imageName);
            return new[]
            {
                Path.Combine(directory, stem + ".xmp"),
                Path.Combine(directory, imageName + ".xmp")
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
        }
    }
}
