using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PhotoImporter.App
{
    internal sealed class LicenseInformationItem
    {
        public LicenseInformationItem(string category, string displayName, string summary, string resourceFileName)
        {
            Category = category ?? throw new ArgumentNullException(nameof(category));
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            LicenseText = LicenseInformationCatalog.LoadResourceText(resourceFileName);
        }

        public string Category { get; }
        public string DisplayName { get; }
        public string Summary { get; }
        public string LicenseText { get; }
    }

    internal static class LicenseInformationCatalog
    {
        private const string ResourcePrefix = "PhotoImporter.App.Legal.Licenses.";

        public static IReadOnlyList<LicenseInformationItem> Items { get; } =
            Array.AsReadOnly(new[]
            {
                new LicenseInformationItem(
                    "このアプリのライセンス",
                    "Photo Importer 0.1.0",
                    "Copyright © 2026 KOBAT — MIT License",
                    "PhotoImporterMIT.txt"),
                new LicenseInformationItem(
                    "第三者ライブラリのライセンス",
                    "MetadataExtractor 2.9.3",
                    "Copyright Drew Noakes 2002–2026; 2014 Imazen LLC — Apache License 2.0",
                    "MetadataExtractorApache2.txt"),
                new LicenseInformationItem(
                    "第三者ライブラリのライセンス",
                    "XmpCore 6.1.10.1",
                    "Copyright 2015–2021 XmpCore contributors / Adobe XMP SDK 由来 — BSD 3-Clause License",
                    "XmpCoreBSD.txt"),
                new LicenseInformationItem(
                    "第三者ライブラリのライセンス",
                    "Microsoft .NET 補助ライブラリ",
                    "System.Buffers 4.6.1 / System.Memory 4.6.3 / System.Numerics.Vectors 4.6.1 / " +
                    "System.Runtime.CompilerServices.Unsafe 6.1.2 / System.Text.Encoding.CodePages 10.0.5 — MIT License",
                    "MicrosoftMIT.txt"),
                new LicenseInformationItem(
                    "第三者ライブラリのライセンス",
                    "Microsoft 第三者ライブラリ",
                    "System.Text.Encoding.CodePages 10.0.5 に同梱された第三者ライセンス通知",
                    "MicrosoftThirdPartyNotices.txt")
            });

        internal static string LoadResourceText(string resourceFileName)
        {
            if (string.IsNullOrWhiteSpace(resourceFileName))
                throw new ArgumentException("Resource file name is required.", nameof(resourceFileName));

            var resourceName = ResourcePrefix + resourceFileName;
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("License resource was not found: " + resourceName);

                using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
                    return reader.ReadToEnd();
            }
        }
    }
}
