using System;

namespace PhotoImporter.Core.Metadata
{
    public enum PhotoMetadataReadStatus
    {
        Success,
        NoMetadata,
        Unsupported,
        ReadError
    }

    public sealed class PhotoMetadataReadResult
    {
        private PhotoMetadataReadResult(
            PhotoMetadataReadStatus status,
            PhotoMetadata metadata,
            Exception error)
        {
            Status = status;
            Metadata = metadata ?? PhotoMetadata.Empty;
            Error = error;
        }

        public PhotoMetadataReadStatus Status { get; }
        public PhotoMetadata Metadata { get; }
        public Exception Error { get; }

        public bool IsCacheable => Status != PhotoMetadataReadStatus.ReadError;

        public static PhotoMetadataReadResult Success(PhotoMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (!metadata.HasValues)
                throw new ArgumentException("Successful metadata must contain at least one extracted value.", nameof(metadata));
            return new PhotoMetadataReadResult(PhotoMetadataReadStatus.Success, metadata, null);
        }

        public static PhotoMetadataReadResult NoMetadata() =>
            new PhotoMetadataReadResult(PhotoMetadataReadStatus.NoMetadata, PhotoMetadata.Empty, null);

        public static PhotoMetadataReadResult Unsupported() =>
            new PhotoMetadataReadResult(PhotoMetadataReadStatus.Unsupported, PhotoMetadata.Empty, null);

        public static PhotoMetadataReadResult ReadError(Exception error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            return new PhotoMetadataReadResult(PhotoMetadataReadStatus.ReadError, PhotoMetadata.Empty, error);
        }
    }
}
