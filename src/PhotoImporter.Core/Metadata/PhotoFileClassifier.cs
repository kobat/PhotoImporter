using System;
using System.Collections.Generic;
using System.IO;

namespace PhotoImporter.Core.Metadata
{
    public enum PhotoFileType
    {
        Jpeg,
        Raw,
        OtherImage,
        Video,
        Other
    }

    public static class PhotoFileClassifier
    {
        private static readonly HashSet<string> JpegExtensions = CreateSet(".jpg", ".jpeg");
        private static readonly HashSet<string> RawExtensions = CreateSet(
            ".arw", ".cr2", ".cr3", ".dng", ".nef", ".nrw", ".orf", ".raf", ".rw2", ".pef", ".srw");
        private static readonly HashSet<string> OtherImageExtensions = CreateSet(
            ".bmp", ".gif", ".heic", ".heif", ".png", ".tif", ".tiff", ".webp");
        private static readonly HashSet<string> VideoExtensions = CreateSet(".avi", ".m4v", ".mov", ".mp4");

        public static string NormalizeExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var extension = Path.GetExtension(path);
            return (extension ?? string.Empty).ToLowerInvariant();
        }

        public static PhotoFileType Classify(string path)
        {
            var extension = NormalizeExtension(path);
            if (JpegExtensions.Contains(extension)) return PhotoFileType.Jpeg;
            if (RawExtensions.Contains(extension)) return PhotoFileType.Raw;
            if (OtherImageExtensions.Contains(extension)) return PhotoFileType.OtherImage;
            if (VideoExtensions.Contains(extension)) return PhotoFileType.Video;
            return PhotoFileType.Other;
        }

        public static bool IsJpeg(string path) => Classify(path) == PhotoFileType.Jpeg;
        public static bool IsRaw(string path) => Classify(path) == PhotoFileType.Raw;

        private static HashSet<string> CreateSet(params string[] extensions) =>
            new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
    }
}
