using System;
using System.IO;

namespace PhotoImporter.App
{
    internal sealed class PreviewFileSnapshot
    {
        private PreviewFileSnapshot(
            string fullName,
            string name,
            long length,
            DateTime lastWriteTime,
            DateTime lastWriteTimeUtc,
            FileAttributes attributes)
        {
            FullName = fullName;
            Name = name;
            Length = length;
            LastWriteTime = lastWriteTime;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Attributes = attributes;
        }

        public string FullName { get; }
        public string Name { get; }
        public long Length { get; }
        public DateTime LastWriteTime { get; }
        public DateTime LastWriteTimeUtc { get; }
        public FileAttributes Attributes { get; }

        public static PreviewFileSnapshot CaptureTarget(string path) =>
            Capture(path, AppLocalization.Text("コピー元ファイル", "The source file"));

        public static PreviewFileSnapshot CaptureAnalysisSource(
            PreviewFileSnapshot targetSnapshot,
            string targetPath,
            string analysisSourcePath)
        {
            if (targetSnapshot == null) throw new ArgumentNullException(nameof(targetSnapshot));
            if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));
            if (analysisSourcePath == null) throw new ArgumentNullException(nameof(analysisSourcePath));

            return string.Equals(targetPath, analysisSourcePath, StringComparison.OrdinalIgnoreCase)
                ? targetSnapshot
                : Capture(
                    analysisSourcePath,
                    AppLocalization.Text("RAW+JPEGペアの解析元ファイル", "The analysis source file for the RAW+JPEG pair"));
        }

        private static PreviewFileSnapshot Capture(string path, string description)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(
                    AppLocalization.Text("ファイルのパスが必要です。", "A file path is required."),
                    nameof(path));

            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists)
                throw new FileNotFoundException(
                    description + AppLocalization.Text(
                        "が見つかりません。もう一度スキャンしてください。",
                        " was not found. Scan again."),
                    info.FullName);

            return new PreviewFileSnapshot(
                info.FullName,
                info.Name,
                info.Length,
                info.LastWriteTime,
                info.LastWriteTimeUtc,
                info.Attributes);
        }
    }
}
