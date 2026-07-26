using PhotoImporter.App;
using PhotoImporter.Core.Metadata;
using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class ImagePreviewLoaderTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "PhotoImporter-ImagePreview-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Load_UsesEmbeddedFrameThumbnailBeforeFullImage()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "embedded.jpg");
            Save(
                path,
                new JpegBitmapEncoder(),
                BitmapFrame.Create(
                    CreateBitmap(80, 60, Colors.Red),
                    CreateBitmap(8, 6, Colors.Blue)));
            var loader = new ImagePreviewLoader();

            var result = loader.Load(
                path,
                PhotoFileType.Jpeg,
                32,
                18,
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(ImagePreviewSourceKind.EmbeddedThumbnail, result.SourceKind);
            Assert.Equal(8, result.Image.PixelWidth);
            Assert.Equal(6, result.Image.PixelHeight);
            Assert.True(result.Image.IsFrozen);
        }

        [Fact]
        public void Load_ReducesNormalImageAndCachesSuccessfulResult()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "normal.png");
            Save(path, new PngBitmapEncoder(), BitmapFrame.Create(
                CreateBitmap(80, 60, Colors.Green)));
            var loader = new ImagePreviewLoader();

            var first = loader.Load(
                path,
                PhotoFileType.OtherImage,
                20,
                20,
                CancellationToken.None);
            var second = loader.Load(
                path,
                PhotoFileType.OtherImage,
                20,
                20,
                CancellationToken.None);

            Assert.True(first.IsSuccess);
            Assert.Equal(ImagePreviewSourceKind.ReducedImage, first.SourceKind);
            Assert.Equal(20, first.Image.PixelWidth);
            Assert.True(first.Image.IsFrozen);
            Assert.True(second.IsSuccess);
            Assert.Equal(ImagePreviewSourceKind.MemoryCache, second.SourceKind);
            Assert.Same(first.Image, second.Image);
        }

        [Fact]
        public void Load_DoesNotFallBackToFullDecodeForRawFile()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "standalone.dng");
            Save(path, new BmpBitmapEncoder(), BitmapFrame.Create(
                CreateBitmap(80, 60, Colors.Purple)));
            var loader = new ImagePreviewLoader();

            var result = loader.Load(
                path,
                PhotoFileType.Raw,
                20,
                20,
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("RAW本体", result.Message);
        }

        [Fact]
        public void PreviewItem_UsesPrecomputedPreviewSourcePath()
        {
            var item = new PreviewItem(
                @"DCIM\photo.arw",
                "destination.arw",
                PhotoImporter.Core.Templates.DestinationStatus.NotImported,
                null,
                imagePreviewSourcePath: @"DCIM\photo.jpg");

            Assert.Equal(@"DCIM\photo.jpg", item.ImagePreviewSourcePath);
        }

        private static BitmapSource CreateBitmap(
            int width,
            int height,
            Color color)
        {
            const int bytesPerPixel = 4;
            var stride = width * bytesPerPixel;
            var pixels = new byte[stride * height];
            for (var index = 0; index < pixels.Length; index += bytesPerPixel)
            {
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = color.A;
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            bitmap.Freeze();
            return bitmap;
        }

        private static void Save(
            string path,
            BitmapEncoder encoder,
            BitmapFrame frame)
        {
            encoder.Frames.Add(frame);
            using (var stream = File.Create(path))
                encoder.Save(stream);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
