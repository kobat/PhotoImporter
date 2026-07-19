# レビュー指摘事項まとめ

レビュー日: 2026-07-19
対象: main ブランチ (29d497d) の仕様 (DESIGN.md / TEMPLATE_SPEC.md / README.md) と実装の全体
テスト実行結果: Core 単体テスト 115 件すべて成功

## 総評

- コピーの最終防衛線(利用者が確認した通りのコピー、`CopyFile2` による安全なコピー、スキャン後に宛先へ出現したファイルの検出と中止)は、いずれも設計・実装とも堅実で致命的な欠陥はない。
- コピーパイプライン(計画固定 → 元/先の再検証 → `CopyFile2`+`FAIL_IF_EXISTS`+フラッシュ → 一時ファイル検証 → `MoveFileExW` 確定 → 事後検証 → 制約付き削除/保全)は仕様と実装が高い精度で一致している。
- 修正優先度が高いのは、下記の【中】に分類した項目。いずれも「安全側に失敗する」ためデータ破壊はないが、実運用(プロテクト写真、FAT/exFAT の外付けドライブ、別カードの同名ファイル)で確実に踏む。

---

## 重要度【中】: 修正を推奨する問題

### M-1. 読み取り専用(プロテクト)ファイル絡みの2つの穴

`CopyFile2` はファイル属性を保持するため、DCF プロテクト(読み取り専用属性)が一時ファイルと宛先に伝播するが、その帰結が扱われていない。

1. **`.partial` が読み取り専用になり、失敗時の削除が静かに失敗する。**
   プロテクトされた元ファイルのコピー完了後にキャンセルやリネーム失敗が起きると、`TryDeleteSafePartial` の `File.Delete` が `UnauthorizedAccessException` を投げて握りつぶされ(`src/PhotoImporter.Core/Copying/CopyEngine.cs:186`)、通知のないまま `.partial` が残留する。
   → 対策: 安全条件を満たした自アプリ作成ファイルに限り、削除前に ReadOnly 属性を解除する。
2. **読み取り専用の宛先は上書きできない。**
   過去に取り込んだプロテクト写真が上書き対象になると、`MoveFileExW` + `MOVEFILE_REPLACE_EXISTING` が `ERROR_ACCESS_DENIED` で必ず失敗する。データは失われないが仕様に挙動の定義がない。
   → 対策: 「上書き時は宛先の読み取り専用を明示的に解除する」か「プロテクト済み宛先は上書き不可と表示する」かを仕様として決める。

### M-2. FAT32/exFAT のコピー先ではコピーが恒常的に失敗する

スナップショット照合が `LastWriteTimeUtc` の Ticks 単位完全一致を要求している(`src/PhotoImporter.Core/Copying/CopyEngine.cs:163`)。宛先が FAT32(2秒粒度)/exFAT(10ms粒度)だと書き込まれたタイムスタンプが切り捨てられ、

- 一時ファイル検証が毎回失敗し、全ファイルがコピー不能になる
- 未取込判定(`source <= destination`)でも宛先側が過去になり、取り込み済みファイルが永遠に「上書き対象/競合」と再表示される

DESIGN.md §4 はコピー元が FAT の場合しか論じていない。
→ 対策: robocopy と同様の2秒許容窓を入れるか、「コピー先は NTFS のみ対応」を仕様・UI に明記する。

### M-3. 宛先パス長バリデーションが配線されていない

`TemplateEvaluator.EvaluateDetailed` には `maximumFullPathLength`/`destinationRoot` による `PathTooLong` 検証があるが、実フローの `DestinationAllocator`(`src/PhotoImporter.Core/Templates/DestinationAllocator.cs:86` 以降)はこれを渡しておらず、死んだコードになっている。さらに DESIGN.md §2 が求める app.config(`UseLegacyPathHandling`)・app.manifest(longPathAware、Per-Monitor V2 DPI)がプロジェクトに存在しない。現状は 260 文字超の宛先が偶然「スキャンエラー行」として安全に落ちるだけで、仕様が意図した事前検証になっていない。
→ 対策: `Allocate` から `EvaluateDetailed` へ `destinationRoot` を渡す。app.manifest / app.config を追加する。

### M-4. 取込済判定がファイルサイズを完全に無視している

`src/PhotoImporter.Core/Templates/DestinationAllocator.cs:119` は「宛先に存在し、コピー元の更新日時が同じか古ければ取込済」だけで判定する(仕様通り)。しかし別カードの同名ファイル(例: 2枚のカードがどちらも `DCIM\100MSDCF\DSC00001.JPG` を持つ)では、2枚目のファイルが1枚目のコピーより古ければ「取込済」と誤判定され、その写真は永遠に取り込まれない。`{Sequence}` があっても「取込済」判定が先に成立するため連番退避は起きない。
→ 対策: 宛先スナップショットにサイズが既にあるので、「更新日時が同じか古い かつ サイズ不一致」を競合として警告する。仕様(DESIGN.md §4)への追記を含めて検討。

### M-5. テンプレートの静的な誤りが「全ファイルのスキャンエラー」として表面化する

パーサは数値書式だけをサンプル値で事前検証する(`src/PhotoImporter.Core/Templates/TemplateParser.cs:210`)が、日付書式は検証しない。`{ModifiedDate:HH:mm}`(結果にコロン)や先頭・末尾 `\` のテンプレートはパース成功後、評価時にファイル1件ごとに `TemplateException` → 全行スキャンエラーになる。TEMPLATE_SPEC §2.3 はこれらを「無効(=入力エラー)」と定義している。
→ 対策: パース時にサンプル日時で日付書式を検証(数値と同じ方式)し、先頭・末尾区切りは構文検証して、1回のテンプレートエラーとして表示する。

### M-6. アプリにグローバル例外ハンドラがない

`src/PhotoImporter.App/App.xaml.cs` は空で、`DispatcherUnhandledException` / `TaskScheduler.UnobservedTaskException` の処理がない。`Scan_Click`/`Copy_Click` は `async void` のため、内部の catch から漏れた例外は無言のクラッシュになり、設定保存(Closing)も走らず、コピー中なら結果表示も失われる。
→ 対策: 最低限のハンドラ+エラーダイアログを追加する。

### M-7. 取込済行のチェックボックスが「見た目だけ ON」になる表示不整合

対象列の要件のうち「チェックしたものだけコピー」(`src/PhotoImporter.App/MainWindow.xaml.cs:288` の二重フィルタ)と「取込済はコピーされない」(セッターの強制 false 丸め)は守られている。しかし `IsSelected` セッター(`src/PhotoImporter.App/MainWindow.xaml.cs:1058`)は拒否時に `PropertyChanged` を発火しないため、取込済行のチェックボックスをクリックすると見た目だけ ON のまま残る(モデルは false、コピーはされない)。行仮想化で画面外→内に戻ると表示が戻るため、挙動が不安定に見える。
→ 対策(両方推奨):
1. `DataGridCheckBoxColumn` に `ElementStyle` を追加し、`IsEnabled` を `CanCopy` にバインドして取込済・競合・スキャンエラー行をグレーアウトする(根本対応)。
2. セッターで拒否した場合(`value != next`)も `PropertyChanged(nameof(IsSelected))` を発火して表示を戻す(防御の整合性)。

---

## 重要度【低】: 実装上の改善推奨

- **L-1. ペア JPEG がスキャン中に消えると 1601 年へフォールバックする。** `src/PhotoImporter.App/MainWindow.xaml.cs:458` の `new FileInfo(analysisSource)` は存在しないファイルで例外を出さず `LastWriteTime` が 1601-01-01 を返す。`Exists` チェックを1つ足せば防げる。
- **L-2. `VerifyDirectoryWritable` が確認ダイアログの前にフォルダを作る。** `src/PhotoImporter.App/MainWindow.xaml.cs:580` は書込み検証(`Directory.CreateDirectory` を含む)→確認の順のため、キャンセルしても空フォルダが残る。順序を入れ替えるか片付ける。
- **L-3. `ExifCacheKeyPlan` は製品コードから未使用。** `src/PhotoImporter.Core/Metadata/ExifCacheKey.cs:105` はテストからしか参照されていない。将来の並列化用なら意図をコメントに、そうでなければ削除。
- **L-4. 旧 entries.json の移行パスは事実上デッドコード。** `LoadLegacyJson`(`src/PhotoImporter.Core/Metadata/ExifCacheStore.cs:318`)は `ExtractionVersion == 2` を要求するが、旧 JSON 時代の抽出仕様は 1 のため、実在する旧 JSON は全て「破損・互換性なし」警告つきで破棄される。移行コードを削除して「旧 JSON は破棄」に仕様を単純化するのが正直。
- **L-5. Mutex 名が名前空間プレフィックスなし(セッションローカル)。** `src/PhotoImporter.Core/Metadata/ExifCacheStore.cs:115`。同一ユーザーセッション内の排他は正しいが、別ユーザーセッションからは排他されない。仕様上の割り切りとして明記推奨。
- **L-6. 設定保存はウィンドウを閉じたときのみ。** クラッシュ時はキャッシュ保存先変更履歴を含む設定変更が失われる。保存先変更の確定時だけでも即時保存する価値がある。
- **L-7. `STREAM_FINISHED` のハンドル無検査。** `FlushFileBuffers(stream.DestinationFile)` の前に `IntPtr.Zero`/`INVALID_HANDLE_VALUE` ガードを足すと堅くなる(防御的強化)。
- **L-8. 進捗バイト数が失敗ファイル分を加算しない。** エラーがあると進捗バーが 100% に届かない(表示のみの問題)。
- **L-9. コピー後の自動再スキャンは、キャッシュ OFF 時に全ファイルの Exif を再読取する。** 実害はないが大量ファイルで遅い。
- **L-10. 細かいもの。**
  - README.md の「96件の Core 単体テスト」は 115 件に更新が必要
  - `MainWindow._templateText` の初期値が `PhotoImporterSettings.DefaultTemplate` の複製(定数参照にすべき)
  - オフセットが「不正」(`TakenDateOffsetState.Invalid`)の場合も警告表示が「Exif時差なし」になり、欠落と区別できない

---

## 仕様と実装の齟齬(ドキュメント修正または方針決定を推奨)

- **D-1. 既定テンプレートの三者不一致。** TEMPLATE_SPEC.md §1 と DESIGN.md §3 は `{TakenDate:yyyy-MM-dd}\{OriginalName}`、実装(`src/PhotoImporter.Core/Settings/PhotoImporterSettings.cs:13`)は `{ModifiedDate:yyyy-MM-dd}\{FileName}{Sequence}{Extension}`。実装値の方が実用的(Exif 不要+連番付き)なので、仕様側を直すのが良い。
- **D-2. 拡張子フィルタ未実装。** DESIGN.md §2 は「対応拡張子によるファイル種別フィルタ」を規定するが、スキャンは全ファイルを列挙・コピー対象にする(`Thumbs.db` や `desktop.ini` も取り込まれる)。また実カードでは `System Volume Information` が毎回アクセス拒否のスキャンエラー行として出てノイズになる。
- **D-3. フォルダ選択が仕様と異なる旧式ダイアログ。** DESIGN.md §2 は `IFileOpenDialog` の COM ラッパーを明記するが、実装(`src/PhotoImporter.App/MainWindow.xaml.cs:706`)は WinForms の `FolderBrowserDialog`(.NET Framework では旧ツリー型)。
- **D-4. 3文字タイムゾーンコードは DST 込みで変換される。** `PST`→"Pacific Standard Time" 等のマッピング(`src/PhotoImporter.Core/Templates/TemplateTimeZone.cs:10`)は夏時間中は実質 PDT へ変換される。`CST` は米国中部であり中国標準時ではない。TEMPLATE_SPEC に DST の扱いを明記すべき。
- **D-5. 「カード全体が読めなくなったらスキャン中止」(DESIGN.md §2)は未実装。** フォルダ単位のエラー継続のみ。同様に「コピー先全体が利用不能なら中止」も未実装で、コピー中にドライブが抜けると残り全件が個別エラーになる。
- **D-6. 連番枯渇は仕様上「競合」だが、実装ではスキャンエラー(`SequenceExhausted`)表示になる。** 実害なし。
- **D-7. 上書きモードの残余 TOCTOU。** 最終検証→`MoveFileExW` 間にミリ秒級の窓が残る。OS 上原子的に閉じる手段がないため許容で妥当だが、設計文書に既知の残余リスクとして明記推奨。非上書きモードは `MOVEFILE_REPLACE_EXISTING` を付けないため OS レベルで原子的に守られている。
- **D-8. 「取込済はチェック不可」がどの仕様書にも明文化されていない。** DESIGN.md §5 の画面イメージが「取込済=☐」を示すだけ。「対象チェックは未取込・上書き対象の行のみ操作可能。取込済・競合・スキャンエラー行は常にチェック不可」と明記すべき(現実装と一致)。
- **D-9. 再スキャンでチェック状態がリセットされる点も仕様化を推奨。** コピー完了後の自動再スキャンで行が作り直され、コピー可能な行はすべて再びチェック ON に戻る。「一部だけ外してコピー→続けてもう一度コピー」で、意図的に外したファイルが自動で対象に戻り、気づかずコピーされ得る。最低限「再スキャン後は選択が既定状態に戻る」ことの明記、可能なら再スキャン時に(コピー元相対パスをキーに)チェック状態を引き継ぐ改善を検討。
- **D-10. トークン名の正規化未実装。** TEMPLATE_SPEC §2.1「保存時と UI の候補表示では正式表記へ正規化する」(大文字小文字)は未実装。
- **D-11. 起動時の残存 `.partial` 検出・復旧案内 UI は未実装。** DESIGN.md §7 の次アクションに記載済み(README にも制限として記載あり)。

---

## テスト不足

- **T-1. コピーエンジンの最終防衛線のテストがない。** `tests/PhotoImporter.Core.Tests/CopyEngineTests.cs` は4件のみで、以下が未検証:
  - スキャン後に宛先へファイルが出現(非上書き)→ 中止すること
  - スキャン後に宛先が変更(上書きモード)→ 中止すること
  - キャンセル時に `.partial` が削除され宛先が汚れないこと
  - 読み取り専用のソース/宛先(M-1)
  - `MoveFileExW` 失敗時の `CopyRecoveryException` / 一時ファイル保全
- **T-2. `PreviewItem` の選択制御のテストがない。** 追加推奨:
  - `CanCopy == false`(取込済・競合・スキャンエラー)の行に `IsSelected = true` を代入しても false のままであること
  - `SetCopyError` 後にチェックが外れ、以後チェック不可になること
  - コピー計画のある行の初期選択が ON、それ以外は OFF であること

---

## 問題なしを確認した点(抜粋)

- **コピーの最終防衛線3観点**: 計画固定(コピー時の再評価・再割り当てなし)、`CopyFile2` P/Invoke の構造体レイアウト・メッセージ種別・フラッシュエラー伝搬・キャンセル・ハンドル寿命の正確さ、宛先出現時の中止(検証2回+非上書き時は `MoveFileExW` が OS レベルで原子的に失敗)。
- **一時ファイル削除の多層防御**: 宛先ルート配下・期待フォルダ一致・`^PI_[0-9A-Fa-f]{32}\.partial$` 厳密一致・非ディレクトリ・非リパースポイントの全条件。
- **パス脱出への二重防御**: `TemplateEvaluator.ValidateRelativePath`(絶対パス・`..`・`.`・空要素・不正文字・予約デバイス名の拒否)+ `CopyPlanItem` コンストラクタのルート配下検証。
- **TSV の引用・エスケープ**: タブ/CR/LF/`"` の引用、フィールド先頭のみ引用開始、閉じ引用後の文字検査、破損時のファイル全体無効→再生成。
- **キャッシュキー**: 4要素完全一致、`ComparisonPath` の非永続化と再生成、追記型保持。
- **Exif 読取中のファイル変更検知**(読取前後スナップショット比較→キャッシュ非保存+エラー)と、キャンセル時の「現在ファイル完了→保存→Mutex 解放」の順序。
- **RAW+JPEG ペア判定**: 同ディレクトリ・同名・JPEG 1件+RAW 1件のみペア、曖昧時は個別解析、`{ModifiedDate}` 等は自ファイル値、Exif フォールバックは解析元の更新日時。
- **メタデータのサニタイズ**: 不正文字置換・末尾ドット/空白除去・予約デバイス名回避により Exif 値経由のパス注入は不可能。GPS の無効な組の正規化、露光時間の既約分数化も仕様通り。
- **設定・キャッシュの原子的保存**、キャッシュルートとコピー元/先の重なり検出、Mutex タイムアウト時のキャッシュなし続行。
- **対象列のコピー対象抽出**: `IsSelected && CanCopy` の二重フィルタとボタン有効条件、コピー中の一覧無効化。
