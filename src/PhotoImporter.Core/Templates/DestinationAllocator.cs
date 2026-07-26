using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
            return Allocate(context, sourceLastWriteTimeUtc, null);
        }

        public DestinationAllocation Allocate(
            FileTemplateContext context,
            DateTime sourceLastWriteTimeUtc,
            Func<string, bool> isBlockedByOrphanSidecar)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (sourceLastWriteTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The timestamp must be UTC.", nameof(sourceLastWriteTimeUtc));

            var basicEvaluation = TemplateEvaluator.EvaluateDetailed(
                _template, context, null, _maximumFullPathLength, _destinationRoot);
            var basicCandidate = basicEvaluation.RelativePath;
            bool blockedByOrphanSidecar;
            var basic = CheckCandidate(
                basicCandidate,
                context.FileSize,
                sourceLastWriteTimeUtc,
                basicEvaluation.Warnings,
                null,
                isBlockedByOrphanSidecar,
                out blockedByOrphanSidecar);
            if (basic != null) return basic;

            if (!_template.HasSequence)
                return ReserveConflict(
                    basicCandidate,
                    AddOrphanWarning(basicEvaluation.Warnings, blockedByOrphanSidecar));

            var width = _template.SequenceWidth.Value;
            var maximum = (int)Math.Min(int.MaxValue, Math.Pow(10, width) - 1);
            var orphanSidecarForcedSequence = blockedByOrphanSidecar;
            for (var sequence = 1; sequence <= maximum; sequence++)
            {
                var evaluation = TemplateEvaluator.EvaluateDetailed(
                    _template, context, sequence, _maximumFullPathLength, _destinationRoot);
                var candidate = evaluation.RelativePath;
                bool candidateBlockedByOrphanSidecar;
                var allocation = CheckCandidate(
                    candidate,
                    context.FileSize,
                    sourceLastWriteTimeUtc,
                    AddOrphanWarning(evaluation.Warnings, orphanSidecarForcedSequence),
                    sequence,
                    isBlockedByOrphanSidecar,
                    out candidateBlockedByOrphanSidecar);
                if (allocation != null) return allocation;
                orphanSidecarForcedSequence |= candidateBlockedByOrphanSidecar;
            }

            throw new TemplateException(new TemplateError(TemplateErrorCode.SequenceExhausted, 0, _template.Source.Length));
        }

        private DestinationAllocation CheckCandidate(
            string relativePath,
            long sourceFileSize,
            DateTime sourceLastWriteTimeUtc,
            IReadOnlyList<TemplateWarningCode> warnings,
            int? sequenceNumber,
            Func<string, bool> isBlockedByOrphanSidecar,
            out bool blockedByOrphanSidecar)
        {
            blockedByOrphanSidecar = false;
            if (_reserved.Contains(relativePath)) return null;

            DestinationFileSnapshot destination;
            if (!_lookup.TryGetFile(relativePath, out destination))
            {
                if (isBlockedByOrphanSidecar != null &&
                    isBlockedByOrphanSidecar(relativePath))
                {
                    blockedByOrphanSidecar = true;
                    return null;
                }
                return Reserve(relativePath, DestinationStatus.NotImported, null, warnings, sequenceNumber);
            }

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

        public DestinationAllocation AllocateFixed(
            string relativePath,
            long sourceFileSize,
            DateTime sourceLastWriteTimeUtc,
            IReadOnlyList<TemplateWarningCode> warnings,
            int? sequenceNumber,
            bool overwriteExisting)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("The relative path is required.", nameof(relativePath));
            if (sourceFileSize < 0) throw new ArgumentOutOfRangeException(nameof(sourceFileSize));
            if (sourceLastWriteTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The timestamp must be UTC.", nameof(sourceLastWriteTimeUtc));

            DestinationFileSnapshot destination;
            if (_reserved.Contains(relativePath))
            {
                _lookup.TryGetFile(relativePath, out destination);
                return Reserve(
                    relativePath,
                    DestinationStatus.Conflict,
                    destination,
                    warnings,
                    sequenceNumber);
            }
            if (!_lookup.TryGetFile(relativePath, out destination))
                return Reserve(
                    relativePath,
                    DestinationStatus.NotImported,
                    null,
                    warnings,
                    sequenceNumber);
            if (sourceFileSize == destination.FileSize &&
                _timestampPolicy.Matches(sourceLastWriteTimeUtc, destination.LastWriteTimeUtc))
            {
                return Reserve(
                    relativePath,
                    DestinationStatus.Imported,
                    destination,
                    warnings,
                    sequenceNumber);
            }
            return Reserve(
                relativePath,
                overwriteExisting ? DestinationStatus.Overwrite : DestinationStatus.Conflict,
                destination,
                warnings,
                sequenceNumber);
        }

        private static IReadOnlyList<TemplateWarningCode> AddOrphanWarning(
            IReadOnlyList<TemplateWarningCode> warnings,
            bool include)
        {
            if (!include || warnings.Contains(TemplateWarningCode.OrphanSidecarForcedSequence))
                return warnings;
            var result = new List<TemplateWarningCode>(warnings)
            {
                TemplateWarningCode.OrphanSidecarForcedSequence
            };
            return result;
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
