# 一覧フィルター実装後 レビュー指摘事項まとめ

レビュー日: 2026-07-25
対象: main ブランチ (3b1afbf "Complete list filtering with UI and lazy Exif loading") の仕様 (DESIGN.md / FILTER_SPEC.md / TEMPLATE_SPEC.md) と実装
テスト実行結果: Release 構成で 223 件すべて成功 (`dotnet test PhotoImporter.sln -c Release`)

## このレビューの位置づけ

前回のレビュー ([REVIEW_FINDINGS.md](REVIEW_FINDINGS.md), 2026-07-19, 対象 29d497d, テスト 115 件) 以降、次の変更が入っている。

- `d1a64f1` コピー対象選択状態の管理
- `8bc5532` 一覧フィルター仕様 ([FILTER_SPEC.md](FILTER_SPEC.md)) の策定
- `e8d62ba` フィルター第1段階 (型付きモデル・評価器・コア単体テスト)
- `7d690fd` フィルター第2段階 (表示分離・選択・件数表示)
- `3b1afbf` フィルター第3段階 (WPF条件編集UI・遅延Exif読込連携)

本書は**一覧フィルター機能が3段階すべて実装された後の状態**を対象とし、新規追加された `PhotoImporter.Core.Filtering`、`PhotoImporter.App.FilterUiModels`、`PhotoImporter.App.PreviewItemCollectionState`、および `MainWindow` のフィルター連携部分を重点的に確認した。あわせて、アプリの信頼性の最終防衛線であるコピー処理 (コピー対象・コピー元・コピー先の一致、Windows API による安全なコピー) を、フィルター導入によって崩れていないかという観点で再検証している。

前回レビューの指摘は本書では再掲しない。REVIEW_FINDINGS.md の M-1 (読み取り専用ファイル)、M-3 (パス長検証)、および `app.manifest` / `App.config` の欠落は、`fdc950c` / `d497497` で解消済みであることを確認した。

## 総評

- **フィルター導入後もコピーの最終防衛線は崩れていない。** コピー対象は `Items` 全件のうち「チェック済み かつ コピー可能」のみ、宛先はスキャン時に確定した `CopyPlan` のまま、`CopyFile2` → `MoveFileExW` の経路と一時ファイルの安全条件にも変更はない。表示状態 (`ItemsView`) とチェック状態 (`Items`) を分離した設計は妥当で、表示フィルターがコピー対象の抽出ロジックへ混入していない。
- ただし**1件だけ、フィルターの「適用」がコピー対象を利用者の確認なしに増やし得る経路がある** (F-1)。フィルター機能で唯一、最終防衛線の要件に直接抵触する問題であり、最優先で対処すべき。
- 条件編集 UI には、項目を切り替えたときの状態リセット漏れに起因する問題が2件ある (F-2 / F-3)。いずれも「画面の表示と実際に適用される条件が食い違う」または「行が回復不能な無効状態になる」もので、フィルターの結果がコピー対象へ波及する以上、軽視できない。
- コア評価器 (`FilterEvaluator` / `FilterCandidate` / `FilterModels`) は FILTER_SPEC の記述との一致度が高く、AND/OR、対象/除外×Unknown の4組合せ、撮影日時の非フォールバック、Orientation 反映寸法、露光秒数の型付き比較はいずれも仕様どおりに実装されている。

---

## 重要度【高】: 最終防衛線に関わる問題

### F-1. フィルターの「適用」が裏で全再スキャンし、利用者が未確認のファイルを自動でチェックする

Exif 系条件を含むフィルターを適用すると、`ApplyFilter_Click` から `LoadExifForFilterAsync` を経て `BuildPreview` が丸ごと再実行され (`src/PhotoImporter.App/MainWindow.xaml.cs:773`)、`ReplacePreviewItems` で一覧が差し替えられる (`src/PhotoImporter.App/MainWindow.xaml.cs:819`)。

```csharp
foreach (var row in replacement)
{
    bool isSelected;
    if (selection != null && selection.TryGetValue(row.SourcePath, out isSelected))
        row.IsSelected = isSelected;   // ← 旧一覧に無い行はこの分岐に入らない
    row.PropertyChanged += PreviewItem_PropertyChanged;
    Items.Add(row);
}
```

旧一覧に存在しなかった `SourcePath` の行はこの分岐へ入らず、`PreviewItem` コンストラクタの既定値 `_isSelected = CanCopy` (`src/PhotoImporter.App/MainWindow.xaml.cs:1358`) のまま、つまり**チェック済み**で追加される。

- スキャン後にコピー元へファイルが増えていた場合 (カードへの追記、別アプリの書き込み、アクセス拒否だったフォルダーが読めるようになった場合など)、利用者は「適用」を押しただけでコピー対象が増える。
- 同時に宛先と `{Sequence}` も再割り当てされるため、利用者が確認済みのコピー先が変わり得る。

DESIGN.md §5 は自動チェックを「利用者が『スキャン』を押した手動スキャン」に限定し、コピー完了後の自動再スキャンでは相対パスをキーに選択を引き継ぐと定めている。FILTER_SPEC.md §5 も「Exifスキャンを開始し、既存の進捗表示とキャンセル操作を使用する」としか書いておらず、対象集合の変更やチェックの付与は許可していない。「コピー対象は利用者が確認する」「余計な動作をしない」という要件に直接抵触する。

→ 対策 (いずれか):
1. 遅延 Exif 読込の再構築では、旧一覧に無い行を `IsSelected = false` で追加し、「新しいファイルを N 件検出しました。再スキャンしてください」と通知する。
2. 一覧の差し替えではなく、既存 `PreviewItem` に対する Exif 情報の後追い充当に留める (対象集合と宛先を変えない)。

**付随する順序の問題**: `ReplacePreviewItems` は末尾で `RefreshItemsView()` を呼ぶが (`src/PhotoImporter.App/MainWindow.xaml.cs:833`)、この時点の `View.Filter` はまだ**直前の**適用済みフィルターである。その後 `ApplyPreviewFilter` で新条件を適用するため、新旧フィルターの非表示集合の和に対してチェック解除が走る。旧フィルターで非表示・新フィルターで表示になる行のチェックが不要に外れる。`ReplacePreviewItems` 内の `Refresh` を省くか、新条件の適用を先に行うべき。

---

## 重要度【中】: 修正を推奨する問題

### F-2. 条件の項目を切り替えると「Unknownを含める」の表示と実値がずれる

`FilterConditionEditor.SelectedField` のセッターは `_includeUnknown = false;` とバッキングフィールドへ直接代入するが (`src/PhotoImporter.App/FilterUiModels.cs:156`)、続く `NotifyAll()` は `IncludeUnknown` の `PropertyChanged` を発火しない (`src/PhotoImporter.App/FilterUiModels.cs:355`)。

結果、チェックボックスは ✓ のまま、実際の条件は「Unknownを除外」になる。項目を戻した場合も同様で、「Unknownを含める」が ✓ に見えているのに Unknown 項目が非表示になる。表示されているフィルターと適用されるフィルターの食い違いは、表示件数 →「絞り込みで非表示になった項目のチェックを外す」→ コピー対象、という経路でコピー結果まで伝播する。

再現をユニットテストで確認済み (項目切替後に `IncludeUnknown == false` になる一方、`PropertyChanged` は発火しない)。

→ 対策: `IncludeUnknown = false;` とプロパティ経由で代入するか、`NotifyAll()` へ `OnPropertyChanged(nameof(IncludeUnknown))` を追加する。

### F-3. Sequence / Rating 専用オプションが項目切替後も残り、条件行が回復不能な無効状態になる

`SelectedField` 切替時に `_includeNoSequence` / `_includeRejectedRating` はリセットされない (`src/PhotoImporter.App/FilterUiModels.cs:148-159`)。

`{Sequence}` を選んで「連番なし」を ON にした後、項目を `{Iso}` などへ変更すると、`NumberFilterCondition.TryPrepare` が `OptionNotSupported` を返して行が無効になる (`src/PhotoImporter.Core/Filtering/FilterEvaluator.cs:328`)。ところが該当チェックボックスは `IsSequence` / `IsRating` が真のときしか表示されないため (`src/PhotoImporter.App/MainWindow.xaml:293-308`)、**画面から解除する手段がない**。`CanApplyFilter` が false のままとなり、その行を削除しない限りフィルター全体を適用できなくなる。`{Rating}` の「Rejected」も同様。

さらに `TranslateValidation` に `OptionNotSupported` の分岐がないため (`src/PhotoImporter.App/FilterUiModels.cs:341`)、既定の「この条件を適用できません。」しか表示されず原因が分からない。FILTER_SPEC.md §6「無効な条件がある間は『適用』を無効にし、行内に理由を表示する」を満たしていない。

再現をユニットテストで確認済み。

→ 対策: `SelectedField` セッターで両フラグをリセットし通知する。あわせて `OptionNotSupported` に専用のメッセージを用意する。

### F-4. `{Extension}` に「大文字・小文字を区別」が表示されるが、ON にすると必ず不一致になる

FILTER_SPEC.md §2.1 は拡張子を「先頭の `.` を含む小文字へ正規化し、大文字・小文字を区別せず照合する」と規定し、実装も `PhotoFileClassifier.NormalizeExtension` で小文字化している (`src/PhotoImporter.Core/Filtering/FilterCandidate.cs:69`)。

一方 UI は `{Extension}` を String 型として扱うため区別チェックボックスが表示され (`src/PhotoImporter.App/MainWindow.xaml:264`)、ON にして `.ARW` と入力すると `StringComparison.Ordinal` 比較になり**絶対に一致しない**。利用者は「該当0件」の理由に気付けない。

→ 対策: `{Extension}` では大文字・小文字を区別のチェックボックスを非表示または無効にする。

### F-5. スキャンエラー行が `{Extension}` / `{Protected}` 条件で必ず一覧から消える

`{Extension}` と `{Protected}` は `CanBeUnknown = false` として定義されているが (`src/PhotoImporter.Core/Filtering/FilterModels.cs:94,102`)、`FilterCandidate.GetValue` はスキャンエラー行に対して Unknown を返す (`src/PhotoImporter.Core/Filtering/FilterCandidate.cs:63-87`)。`IncludeUnknown` を指定できないため、これらの条件を1つでも使うとスキャンエラー行は無条件に非表示となり、利用者は取り込めなかったファイルの存在に気付けない。

FILTER_SPEC.md §4.1 は「値が必ず存在する拡張子、`{Protected}`、`{HasGps}` などではUnknown指定を無効表示する。**ただしスキャンエラーによりファイル情報自体を取得できない行はUnknownになり得る**」と明記しており、後半が実装へ反映されていない。

→ 対策: これらの項目でも `IncludeUnknown` を許可する (スキャンエラー行にのみ意味を持つ) か、スキャンエラー行はファイルシステム系条件の対象外として常に表示する。仕様側の意図を確定させたうえで揃える。

---

## 重要度【低】: 実装上の改善推奨

- **F-6. スキャンエラー行の Exif 読込状態が「読取エラー」として分類される。** `PreviewItem.CreateFilterCandidate` はスキャンエラー行へ `ScanErrorMetadataResult` (= `ReadError`) を渡す (`src/PhotoImporter.App/MainWindow.xaml.cs:1333,1433`)。ファイル情報自体を読めなかった行が Exif 読取失敗と同一視され、「Exif読込状態 = 読取エラー」で絞ると両者が混ざる。また `FilterExifReadStatus.Unread` は `RebuildChoices` で選択肢に出さず、`GetValue` も `ExifUnread` 状態を返すため、到達不能な死んだ列挙値になっている (`src/PhotoImporter.Core/Filtering/FilterModels.cs:154`)。
- **F-7. フィルター評価例外が `Copy_Click` から未捕捉で外へ出る。** `src/PhotoImporter.App/MainWindow.xaml.cs:399` の `_itemCollectionState.Refresh()` は `try/finally` 内だが `catch` がなく、`View.Refresh()` が実行する述語から `FilterEvaluationException` (正規表現タイムアウト) が出ると `async void` ハンドラを抜けて未処理例外になる。`ScanAsync` 側の同じ呼び出しは `catch (Exception)` で保護されているので揃えるべき。発生確率は低いが、コピー直後という最悪のタイミングで落ちる。REVIEW_FINDINGS.md の M-6 (グローバル例外ハンドラ不在) が未対応のため、影響が無言のクラッシュになる点も変わっていない。
- **F-8. 入力のたびに `Regex` を再構築している。** `IsValid` と `ValidationMessage` がどちらも `TryBuild()` → `FilterSet.Prepare()` を呼ぶため (`src/PhotoImporter.App/FilterUiModels.cs:195-196`)、1文字入力するごとに条件行あたり2回 `new Regex(...)` が走る。FILTER_SPEC.md §8「正規表現などの入力は『適用』時に一度検証・準備し、項目ごとに再構築しない」の方針からも外れている。検証結果をキャッシュすれば解消する。
- **F-9. `LoadExifForFilterAsync` の `_appliedFilter` が null チェックなしでクロージャに取り込まれている。** `src/PhotoImporter.App/MainWindow.xaml.cs:790`。`RequiresExif` が真なら条件数は1以上なので現状は到達しないが、`TryCommitFilter` 側 (`src/PhotoImporter.App/MainWindow.xaml.cs:731`) と同じ null ガードを入れておくのが安全。
- **F-10. `PreviewItemCollectionState.Refresh` はフィルター未適用時にも非表示チェック解除を実行する。** `src/PhotoImporter.App/PreviewItemCollectionState.cs:37`。`HasActiveFilter` が偽なら全件が表示中なので実害はないが、全件の `HashSet` 構築が毎回走る。`HasActiveFilter` で早期リターンするのが素直。

---

## 仕様と実装の齟齬 (方針決定を推奨)

- **FD-1. 「対象から外す」と特殊値の組合せが未定義。** `IncludeMatches = false` のとき、`{Rating}` の `Rejected` と `{Sequence}` の「連番なし」の判定結果も反転する (`src/PhotoImporter.Core/Filtering/FilterEvaluator.cs:351,355`)。「評価1〜5を対象から外す、Rejected は含めない」と指定すると Rejected が**含まれる**。FILTER_SPEC.md §4.2 は Unknown についてしか表を持たないため、特殊値についても同等の表を追加し、テストで固定すべき。
- **FD-2. `{HasGps}` の Unknown の扱いが仕様内で矛盾している。** FILTER_SPEC.md §2.3 は「`GPS` / `NoGPS` の2値とし、Unknownを持たない」とする一方、§4.1 は `Unsupported` / `NoMetadata` を Unknown になり得る場合として挙げている。実装は §2.3 を採用しており、Exif 非対応形式やメタデータなしのファイルも `NoGPS` として一致する (`src/PhotoImporter.Core/Filtering/FilterCandidate.cs:128`)。仕様の記述を一本化すべき。
- **FD-3. フィルター条件と「非表示項目のチェック解除」設定は永続化されない。** FILTER_SPEC.md §6 / §7.1 が「v1では永続化しない」「アプリ再起動時はONへ戻す」と定めており実装は仕様どおりだが、DESIGN.md §6 のステップ9で「一覧フィルター条件の永続化」が未実装として残っている。v2 の課題として整理されている旨を明記しておくと混乱がない。

---

## テスト不足

- **FT-1. 条件編集 UI の項目切替に関するテストがない。** `tests/PhotoImporter.Core.Tests/FilterUiModelTests.cs` は各項目の単独ケースのみで、`SelectedField` を切り替えた後の状態を検証していない。F-2 / F-3 はこの穴から漏れている。追加推奨:
  - 項目切替後に `IncludeUnknown` / `IncludeNoSequence` / `IncludeRejectedRating` がリセットされ、対応する `PropertyChanged` が発火すること
  - `{Sequence}` → 他の数値項目へ切り替えた行が有効なままであること
- **FT-2. フィルター適用時の遅延 Exif 読込のテストがない。** `LoadExifForFilterAsync` の経路 (対象集合の維持、チェック状態の引き継ぎ、キャンセル時の直前状態維持) は UI 層にあり自動テストが無い。F-1 はここに該当する。最低限、選択状態の引き継ぎ規則を `PreviewSelectionState` 相当の純粋関数へ切り出してテスト可能にすることを推奨。
- **FT-3. スキャンエラー行 × ファイルシステム系条件の組合せが未検証。** F-5 の挙動 (スキャンエラー行が消える) を意図として固定するか、修正して固定するかを決めたうえでテストを追加する。

---

## 問題なしを確認した点 (抜粋)

### コピーの最終防衛線 (フィルター導入後の再検証)

- **コピー対象の抽出**: `PreviewItemCollectionState.CopyTargets` は `Items.Where(CanCopy && IsSelected)` で、表示状態 (`ItemsView`) を参照しない (`src/PhotoImporter.App/PreviewItemCollectionState.cs:25`)。FILTER_SPEC.md §7.3 の「コピーは表示状態ではなく全スキャン結果を対象とする」と一致し、チェック維持モードでの表示外項目も正しくコピー対象に入る。
- **コピー対象件数の可視化**: `ViewSelectionSummary` が「表示 / 全 / チェック / 表示外チェック」を常時表示し、コピーボタンにも総件数を表示する (`src/PhotoImporter.App/MainWindow.xaml.cs:199-212`)。表示外のチェック済み項目が隠れたままコピーされる状況にはならない。
- **チェック不可行の保護**: `PreviewItem.IsSelected` セッターが `CanCopy && value` で丸めるため、取込済・競合・スキャンエラー・コピーエラー行はフィルター操作や全選択を経由してもチェックできない (`src/PhotoImporter.App/MainWindow.xaml.cs:1379`)。
- **計画の固定**: コピー時は `item.CopyPlan` をそのまま渡し、テンプレート再評価も宛先再割り当ても行わない (`src/PhotoImporter.App/MainWindow.xaml.cs:363`)。`CopyEngine.ExecuteOne` はコピー元・コピー先をスキャン時スナップショットと再検証し、変化していればコピーせずエラー化する (`src/PhotoImporter.Core/Copying/CopyEngine.cs:101-103`)。
- **`CopyFile2` P/Invoke の正確さ**: `COPYFILE2_EXTENDED_PARAMETERS` のフィールド順とサイズ、`COPYFILE2_MESSAGE` の `Info` オフセット (+8)、`ChunkFinished` / `StreamFinished` のメンバ配置は x64 のネイティブ定義と一致。`COPY_FILE_FAIL_IF_EXISTS` により一時ファイルが既存ファイルを上書きしない。
- **フラッシュとキャンセル**: `STREAM_FINISHED` で `FlushFileBuffers` を呼び、失敗時はコンテキストへ Win32 エラーを保存して `CANCEL` を返す。マネージ例外をネイティブ境界の外へ出していない (`src/PhotoImporter.Core/Copying/CopyFile2Native.cs:88-105`)。
- **アンマネージメモリの寿命**: キャンセル用 `Marshal.AllocHGlobal` は `CancellationTokenRegistration` の `Dispose` (`using` 終了) の**後**に `FreeHGlobal` される順序になっており、解放済みメモリへの書き込みは起きない。`GC.KeepAlive(callback)` によるデリゲートの寿命保護も適切。
- **確定と事後検証**: `MoveFileExW` は `MOVEFILE_WRITE_THROUGH` (上書き時のみ `MOVEFILE_REPLACE_EXISTING`)、確定後に一時ファイルの不在と宛先スナップショットを再検証する。失敗時は旧状態の再確認と ReadOnly 復元を経て、判定できない場合は一時ファイルを保全して復旧エラーとする。
- **一時ファイル削除の多層防御**: コピー先ルート配下・期待フォルダー一致・`^PI_[0-9A-Fa-f]{32}\.partial$` 厳密一致・非ディレクトリ・非リパースポイントの全条件を満たす場合のみ削除 (`src/PhotoImporter.Core/Copying/CopyEngine.cs:293-345`)。利用者のファイルを削除する経路はない。
- **長いパスへの対応**: `app.manifest` の `longPathAware` と `App.config` の `UseLegacyPathHandling=false` / `BlockLongPaths=false` が設定済み (前回レビュー M-3 の対応を確認)。

### フィルター機能

- **表示とチェックの分離**: `Items` (全スキャン結果) と `ICollectionView` の `ItemsView` を分離し、コピー・チェック総数・全体状態集計は `Items`、全選択と表示件数は `ItemsView` を使う構成が FILTER_SPEC.md §8 と一致している。
- **全選択の3状態**: `GetSelectAllState` が「表示中のコピー可能項目のみ」を対象に false / null / true を返し、クリック時は全選択なら全解除・それ以外は全選択となる (`src/PhotoImporter.App/MainWindow.xaml.cs:426`, `PreviewSelectionState.GetSelectAllState`)。表示外のチェック状態は変更されない。
- **非表示チェックの解除/維持**: 既定 ON、OFF→ON 切替時にも現在の表示外項目を解除、フィルター解除時にチェックを復元しない、という §7.1 の全項目を満たす (`src/PhotoImporter.App/PreviewItemCollectionState.cs:37-55`)。
- **対象/除外 × Unknown の4組合せ**: `PreparedFilterCondition.Matches` が「Unknown なら `IncludeUnknown` を返し、既知値なら `IncludeMatches` で反転」という構造になっており、§4.2 の表と厳密に一致する (`src/PhotoImporter.Core/Filtering/FilterEvaluator.cs:477-485`)。
- **Unknown の定義**: 文字列リテラル `"Unknown"` ではなく `FilterValueState` による状態表現。実データの `"Unknown"` は既知の文字列として扱われる。
- **撮影日時の非フォールバック**: `FilterCandidate` は `metadata.TakenDate` が無い場合に更新日時へフォールバックせず Unknown を返す (`src/PhotoImporter.Core/Filtering/FilterCandidate.cs:163`)。テンプレート評価側のフォールバックと分離されており、§2.3 の意図どおり「Exif 撮影日時が存在しないファイル」を抽出できる。
- **型付き比較**: `{Width}` / `{Height}` は `PhotoMetadataValues.GetOrientedDimensions` を共有して Orientation 反映後の寸法を比較、`{ShutterSpeed}` / `{ExposureTime}` はともに露光秒数へ変換して比較。書式化文字列や単位は比較値に含まれない。
- **正規表現の扱い**: `RegexOptions.Compiled` を使わず、既定 250ms のタイムアウトを設定し、`RegexMatchTimeoutException` を `FilterEvaluationException` へ変換。`TryCommitFilter` は適用前に全件を試行し、例外が出た場合は条件を適用しない (`src/PhotoImporter.App/MainWindow.xaml.cs:724-744`)。§3.2 の要求を満たす。
- **日時境界**: 日付のみ指定の終了日は翌日 00:00:00 未満 (排他) として評価され、終了日全体を含む。時刻を指定した場合は境界値を含む包含比較になる (`src/PhotoImporter.App/FilterUiModels.cs:236-241`)。
- **ファイルサイズ単位**: B / KiB / MiB / GiB を 1024 進で解釈し、オーバーフローと端数バイトを拒否する (`src/PhotoImporter.Core/Filtering/FileSizeFilterParser`)。
- **Exif 読込要求**: `FilterSet.RequiresExif` が Exif 系条件の有無から算出され、テンプレートが Exif 不要かつ「未使用でも読み込む」が OFF でも遅延読込が開始される。キャンセル・全体エラー時は既存一覧と直前の適用済み条件を維持し、作成途中の結果へ切り替えない (`src/PhotoImporter.App/MainWindow.xaml.cs:797-808`)。§5 の要求どおり。
- **詳細選択の解除**: フィルター適用で選択中の行が非表示になった場合、詳細選択を解除し、自動的に別の行を選択しない (`src/PhotoImporter.App/MainWindow.xaml.cs:654-656`)。§7.3 と一致。
- **編集中条件と適用済み条件の分離**: 文字入力ごとの自動適用は行わず、「適用」を押した時点でのみ表示と選択状態が変わる。無効な条件がある間は「適用」が無効になる (`CanApplyFilter`)。
