using System;
using System.Linq;
using PhotoImporter.Core.Metadata;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class SidecarAssociationPlanTests
    {
        [Fact]
        public void ExactImageFileNameTakesPriority()
        {
            var image = @"C:\card\photo.jpg";
            var sidecar = @"C:\card\photo.jpg.xmp";

            var plan = SidecarAssociationPlan.Create(new[] { image, sidecar });

            SidecarAssociation association;
            Assert.True(plan.TryGetAssociation(sidecar, out association));
            Assert.Equal(image, association.ImagePath);
            Assert.Equal(SidecarNamingStyle.FullImageFileName, association.NamingStyle);
        }

        [Fact]
        public void StemSidecarUsesOnlyMatchingImage()
        {
            var image = @"C:\card\photo.nef";
            var sidecar = @"C:\card\PHOTO.XMP";

            var plan = SidecarAssociationPlan.Create(new[] { image, sidecar });

            var association = Assert.Single(plan.GetSidecars(image));
            Assert.Equal(sidecar, association.SidecarPath);
            Assert.Equal(SidecarNamingStyle.ImageStem, association.NamingStyle);
        }

        [Fact]
        public void StemSidecarUsesRawForUnambiguousRawJpegPair()
        {
            var raw = @"C:\card\photo.arw";
            var jpeg = @"C:\card\photo.jpg";
            var sidecar = @"C:\card\photo.xmp";

            var plan = SidecarAssociationPlan.Create(new[] { jpeg, sidecar, raw });

            SidecarAssociation association;
            Assert.True(plan.TryGetAssociation(sidecar, out association));
            Assert.Equal(raw, association.ImagePath);
            Assert.Empty(plan.GetSidecars(jpeg));
        }

        [Fact]
        public void AmbiguousImagesLeaveSidecarIndependentAndReportWarning()
        {
            var sidecar = @"C:\card\photo.xmp";

            var plan = SidecarAssociationPlan.Create(new[]
            {
                @"C:\card\photo.jpg",
                @"C:\card\photo.png",
                sidecar
            });

            SidecarAssociation association;
            Assert.False(plan.TryGetAssociation(sidecar, out association));
            Assert.Contains(plan.Warnings, warning => warning.Contains("曖昧"));
        }

        [Theory]
        [InlineData(SidecarNamingStyle.ImageStem, @"2026\renamed_001.xmp")]
        [InlineData(SidecarNamingStyle.FullImageFileName, @"2026\renamed_001.arw.xmp")]
        public void DerivesDestinationFromFinalImageName(
            SidecarNamingStyle style,
            string expected)
        {
            var result = SidecarDestinationPath.Derive(
                @"2026\renamed_001.arw",
                @"C:\card\photo.XMP",
                style);

            Assert.Equal(expected, result, ignoreCase: true);
        }

        [Fact]
        public void PotentialPathsCoverStemAndFullFileNameConventions()
        {
            var paths = SidecarDestinationPath.GetPotentialXmpPaths(@"2026\photo_001.arw");

            Assert.Equal(
                new[] { @"2026\photo_001.xmp", @"2026\photo_001.arw.xmp" },
                paths,
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
