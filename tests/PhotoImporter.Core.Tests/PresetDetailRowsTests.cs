using PhotoImporter.App;
using PhotoImporter.Core.Settings;
using System;
using System.Linq;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PresetDetailRowsTests
    {
        [Fact]
        public void SettingsUseMainScreenLabelsAndYesNoValues()
        {
            var preset = CreatePreset();
            preset.OverwriteExisting = false;
            preset.SourceFileSelectionMode = SourceFileSelectionMode.AllFiles;
            preset.AssociateSidecars = true;
            preset.AnalyzeJpegOnlyForRawJpegPair = false;
            preset.ReadExifInformation = true;
            var rows = PresetDetailRows.CreateSettings(preset).ToDictionary(item => item.Label, item => item.Value);

            Assert.Equal("いいえ", rows["既存ファイルを上書きする"]);
            Assert.Equal("はい", rows["画像・動画以外のファイルも含める"]);
            Assert.Equal("はい", rows["同名のサイドカーファイルを画像に関連付ける"]);
            Assert.Equal(".xmp, .pp3", rows["対象拡張子"]);
            Assert.Equal("いいえ", rows["RAW+JPEGペアではJPEGのみ解析する"]);
            Assert.Equal("はい", rows["テンプレートで未使用の場合もExif情報を読み込む"]);
        }

        [Fact]
        public void UnsavedSourceAndUnusedPresetHaveExplicitValues()
        {
            var preset = CreatePreset();
            preset.SaveSourceFolder = false;
            preset.LastUsedUtc = null;

            var settings = PresetDetailRows.CreateSettings(preset).ToDictionary(item => item.Label, item => item.Value);
            var information = PresetDetailRows.CreateInformation(preset)
                .ToDictionary(item => item.Label, item => item.Value);

            Assert.Equal("（プリセットに保存しない）", settings["コピー元"]);
            Assert.Equal("未使用", information["最終利用日時"]);
        }

        private static PhotoImporterPreset CreatePreset()
        {
            var preset = new PhotoImporterPreset
            {
                Id = Guid.NewGuid(),
                Name = "Test",
                CreatedUtc = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
                UpdatedUtc = new DateTime(2026, 8, 2, 1, 0, 0, DateTimeKind.Utc),
                SaveSourceFolder = true,
                SourceFolder = @"E:\DCIM",
                DestinationFolder = @"D:\Photos",
                TemplateText = @"{FileName}{Extension}"
            };
            preset.SidecarExtensions.Add(".xmp");
            preset.SidecarExtensions.Add(".pp3");
            return preset;
        }
    }
}
