using PhotoImporter.Core.Settings;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PresetSettingsSnapshotTests
    {
        [Fact]
        public void ComparisonNormalizesPathsAndSidecarSet()
        {
            var snapshot = new PresetSettingsSnapshot(
                @"E:\DCIM\", @"D:\Photos\", "template", false,
                SourceFileSelectionMode.MediaOnly, true, new[] { "XMP", ".pp3" }, true, false);
            var preset = CreatePreset();
            preset.SourceFolder = @"e:\dcim";
            preset.DestinationFolder = @"d:\photos";
            preset.SidecarExtensions.Clear();
            preset.SidecarExtensions.Add(".PP3");
            preset.SidecarExtensions.Add(".xmp");

            Assert.True(snapshot.Matches(preset));
        }

        [Fact]
        public void UnsavedSourceIsIgnored()
        {
            var snapshot = new PresetSettingsSnapshot(
                @"Z:\Other", @"D:\Photos", "template", false,
                SourceFileSelectionMode.MediaOnly, true, new[] { ".xmp" }, true, false);
            var preset = CreatePreset();
            preset.SaveSourceFolder = false;

            Assert.True(snapshot.Matches(preset));
        }

        private static PhotoImporterPreset CreatePreset()
        {
            var preset = new PhotoImporterPreset
            {
                SaveSourceFolder = true,
                SourceFolder = @"E:\DCIM",
                DestinationFolder = @"D:\Photos",
                TemplateText = "template",
                SourceFileSelectionMode = SourceFileSelectionMode.MediaOnly,
                AssociateSidecars = true,
                AnalyzeJpegOnlyForRawJpegPair = true
            };
            preset.SidecarExtensions.Add(".xmp");
            return preset;
        }
    }
}
