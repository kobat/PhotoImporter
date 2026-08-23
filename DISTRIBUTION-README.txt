Photo Importer v0.1.0
=====================

日本語
------

Photo Importer は、Windows 11向けの写真取り込みアプリです。

1. ZIPをフォルダーへ展開します。
2. PhotoImporter.exe を起動します。
3. コピー元、コピー先、テンプレートを指定してスキャンします。
4. コピー予定を確認してからコピーを実行します。

必要な環境: Windows 11 / .NET Framework 4.8.1

このバージョンの実行ファイルにはコード署名がありません。Windowsの警告が表示された
場合は、GitHub Releasesから入手したファイルであることと、公開されているSHA-256が
一致することを確認してください。

Exifキャッシュの既定の保存先は、PhotoImporter.exe と同じフォルダーの ExifCache
です。書き込み可能なフォルダーへ展開して使用してください。

アプリの設定は %LocalAppData%\PhotoImporter に保存されます。写真ファイルはコピー元
から削除されません。

English
-------

Photo Importer is a photo import application for Windows 11.

1. Extract the ZIP archive to a folder.
2. Start PhotoImporter.exe.
3. Select a source, destination, and template, then scan.
4. Review the copy plan before starting the copy.

Requirements: Windows 11 / .NET Framework 4.8.1

This version is not code-signed. If Windows displays a warning, confirm that
the archive came from GitHub Releases and that its SHA-256 matches the
published checksum.

The default Exif cache location is the ExifCache folder beside
PhotoImporter.exe. Extract the application to a writable folder.

Application settings are stored under %LocalAppData%\PhotoImporter. Source
photo files are never deleted.

License
-------

Photo Importer is released under the MIT License. See LICENSE.txt.
Third-party notices and license texts are included in
THIRD-PARTY-NOTICES.txt and the Licenses folder.

Repository: https://github.com/kobat/PhotoImporter
