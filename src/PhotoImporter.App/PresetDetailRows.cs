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
                    "コピー元",
                    preset.SaveSourceFolder
                        ? preset.SourceFolder ?? string.Empty
                        : "（プリセットに保存しない）"),
                new PresetDetailRow("コピー先", preset.DestinationFolder),
                new PresetDetailRow("テンプレート", preset.TemplateText),
                new PresetDetailRow("既存ファイルを上書きする", YesNo(preset.OverwriteExisting)),
                new PresetDetailRow(
                    "画像・動画以外のファイルも含める",
                    YesNo(preset.SourceFileSelectionMode == SourceFileSelectionMode.AllFiles)),
                new PresetDetailRow(
                    "同名のサイドカーファイルを画像に関連付ける",
                    YesNo(preset.AssociateSidecars)),
                new PresetDetailRow("対象拡張子", string.IsNullOrEmpty(extensions) ? "（指定なし）" : extensions),
                new PresetDetailRow(
                    "RAW+JPEGペアではJPEGのみ解析する",
                    YesNo(preset.AnalyzeJpegOnlyForRawJpegPair)),
                new PresetDetailRow(
                    "テンプレートで未使用の場合もExif情報を読み込む",
                    YesNo(preset.ReadExifInformation))
            };
        }

        public static IReadOnlyList<PresetDetailRow> CreateInformation(PhotoImporterPreset preset)
        {
            if (preset == null) return Array.Empty<PresetDetailRow>();
            return new[]
            {
                new PresetDetailRow("作成日時", preset.CreatedUtc.ToLocalTime().ToString("g")),
                new PresetDetailRow("更新日時", preset.UpdatedUtc.ToLocalTime().ToString("g")),
                new PresetDetailRow(
                    "最終利用日時",
                    preset.LastUsedUtc.HasValue
                        ? preset.LastUsedUtc.Value.ToLocalTime().ToString("g")
                        : "未使用")
            };
        }

        private static string YesNo(bool value) => value ? "はい" : "いいえ";
    }
}
