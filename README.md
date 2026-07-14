# Photo Importer

Windows 11向けの写真取り込みアプリです。SDカードやCFexpressカードなどのフォルダーを再帰的にスキャンし、テンプレートからコピー先をプレビューして、未取り込みのファイルをコピーします。

ファイルシステム情報とExif情報を使うテンプレートに対応しています。Exifキャッシュは通常スキャンへ接続済みで、利用ON/OFFと保存先を設定できます。カード管理画面、多言語化、状態フィルター、単一exe配布は未実装です。

## 必要な環境

- Windows 11
- .NET Framework 4.8.1
- ビルドおよび開発には、対応する .NET SDK と .NET Framework 4.8.1 Developer Pack

## 起動

リポジトリのルートで、実行するWPFプロジェクトを明示します。

```powershell
dotnet run --project .\src\PhotoImporter.App\PhotoImporter.App.csproj
```

ルートで引数なしの `dotnet run` を実行しても、ルート直下には `.csproj` がないため起動できません。

ビルド済みの場合は、次の実行ファイルを直接起動することもできます。

```text
src\PhotoImporter.App\bin\Debug\net481\PhotoImporter.exe
```

## ビルドとテスト

```powershell
dotnet build .\PhotoImporter.sln
dotnet test .\PhotoImporter.sln
```

2026-07-15時点では、96件のCore単体テストがすべて成功しています。

## 基本的な使い方

1. コピー元フォルダーを選択します。
2. コピー先フォルダーを選択します。
3. テンプレートと「既存ファイルを上書きする」設定を指定します。
4. 「スキャン」を押し、表示されたコピー先と状態を確認します。Exif情報を読み取るスキャンは「キャンセル」で途中停止できます。
5. コピー対象のチェックを調整して「コピー」を押します。
6. コピー後は自動的に再スキャンされ、状態が更新されます。

コピー元、コピー先、テンプレート、上書き設定、RAW+JPEG解析設定、Exifキャッシュ設定は `%LocalAppData%\PhotoImporter\settings.xml` に保存され、次回起動時に復元されます。

コピー元とコピー先に、同じフォルダーまたは互いの配下にあるフォルダーは指定できません。コピー元ファイルは変更・削除しません。

## テンプレート例

```text
{ModifiedDate:yyyy-MM-dd}\{FileName}{Sequence}{Extension}
```

ファイルシステム系の主なトークン:

- `{OriginalName}`
- `{FileName}`
- `{Extension}`
- `{SourceRelativeDirectory}`
- `{ModifiedDate}`
- `{FileSize}`
- `{Sequence}`

Exif系のトークン:

- `{TakenDate}`
- `{TakenDateLocal}`
- `{TakenDateInTimeZone:JST|yyyy-MM-dd}`
- `{CameraMake}`
- `{CameraModel}`
- `{Lens}`

Exifに撮影日時がない場合はファイルの更新日時を使用し、プレビューの状態欄に警告を表示します。

## RAW+JPEGペア解析

同じフォルダーにある同名のRAWとJPEGをペアとして扱い、既定ではJPEGだけを解析して、そのExifをJPEGとRAWの両方へ適用します。単独RAWはRAW自身を解析します。

設定「RAW+JPEGペアではJPEGのみ解析する」は既定でONとし、OFFにするとJPEGとRAWをそれぞれ解析します。JPEGのExifをRAWへ適用した場合も、ファイル名、拡張子、相対ディレクトリ、サイズ、`{ModifiedDate}`、コピー時の変更検知にはRAW自身の値を使用します。

JPEGのみ解析する設定ではJPEGキャッシュを情報源とします。既存のRAWキャッシュは削除せず、設定をOFFにした場合や単独RAWの解析時に再利用します。RAWキャッシュの有無によってコピー先が変わらないよう、JPEGのみ解析する設定ではペアRAW自身のキャッシュを適用しません。

## Exifキャッシュ設定

画面の「Exif キャッシュ設定」を開くと、キャッシュ利用のON/OFF、現在の絶対パス、保存先変更、既定の保存先へ戻す操作を利用できます。既定の保存先は実行ファイル直下の `ExifCache` です。

保存先の変更前に書き込み可能性を確認します。旧保存先のキャッシュは自動で移動・削除せず、そのパスを設定履歴に保持します。キャッシュをOFFにした場合、Exifは直接解析され、キャッシュの読取・書込と排他制御を行いません。

完全な構文と競合時の規則は [TEMPLATE_SPEC.md](TEMPLATE_SPEC.md) を参照してください。設計と実装ロードマップは [DESIGN.md](DESIGN.md) にあります。

## コピーの安全性

- プレビューで確定したコピー先をコピー時に再計算しません。
- コピー直前と正式名確定直前に、コピー元・コピー先がスキャン時から変化していないか検証します。
- Windowsの `CopyFile2` を使い、コピー先フォルダー内の `PI_<32桁GUID>.partial` へ一時コピーします。
- 各ストリーム完了時に `FlushFileBuffers` を実行し、コピー後にサイズと更新日時を検証します。
- 正式名への確定には `MoveFileExW` を使います。
- キャンセルとファイル単位のエラー継続に対応します。

クラッシュ後などに残った `.partial` ファイルは、名前だけで成否を判断できないため、自動削除・自動昇格しません。

## 現在の制限

- Exifを使うスキャンでは、設定中の保存先にボリューム別の人間可読な `entries.tsv` キャッシュを保存します。2回目以降は一致するファイルの解析を省略し、進捗にキャッシュヒット数を表示します。途中停止した場合も、現在読み取り中の1ファイルを完了して解析済み結果を保存します。旧 `entries.json` は初回利用時に自動移行します。読取中に変更されたファイルはエラーとし、キャッシュ障害時は警告を表示して直接解析を続けます。`meta.json`、以前の保存先を含むカード管理画面、縮小版は未実装です。
- UIは現在、日本語のみです。
- 状態による一覧フィルターは未実装です。
- 残存する `.partial` ファイルの起動時検出と復旧案内UIは未実装です。
- 配布用の単一exe化は未実装です。
