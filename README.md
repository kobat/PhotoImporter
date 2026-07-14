# Photo Importer

Windows 11向けの写真取り込みアプリです。SDカードやCFexpressカードなどのフォルダーを再帰的にスキャンし、テンプレートからコピー先をプレビューして、未取り込みのファイルをコピーします。

ファイルシステム情報とExif情報を使うテンプレートに対応しています。Exifキャッシュ、設定保存、多言語化、状態フィルター、単一exe配布は未実装です。

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

2026-07-14時点では、56件のCore単体テストがすべて成功しています。

## 基本的な使い方

1. コピー元フォルダーを選択します。
2. コピー先フォルダーを選択します。
3. テンプレートと「既存ファイルを上書きする」設定を指定します。
4. 「スキャン」を押し、表示されたコピー先と状態を確認します。
5. コピー対象のチェックを調整して「コピー」を押します。
6. コピー後は自動的に再スキャンされ、状態が更新されます。

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

- Exifはスキャンごとに読み取ります。読み取り中は件数と進捗率を表示しますが、Exifキャッシュは未実装です。
- UIは現在、日本語のみです。
- 状態による一覧フィルターは未実装です。
- フォルダー、テンプレート、上書き設定は終了後に保存されません。
- 残存する `.partial` ファイルの起動時検出と復旧案内UIは未実装です。
- 配布用の単一exe化は未実装です。
