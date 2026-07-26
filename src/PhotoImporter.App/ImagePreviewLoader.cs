using PhotoImporter.Core.Metadata;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoImporter.App
{
    internal enum ImagePreviewSourceKind
    {
        MemoryCache,
        EmbeddedThumbnail,
        EmbeddedPreview,
        ReducedImage
    }

    internal sealed class ImagePreviewLoadResult
    {
        private ImagePreviewLoadResult(
            BitmapSource image,
            ImagePreviewSourceKind? sourceKind,
            string message)
        {
            Image = image;
            SourceKind = sourceKind;
            Message = message;
        }

        public BitmapSource Image { get; }
        public ImagePreviewSourceKind? SourceKind { get; }
        public string Message { get; }
        public bool IsSuccess => Image != null;

        public static ImagePreviewLoadResult Success(
            BitmapSource image,
            ImagePreviewSourceKind sourceKind)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            return new ImagePreviewLoadResult(image, sourceKind, string.Empty);
        }

        public static ImagePreviewLoadResult Unavailable(string message) =>
            new ImagePreviewLoadResult(null, null, message);
    }

    internal sealed class ImagePreviewLoader
    {
        internal const int DefaultMaximumWidth = 320;
        internal const int DefaultMaximumHeight = 180;
        private const int MaximumCacheEntries = 24;

        private readonly object _cacheLock = new object();
        private readonly Dictionary<ImagePreviewCacheKey, LinkedListNode<ImagePreviewCacheEntry>> _cache =
            new Dictionary<ImagePreviewCacheKey, LinkedListNode<ImagePreviewCacheEntry>>();
        private readonly LinkedList<ImagePreviewCacheEntry> _cacheUsage =
            new LinkedList<ImagePreviewCacheEntry>();

        public ImagePreviewLoadResult Load(
            string path,
            PhotoFileType originalFileType,
            int maximumWidth,
            int maximumHeight,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("プレビュー元のパスが必要です。", nameof(path));
            if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
            if (maximumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeight));

            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            var previewFileType = PhotoFileClassifier.Classify(fullPath);
            if (originalFileType == PhotoFileType.Video ||
                previewFileType == PhotoFileType.Video)
                return ImagePreviewLoadResult.Unavailable("動画のプレビューには対応していません。");
            if (previewFileType == PhotoFileType.Other)
                return ImagePreviewLoadResult.Unavailable("このファイル形式は画像プレビューの対象外です。");

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                info.Refresh();
                if (!info.Exists)
                    return ImagePreviewLoadResult.Unavailable("画像ファイルが見つかりません。");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                return ImagePreviewLoadResult.Unavailable("画像ファイルを確認できません: " + ex.Message);
            }

            var cacheKey = new ImagePreviewCacheKey(
                info.FullName,
                info.Length,
                info.LastWriteTimeUtc,
                maximumWidth,
                maximumHeight);
            BitmapSource cached;
            if (TryGetCached(cacheKey, out cached))
                return ImagePreviewLoadResult.Success(cached, ImagePreviewSourceKind.MemoryCache);

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ImagePreviewSourceKind sourceKind;
                var image = TryLoadEmbeddedImage(
                    info.FullName,
                    maximumWidth,
                    maximumHeight,
                    out sourceKind);
                cancellationToken.ThrowIfCancellationRequested();

                if (image == null && previewFileType != PhotoFileType.Raw)
                {
                    image = LoadReducedImage(info.FullName, maximumWidth);
                    sourceKind = ImagePreviewSourceKind.ReducedImage;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (image == null)
                    return ImagePreviewLoadResult.Unavailable(
                        "埋め込みプレビューを取得できませんでした。RAW本体の全体デコードは行いません。");

                AddToCache(cacheKey, image);
                return ImagePreviewLoadResult.Success(image, sourceKind);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException || ex is FileFormatException)
            {
                return ImagePreviewLoadResult.Unavailable("画像プレビューを読み込めません: " + ex.Message);
            }
        }

        private static BitmapSource TryLoadEmbeddedImage(
            string path,
            int maximumWidth,
            int maximumHeight,
            out ImagePreviewSourceKind sourceKind)
        {
            sourceKind = ImagePreviewSourceKind.EmbeddedThumbnail;
            using (var stream = OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnDemand);

                BitmapSource source = null;
                if (decoder.Frames.Count > 0)
                    source = decoder.Frames[0].Thumbnail;
                if (source == null)
                    source = decoder.Thumbnail;
                if (source == null)
                {
                    source = decoder.Preview;
                    sourceKind = ImagePreviewSourceKind.EmbeddedPreview;
                }
                if (source == null) return null;

                return DetachAndScale(source, maximumWidth, maximumHeight);
            }
        }

        private static BitmapSource LoadReducedImage(string path, int maximumWidth)
        {
            using (var stream = OpenRead(path))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = maximumWidth;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        private static FileStream OpenRead(string path) =>
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.RandomAccess);

        private static BitmapSource DetachAndScale(
            BitmapSource source,
            int maximumWidth,
            int maximumHeight)
        {
            BitmapSource materializedSource = source;
            if (source.PixelWidth > 0 && source.PixelHeight > 0)
            {
                var scale = Math.Min(
                    1.0,
                    Math.Min(
                        (double)maximumWidth / source.PixelWidth,
                        (double)maximumHeight / source.PixelHeight));
                if (scale < 1.0)
                {
                    var transform = new ScaleTransform(scale, scale);
                    transform.Freeze();
                    materializedSource = new TransformedBitmap(source, transform);
                }
            }

            var cached = new CachedBitmap(
                materializedSource,
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            cached.Freeze();
            return cached;
        }

        private bool TryGetCached(ImagePreviewCacheKey key, out BitmapSource image)
        {
            lock (_cacheLock)
            {
                LinkedListNode<ImagePreviewCacheEntry> node;
                if (!_cache.TryGetValue(key, out node))
                {
                    image = null;
                    return false;
                }

                _cacheUsage.Remove(node);
                _cacheUsage.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        private void AddToCache(ImagePreviewCacheKey key, BitmapSource image)
        {
            lock (_cacheLock)
            {
                LinkedListNode<ImagePreviewCacheEntry> existing;
                if (_cache.TryGetValue(key, out existing))
                {
                    _cacheUsage.Remove(existing);
                    _cacheUsage.AddFirst(existing);
                    return;
                }

                var node = new LinkedListNode<ImagePreviewCacheEntry>(
                    new ImagePreviewCacheEntry(key, image));
                _cache.Add(key, node);
                _cacheUsage.AddFirst(node);
                while (_cache.Count > MaximumCacheEntries)
                {
                    var last = _cacheUsage.Last;
                    _cacheUsage.RemoveLast();
                    _cache.Remove(last.Value.Key);
                }
            }
        }
    }

    internal struct ImagePreviewCacheKey : IEquatable<ImagePreviewCacheKey>
    {
        public ImagePreviewCacheKey(
            string path,
            long fileSize,
            DateTime lastWriteTimeUtc,
            int maximumWidth,
            int maximumHeight)
        {
            Path = path;
            FileSize = fileSize;
            LastWriteTimeUtc = lastWriteTimeUtc;
            MaximumWidth = maximumWidth;
            MaximumHeight = maximumHeight;
        }

        public string Path { get; }
        public long FileSize { get; }
        public DateTime LastWriteTimeUtc { get; }
        public int MaximumWidth { get; }
        public int MaximumHeight { get; }

        public bool Equals(ImagePreviewCacheKey other) =>
            string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
            FileSize == other.FileSize &&
            LastWriteTimeUtc == other.LastWriteTimeUtc &&
            MaximumWidth == other.MaximumWidth &&
            MaximumHeight == other.MaximumHeight;

        public override bool Equals(object obj) =>
            obj is ImagePreviewCacheKey && Equals((ImagePreviewCacheKey)obj);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Path);
                hash = (hash * 397) ^ FileSize.GetHashCode();
                hash = (hash * 397) ^ LastWriteTimeUtc.GetHashCode();
                hash = (hash * 397) ^ MaximumWidth;
                return (hash * 397) ^ MaximumHeight;
            }
        }
    }

    internal sealed class ImagePreviewCacheEntry
    {
        public ImagePreviewCacheEntry(ImagePreviewCacheKey key, BitmapSource image)
        {
            Key = key;
            Image = image;
        }

        public ImagePreviewCacheKey Key { get; }
        public BitmapSource Image { get; }
    }
}
