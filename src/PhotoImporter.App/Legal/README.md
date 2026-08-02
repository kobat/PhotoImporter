# License data maintenance

The application embeds the files in `Licenses` so that the About screen can
show all license information without a network connection. The same files are
copied to the Release output under `Licenses`.

Sources for the current v0.1.0 dependency set:

- `MetadataExtractorApache2.txt`: the `LICENSE` file from
  <https://github.com/drewnoakes/metadata-extractor-dotnet>. The package is
  MetadataExtractor 2.9.3 (Apache-2.0).
- `XmpCoreBSD.txt`: the Adobe XMP SDK BSD license referenced by
  <https://github.com/drewnoakes/xmp-core-dotnet>. The text was verified
  against `XMP-Toolkit-SDK-CC201607/BSD-License.txt` in
  <https://github.com/Exiv2/adobe_xmp_sdk>. The package is XmpCore 6.1.10.1.
- `MicrosoftMIT.txt`: the .NET runtime MIT license from
  <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>.
- `MicrosoftThirdPartyNotices.txt`: copied verbatim from the
  System.Text.Encoding.CodePages 10.0.5 NuGet package.
- Photo Importer's own license is embedded directly from the repository-root
  `LICENSE` file, so there is only one source of truth.

Whenever a package version changes, compare this catalog with the DLLs in the
Release output, update `THIRD-PARTY-NOTICES.txt`, replace any package-specific
license or notice text, and run the full Release test suite.
