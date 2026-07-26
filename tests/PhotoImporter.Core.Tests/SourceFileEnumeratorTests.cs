using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PhotoImporter.App;
using PhotoImporter.Core.Settings;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class SourceFileEnumeratorTests
    {
        [Fact]
        public void IncludesOnlySupportedExtensionsCaseInsensitively()
        {
            var root = @"E:\";
            var fileSystem = new FakeSourceFileSystem(root)
                .AddFile(root, "photo.JPG")
                .AddFile(root, "negative.nEf")
                .AddFile(root, "clip.MP4")
                .AddFile(root, "notes.txt");

            var result = new SourceFileEnumerator(fileSystem).Enumerate(
                root, CancellationToken.None);

            Assert.Equal(
                new[] { @"E:\clip.MP4", @"E:\negative.nEf", @"E:\photo.JPG" },
                result.Files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            Assert.Empty(result.Issues);
        }

        [Fact]
        public void AllFilesModeIncludesUnsupportedAndExtensionlessFiles()
        {
            var root = @"E:\";
            var fileSystem = new FakeSourceFileSystem(root)
                .AddFile(root, "photo.JPG")
                .AddFile(root, "notes.txt")
                .AddFile(root, "README");

            var result = new SourceFileEnumerator(fileSystem).Enumerate(
                root,
                SourceFileSelectionMode.AllFiles,
                CancellationToken.None);

            Assert.Equal(
                new[] { @"E:\notes.txt", @"E:\photo.JPG", @"E:\README" },
                result.Files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            Assert.Empty(result.Issues);
        }

        [Fact]
        public void SidecarCandidateCanBeCollectedWithoutIncludingOtherFiles()
        {
            var root = @"E:\";
            var fileSystem = new FakeSourceFileSystem(root)
                .AddFile(root, "photo.jpg")
                .AddFile(root, "photo.xmp")
                .AddFile(root, "notes.txt");

            var result = new SourceFileEnumerator(fileSystem).Enumerate(
                root,
                SourceFileSelectionMode.MediaOnly,
                true,
                CancellationToken.None);

            Assert.Equal(
                new[] { @"E:\photo.jpg", @"E:\photo.xmp" },
                result.Files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void AllFilesModeStillExcludesKnownSystemFilesAndAreas()
        {
            var root = @"E:\";
            var fileSystem = new FakeSourceFileSystem(root)
                .AddFile(root, "Thumbs.db")
                .AddFile(root, "desktop.ini")
                .AddFile(root, "autorun.inf")
                .AddDirectory(root, "System Volume Information")
                .AddDirectory(root, "$RECYCLE.BIN");

            var result = new SourceFileEnumerator(fileSystem).Enumerate(
                root,
                SourceFileSelectionMode.AllFiles,
                CancellationToken.None);

            Assert.Equal(new[] { @"E:\autorun.inf" }, result.Files);
            Assert.DoesNotContain(
                fileSystem.RequestedDirectories,
                path => path.IndexOf("System Volume Information", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void ExcludesKnownSystemFilesAndDirectories()
        {
            var root = @"E:\";
            var fileSystem = new FakeSourceFileSystem(root)
                .AddFile(root, "Thumbs.db")
                .AddFile(root, "desktop.ini")
                .AddDirectory(root, "System Volume Information")
                .AddDirectory(root, "$RECYCLE.BIN")
                .AddDirectory(root, "DCIM")
                .AddFile(@"E:\DCIM", "photo.jpg");

            var result = new SourceFileEnumerator(fileSystem).Enumerate(
                root, CancellationToken.None);

            Assert.Equal(new[] { @"E:\DCIM\photo.jpg" }, result.Files);
            Assert.DoesNotContain(
                fileSystem.RequestedDirectories,
                path => path.IndexOf("System Volume Information", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void ContinuesAfterInaccessibleSubfolder()
        {
            var root = @"E:\";
            var fileSystem = new FakeSourceFileSystem(root)
                .AddDirectory(root, "PRIVATE")
                .AddDirectory(root, "DCIM")
                .AddFile(@"E:\DCIM", "photo.jpg")
                .FailDirectory(@"E:\PRIVATE", new UnauthorizedAccessException("access denied"));

            var result = new SourceFileEnumerator(fileSystem).Enumerate(
                root, CancellationToken.None);

            Assert.Equal(new[] { @"E:\DCIM\photo.jpg" }, result.Files);
            Assert.Equal("PRIVATE", Assert.Single(result.Issues).Path);
        }

        [Fact]
        public void AbortsWhenSourceRootBecomesUnavailable()
        {
            var root = @"E:\";
            var fileSystem = new FakeSourceFileSystem(root)
                .AddDirectory(root, "DCIM")
                .FailDirectory(@"E:\DCIM", new IOException("device removed"));
            fileSystem.OnGetEntries = path =>
            {
                if (string.Equals(path, @"E:\DCIM", StringComparison.OrdinalIgnoreCase))
                    fileSystem.RootAvailable = false;
            };

            var error = Assert.Throws<IOException>(() =>
                new SourceFileEnumerator(fileSystem).Enumerate(root, CancellationToken.None));

            Assert.Contains("コピー元全体", error.Message);
            Assert.Contains("スキャンを中止", error.Message);
        }

        private sealed class FakeSourceFileSystem : ISourceFileSystem
        {
            private readonly string _root;
            private readonly Dictionary<string, List<SourceEntry>> _entries =
                new Dictionary<string, List<SourceEntry>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Exception> _errors =
                new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

            internal FakeSourceFileSystem(string root)
            {
                _root = root;
                RootAvailable = true;
                _entries[root] = new List<SourceEntry>();
            }

            internal bool RootAvailable { get; set; }
            internal Action<string> OnGetEntries { get; set; }
            internal List<string> RequestedDirectories { get; } = new List<string>();

            internal FakeSourceFileSystem AddFile(string directory, string name)
            {
                Entries(directory).Add(new SourceEntry(
                    Path.Combine(directory, name), name, false, FileAttributes.Normal));
                return this;
            }

            internal FakeSourceFileSystem AddDirectory(string directory, string name)
            {
                var fullPath = Path.Combine(directory, name);
                Entries(directory).Add(new SourceEntry(
                    fullPath, name, true, FileAttributes.Directory));
                Entries(fullPath);
                return this;
            }

            internal FakeSourceFileSystem FailDirectory(string directory, Exception error)
            {
                _errors[directory] = error;
                return this;
            }

            public bool DirectoryExists(string path) =>
                !string.Equals(path, _root, StringComparison.OrdinalIgnoreCase) || RootAvailable;

            public IReadOnlyList<SourceEntry> GetEntries(string directory)
            {
                RequestedDirectories.Add(directory);
                if (_errors.TryGetValue(directory, out var error))
                {
                    OnGetEntries?.Invoke(directory);
                    throw error;
                }
                return Entries(directory);
            }

            private List<SourceEntry> Entries(string directory)
            {
                if (!_entries.TryGetValue(directory, out var entries))
                {
                    entries = new List<SourceEntry>();
                    _entries.Add(directory, entries);
                }
                return entries;
            }
        }
    }
}
