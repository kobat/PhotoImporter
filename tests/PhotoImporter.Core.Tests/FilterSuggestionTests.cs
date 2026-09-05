using PhotoImporter.App;
using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    [Collection(JapaneseLocalizationCollection.Name)]
    public sealed class FilterSuggestionTests
    {
        [Fact]
        public void Catalog_GroupsDaysCountsAndCachesWithoutReadingExif()
        {
            var catalog = new FilterSuggestionCatalog(new[]
            {
                Candidate("a.jpg", new DateTime(2026, 9, 5, 1, 0, 0)),
                Candidate("b.jpg", new DateTime(2026, 9, 5, 23, 0, 0)),
                Candidate("c.nef", new DateTime(2026, 9, 6))
            });
            var dates = catalog.Get(FilterField.ModifiedDate);
            Assert.Equal(new DateTime(2026, 9, 6), dates.Values[0].Value);
            Assert.Equal(2, dates.Values[1].Count);
            Assert.Same(dates, catalog.Get(FilterField.ModifiedDate));
            Assert.Equal(3, catalog.Get(FilterField.ModifiedDate, exactTimes: true).Values.Count);
            var cameras = catalog.Get(FilterField.CameraModel);
            Assert.Empty(cameras.Values);
            Assert.Equal(3, cameras.UnreadCount);
            Assert.Equal(0, cameras.UnknownCount);
            Assert.Equal(2, catalog.Get(FilterField.Extension).Values.Single(item => Equals(item.Value, ".jpg")).Count);
        }

        [Fact]
        public void Catalog_DistinguishesMissingFromUnreadAndKeepsCaseVariants()
        {
            var catalog = new FilterSuggestionCatalog(new[]
            {
                Candidate("a.jpg", metadata: Metadata("Camera A")),
                Candidate("b.jpg", metadata: Metadata("camera a")),
                Candidate("c.jpg", metadata: PhotoMetadataReadResult.NoMetadata()),
                Candidate("d.jpg")
            });
            var cameras = catalog.Get(FilterField.CameraModel);
            Assert.Equal(2, cameras.Values.Count);
            Assert.Equal(1, cameras.UnknownCount);
            Assert.Equal(1, cameras.UnreadCount);
        }

        [Fact]
        public void Catalog_UsesFilterTimezoneAndSortsNumbersNumerically()
        {
            var metadata = PhotoMetadataReadResult.Success(new PhotoMetadata(
                new DateTime(2026, 9, 5, 23, 0, 0), TimeSpan.Zero, TakenDateOffsetState.Valid, null, null, null));
            var catalog = new FilterSuggestionCatalog(new[] { Candidate("a.jpg", metadata: metadata, size: 100), Candidate("b.jpg", size: 20) });
            Assert.Equal(new DateTime(2026, 9, 6), catalog.Get(FilterField.TakenDateInTimeZone, "JST").Values.Single().Value);
            Assert.Equal(new DateTime(2026, 9, 5), catalog.Get(FilterField.TakenDateInTimeZone, "UTC").Values.Single().Value);
            Assert.Equal(20m, catalog.Get(FilterField.FileSize).Values[0].Value);
            Assert.Throws<ArgumentException>(() => catalog.Get(FilterField.TakenDateInTimeZone, "invalid"));
            Assert.Throws<OperationCanceledException>(() => catalog.Get(FilterField.OriginalName, cancellationToken: new CancellationToken(true)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SelectedStrings_AreLiteralAlternativesWithExclusion(bool exclude)
        {
            var editor = Editor(FilterField.OriginalName);
            editor.SetSelectedValues(new[] { "a[1].jpg", "b*.jpg" });
            editor.SelectedTargetMode = editor.TargetModes.Single(item => item.Value == !exclude);
            var filter = Prepare(editor);
            Assert.Equal(!exclude, filter.Matches(Candidate("a[1].jpg")));
            Assert.Equal(!exclude, filter.Matches(Candidate("b*.jpg")));
            Assert.Equal(exclude, filter.Matches(Candidate("b2.jpg")));
        }

        [Fact]
        public void SelectedStrings_RespectCaseSensitivityAndAndAcrossConditions()
        {
            var editor = Editor(FilterField.OriginalName);
            editor.SetSelectedValues(new[] { "A.jpg", "B.jpg" });
            editor.CaseSensitive = true;
            FilterCondition choice;
            string error;
            Assert.True(editor.TryBuild(out choice, out error));
            var filter = new FilterSet(new[] { choice, new NumberFilterCondition(FilterField.FileSize, 10, null) }).Prepare().Filter;
            Assert.True(filter.Matches(Candidate("A.jpg", size: 10)));
            Assert.False(filter.Matches(Candidate("A.jpg", size: 9)));
            Assert.False(filter.Matches(Candidate("a.jpg", size: 10)));
        }

        [Fact]
        public void DraftEditsAreIsolatedAndFieldChangeClearsSelectedStrings()
        {
            var editor = Editor(FilterField.CameraModel);
            editor.Pattern = "original";
            editor.SetSelectedValues(new[] { "A", "B" });
            var before = editor.StateKey;
            var draft = editor.Clone();
            draft.SetSelectedValues(new[] { "C" });
            Assert.Equal(before, editor.StateKey);
            editor.CopyFrom(draft);
            Assert.Equal(new[] { "C" }, editor.SelectedValues);
            editor.SetSelectedValues(new string[0]);
            Assert.True(editor.IsStringInput);
            Assert.Equal("original", editor.Pattern);
            editor.SetSelectedValues(new[] { "D" });
            editor.SelectedField = editor.FieldOptions.Single(field => field.Field == FilterField.Lens);
            Assert.False(editor.UsesSelectedValues);
        }

        [Fact]
        public void DateSuggestionPreservesOtherBoundaryAndExactTicks()
        {
            var editor = Editor(FilterField.ModifiedDate);
            editor.StartDate = new DateTime(2026, 9, 1);
            editor.UseSuggestion(new DateTime(2026, 9, 5, 12, 0, 0), "Maximum");
            Assert.Equal(new DateTime(2026, 9, 1), editor.StartDate);
            Assert.True(Prepare(editor).Matches(Candidate("a.jpg", new DateTime(2026, 9, 5, 23, 59, 59).AddTicks(9999999))));
            Assert.False(Prepare(editor).Matches(Candidate("a.jpg", new DateTime(2026, 9, 6))));
            var exact = new DateTime(2026, 9, 5, 12, 0, 0).AddTicks(1234567);
            editor.UseSuggestion(exact, exactTime: true);
            Assert.True(Prepare(editor).Matches(Candidate("a.jpg", exact)));
            Assert.False(Prepare(editor).Matches(Candidate("a.jpg", exact.AddTicks(1))));
        }

        [Fact]
        public void NumericSuggestionRoundTripsUnderCommaDecimalCulture()
        {
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var editor = Editor(FilterField.Aperture);
                editor.UseSuggestion(2.8m);
                FilterCondition condition;
                string error;
                Assert.True(editor.TryBuild(out condition, out error), error);
                Assert.Equal(2.8m, ((NumberFilterCondition)condition).Minimum);
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }

        [Fact]
        public void SpecialOnlySuggestionsDoNotMatchOrdinaryValues()
        {
            var sequence = Editor(FilterField.Sequence);
            sequence.UseSuggestion(FilterSpecialValue.NoSequence);
            Assert.True(Prepare(sequence).Matches(Candidate("a.jpg")));
            Assert.False(Prepare(sequence).Matches(Candidate("b.jpg", sequence: 1)));
            var rating = Editor(FilterField.Rating);
            rating.UseSuggestion(-1m);
            Assert.True(Prepare(rating).Matches(Candidate("a.jpg", metadata: Metadata(rating: -1))));
            Assert.False(Prepare(rating).Matches(Candidate("a.jpg", metadata: Metadata(rating: 5))));
        }

        private static FilterConditionEditor Editor(FilterField field)
        {
            var fields = FilterFieldOption.CreateAll();
            return new FilterConditionEditor(fields) { SelectedField = fields.Single(item => item.Field == field) };
        }
        private static PreparedFilter Prepare(FilterConditionEditor editor)
        {
            FilterCondition condition;
            string error;
            Assert.True(editor.TryBuild(out condition, out error), error);
            return new FilterSet(new[] { condition }).Prepare().Filter;
        }
        private static PhotoMetadataReadResult Metadata(string camera = null, int? rating = null) =>
            PhotoMetadataReadResult.Success(new PhotoMetadata(null, null, TakenDateOffsetState.Missing, null, camera, null, rating: rating));
        private static FilterCandidate Candidate(string name, DateTime? modified = null, PhotoMetadataReadResult metadata = null, long size = 1, int? sequence = null) =>
            new FilterCandidate(name, modified, size, "", false, sequence, FilterCopyStatus.NotImported, metadata);
    }
}
