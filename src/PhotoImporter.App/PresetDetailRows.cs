using PhotoImporter.Core.Settings;
using System;
using System.Collections.Generic;

namespace PhotoImporter.App
{
    public sealed class PresetDetailRow
    {
        public PresetDetailRow(string label, string value)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Value = value ?? string.Empty;
        }

        public string Label { get; }
        public string Value { get; }
    }

    internal static class PresetDetailRows
    {
        public static IReadOnlyList<PresetDetailRow> CreateSettings(PhotoImporterPreset preset)
        {
            if (preset == null) return Array.Empty<PresetDetailRow>();
            var extensions = string.Join(", ", preset.SidecarExtensions);
            return new[]
            {
                new PresetDetailRow(
                    AppLocalization.Text("コピー元", "Source"),
                    preset.SaveSourceFolder
                        ? preset.SourceFolder ?? string.Empty
                        : AppLocalization.Text("（プリセットに保存しない）", "(Not saved in preset)")),
                new PresetDetailRow(AppLocalization.Text("コピー先", "Destination"), preset.DestinationFolder),
                new PresetDetailRow(AppLocalization.Text("テンプレート", "Template"), preset.TemplateText),
                new PresetDetailRow(AppLocalization.Text("既存ファイルを上書きする", "Overwrite existing files"), YesNo(preset.OverwriteExisting)),
                new PresetDetailRow(
                    AppLocalization.Text("画像・動画以外のファイルも含める", "Include files other than images and videos"),
                    YesNo(preset.SourceFileSelectionMode == SourceFileSelectionMode.AllFiles)),
                new PresetDetailRow(
                    AppLocalization.Text("同名のサイドカーファイルを画像に関連付ける", "Associate same-named sidecar files with images"),
                    YesNo(preset.AssociateSidecars)),
                new PresetDetailRow(AppLocalization.Text("対象拡張子", "Extensions"), string.IsNullOrEmpty(extensions) ? AppLocalization.Text("（指定なし）", "(None)") : extensions),
                new PresetDetailRow(
                    AppLocalization.Text("RAW+JPEGペアではJPEGのみ解析する", "Analyze only the JPEG in RAW+JPEG pairs"),
                    YesNo(preset.AnalyzeJpegOnlyForRawJpegPair)),
                new PresetDetailRow(
                    AppLocalization.Text("テンプレートで未使用の場合もExif情報を読み込む", "Read Exif data even when unused by the template"),
                    YesNo(preset.ReadExifInformation))
            };
        }

        public static IReadOnlyList<PresetDetailRow> CreateInformation(PhotoImporterPreset preset)
        {
            if (preset == null) return Array.Empty<PresetDetailRow>();
            return new[]
            {
                new PresetDetailRow(AppLocalization.Text("作成日時", "Created"), preset.CreatedUtc.ToLocalTime().ToString("g")),
                new PresetDetailRow(AppLocalization.Text("更新日時", "Updated"), preset.UpdatedUtc.ToLocalTime().ToString("g")),
                new PresetDetailRow(
                    AppLocalization.Text("最終利用日時", "Last used"),
                    preset.LastUsedUtc.HasValue
                        ? preset.LastUsedUtc.Value.ToLocalTime().ToString("g")
                        : AppLocalization.Text("未使用", "Never"))
            };
        }

        private static string YesNo(bool value) => value
            ? AppLocalization.Text("はい", "Yes")
            : AppLocalization.Text("いいえ", "No");
    }
}
