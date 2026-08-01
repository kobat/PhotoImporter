using PhotoImporter.Core.Settings;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PhotoImporterPresetStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "PhotoImporter.PresetTests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void AddAndLoadRoundTripsPresetAndUsesDocumentedEncoding()
        {
            var path = Path.Combine(_root, "presets.xml");
            var store = new PhotoImporterPresetStore(path);
            var preset = CreatePreset("旅行RAW取込");

            store.Add(preset);
            var result = store.Load();

            var loaded = Assert.Single(result.Presets);
            Assert.Null(result.Warning);
            Assert.Equal(preset.Id, loaded.Id);
            Assert.Equal(preset.Name, loaded.Name);
            Assert.True(loaded.SaveSourceFolder);
            Assert.Equal(@"E:\DCIM", loaded.SourceFolder);
            Assert.Equal(@"D:\Photos", loaded.DestinationFolder);
            Assert.Equal(new[] { ".xmp", ".pp3" }, loaded.SidecarExtensions);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes.Take(3).ToArray());
            Assert.Contains("\r\n", Encoding.UTF8.GetString(bytes));
        }

        [Fact]
        public void SemanticInvalidValuesArePreserved()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "presets.xml");
            File.WriteAllText(path, CreateXml("<bad-template>", "*.jpg"), new UTF8Encoding(true));

            var loaded = Assert.Single(new PhotoImporterPresetStore(path).Load().Presets);

            Assert.Equal("<bad-template>", loaded.TemplateText);
            Assert.Equal("*.jpg", Assert.Single(loaded.SidecarExtensions));
        }

        [Fact]
        public void UnsupportedVersionIsLeftUntouched()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "presets.xml");
            var original = "<PhotoImporterPresets version=\"2\"><Future /></PhotoImporterPresets>";
            File.WriteAllText(path, original);

            var result = new PhotoImporterPresetStore(path).Load();

            Assert.True(result.UnsupportedVersion);
            Assert.Empty(result.Presets);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(_root, "presets.bad*.xml"));
        }

        [Fact]
        public void CorruptFileIsBackedUpWithoutOverwritingExistingBackup()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "presets.xml");
            File.WriteAllText(Path.Combine(_root, "presets.bad.xml"), "keep");
            File.WriteAllText(path, "<broken>");

            var result = new PhotoImporterPresetStore(path).Load();

            Assert.Empty(result.Presets);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "presets.bad.xml")));
            Assert.True(File.Exists(Path.Combine(_root, "presets.bad.1.xml")));
            Assert.Empty(new PhotoImporterPresetStore(path).Load().Presets);
        }

        [Fact]
        public void UpdateRereadsAndDoesNotLoseAnotherStoreAddition()
        {
            var path = Path.Combine(_root, "presets.xml");
            var firstStore = new PhotoImporterPresetStore(path);
            var secondStore = new PhotoImporterPresetStore(path);
            var first = CreatePreset("A");
            var second = CreatePreset("B");
            firstStore.Add(first);
            secondStore.Add(second);
            first.Name = "A2";
            first.UpdatedUtc = first.UpdatedUtc.AddMinutes(1);

            firstStore.Update(first);

            var loaded = firstStore.Load().Presets;
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, item => item.Name == "A2");
            Assert.Contains(loaded, item => item.Name == "B");
        }

        [Fact]
        public void MutexTimeoutFailsMutation()
        {
            var path = Path.Combine(_root, "presets.xml");
            Directory.CreateDirectory(_root);
            using (var mutex = new Mutex(false, PhotoImporterPresetStore.CreateMutexName(path)))
            {
                Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(1)));
                Exception failure = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        new PhotoImporterPresetStore(path, TimeSpan.FromMilliseconds(50)).Add(CreatePreset("A"));
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });
                thread.Start();
                Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
                Assert.IsType<TimeoutException>(failure);
                mutex.ReleaseMutex();
            }
        }

        [Fact]
        public void AbandonedMutexIsAcquiredAndMutationContinues()
        {
            var path = Path.Combine(_root, "presets.xml");
            Directory.CreateDirectory(_root);
            var thread = new Thread(() =>
            {
                var mutex = new Mutex(false, PhotoImporterPresetStore.CreateMutexName(path));
                mutex.WaitOne();
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)));

            var saved = new PhotoImporterPresetStore(path).Add(CreatePreset("A"));

            Assert.Single(saved);
        }

        [Fact]
        public void UpdateDoesNotRecreatePresetDeletedByAnotherStore()
        {
            var path = Path.Combine(_root, "presets.xml");
            var firstStore = new PhotoImporterPresetStore(path);
            var secondStore = new PhotoImporterPresetStore(path);
            var preset = CreatePreset("A");
            firstStore.Add(preset);
            secondStore.Delete(preset.Id);
            preset.Name = "A2";

            var error = Assert.Throws<InvalidOperationException>(() => firstStore.Update(preset));

            Assert.Contains("削除", error.Message);
            Assert.Empty(firstStore.Load().Presets);
        }

        [Fact]
        public void UnknownElementsArePreservedOnUpdate()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "presets.xml");
            File.WriteAllText(path, CreateXml("{FileName}{Extension}", ".xmp")
                .Replace("<PhotoImporterPresets version=\"1\">", "<PhotoImporterPresets version=\"1\" future=\"root\"><FutureRoot />")
                .Replace("</Preset>", "<Filter future=\"yes\" /></Preset>"));
            var store = new PhotoImporterPresetStore(path);
            var preset = Assert.Single(store.Load().Presets);
            preset.Name = "更新後";
            preset.UpdatedUtc = preset.UpdatedUtc.AddMinutes(1);

            store.Update(preset);

            var xml = File.ReadAllText(path);
            Assert.Contains("future=\"root\"", xml);
            Assert.Contains("<FutureRoot", xml);
            Assert.Contains("<Filter future=\"yes\"", xml);
        }

        private static PhotoImporterPreset CreatePreset(string name)
        {
            var now = new DateTime(2026, 8, 2, 1, 23, 45, DateTimeKind.Utc).AddTicks(6789012);
            var preset = new PhotoImporterPreset
            {
                Id = Guid.NewGuid(),
                Name = name,
                CreatedUtc = now,
                UpdatedUtc = now,
                LastUsedUtc = now,
                SaveSourceFolder = true,
                SourceFolder = @"E:\DCIM",
                DestinationFolder = @"D:\Photos",
                TemplateText = @"{TakenDate:yyyy-MM-dd}\{FileName}{Extension}",
                OverwriteExisting = true,
                SourceFileSelectionMode = SourceFileSelectionMode.AllFiles,
                AssociateSidecars = true,
                AnalyzeJpegOnlyForRawJpegPair = false,
                ReadExifInformation = true
            };
            preset.SidecarExtensions.Add(".xmp");
            preset.SidecarExtensions.Add(".pp3");
            return preset;
        }

        private static string CreateXml(string template, string extension)
        {
            return "<PhotoImporterPresets version=\"1\">" +
                   "<Preset id=\"01234567-89ab-cdef-0123-456789abcdef\" name=\"Test\" " +
                   "createdUtc=\"2026-08-02T01:23:45.0000000Z\" updatedUtc=\"2026-08-02T01:23:45.0000000Z\">" +
                   "<SourceFolder saved=\"false\" />" +
                   "<DestinationFolder>D:\\Photos</DestinationFolder>" +
                   "<TemplateText>" + System.Security.SecurityElement.Escape(template) + "</TemplateText>" +
                   "<OverwriteExisting>false</OverwriteExisting>" +
                   "<SourceFileSelectionMode>MediaOnly</SourceFileSelectionMode>" +
                   "<AssociateSidecars>true</AssociateSidecars>" +
                   "<SidecarExtensions><Extension>" + System.Security.SecurityElement.Escape(extension) + "</Extension></SidecarExtensions>" +
                   "<AnalyzeJpegOnlyForRawJpegPair>true</AnalyzeJpegOnlyForRawJpegPair>" +
                   "<ReadExifInformation>false</ReadExifInformation>" +
                   "</Preset></PhotoImporterPresets>";
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
