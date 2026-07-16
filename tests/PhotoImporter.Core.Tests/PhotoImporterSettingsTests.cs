using System;
using System.IO;
using PhotoImporter.Core.Settings;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PhotoImporterSettingsTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "PhotoImporter.Tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void MissingFileReturnsDocumentedDefaults()
        {
            var store = new PhotoImporterSettingsStore(Path.Combine(_root, "settings.xml"));

            var settings = store.Load();

            Assert.Equal(PhotoImporterSettings.DefaultTemplate, settings.TemplateText);
            Assert.True(settings.AnalyzeJpegOnlyForRawJpegPair);
            Assert.True(settings.UseExifCache);
            Assert.False(settings.ReadExifInformation);
            Assert.Null(settings.CustomExifCacheRoot);
            Assert.Empty(settings.PreviousExifCacheRoots);
        }

        [Fact]
        public void SaveAndLoadRoundTripsAllSettings()
        {
            var settingsPath = Path.Combine(_root, "config", "settings.xml");
            var customCache = Path.Combine(_root, "custom & cache");
            var previousCache = Path.Combine(_root, "previous cache");
            var store = new PhotoImporterSettingsStore(settingsPath);
            var settings = new PhotoImporterSettings
            {
                SourceFolder = @"D:\DCIM & Media",
                DestinationFolder = @"E:\写真",
                TemplateText = @"{TakenDate:yyyy}\A&B\{FileName}{Extension}",
                OverwriteExisting = true,
                AnalyzeJpegOnlyForRawJpegPair = false,
                UseExifCache = false,
                ReadExifInformation = true,
                CustomExifCacheRoot = customCache
            };
            settings.PreviousExifCacheRoots.Add(previousCache);
            settings.PreviousExifCacheRoots.Add(previousCache.ToUpperInvariant());

            store.Save(settings);
            var loaded = store.Load();

            Assert.Equal(settings.SourceFolder, loaded.SourceFolder);
            Assert.Equal(settings.DestinationFolder, loaded.DestinationFolder);
            Assert.Equal(settings.TemplateText, loaded.TemplateText);
            Assert.True(loaded.OverwriteExisting);
            Assert.False(loaded.AnalyzeJpegOnlyForRawJpegPair);
            Assert.False(loaded.UseExifCache);
            Assert.True(loaded.ReadExifInformation);
            Assert.Equal(Path.GetFullPath(customCache), loaded.CustomExifCacheRoot);
            Assert.Single(loaded.PreviousExifCacheRoots);
            Assert.Equal(Path.GetFullPath(previousCache), loaded.PreviousExifCacheRoots[0], ignoreCase: true);
        }

        [Fact]
        public void InvalidDocumentIsReportedInsteadOfSilentlyUsingDefaults()
        {
            Directory.CreateDirectory(_root);
            var settingsPath = Path.Combine(_root, "settings.xml");
            File.WriteAllText(settingsPath, "<not-settings />");
            var store = new PhotoImporterSettingsStore(settingsPath);

            var error = Assert.Throws<InvalidDataException>(() => store.Load());

            Assert.Contains("形式", error.Message);
        }

        [Fact]
        public void RelativeCustomCacheRootIsIgnored()
        {
            Directory.CreateDirectory(_root);
            var settingsPath = Path.Combine(_root, "settings.xml");
            File.WriteAllText(settingsPath,
                "<PhotoImporterSettings version=\"1\"><CustomExifCacheRoot>relative\\cache</CustomExifCacheRoot></PhotoImporterSettings>");
            var store = new PhotoImporterSettingsStore(settingsPath);

            var settings = store.Load();

            Assert.Null(settings.CustomExifCacheRoot);
        }

        [Fact]
        public void DefaultCacheRootTracksApplicationDirectory()
        {
            var settings = new PhotoImporterSettings();

            var first = settings.ResolveExifCacheRoot(Path.Combine(_root, "app1"));
            var second = settings.ResolveExifCacheRoot(Path.Combine(_root, "app2"));

            Assert.Equal(Path.Combine(Path.GetFullPath(Path.Combine(_root, "app1")), "ExifCache"), first);
            Assert.Equal(Path.Combine(Path.GetFullPath(Path.Combine(_root, "app2")), "ExifCache"), second);
        }

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
