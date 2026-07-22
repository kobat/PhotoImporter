using PhotoImporter.App;
using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using System;
using System.Linq;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class FilterUiModelTests
    {
        [Fact]
        public void ChoiceEditor_RequiresASelectionAndBuildsTypedChoiceCondition()
        {
            var editor = CreateEditor(FilterField.FileType);
            Assert.False(editor.IsValid);

            editor.Choices.Single(item => Equals(item.Value, PhotoFileType.Raw)).IsSelected = true;

            FilterCondition condition;
            string error;
            Assert.True(editor.TryBuild(out condition, out error), error);
            var filter = new FilterSet(new[] { condition }).Prepare().Filter;
            Assert.True(filter.Matches(CreateCandidate("photo.nef")));
            Assert.False(filter.Matches(CreateCandidate("photo.jpg")));
        }

        [Fact]
        public void FileSizeEditor_ParsesBinaryUnits()
        {
            var editor = CreateEditor(FilterField.FileSize);
            editor.MinimumText = "2 KiB";
            editor.MaximumText = "3 KiB";

            var filter = Prepare(editor);

            Assert.True(filter.Matches(CreateCandidate("photo.jpg", 2048)));
            Assert.True(filter.Matches(CreateCandidate("photo.jpg", 3072)));
            Assert.False(filter.Matches(CreateCandidate("photo.jpg", 3073)));
        }

        [Fact]
        public void DateEditor_DateOnlyEndIncludesTheWholeDay()
        {
            var editor = CreateEditor(FilterField.ModifiedDate);
            editor.StartDate = new DateTime(2026, 7, 23);
            editor.EndDate = new DateTime(2026, 7, 23);

            var filter = Prepare(editor);

            Assert.True(filter.Matches(CreateCandidate("photo.jpg", modified: new DateTime(2026, 7, 23, 23, 59, 59))));
            Assert.False(filter.Matches(CreateCandidate("photo.jpg", modified: new DateTime(2026, 7, 24, 0, 0, 0))));
        }

        [Fact]
        public void DateEditor_OptionalTimeUsesInclusiveBoundary()
        {
            var editor = CreateEditor(FilterField.ModifiedDate);
            editor.EndDate = new DateTime(2026, 7, 23);
            editor.EndTimeText = "12:30:15";

            var filter = Prepare(editor);

            Assert.True(filter.Matches(CreateCandidate("photo.jpg", modified: new DateTime(2026, 7, 23, 12, 30, 15))));
            Assert.False(filter.Matches(CreateCandidate("photo.jpg", modified: new DateTime(2026, 7, 23, 12, 30, 16))));
        }

        [Fact]
        public void TimeWithoutDate_IsInvalid()
        {
            var editor = CreateEditor(FilterField.ModifiedDate);
            editor.StartTimeText = "10:00";

            Assert.False(editor.IsValid);
            Assert.Contains("日付", editor.ValidationMessage);
        }

        private static FilterConditionEditor CreateEditor(FilterField field)
        {
            var fields = FilterFieldOption.CreateAll();
            var editor = new FilterConditionEditor(fields)
            {
                SelectedField = fields.Single(item => item.Field == field)
            };
            return editor;
        }

        private static PreparedFilter Prepare(FilterConditionEditor editor)
        {
            FilterCondition condition;
            string error;
            Assert.True(editor.TryBuild(out condition, out error), error);
            return new FilterSet(new[] { condition }).Prepare().Filter;
        }

        private static FilterCandidate CreateCandidate(
            string name,
            long size = 1,
            DateTime? modified = null) =>
            new FilterCandidate(
                name,
                modified ?? new DateTime(2026, 7, 23),
                size,
                string.Empty,
                false,
                null,
                FilterCopyStatus.NotImported);
    }
}
