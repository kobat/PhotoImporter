using System;
using System.IO;
using System.Linq;
using PhotoImporter.Core.Settings;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class RecentInputHistoryStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "PhotoImporter.Tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void MissingFileReturnsEmptyHistory()
        {
            var store = CreateStore();

            var history = store.Load();

            Assert.Empty(history.SourceFolders);
            Assert.Empty(history.DestinationFolders);
            Assert.Empty(history.Templates);
        }

        [Fact]
        public void RecordStoresEachValueMostRecentFirst()
        {
            var store = CreateStore();
            var source1 = Path.Combine(_root, "source1");
            var source2 = Path.Combine(_root, "source2");
            var destination1 = Path.Combine(_root, "destination1");
            var destination2 = Path.Combine(_root, "destination2");

            store.Record(source1, destination1, "Template A", 10);
            var history = store.Record(source2, destination2, "Template B", 10);

            Assert.Equal(new[] { Path.GetFullPath(source2), Path.GetFullPath(source1) }, history.SourceFolders);
            Assert.Equal(new[] { Path.GetFullPath(destination2), Path.GetFullPath(destination1) }, history.DestinationFolders);
            Assert.Equal(new[] { "Template B", "Template A" }, history.Templates);
        }

        [Fact]
        public void RecordingExistingValuesPromotesThemWithoutDuplicates()
        {
            var store = CreateStore();
            var source = Path.Combine(_root, "source");
            var destination = Path.Combine(_root, "destination");
            store.Record(source, destination, "Template", 10);
            store.Record(Path.Combine(_root, "other-source"), Path.Combine(_root, "other-destination"), "Other", 10);

            var history = store.Record(
                source.ToUpperInvariant(),
                destination.ToUpperInvariant(),
                "Template",
                10);

            Assert.Equal(2, history.SourceFolders.Count);
            Assert.Equal(Path.GetFullPath(source), history.SourceFolders[0], ignoreCase: true);
            Assert.Equal(2, history.DestinationFolders.Count);
            Assert.Equal(Path.GetFullPath(destination), history.DestinationFolders[0], ignoreCase: true);
            Assert.Equal(new[] { "Template", "Other" }, history.Templates);
        }

        [Fact]
        public void FolderHistoryIgnoresTrailingDirectorySeparators()
        {
            var store = CreateStore();
            var source = Path.Combine(_root, "source");
            var destination = Path.Combine(_root, "destination");
            store.Record(source, destination, "Template", 10);

            var history = store.Record(
                source + Path.DirectorySeparatorChar,
                destination + Path.DirectorySeparatorChar,
                "Template",
                10);

            Assert.Single(history.SourceFolders);
            Assert.Single(history.DestinationFolders);
        }

        [Fact]
        public void RecordTrimsOldestValuesToLimit()
        {
            var store = CreateStore();

            for (var index = 0; index < 12; index++)
            {
                store.Record(
                    Path.Combine(_root, "source" + index),
                    Path.Combine(_root, "destination" + index),
                    "Template " + index,
                    10);
            }

            var history = store.Load();
            Assert.Equal(10, history.SourceFolders.Count);
            Assert.EndsWith("source11", history.SourceFolders[0]);
            Assert.EndsWith("source2", history.SourceFolders[9]);
            Assert.Equal("Template 11", history.Templates[0]);
            Assert.Equal("Template 2", history.Templates[9]);
        }

        [Fact]
        public void ZeroLimitClearsAndDisablesHistory()
        {
            var store = CreateStore();
            store.Record(
                Path.Combine(_root, "source"),
                Path.Combine(_root, "destination"),
                "Template",
                10);

            var history = store.Record(
                Path.Combine(_root, "source2"),
                Path.Combine(_root, "destination2"),
                "Template 2",
                0);

            Assert.Empty(history.SourceFolders);
            Assert.Empty(history.DestinationFolders);
            Assert.Empty(history.Templates);
            Assert.Empty(store.Load().Templates);
        }

        [Fact]
        public void XmlRoundTripPreservesTemplateCharacters()
        {
            var store = CreateStore();
            var template = "{TakenDate:yyyy}\\写真 & 動画\\{FileName}\n{Extension}";

            store.Record(
                Path.Combine(_root, "source & media"),
                Path.Combine(_root, "destination & media"),
                template,
                10);
            var history = store.Load();

            Assert.Equal(template, Assert.Single(history.Templates));
        }

        [Fact]
        public void RecordArchivesCorruptFileAndStartsNewHistory()
        {
            Directory.CreateDirectory(_root);
            var historyPath = Path.Combine(_root, "history.xml");
            File.WriteAllText(historyPath, "<broken>");
            var store = new RecentInputHistoryStore(historyPath);

            var history = store.Record(
                Path.Combine(_root, "source"),
                Path.Combine(_root, "destination"),
                "Template",
                10);

            Assert.Single(history.SourceFolders);
            Assert.True(File.Exists(Path.Combine(_root, "history.bad.xml")));
            Assert.Single(store.Load().Templates);
        }

        [Fact]
        public void LoadReportsUnsupportedVersion()
        {
            Directory.CreateDirectory(_root);
            var historyPath = Path.Combine(_root, "history.xml");
            File.WriteAllText(historyPath, "<PhotoImporterInputHistory version=\"2\" />");
            var store = new RecentInputHistoryStore(historyPath);

            var error = Assert.Throws<InvalidDataException>(() => store.Load());

            Assert.Contains("バージョン", error.Message);
        }

        [Fact]
        public void RecordDoesNotReplaceUnsupportedVersion()
        {
            Directory.CreateDirectory(_root);
            var historyPath = Path.Combine(_root, "history.xml");
            var futureDocument = "<PhotoImporterInputHistory version=\"2\" />";
            File.WriteAllText(historyPath, futureDocument);
            var store = new RecentInputHistoryStore(historyPath);

            Assert.ThrowsAny<NotSupportedException>(() => store.Record(
                Path.Combine(_root, "source"),
                Path.Combine(_root, "destination"),
                "Template",
                10));

            Assert.Equal(futureDocument, File.ReadAllText(historyPath));
            Assert.Empty(Directory.GetFiles(_root, "history.bad*.xml"));
        }

        private RecentInputHistoryStore CreateStore() =>
            new RecentInputHistoryStore(Path.Combine(_root, "history.xml"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
