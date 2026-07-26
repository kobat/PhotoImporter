using PhotoImporter.App;
using PhotoImporter.Core.Metadata;
using System;
using System.IO;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PreviewFileSnapshotTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "PhotoImporter-PreviewSnapshot-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void CaptureAnalysisSource_WhenTargetIsAnalysisSource_ReusesTargetSnapshot()
        {
            var path = CreateFile("single.arw", "raw");
            var target = PreviewFileSnapshot.CaptureTarget(path);

            var analysisSource = PreviewFileSnapshot.CaptureAnalysisSource(target, path, path);

            Assert.Same(target, analysisSource);
        }

        [Fact]
        public void CaptureAnalysisSource_ForPairUsesRefreshedJpegSnapshot()
        {
            var raw = CreateFile("pair.arw", "raw");
            var jpeg = CreateFile("pair.jpg", "jpeg");
            var expectedUtc = new DateTime(2026, 7, 20, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(jpeg, expectedUtc);
            var plan = RawJpegAnalysisPlan.Create(new[] { raw, jpeg });
            var target = PreviewFileSnapshot.CaptureTarget(raw);

            var analysisSource = PreviewFileSnapshot.CaptureAnalysisSource(
                target,
                raw,
                plan.GetAnalysisSource(raw));

            Assert.Equal(Path.GetFullPath(jpeg), analysisSource.FullName, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(File.GetLastWriteTimeUtc(jpeg), analysisSource.LastWriteTimeUtc);
            Assert.NotEqual(new DateTime(1601, 1, 1), analysisSource.LastWriteTime);
        }

        [Fact]
        public void CaptureAnalysisSource_WhenPairedJpegWasDeleted_ThrowsInsteadOfReturning1601()
        {
            var raw = CreateFile("deleted-pair.arw", "raw");
            var jpeg = CreateFile("deleted-pair.jpg", "jpeg");
            var plan = RawJpegAnalysisPlan.Create(new[] { raw, jpeg });
            var target = PreviewFileSnapshot.CaptureTarget(raw);
            File.Delete(jpeg);

            var error = Assert.Throws<FileNotFoundException>(() =>
                PreviewFileSnapshot.CaptureAnalysisSource(
                    target,
                    raw,
                    plan.GetAnalysisSource(raw)));

            Assert.Equal(Path.GetFullPath(jpeg), error.FileName, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("RAW+JPEGペアの解析元ファイル", error.Message);
            Assert.Contains("もう一度スキャン", error.Message);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string CreateFile(string name, string contents)
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, contents);
            return path;
        }
    }
}
