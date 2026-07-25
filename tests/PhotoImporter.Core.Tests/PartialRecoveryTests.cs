using PhotoImporter.App;
using PhotoImporter.Core.Copying;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PartialRecoveryTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "PhotoImporterPartialRecoveryTests",
            Guid.NewGuid().ToString("N"));

        public PartialRecoveryTests() => Directory.CreateDirectory(_root);

        [Fact]
        public void ScanFindsOnlyStrictlyNamedNormalFilesUnderDestination()
        {
            var validRoot = CreateFile("PI_0123456789abcdef0123456789ABCDEF.partial");
            var validNested = CreateFile(
                "nested\\PI_FEDCBA9876543210fedcba9876543210.partial");
            CreateFile("PI_0123456789abcdef0123456789ABCDE.partial");
            CreateFile("prefix_PI_0123456789abcdef0123456789ABCDEF.partial");
            CreateFile("PI_0123456789abcdef0123456789ABCDEF.partial.txt");
            Directory.CreateDirectory(Path.Combine(
                _root, "PI_11111111111111111111111111111111.partial"));

            var result = new PartialRecoveryDetector().Scan(_root);

            Assert.Equal(
                new[] { validRoot, validNested }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
                result.Candidates.Select(item => item.Path));
            Assert.Empty(result.Warnings);
        }

        [Theory]
        [InlineData(FileAttributes.Directory)]
        [InlineData(FileAttributes.ReparsePoint)]
        [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint)]
        public void CandidateCheckRejectsDirectoriesAndReparsePoints(FileAttributes attributes)
        {
            var path = Path.Combine(
                _root, "PI_0123456789abcdef0123456789ABCDEF.partial");

            Assert.False(PartialRecoveryDetector.IsRecoveryCandidate(path, _root, attributes));
        }

        [Fact]
        public void CandidateCheckRejectsRootOutsideAndNameMismatch()
        {
            var outsideRoot = Path.Combine(_root + "-outside");
            var outside = Path.Combine(
                outsideRoot, "PI_0123456789abcdef0123456789ABCDEF.partial");
            var mismatch = Path.Combine(_root, "PI_not-a-guid.partial");

            Assert.False(PartialRecoveryDetector.IsRecoveryCandidate(
                outside, _root, FileAttributes.Normal));
            Assert.False(PartialRecoveryDetector.IsRecoveryCandidate(
                mismatch, _root, FileAttributes.Normal));
        }

        [Theory]
        [InlineData(PartialRecoveryDestinationState.Missing, "保全")]
        [InlineData(PartialRecoveryDestinationState.MatchesExpectedSource, "コピー完了")]
        [InlineData(PartialRecoveryDestinationState.MatchesPreviousSnapshot, "コピーをやり直")]
        [InlineData(PartialRecoveryDestinationState.RequiresComparison, "比較")]
        public void GuidanceChangesWithDestinationAndSnapshotState(
            PartialRecoveryDestinationState state,
            string expected)
        {
            Assert.Contains(expected, PartialRecoveryGuidance.Describe(state));
        }

        [Fact]
        public void StartupMessageNamesCandidatesAndNeverSuggestsAutomaticDeletion()
        {
            var path = Path.Combine(
                _root, "PI_0123456789abcdef0123456789ABCDEF.partial");
            var result = new PartialRecoveryScanResult(
                new List<PartialRecoveryCandidate>
                {
                    new PartialRecoveryCandidate(path, 10, DateTime.UtcNow)
                },
                new List<string>());

            var message = MainWindow.BuildPartialRecoveryMessage(result);

            Assert.Contains(path, message);
            Assert.Contains("自動削除・自動昇格はしていません", message);
            Assert.Contains("手動で再スキャン", message);
        }

        private string CreateFile(string relativePath)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            return Path.GetFullPath(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
