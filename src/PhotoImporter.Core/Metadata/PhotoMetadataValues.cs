namespace PhotoImporter.Core.Metadata
{
    internal static class PhotoMetadataValues
    {
        public static void GetOrientedDimensions(PhotoMetadata metadata, out int? width, out int? height)
        {
            if (metadata.DecodedWidth.HasValue && metadata.DecodedHeight.HasValue)
            {
                width = metadata.DecodedWidth;
                height = metadata.DecodedHeight;
            }
            else if (metadata.ExifWidth.HasValue && metadata.ExifHeight.HasValue)
            {
                width = metadata.ExifWidth;
                height = metadata.ExifHeight;
            }
            else
            {
                width = null;
                height = null;
                return;
            }

            if (metadata.Orientation >= 5 && metadata.Orientation <= 8)
            {
                var temporary = width;
                width = height;
                height = temporary;
            }
        }
    }
}
