using System.Linq;
using PhotoImporter.App;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class LicenseInformationCatalogTests
    {
        [Fact]
        public void EveryCatalogEntryHasEmbeddedLicenseText()
        {
            Assert.Equal(5, LicenseInformationCatalog.Items.Count);
            Assert.All(LicenseInformationCatalog.Items, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
                Assert.False(string.IsNullOrWhiteSpace(item.Summary));
                Assert.True(item.LicenseText.Length > 100);
            });
        }

        [Fact]
        public void CatalogCoversApplicationAndDistributedLibraries()
        {
            var names = LicenseInformationCatalog.Items.Select(item => item.DisplayName).ToArray();

            Assert.Contains(names, name => name.StartsWith("Photo Importer"));
            Assert.Contains(names, name => name.StartsWith("MetadataExtractor"));
            Assert.Contains(names, name => name.StartsWith("XmpCore"));
            Assert.Contains(names, name => name.Contains("Microsoft .NET"));
            Assert.Contains(names, name => name.Contains("Microsoft 第三者通知"));
        }
    }
}
