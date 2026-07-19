using PhotoImporter.App;
using PhotoImporter.Core.Copying;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Templates;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class PreviewSelectionStateTests
    {
        [Fact]
        public void NonCopyableItem_RejectsSelectionAndNotifiesBinding()
        {
            var item = CreateItem("imported.jpg", DestinationStatus.Imported, false);
            var notifications = new List<string>();
            item.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

            item.IsSelected = true;

            Assert.False(item.IsSelected);
            Assert.Contains(nameof(PreviewItem.IsSelected), notifications);
        }

        [Fact]
        public void SetCopyError_DeselectsAndPreventsReselection()
        {
            var item = CreateItem("failed.jpg", DestinationStatus.NotImported, true);

            item.SetCopyError("write failed");
            item.IsSelected = true;

            Assert.False(item.CanCopy);
            Assert.False(item.IsSelected);
            Assert.Contains("write failed", item.Status);
        }

        [Theory]
        [InlineData(DestinationStatus.NotImported, true, true)]
        [InlineData(DestinationStatus.Overwrite, true, true)]
        [InlineData(DestinationStatus.Imported, false, false)]
        [InlineData(DestinationStatus.Conflict, false, false)]
        public void Constructor_SelectsOnlyCopyableItems(
            DestinationStatus status,
            bool hasCopyPlan,
            bool expectedSelected)
        {
            var item = CreateItem("photo.jpg", status, hasCopyPlan);

            Assert.Equal(expectedSelected, item.IsSelected);
        }

        [Fact]
        public void RestoreAfterCopy_PreservesExcludedPendingItem()
        {
            var before = CreateItem("excluded.jpg", DestinationStatus.NotImported, true);
            before.IsSelected = false;
            var previous = PreviewSelectionState.Capture(new[] { before });
            var rescanned = CreateItem("excluded.jpg", DestinationStatus.NotImported, true);

            PreviewSelectionState.RestoreAfterCopy(
                new[] { rescanned },
                previous,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            Assert.False(rescanned.IsSelected);
        }

        [Fact]
        public void RestoreAfterCopy_DeselectsSuccessfulConflictAndErrorRows()
        {
            var before = new[]
            {
                CreateItem("copied.jpg", DestinationStatus.NotImported, true),
                CreateItem("conflict.jpg", DestinationStatus.NotImported, true),
                CreateItem("failed.jpg", DestinationStatus.NotImported, true)
            };
            var previous = PreviewSelectionState.Capture(before);
            var rescanned = new[]
            {
                CreateItem("copied.jpg", DestinationStatus.Imported, false),
                CreateItem("conflict.jpg", DestinationStatus.Conflict, false),
                CreateItem("failed.jpg", DestinationStatus.NotImported, true)
            };

            PreviewSelectionState.RestoreAfterCopy(
                rescanned,
                previous,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["failed.jpg"] = "copy failed"
                });

            Assert.All(rescanned, item => Assert.False(item.IsSelected));
            Assert.False(rescanned[2].CanCopy);
        }

        [Fact]
        public void GetSelectAllState_ReportsCheckedUncheckedAndIndeterminate()
        {
            var first = CreateItem("first.jpg", DestinationStatus.NotImported, true);
            var second = CreateItem("second.jpg", DestinationStatus.NotImported, true);
            var imported = CreateItem("imported.jpg", DestinationStatus.Imported, false);

            Assert.True(PreviewSelectionState.GetSelectAllState(new[] { first, second, imported }));

            second.IsSelected = false;
            Assert.Null(PreviewSelectionState.GetSelectAllState(new[] { first, second, imported }));

            first.IsSelected = false;
            Assert.False(PreviewSelectionState.GetSelectAllState(new[] { first, second, imported }));
        }

        [Fact]
        public void SetAllCopyable_ChangesOnlyCopyableItems()
        {
            var first = CreateItem("first.jpg", DestinationStatus.NotImported, true);
            var second = CreateItem("second.jpg", DestinationStatus.Overwrite, true);
            var imported = CreateItem("imported.jpg", DestinationStatus.Imported, false);

            PreviewSelectionState.SetAllCopyable(new[] { first, second, imported }, false);

            Assert.False(first.IsSelected);
            Assert.False(second.IsSelected);
            Assert.False(imported.IsSelected);

            PreviewSelectionState.SetAllCopyable(new[] { first, second, imported }, true);

            Assert.True(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.False(imported.IsSelected);
        }

        private static PreviewItem CreateItem(
            string sourcePath,
            DestinationStatus status,
            bool hasCopyPlan)
        {
            var plan = hasCopyPlan
                ? new CopyPlanItem(
                    @"C:\source\" + sourcePath,
                    @"C:\destination",
                    @"C:\destination\" + sourcePath,
                    new FileSnapshot(1, DateTime.UtcNow),
                    status == DestinationStatus.Overwrite
                        ? new DestinationFileSnapshot(2, DateTime.UtcNow.AddMinutes(-1))
                        : null,
                    FileSystemTimestampPolicy.Create("NTFS"),
                    status == DestinationStatus.Overwrite)
                : null;
            return new PreviewItem(sourcePath, sourcePath, status, plan);
        }
    }
}
