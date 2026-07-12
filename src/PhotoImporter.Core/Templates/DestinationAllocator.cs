using System;
using System.Collections.Generic;

namespace PhotoImporter.Core.Templates
{
    public interface IDestinationFileLookup
    {
        bool TryGetFileSize(string relativePath, out long fileSize);
    }

    public enum DestinationStatus
    {
        NotImported,
        Imported
    }

    public sealed class DestinationAllocation
    {
        internal DestinationAllocation(string relativePath, DestinationStatus status)
        {
            RelativePath = relativePath;
            Status = status;
        }

        public string RelativePath { get; }
        public DestinationStatus Status { get; }
    }

    public sealed class DestinationAllocator
    {
        private readonly ParsedTemplate _template;
        private readonly IDestinationFileLookup _lookup;
        private readonly HashSet<string> _reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DestinationAllocator(ParsedTemplate template, IDestinationFileLookup lookup)
        {
            _template = template ?? throw new ArgumentNullException(nameof(template));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        }

        public DestinationAllocation Allocate(FileTemplateContext context)
        {
            var basicCandidate = TemplateEvaluator.Evaluate(_template, context);
            var basic = CheckCandidate(basicCandidate, context.FileSize);
            if (basic != null) return basic;

            if (!_template.HasSequence)
                throw new TemplateException(new TemplateError(TemplateErrorCode.DestinationConflict, 0, _template.Source.Length));

            var width = _template.SequenceWidth.Value;
            var maximum = (int)Math.Min(int.MaxValue, Math.Pow(10, width) - 1);
            for (var sequence = 1; sequence <= maximum; sequence++)
            {
                var candidate = TemplateEvaluator.Evaluate(_template, context, sequence);
                var allocation = CheckCandidate(candidate, context.FileSize);
                if (allocation != null) return allocation;
            }

            throw new TemplateException(new TemplateError(TemplateErrorCode.SequenceExhausted, 0, _template.Source.Length));
        }

        private DestinationAllocation CheckCandidate(string relativePath, long sourceSize)
        {
            if (_reserved.Contains(relativePath)) return null;

            long destinationSize;
            var exists = _lookup.TryGetFileSize(relativePath, out destinationSize);
            if (exists && destinationSize != sourceSize) return null;

            _reserved.Add(relativePath);
            return new DestinationAllocation(
                relativePath,
                exists ? DestinationStatus.Imported : DestinationStatus.NotImported);
        }
    }
}
