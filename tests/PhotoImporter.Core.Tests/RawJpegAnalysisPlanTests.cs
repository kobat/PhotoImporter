using System;
using System.Linq;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class RawJpegAnalysisPlanTests
    {
        [Fact]
        public void UsesJpegOnceForAnUnambiguousPairByDefault()
        {
            var plan = RawJpegAnalysisPlan.Create(new[]
            {
                @"C:\card\DCIM\photo.ARW",
                @"C:\card\DCIM\PHOTO.jpg"
            });

            Assert.Equal(@"C:\card\DCIM\PHOTO.jpg", plan.GetAnalysisSource(@"C:\card\DCIM\photo.ARW"));
            Assert.Equal(@"C:\card\DCIM\PHOTO.jpg", plan.GetAnalysisSource(@"C:\card\DCIM\PHOTO.jpg"));
            Assert.Equal(new[] { @"C:\card\DCIM\PHOTO.jpg" }, plan.AnalysisSources);
        }

        [Fact]
        public void AnalyzeBothUsesEachPhysicalFile()
        {
            var paths = new[] { @"C:\card\photo.jpg", @"C:\card\photo.nef" };

            var plan = RawJpegAnalysisPlan.Create(paths, RawJpegAnalysisMode.AnalyzeBoth);

            Assert.Equal(paths, plan.AnalysisSources);
            Assert.All(paths, path => Assert.Equal(path, plan.GetAnalysisSource(path)));
        }

        [Theory]
        [InlineData(@"C:\card\photo.jpg", @"C:\card\other\photo.arw")]
        [InlineData(@"C:\card\photo.jpg", @"C:\card\photo.tiff")]
        public void DoesNotPairDifferentDirectoriesOrUnsupportedExtensions(string first, string second)
        {
            var plan = RawJpegAnalysisPlan.Create(new[] { first, second });

            Assert.Equal(2, plan.AnalysisSources.Count);
            Assert.Equal(first, plan.GetAnalysisSource(first));
            Assert.Equal(second, plan.GetAnalysisSource(second));
        }

        [Fact]
        public void AmbiguousCandidatesAreAnalyzedIndividually()
        {
            var paths = new[]
            {
                @"C:\card\photo.jpg",
                @"C:\card\photo.jpeg",
                @"C:\card\photo.cr3"
            };

            var plan = RawJpegAnalysisPlan.Create(paths);

            Assert.Equal(3, plan.AnalysisSources.Count);
            Assert.All(paths, path => Assert.Equal(path, plan.GetAnalysisSource(path)));
        }

        [Fact]
        public void SingleRawIsAnalyzedFromItself()
        {
            var plan = RawJpegAnalysisPlan.Create(new[] { @"C:\card\photo.raf" });

            Assert.Equal(@"C:\card\photo.raf", plan.AnalysisSources.Single());
        }
    }
}
