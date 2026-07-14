using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace PhotoImporter.Core.Metadata
{
    public enum RawJpegAnalysisMode
    {
        JpegOnlyForPair,
        AnalyzeBoth
    }

    public sealed class RawJpegAnalysisPlan
    {
        private static readonly HashSet<string> JpegExtensions = new HashSet<string>(
            new[] { ".jpg", ".jpeg" }, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> RawExtensions = new HashSet<string>(
            new[] { ".arw", ".cr2", ".cr3", ".dng", ".nef", ".nrw", ".orf", ".raf", ".rw2", ".pef", ".srw" },
            StringComparer.OrdinalIgnoreCase);

        private readonly IReadOnlyDictionary<string, string> _analysisSourceByTarget;

        private RawJpegAnalysisPlan(
            IDictionary<string, string> analysisSourceByTarget,
            IList<string> analysisSources)
        {
            _analysisSourceByTarget = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(analysisSourceByTarget, StringComparer.OrdinalIgnoreCase));
            AnalysisSources = new ReadOnlyCollection<string>(analysisSources);
        }

        public IReadOnlyList<string> AnalysisSources { get; }

        public string GetAnalysisSource(string targetPath)
        {
            if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));
            string source;
            if (!_analysisSourceByTarget.TryGetValue(targetPath, out source))
                throw new ArgumentException("The target path is not part of this analysis plan.", nameof(targetPath));
            return source;
        }

        public static RawJpegAnalysisPlan Create(
            IEnumerable<string> targetPaths,
            RawJpegAnalysisMode mode = RawJpegAnalysisMode.JpegOnlyForPair)
        {
            if (targetPaths == null) throw new ArgumentNullException(nameof(targetPaths));
            if (!Enum.IsDefined(typeof(RawJpegAnalysisMode), mode))
                throw new ArgumentOutOfRangeException(nameof(mode));

            var paths = targetPaths.ToList();
            if (paths.Any(path => path == null))
                throw new ArgumentException("Target paths cannot contain null.", nameof(targetPaths));

            var sourceByTarget = paths.ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase);
            if (mode == RawJpegAnalysisMode.JpegOnlyForPair)
            {
                foreach (var group in paths.GroupBy(GetPairKey, StringComparer.OrdinalIgnoreCase))
                {
                    var jpeg = group.Where(IsJpeg).ToList();
                    var raw = group.Where(IsRaw).ToList();
                    if (jpeg.Count == 1 && raw.Count == 1)
                        sourceByTarget[raw[0]] = jpeg[0];
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<string>();
            foreach (var target in paths)
            {
                var source = sourceByTarget[target];
                if (seen.Add(source)) sources.Add(source);
            }
            return new RawJpegAnalysisPlan(sourceByTarget, sources);
        }

        private static string GetPairKey(string path)
        {
            return (Path.GetDirectoryName(path) ?? string.Empty) + "\0" + Path.GetFileNameWithoutExtension(path);
        }

        private static bool IsJpeg(string path) => JpegExtensions.Contains(Path.GetExtension(path));
        private static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));
    }
}
