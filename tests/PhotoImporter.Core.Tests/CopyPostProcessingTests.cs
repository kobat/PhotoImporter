using PhotoImporter.App;
using PhotoImporter.Core.Filtering;
using System;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class CopyPostProcessingTests
    {
        [Fact]
        public void SuccessfulSelectionRestoreAndRefreshReturnsNoError()
        {
            var restored = false;
            var refreshed = false;

            var error = CopyPostProcessing.TryRun(
                () => restored = true,
                () => refreshed = true);

            Assert.Null(error);
            Assert.True(restored);
            Assert.True(refreshed);
        }

        [Fact]
        public void FilterEvaluationFailureIsReturnedInsteadOfEscaping()
        {
            var filter = new FilterSet(new FilterCondition[]
            {
                new StringFilterCondition(
                    FilterField.OriginalName,
                    @"^(a+)+$",
                    StringFilterMatchMode.RegularExpression,
                    regexTimeout: TimeSpan.FromMilliseconds(1))
            }).Prepare().Filter;
            var candidate = new FilterCandidate(
                new string('a', 20000) + "!",
                DateTime.Now,
                1,
                string.Empty,
                false,
                null,
                FilterCopyStatus.NotImported);

            var error = CopyPostProcessing.TryRun(
                () => { },
                () => filter.Matches(candidate));

            Assert.Contains("フィルター評価エラー", error);
        }

        [Fact]
        public void UnexpectedRefreshFailureIsReturnedInsteadOfEscaping()
        {
            var error = CopyPostProcessing.TryRun(
                () => { },
                () => throw new InvalidOperationException("refresh failed"));

            Assert.Contains("一覧更新エラー", error);
            Assert.Contains("refresh failed", error);
        }
    }
}
