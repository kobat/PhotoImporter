using System;
using System.Collections.Generic;

namespace PhotoImporter.Core.Templates
{
    public sealed class DestinationFileSnapshot
    {
        public DestinationFileSnapshot(long fileSize, DateTime lastWriteTimeUtc)
        {
            if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
            FileSize = fileSize;
            LastWriteTimeUtc = EnsureUtc(lastWriteTimeUtc, nameof(lastWriteTimeUtc));
        }

        public long FileSize { get; }
        public DateTime LastWriteTimeUtc { get; }

        private static DateTime EnsureUtc(DateTime value, string name)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The timestamp must be UTC.", name);
            return value;
        }
    }

    public interface IDestinationFileLookup
    {
        bool TryGetFile(string relativePath, out DestinationFileSnapshot snapshot);
    }

    public enum DestinationStatus
    {
        NotImported,
        Imported,
        Overwrite,
        Conflict
    }

    public sealed class DestinationAllocation
    {
        internal DestinationAllocation(
            string relativePath,
            DestinationStatus status,
            DestinationFileSnapshot destinationSnapshot)
        {
            RelativePath = relativePath;
            Status = status;
            DestinationSnapshot = destinationSnapshot;
        }

        public string RelativePath { get; }
        public DestinationStatus Status { get; }
        public DestinationFileSnapshot DestinationSnapshot { get; }
    }

    public sealed class DestinationAllocator
    {
        private readonly ParsedTemplate _template;
        private readonly IDestinationFileLookup _lookup;
        private readonly bool _overwriteExisting;
        private readonly HashSet<string> _reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DestinationAllocator(
            ParsedTemplate template,
            IDestinationFileLookup lookup,
            bool overwriteExisting = false)
        {
            _template = template ?? throw new ArgumentNullException(nameof(template));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _overwriteExisting = overwriteExisting;
        }

        public DestinationAllocation Allocate(FileTemplateContext context, DateTime sourceLastWriteTimeUtc)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (sourceLastWriteTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The timestamp must be UTC.", nameof(sourceLastWriteTimeUtc));

            var basicCandidate = TemplateEvaluator.Evaluate(_template, context);
            var basic = CheckCandidate(basicCandidate, sourceLastWriteTimeUtc);
            if (basic != null) return basic;

            if (!_template.HasSequence)
                return ReserveConflict(basicCandidate);

            var width = _template.SequenceWidth.Value;
            var maximum = (int)Math.Min(int.MaxValue, Math.Pow(10, width) - 1);
            for (var sequence = 1; sequence <= maximum; sequence++)
            {
                var candidate = TemplateEvaluator.Evaluate(_template, context, sequence);
                var allocation = CheckCandidate(candidate, sourceLastWriteTimeUtc);
                if (allocation != null) return allocation;
            }

            throw new TemplateException(new TemplateError(TemplateErrorCode.SequenceExhausted, 0, _template.Source.Length));
        }

        private DestinationAllocation CheckCandidate(string relativePath, DateTime sourceLastWriteTimeUtc)
        {
            if (_reserved.Contains(relativePath)) return null;

            DestinationFileSnapshot destination;
            if (!_lookup.TryGetFile(relativePath, out destination))
                return Reserve(relativePath, DestinationStatus.NotImported, null);

            if (sourceLastWriteTimeUtc <= destination.LastWriteTimeUtc)
                return Reserve(relativePath, DestinationStatus.Imported, destination);

            if (_template.HasSequence)
                return null;

            return Reserve(
                relativePath,
                _overwriteExisting ? DestinationStatus.Overwrite : DestinationStatus.Conflict,
                destination);
        }

        private DestinationAllocation ReserveConflict(string relativePath)
        {
            DestinationFileSnapshot destination;
            _lookup.TryGetFile(relativePath, out destination);
            return Reserve(relativePath, DestinationStatus.Conflict, destination);
        }

        private DestinationAllocation Reserve(
            string relativePath,
            DestinationStatus status,
            DestinationFileSnapshot destination)
        {
            _reserved.Add(relativePath);
            return new DestinationAllocation(relativePath, status, destination);
        }
    }
}
