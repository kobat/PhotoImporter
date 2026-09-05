using System;
using System.Collections.Generic;
using System.Globalization;

namespace PhotoImporter.App
{
    internal static class AppLocalization
    {
        public const string Automatic = "auto";
        public const string Japanese = "ja";
        public const string English = "en";

        private static readonly IReadOnlyDictionary<string, string> EnglishText =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["トークン詳細の表示切り替え"] = "Toggle token details",
                ["トークンの説明と書式設定の表示を切り替えます"] = "Show or hide the token description and format settings.",
                ["書式設定"] = "Format",
                ["コピー元"] = "Source",
                ["写真があるフォルダー"] = "Folder containing the photos",
                ["コピー元フォルダー"] = "Source folder",
                ["選択..."] = "Browse...",
                ["コピー先"] = "Destination",
                ["取り込み先のフォルダー"] = "Destination folder",
                ["コピー先フォルダー"] = "Destination folder",
                ["テンプレート"] = "Template",
                ["スキャン"] = "Scan",
                ["画像・動画以外のファイルも含める"] = "Include files other than images and videos",
                ["有効にすると、既知のシステム管理項目を除くすべての通常ファイルをコピー対象としてスキャンします。"] = "When enabled, scan all regular files except known system-managed items.",
                ["同名のサイドカーファイルを画像に関連付ける"] = "Associate same-named sidecar files with images",
                ["画像と同じフォルダー・同じ名前の対象ファイルを、画像のコピー先名と連番へ追従させます。"] = "Make matching files in the same folder follow the image destination name and sequence number.",
                ["対象拡張子"] = "Extensions",
                ["セミコロン、カンマ、または空白で区切ります。例: xmp; aae; pp3"] = "Separate with semicolons, commas, or spaces. Example: xmp; aae; pp3",
                ["RAW+JPEGペアではJPEGのみ解析する"] = "Analyze only the JPEG in RAW+JPEG pairs",
                ["同じフォルダーにある同名のRAWとJPEGをペアとして扱います。ペアにならないRAWは個別に解析します"] = "Treat same-named RAW and JPEG files in the same folder as a pair. Unpaired RAW files are analyzed separately.",
                ["Exif設定"] = "Exif settings",
                ["設定..."] = "Settings...",
                ["Exif設定を開く"] = "Open Exif settings",
                ["閉じる"] = "Close",
                ["Exif設定を閉じる"] = "Close Exif settings",
                ["テンプレートで未使用の場合もExif情報を読み込む"] = "Read Exif data even when the template does not use it",
                ["Exif キャッシュを利用する"] = "Use the Exif cache",
                ["現在使用する Exif キャッシュの保存先"] = "Current Exif cache location",
                ["保存先を変更..."] = "Change location...",
                ["既定に戻す"] = "Restore default",
                ["保存先を変えても、以前の保存先にあるキャッシュは移動・削除されません。"] = "Changing the location does not move or delete caches in previous locations.",
                ["Exif キャッシュを管理..."] = "Manage Exif cache...",
                ["編集..."] = "Edit...",
                ["フィルター条件を編集"] = "Edit filter conditions",
                ["絞り込みで非表示になった項目のチェックを外す"] = "Clear selections hidden by filtering",
                ["コピー可能な項目をすべて選択または解除"] = "Select or clear all copyable items",
                ["コピー可能な項目をすべて選択／解除"] = "Select or clear all copyable items",
                ["対象"] = "Copy",
                ["関連先"] = "Related file",
                ["コピー先（予定）"] = "Planned destination",
                ["状態"] = "Status",
                ["フィルター条件"] = "Filter conditions",
                ["このファイルから条件を追加..."] = "Add condition from this file...",
                ["候補から選択..."] = "Choose from scan...",
                ["スキャン結果から条件を選択"] = "Choose filter values from scanned files",
                ["文字列入力に戻す"] = "Use text input",
                ["候補から条件を指定"] = "Choose filter values",
                ["条件の項目"] = "Filter field",
                ["候補を検索"] = "Search values",
                ["時刻も選ぶ"] = "Include times",
                ["最新の日"] = "Latest day",
                ["全期間"] = "Full period",
                ["Exifを読み込んで候補を更新"] = "Load Exif to update values",
                ["スキャン結果の候補"] = "Values from scanned files",
                ["候補の使い方"] = "Use selected value as",
                ["条件に設定"] = "Use in condition",
                ["フィルター条件を閉じる"] = "Close filter conditions",
                ["すべての条件に一致する項目を表示します。"] = "Show items that match all conditions.",
                ["削除"] = "Remove",
                ["このフィルター条件を削除"] = "Remove this filter condition",
                ["比較する文字列"] = "Text to compare",
                ["大文字・小文字を区別"] = "Match case",
                ["最小"] = "Minimum",
                ["最大"] = "Maximum",
                ["連番なし"] = "No sequence number",
                ["開始日"] = "Start date",
                ["終了日"] = "End date",
                ["任意の時刻（HH:mm または HH:mm:ss）"] = "Optional time (HH:mm or HH:mm:ss)",
                ["タイムゾーン"] = "Time zone",
                ["Unknownを含める"] = "Include Unknown",
                ["条件を追加"] = "Add condition",
                ["クリア"] = "Clear",
                ["適用"] = "Apply",
                ["トークン詳細"] = "Token details",
                ["画像プレビューを表示"] = "Show image preview",
                ["選択した画像のプレビューを表示"] = "Show a preview of the selected image",
                ["選択した画像のプレビュー"] = "Preview of the selected image",
                ["書式欄を変更すると、その書式で値を再評価します。"] = "Changing a format field reevaluates the value using that format.",
                ["ファイルシステム系"] = "File system",
                ["Exif系"] = "Exif",
                ["プリセット管理"] = "Preset manager",
                ["プリセットの表示順"] = "Preset sort order",
                ["設定内容"] = "Settings",
                ["プリセット情報"] = "Preset information",
                ["名前の変更..."] = "Rename...",
                ["名前を変更..."] = "Rename...",
                ["複製..."] = "Duplicate...",
                ["削除..."] = "Delete...",
                ["テンプレートだけ取り込む"] = "Apply template only",
                ["エクスポート..."] = "Export...",
                ["インポート..."] = "Import...",
                ["プリセット"] = "Preset",
                ["設定プリセット"] = "Settings preset",
                ["プリセット操作"] = "Preset actions",
                ["保存"] = "Save",
                ["名前を付けて保存..."] = "Save as...",
                ["管理..."] = "Manage...",
                ["プリセットを適用しました"] = "Preset applied",
                ["元に戻す"] = "Undo",
                ["既存ファイルを上書きする"] = "Overwrite existing files",
                ["キャンセル"] = "Cancel",
                ["処理の進捗"] = "Progress",
                ["例: {ModifiedDate:yyyy-MM-dd}\\{FileName}{Sequence}{Extension}"] = "Example: {ModifiedDate:yyyy-MM-dd}\\{FileName}{Sequence}{Extension}",

                ["Photo Importer バージョン情報"] = "About Photo Importer",
                ["Photo Importer アプリアイコン"] = "Photo Importer application icon",
                ["GitHub リポジトリ"] = "GitHub repository",
                ["GitHubでリポジトリを開く"] = "Open the repository on GitHub",
                ["ライセンス情報..."] = "License information...",
                ["ライセンス情報を表示する"] = "Show license information",
                ["表示言語"] = "Display language",

                ["Photo Importer ライセンス情報"] = "Photo Importer License Information",
                ["ライセンス情報"] = "License information",
                ["Photo Importer と、配布物に含まれる第三者ライブラリのライセンスを表示します。"] = "Licenses for Photo Importer and the third-party libraries included in the distribution.",
                ["ライセンスの対象"] = "License entries",
                ["ライセンス原文"] = "License text",

                ["Exif キャッシュの管理"] = "Manage Exif Cache",
                ["カードごとのキャッシュ容量を確認し、名前の変更や古いエントリの整理ができます。キャッシュを削除しても写真ファイルは削除されません。"] = "Review cache usage for each card, rename cards, and remove old entries. Deleting a cache does not delete photo files.",
                ["現在の保存先"] = "Current location",
                ["現在の Exif キャッシュ保存先"] = "Current Exif cache location",
                ["保存先"] = "Locations",
                ["Exif キャッシュの保存先一覧"] = "Exif cache locations",
                ["キャッシュのあるカード一覧"] = "Cards with cached data",
                ["名前"] = "Name",
                ["ボリューム"] = "Volume",
                ["種類"] = "Type",
                ["記録媒体容量"] = "Media size",
                ["キャッシュ"] = "Cache",
                ["件数"] = "Entries",
                ["最終利用日 (UTC)"] = "Last used (UTC)",
                ["状態詳細"] = "Details",
                ["この保存先には管理対象のキャッシュがありません。"] = "This location contains no managed cache data.",
                ["古いエントリを整理..."] = "Remove old entries...",
                ["カードのキャッシュを削除..."] = "Delete card cache...",
                ["更新"] = "Refresh"
            };

        private static string _preference = Automatic;
        private static bool _isEnglish;

        static AppLocalization()
        {
            Configure(Automatic);
        }

        public static string Preference => _preference;
        public static bool IsEnglish => _isEnglish;

        public static void Configure(string preference)
        {
            _preference = NormalizePreference(preference);
            _isEnglish = _preference == English ||
                         _preference == Automatic &&
                         !CultureInfo.CurrentUICulture.Name.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizePreference(string preference)
        {
            if (string.Equals(preference, Japanese, StringComparison.OrdinalIgnoreCase)) return Japanese;
            if (string.Equals(preference, English, StringComparison.OrdinalIgnoreCase)) return English;
            return Automatic;
        }

        public static string Text(string japanese, string english)
        {
            return IsEnglish ? english : japanese;
        }

        public static string Translate(string japanese)
        {
            if (!IsEnglish || string.IsNullOrEmpty(japanese)) return japanese;
            string translated;
            return EnglishText.TryGetValue(japanese, out translated) ? translated : japanese;
        }

        public static string Format(string japanese, string english, params object[] arguments)
        {
            return string.Format(CultureInfo.CurrentCulture, IsEnglish ? english : japanese, arguments);
        }

        public static string UserMessage(string message)
        {
            if (!IsEnglish || string.IsNullOrEmpty(message)) return message;
            var replacements = new[]
            {
                Pair("コピー元とコピー先には、同一または互いの配下ではないフォルダーを指定してください。", "The source and destination must be different folders and neither may be inside the other."),
                Pair("別の PhotoImporter がプリセットを使用しています。しばらく待ってから再試行してください。", "Another PhotoImporter instance is using the presets. Wait and try again."),
                Pair("別の PhotoImporter が入力履歴を使用しています。しばらく待ってから再試行してください。", "Another PhotoImporter instance is using the input history. Wait and try again."),
                Pair("設定ファイルの形式またはバージョンに対応していません。", "The settings file format or version is not supported."),
                Pair("設定ファイルを読み込めませんでした。", "The settings file could not be read."),
                Pair("設定ファイルの保存先を特定できません。", "The settings file location could not be determined."),
                Pair("入力履歴ファイルを読み込めませんでした。", "The input history file could not be read."),
                Pair("入力履歴ファイルの形式が正しくありません。", "The input history file format is invalid."),
                Pair("入力履歴ファイルのバージョンに対応していません。", "The input history file version is not supported."),
                Pair("入力履歴ファイルの保存先を特定できません。", "The input history file location could not be determined."),
                Pair("プリセットファイルを読み込めませんでした。", "The preset file could not be read."),
                Pair("プリセットファイルのバージョンに対応していません。", "The preset file version is not supported."),
                Pair("プリセットファイルの保存先を特定できません。", "The preset file location could not be determined."),
                Pair("プリセット名を入力してください。", "Enter a preset name."),
                Pair("プリセット名は100文字以内で入力してください。", "Enter a preset name of no more than 100 characters."),
                Pair("プリセット名に制御文字は使用できません。", "Control characters cannot be used in preset names."),
                Pair("同じ名前のプリセットが既にあります。", "A preset with the same name already exists."),
                Pair("対象のプリセットは別の PhotoImporter で削除されています。", "The preset was deleted by another PhotoImporter instance."),
                Pair("インポートファイルにはプリセットを1件だけ含めてください。", "The import file must contain exactly one preset."),
                Pair("コピー元フォルダーが見つかりません。", "The source folder was not found."),
                Pair("コピー先フォルダーが見つかりません。", "The destination folder was not found."),
                Pair("コピー先がスキャン時から変更されています。再スキャンしてください。", "The destination changed after scanning. Scan again."),
                Pair("が見つかりません。再スキャンしてください。", " was not found. Scan again."),
                Pair("がスキャン時から変更されています。再スキャンしてください。", " changed after scanning. Scan again."),
                Pair("コピーをキャンセルしました。", "Copying was cancelled."),
                Pair("コピー先フォルダーを特定できません。", "The destination folder could not be determined."),
                Pair("正式なファイル名を確定できませんでした。", "The final file name could not be committed."),
                Pair("確定後も一時ファイルが残っています。", "A temporary file remains after commit."),
                Pair("正式ファイルがない場合は、一時ファイルが唯一の残存データである可能性があります。削除や名前変更をせず保全してください。", "If the final file is missing, the temporary file may be the only remaining data. Preserve it without deleting or renaming it."),
                Pair("正式ファイルがコピー元のサイズと更新日時に一致する場合はコピー完了と判断できます。一時ファイルは内容を確認するまで自動削除しません。", "If the final file matches the source size and modified time, the copy can be considered complete. Do not delete the temporary file until its contents have been checked."),
                Pair("正式ファイルがスキャン時の旧状態に一致する場合は、元写真から再スキャンし、必要ならコピーをやり直してください。", "If the final file matches its old state at scan time, scan the original photo again and repeat the copy if necessary."),
                Pair("正式ファイルがあるものの状態を確認できない場合は、正式ファイルを元写真と比較してから再スキャンしてください。", "If the final file exists but its state cannot be verified, compare it with the original photo before scanning again."),
                Pair("破損または互換性のない Exif キャッシュを破棄して再生成しました。", "A damaged or incompatible Exif cache was discarded and regenerated."),
                Pair("Exif の読み取り中にファイルが変更されました。もう一度スキャンしてください。", "A file changed while its Exif data was being read. Scan again."),
                Pair("Exif の読み取り中にファイルの状態を再確認できませんでした。", "The file state could not be verified again while reading Exif data."),
                Pair("Exif キャッシュを保存できませんでした", "The Exif cache could not be saved"),
                Pair("次回は再解析します。", "It will be analyzed again next time."),
                Pair("ファイルが見つかりません。", "The file was not found."),
                Pair("プリセットは100件まで保存できます。", "Up to 100 presets can be saved."),
                Pair("インポートするプリセットのバージョンに対応していません。", "The imported preset version is not supported."),
                Pair("プリセットファイルを読み込めず、別の PhotoImporter が使用中のため退避できませんでした。", "The preset file could not be read or moved aside because another PhotoImporter instance is using it."),
                Pair("プリセットファイルのバージョンに対応していないため更新できません。", "The preset file cannot be updated because its version is not supported."),
                Pair("プリセットファイルのルート要素が正しくありません。", "The preset file root element is invalid."),
                Pair("プリセットファイルのバージョンが正しくありません。", "The preset file version is invalid."),
                Pair("プリセットの id が正しくありません。", "The preset ID is invalid."),
                Pair("プリセットの保存先を特定できません。", "The preset storage location could not be determined."),
                Pair("プリセット件数が100件を超えています。", "The number of presets exceeds 100."),
                Pair("同じ id のプリセットが複数あります。", "More than one preset has the same ID."),
                Pair("同じ名前のプリセットが複数あります。", "More than one preset has the same name."),
                Pair("プリセットの id が空です。", "The preset ID is empty."),
                Pair("対象ファイル列挙モードが正しくありません。", "The source file enumeration mode is invalid."),
                Pair("同じ id のプリセットが既にあります。", "A preset with the same ID already exists."),
                Pair("入力履歴の保存件数は0～100件で指定してください。", "The number of input-history entries to save must be between 0 and 100."),
                Pair("破損または互換性のないエントリを破棄して再生成しました。", "A damaged or incompatible entry was discarded and regenerated."),
                Pair("同じボリュームの Exif キャッシュを別の PhotoImporter が使用しています。キャッシュなしで続行します。", "Another PhotoImporter instance is using the Exif cache for this volume. Continuing without the cache."),
                Pair("指定された Exif キャッシュは見つかりません。", "The specified Exif cache was not found."),
                Pair("Exif キャッシュを利用できません", "The Exif cache is unavailable"),
                Pair("キャッシュなしで続行します。", "Continuing without the cache."),
                Pair("サイドカー拡張子を1件以上指定してください。", "Specify at least one sidecar extension."),
                Pair("サイドカー拡張子が正しくありません: ", "Invalid sidecar extension: "),
                Pair("画像・RAW・動画の拡張子はサイドカーに指定できません: ", "Image, RAW, and video extensions cannot be specified as sidecars: "),
                Pair("ボリュームルートを取得できませんでした。", "The volume root could not be retrieved."),
                Pair("ボリューム情報を取得できませんでした。", "The volume information could not be retrieved."),
                Pair("ボリューム容量を取得できませんでした。", "The volume capacity could not be retrieved."),
                Pair("同名サイドカーの関連先が曖昧なため、独立ファイルとして扱います: ", "A same-named sidecar has an ambiguous association and will be treated as an independent file: "),
                Pair("関連先画像をコピーできなかったため、サイドカーをコピーしませんでした。", "The sidecar was not copied because its associated image could not be copied."),
                Pair("コピー元全体を利用できなくなったため、コピーを中止しました: ", "Copying was stopped because the source is no longer available: "),
                Pair("コピー先全体を利用できなくなったため、コピーを中止しました: ", "Copying was stopped because the destination is no longer available: "),
                Pair("コピーに失敗し、一時ファイルを安全に削除できませんでした。", "Copying failed and the temporary file could not be deleted safely."),
                Pair("コピー元が見つかりません。再スキャンしてください。", "The source was not found. Scan again."),
                Pair("コピー元がスキャン時から変更されています。再スキャンしてください。", "The source changed after scanning. Scan again."),
                Pair("一時ファイルが見つかりません。再スキャンしてください。", "The temporary file was not found. Scan again."),
                Pair("一時ファイルがスキャン時から変更されています。再スキャンしてください。", "The temporary file changed after scanning. Scan again."),
                Pair("コピー先が見つかりません。再スキャンしてください。", "The destination was not found. Scan again."),
                Pair("不明なコピーエラー", "Unknown copy error"),
                Pair("Exif キャッシュを開けませんでした。", "The Exif cache could not be opened."),
                Pair("保存先の容量を取得できません: ", "Unable to determine the location size: "),
                Pair("容量を取得できません: ", "Unable to determine size: ")
            };

            var translated = message;
            foreach (var replacement in replacements)
                translated = translated.Replace(replacement.Key, replacement.Value);
            return translated;
        }

        private static KeyValuePair<string, string> Pair(string japanese, string english) =>
            new KeyValuePair<string, string>(japanese, english);
    }

    internal sealed class LanguageOption
    {
        public LanguageOption(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }

        public string Code { get; }
        public string DisplayName { get; }

        public override string ToString() => DisplayName;
    }
}
