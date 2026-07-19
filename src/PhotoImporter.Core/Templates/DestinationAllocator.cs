using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using PhotoImporter.Core.Metadata;

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
            DestinationFileSnapshot destinationSnapshot,
            IList<TemplateWarningCode> warnings,
            int? sequenceNumber)
        {
            RelativePath = relativePath;
            Status = status;
            DestinationSnapshot = destinationSnapshot;
            Warnings = new ReadOnlyCollection<TemplateWarningCode>(warnings ?? new List<TemplateWarningCode>());
            SequenceNumber = sequenceNumber;
        }

        public string RelativePath { get; }
        public DestinationStatus Status { get; }
        public DestinationFileSnapshot DestinationSnapshot { get; }
        public IReadOnlyList<TemplateWarningCode> Warnings { get; }
        public int? SequenceNumber { get; }
    }

    public sealed class DestinationAllocator
    {
        private readonly ParsedTemplate _template;
        private readonly IDestinationFileLookup _lookup;
        private readonly FileSystemTimestampPolicy _timestampPolicy;
        private readonly bool _overwriteExisting;
        private readonly string _destinationRoot;
        private readonly int _maximumFullPathLength;
        private readonly HashSet<string> _reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DestinationAllocator(
            ParsedTemplate template,
            IDestinationFileLookup lookup,
            FileSystemTimestampPolicy timestampPolicy,
            bool overwriteExisting = false,
            string destinationRoot = null,
            int maximumFullPathLength = TemplateEvaluator.MaximumFullPathLength)
        {
            _template = template ?? throw new ArgumentNullException(nameof(template));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _timestampPolicy = timestampPolicy ?? throw new ArgumentNullException(nameof(timestampPolicy));
            if (maximumFullPathLength < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumFullPathLength));
            _overwriteExisting = overwriteExisting;
            _destinationRoot = destinationRoot;
            _maximumFullPathLength = maximumFullPathLength;
        }

        public DestinationAllocation Allocate(FileTemplateContext context, DateTime sourceLastWriteTimeUtc)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (sourceLastWriteTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The timestamp must be UTC.", nameof(sourceLastWriteTimeUtc));

            var basicEvaluation = TemplateEvaluator.EvaluateDetailed(
                _template, context, null, _maximumFullPathLength, _destinationRoot);
            var basicCandidate = basicEvaluation.RelativePath;
            var basic = CheckCandidate(
                basicCandidate, context.FileSize, sourceLastWriteTimeUtc, basicEvaluation.Warnings, null);
            if (basic != null) return basic;

            if (!_template.HasSequence)
                return ReserveConflict(basicCandidate, basicEvaluation.Warnings);

            var width = _template.SequenceWidth.Value;
            var maximum = (int)Math.Min(int.MaxValue, Math.Pow(10, width) - 1);
            for (var sequence = 1; sequence <= maximum; sequence++)
            {
                var evaluation = TemplateEvaluator.EvaluateDetailed(
                    _template, context, sequence, _maximumFullPathLength, _destinationRoot);
                var candidate = evaluation.RelativePath;
                var allocation = CheckCandidate(
                    candidate, context.FileSize, sourceLastWriteTimeUtc, evaluation.Warnings, sequence);
                if (allocation != null) return allocation;
            }

            throw new TemplateException(new TemplateError(TemplateErrorCode.SequenceExhausted, 0, _template.Source.Length));
        }

        private DestinationAllocation CheckCandidate(
            string relativePath,
            long sourceFileSize,
            DateTime sourceLastWriteTimeUtc,
            IReadOnlyList<TemplateWarningCode> warnings,
            int? sequenceNumber)
        {
            if (_reserved.Contains(relativePath)) return null;

            DestinationFileSnapshot destination;
            if (!_lookup.TryGetFile(relativePath, out destination))
                return Reserve(relativePath, DestinationStatus.NotImported, null, warnings, sequenceNumber);

            if (sourceFileSize == destination.FileSize &&
                _timestampPolicy.Matches(sourceLastWriteTimeUtc, destination.LastWriteTimeUtc))
                return Reserve(relativePath, DestinationStatus.Imported, destination, warnings, sequenceNumber);

            if (_template.HasSequence)
                return null;

            return Reserve(
                relativePath,
                _overwriteExisting ? DestinationStatus.Overwrite : DestinationStatus.Conflict,
                destination,
                warnings,
                sequenceNumber);
        }

        private DestinationAllocation ReserveConflict(string relativePath, IReadOnlyList<TemplateWarningCode> warnings)
        {
            DestinationFileSnapshot destination;
            _lookup.TryGetFile(relativePath, out destination);
            return Reserve(relativePath, DestinationStatus.Conflict, destination, warnings, null);
        }

        private DestinationAllocation Reserve(
            string relativePath,
            DestinationStatus status,
            DestinationFileSnapshot destination,
            IReadOnlyList<TemplateWarningCode> warnings,
            int? sequenceNumber)
        {
            _reserved.Add(relativePath);
            return new DestinationAllocation(
                relativePath, status, destination, new List<TemplateWarningCode>(warnings), sequenceNumber);
        }
    }
}
