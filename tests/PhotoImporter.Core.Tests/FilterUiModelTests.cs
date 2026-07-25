using PhotoImporter.App;
using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using System;
using System.Collections.Generic;
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

        [Fact]
        public void ChangingField_ResetsFieldSpecificOptionsAndNotifiesBindingsOnce()
        {
            var editor = CreateEditor(FilterField.Sequence);
            editor.IncludeUnknown = true;
            editor.IncludeNoSequence = true;
            editor.IncludeRejectedRating = true;
            var changed = new List<string>();
            editor.PropertyChanged += (sender, e) => changed.Add(e.PropertyName);

            SelectField(editor, FilterField.Iso);

            Assert.False(editor.IncludeUnknown);
            Assert.False(editor.IncludeNoSequence);
            Assert.False(editor.IncludeRejectedRating);
            Assert.Equal(1, changed.Count(name => name == nameof(editor.IncludeUnknown)));
            Assert.Equal(1, changed.Count(name => name == nameof(editor.IncludeNoSequence)));
            Assert.Equal(1, changed.Count(name => name == nameof(editor.IncludeRejectedRating)));
            Assert.Equal(1, changed.Count(name => name == nameof(editor.IsValid)));
            Assert.Equal(1, changed.Count(name => name == nameof(editor.ValidationMessage)));
        }

        [Theory]
        [InlineData(FilterField.Sequence)]
        [InlineData(FilterField.Rating)]
        public void ChangingSpecialNumberFieldToIso_KeepsConditionValid(FilterField originalField)
        {
            var editor = CreateEditor(originalField);
            editor.MinimumText = "1";
            if (originalField == FilterField.Sequence) editor.IncludeNoSequence = true;
            else editor.IncludeRejectedRating = true;

            SelectField(editor, FilterField.Iso);

            Assert.True(editor.IsValid, editor.ValidationMessage);
            FilterCondition condition;
            string error;
            Assert.True(editor.TryBuild(out condition, out error), error);
            var numberCondition = Assert.IsType<NumberFilterCondition>(condition);
            Assert.False(numberCondition.IncludeNoSequence);
            Assert.False(numberCondition.IncludeRejectedRating);
        }

        [Fact]
        public void ExtensionEditor_AlwaysUsesCaseInsensitiveComparison()
        {
            var editor = CreateEditor(FilterField.OriginalName);
            editor.CaseSensitive = true;
            var changed = new List<string>();
            editor.PropertyChanged += (sender, e) => changed.Add(e.PropertyName);

            SelectField(editor, FilterField.Extension);
            editor.Pattern = ".ARW";
            editor.CaseSensitive = true;

            Assert.False(editor.CanUseCaseSensitivity);
            Assert.False(editor.CaseSensitive);
            Assert.Contains(nameof(editor.CaseSensitive), changed);
            var filter = Prepare(editor);
            Assert.True(filter.Matches(CreateCandidate("photo.ARW")));
            Assert.True(filter.Matches(CreateCandidate("photo.arw")));

            FilterCondition condition;
            string error;
            Assert.True(editor.TryBuild(out condition, out error), error);
            Assert.False(Assert.IsType<StringFilterCondition>(condition).CaseSensitive);
        }

        [Fact]
        public void OtherStringFields_CanUseCaseSensitiveComparison()
        {
            var editor = CreateEditor(FilterField.OriginalName);
            editor.Pattern = "photo.ARW";
            editor.CaseSensitive = true;

            Assert.True(editor.CanUseCaseSensitivity);
            Assert.True(editor.CaseSensitive);
            Assert.True(Prepare(editor).Matches(CreateCandidate("photo.ARW")));
            Assert.False(Prepare(editor).Matches(CreateCandidate("photo.arw")));
        }

        [Fact]
        public void UnsupportedSpecialNumberOption_HasActionableValidationMessage()
        {
            var editor = CreateEditor(FilterField.Iso);
            editor.MinimumText = "1";
            editor.IncludeNoSequence = true;

            FilterCondition condition;
            string error;
            Assert.False(editor.TryBuild(out condition, out error));
            Assert.Contains("連番なし", error);
            Assert.Contains("{Sequence}", error);
        }

        [Theory]
        [InlineData(FilterField.Extension)]
        [InlineData(FilterField.Protected)]
        [InlineData(FilterField.HasGps)]
        public void FieldsWithErrorStateUnknown_EnableUnknownOption(FilterField field)
        {
            var editor = CreateEditor(field);

            Assert.True(editor.CanIncludeUnknown);
        }

        [Fact]
        public void HasGpsEditor_CanBuildConditionsThatIncludeOrExcludeUnknown()
        {
            var editor = CreateEditor(FilterField.HasGps);
            editor.Choices.Single(item => Equals(item.Value, true)).IsSelected = true;
            var unsupported = new FilterCandidate(
                "photo.bin",
                new DateTime(2026, 7, 23),
                1,
                string.Empty,
                false,
                null,
                FilterCopyStatus.NotImported,
                PhotoMetadataReadResult.Unsupported());

            Assert.False(Prepare(editor).Matches(unsupported));

            editor.IncludeUnknown = true;

            Assert.True(Prepare(editor).Matches(unsupported));
        }

        [Fact]
        public void StringEditor_SummaryDescribesMatchModeValueAndOptions()
        {
            var editor = CreateEditor(FilterField.OriginalName);
            editor.SelectedStringMatchMode = editor.StringMatchModes.Single(
                item => item.Value == StringFilterMatchMode.Contains);
            editor.Pattern = "summer";
            editor.CaseSensitive = true;
            editor.IncludeUnknown = true;

            Assert.Contains("元ファイル名", editor.Summary);
            Assert.Contains("部分一致「summer」", editor.Summary);
            Assert.Contains("Unknownを含む", editor.Summary);
            Assert.Contains("大文字・小文字を区別", editor.Summary);
        }

        [Fact]
        public void NumberEditor_SummaryDescribesRangeAndSpecialValues()
        {
            var editor = CreateEditor(FilterField.Rating);
            editor.MinimumText = "3";
            editor.MaximumText = "5";
            editor.IncludeRejectedRating = true;

            Assert.Contains("3～5 または Rejected", editor.Summary);
        }

        [Fact]
        public void DateEditor_SummaryDescribesRangeAndTimeZone()
        {
            var editor = CreateEditor(FilterField.TakenDateInTimeZone);
            editor.StartDate = new DateTime(2026, 7, 1);
            editor.EndDate = new DateTime(2026, 7, 31);
            editor.TimeZoneSpecifier = "JST";

            Assert.Contains("2026/7/1～2026/7/31", editor.Summary);
            Assert.Contains("[JST]", editor.Summary);
        }

        [Fact]
        public void StateKey_ChangesWhenChoiceSelectionChanges()
        {
            var editor = CreateEditor(FilterField.FileType);
            var before = editor.StateKey;

            editor.Choices.Single(item => Equals(item.Value, PhotoFileType.Raw)).IsSelected = true;

            Assert.NotEqual(before, editor.StateKey);
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

        private static void SelectField(FilterConditionEditor editor, FilterField field) =>
            editor.SelectedField = editor.FieldOptions.Single(item => item.Field == field);

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
