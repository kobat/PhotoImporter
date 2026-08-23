# Release procedure

Photo Importer is distributed from GitHub Releases as a portable ZIP archive.
The managed runtime dependencies are embedded into `PhotoImporter.exe` by
Costura.Fody. License texts remain separate so users can inspect them without
starting the application.

## Local verification

From the repository root:

```powershell
.\build\Build-Release.ps1 -Version 0.1.0
```

The script restores packages, builds the Release configuration, runs all
tests, checks the executable version, produces the ZIP, rejects unexpected
DLL/PDB/config files, and writes a SHA-256 file under `artifacts\release`.

Before publishing, extract the generated ZIP into a new empty folder and
start `PhotoImporter.exe`. Confirm the main screen, About screen, language
selection, and license screen in both Japanese and English.

## GitHub Actions

- `CI` builds and tests every pull request and every push to `main`.
- `Release` can be run manually to produce a downloadable workflow artifact.
- Pushing a semantic version tag such as `v0.1.0` builds the same archive and
  publishes it with its SHA-256 file as a GitHub Release.

The tag version must match `<Version>` in
`src/PhotoImporter.App/PhotoImporter.App.csproj`; otherwise the release build
stops before publishing.

## v0.1.0 checklist

1. Confirm that CI succeeds on `main`.
2. Run the release workflow manually for version `0.1.0` and smoke-test its
   downloaded ZIP on Windows 11.
3. Confirm that `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, and every file under
   `Licenses` are present and readable.
4. Confirm that the SHA-256 file matches the ZIP.
5. Create and push the `v0.1.0` tag.
6. Review the generated GitHub Release notes and attached files.

## Signing status

v0.1.0 is currently prepared as an unsigned portable release. Windows may
show a SmartScreen warning. The distribution README tells users to download
only from GitHub Releases and verify the published SHA-256. Code signing can
be added later without changing the archive or release workflow structure.
