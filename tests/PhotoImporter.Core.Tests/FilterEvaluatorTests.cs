using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using System;
using System.Collections.Generic;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class FilterEvaluatorTests
    {
        [Theory]
        [InlineData(StringFilterMatchMode.Exact, "photo.jpg", true)]
        [InlineData(StringFilterMatchMode.Exact, "photo", false)]
        [InlineData(StringFilterMatchMode.Contains, "OTO.J", true)]
        [InlineData(StringFilterMatchMode.Wildcard, "ph*.jp?", true)]
        [InlineData(StringFilterMatchMode.Wildcard, "*.png", false)]
        [InlineData(StringFilterMatchMode.RegularExpression, @"oto\.(jpg|png)$", true)]
        public void StringModesUseUnformattedOriginalValues(
            StringFilterMatchMode mode,
            string pattern,
            bool expected)
        {
            var filter = Prepare(new StringFilterCondition(FilterField.OriginalName, pattern, mode));

            Assert.Equal(expected, filter.Matches(CreateCandidate("Photo.JPG")));
        }

        [Fact]
        public void StringComparisonCanBeCaseSensitive()
        {
            var filter = Prepare(new StringFilterCondition(
                FilterField.OriginalName, "photo.jpg", StringFilterMatchMode.Exact, caseSensitive: true));

            Assert.False(filter.Matches(CreateCandidate("Photo.JPG")));
        }

        [Fact]
        public void InvalidRegularExpressionCannotBePrepared()
        {
            var result = new FilterSet(new[]
            {
                new StringFilterCondition(FilterField.OriginalName, "[", StringFilterMatchMode.RegularExpression)
            }).Prepare();

            Assert.False(result.IsValid);
            Assert.Equal(FilterValidationCode.InvalidRegularExpression, result.Errors[0].Code);
        }

        [Fact]
        public void RegularExpressionTimeoutIsReportedAsEvaluationError()
        {
            var filter = Prepare(new StringFilterCondition(
                FilterField.OriginalName,
                @"^(a+)+$",
                StringFilterMatchMode.RegularExpression,
                regexTimeout: TimeSpan.FromMilliseconds(1)));
            var candidate = CreateCandidate(new string('a', 20000) + "!");

            Assert.Throws<FilterEvaluationException>(() => filter.Matches(candidate));
        }

        [Fact]
        public void MultipleConditionsAreAndedAndChoicesAreOred()
        {
            var filter = Prepare(
                new ChoiceFilterCondition<PhotoFileType>(
                    FilterField.FileType, new[] { PhotoFileType.Jpeg, PhotoFileType.Raw }),
                new NumberFilterCondition(FilterField.FileSize, 100m, 200m));

            Assert.True(filter.Matches(CreateCandidate("a.NEF", fileSize: 100)));
            Assert.True(filter.Matches(CreateCandidate("a.jpg", fileSize: 200)));
            Assert.False(filter.Matches(CreateCandidate("a.png", fileSize: 150)));
            Assert.False(filter.Matches(CreateCandidate("a.jpg", fileSize: 201)));
        }

        [Fact]
        public void OneSidedNumericRangesIncludeTheirBoundaries()
        {
            var minimumOnly = Prepare(new NumberFilterCondition(FilterField.FileSize, 100, null));
            var maximumOnly = Prepare(new NumberFilterCondition(FilterField.FileSize, null, 100));

            Assert.True(minimumOnly.Matches(CreateCandidate(fileSize: 100)));
            Assert.False(minimumOnly.Matches(CreateCandidate(fileSize: 99)));
            Assert.True(maximumOnly.Matches(CreateCandidate(fileSize: 100)));
            Assert.False(maximumOnly.Matches(CreateCandidate(fileSize: 101)));
        }

        [Fact]
        public void EmptySetMatchesEveryCandidate()
        {
            var result = new FilterSet(new FilterCondition[0]).Prepare();

            Assert.True(result.IsValid);
            Assert.False(result.Filter.RequiresExif);
            Assert.True(result.Filter.Matches(CreateCandidate("anything.bin")));
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, true)]
        [InlineData(false, false, false)]
        public void UnknownHandlingIsIndependentOfIncludeOrExcludeMode(
            bool includeMatches,
            bool includeUnknown,
            bool expected)
        {
            var filter = Prepare(new StringFilterCondition(
                FilterField.CameraMake,
                "Sony",
                StringFilterMatchMode.Exact,
                includeMatches: includeMatches,
                includeUnknown: includeUnknown));
            var candidate = CreateCandidate(metadataResult: PhotoMetadataReadResult.NoMetadata());

            Assert.Equal(expected, filter.Matches(candidate));
        }

        [Theory]
        [InlineData(true, "Sony", true)]
        [InlineData(true, "Canon", false)]
        [InlineData(false, "Sony", false)]
        [InlineData(false, "Canon", true)]
        public void IncludeOrExcludeModeAppliesToKnownValues(
            bool includeMatches,
            string make,
            bool expected)
        {
            var filter = Prepare(new StringFilterCondition(
                FilterField.CameraMake,
                "Sony",
                StringFilterMatchMode.Exact,
                includeMatches: includeMatches));

            Assert.Equal(expected, filter.Matches(CreateCandidate(metadataResult: Metadata(cameraMake: make))));
        }

        [Fact]
        public void LiteralUnknownIsAKnownString()
        {
            var filter = Prepare(new StringFilterCondition(
                FilterField.CameraMake, "Unknown", StringFilterMatchMode.Exact));

            Assert.True(filter.Matches(CreateCandidate(metadataResult: Metadata(cameraMake: "Unknown"))));
            Assert.False(filter.Matches(CreateCandidate(metadataResult: PhotoMetadataReadResult.NoMetadata())));
        }

        [Fact]
        public void EmptyRelativeDirectoryIsAKnownString()
        {
            var filter = Prepare(new StringFilterCondition(
                FilterField.SourceRelativeDirectory, string.Empty, StringFilterMatchMode.Exact));

            Assert.True(filter.Matches(CreateCandidate(relativeDirectory: string.Empty)));
        }

        [Fact]
        public void ExtensionChoicesAreCaseInsensitive()
        {
            var filter = Prepare(new ChoiceFilterCondition<string>(
                FilterField.Extension, new[] { ".JPG", ".NEF" }));

            Assert.True(filter.Matches(CreateCandidate("photo.jpg")));
            Assert.True(filter.Matches(CreateCandidate("photo.NEF")));
            Assert.False(filter.Matches(CreateCandidate("photo.png")));
        }

        [Fact]
        public void ExifUnreadIsNotUnknownAndCannotBeEvaluated()
        {
            var filter = Prepare(new StringFilterCondition(
                FilterField.CameraMake,
                "Sony",
                StringFilterMatchMode.Exact,
                includeUnknown: true));

            Assert.True(filter.RequiresExif);
            Assert.Throws<FilterEvaluationException>(() => filter.Matches(CreateCandidate()));
        }

        [Fact]
        public void TakenDateDoesNotFallBackToModifiedDate()
        {
            var filter = Prepare(new DateTimeFilterCondition(
                FilterField.TakenDate,
                new DateTime(2026, 7, 1),
                new DateTime(2026, 7, 31),
                includeUnknown: false));
            var candidate = CreateCandidate(
                modifiedDate: new DateTime(2026, 7, 15),
                metadataResult: Metadata());

            Assert.False(filter.Matches(candidate));
        }

        [Fact]
        public void DateOnlyEndIncludesTheWholeLastDay()
        {
            var filter = Prepare(DateTimeFilterCondition.ForDateRange(
                FilterField.ModifiedDate,
                new DateTime(2026, 7, 1),
                new DateTime(2026, 7, 22)));

            Assert.True(filter.Matches(CreateCandidate(modifiedDate: new DateTime(2026, 7, 22, 23, 59, 59))));
            Assert.False(filter.Matches(CreateCandidate(modifiedDate: new DateTime(2026, 7, 23, 0, 0, 0))));
        }

        [Fact]
        public void OneSidedDateRangesIncludeSpecifiedTimes()
        {
            var boundary = new DateTime(2026, 7, 22, 12, 30, 0);
            var minimumOnly = Prepare(new DateTimeFilterCondition(FilterField.ModifiedDate, boundary, null));
            var maximumOnly = Prepare(new DateTimeFilterCondition(FilterField.ModifiedDate, null, boundary));

            Assert.True(minimumOnly.Matches(CreateCandidate(modifiedDate: boundary)));
            Assert.False(minimumOnly.Matches(CreateCandidate(modifiedDate: boundary.AddTicks(-1))));
            Assert.True(maximumOnly.Matches(CreateCandidate(modifiedDate: boundary)));
            Assert.False(maximumOnly.Matches(CreateCandidate(modifiedDate: boundary.AddTicks(1))));
        }

        [Fact]
        public void TakenDateInTimeZoneUsesExistingTemplateTimeZoneRules()
        {
            var filter = Prepare(new DateTimeFilterCondition(
                FilterField.TakenDateInTimeZone,
                new DateTime(2026, 7, 22, 12, 0, 0),
                new DateTime(2026, 7, 22, 12, 0, 0),
                timeZoneSpecifier: "UTC+09:00"));
            var metadata = Metadata(
                takenDate: new DateTime(2026, 7, 22, 3, 0, 0),
                offset: TimeSpan.Zero,
                offsetState: TakenDateOffsetState.Valid);

            Assert.True(filter.Matches(CreateCandidate(metadataResult: metadata)));
        }

        [Fact]
        public void TimeZoneIsRequiredForTakenDateInTimeZone()
        {
            var result = new FilterSet(new[]
            {
                new DateTimeFilterCondition(
                    FilterField.TakenDateInTimeZone, new DateTime(2026, 1, 1), null)
            }).Prepare();

            Assert.False(result.IsValid);
            Assert.Equal(FilterValidationCode.TimeZoneRequired, result.Errors[0].Code);
        }

        [Fact]
        public void OrientedDimensionsAndExposureUseTypedNumbers()
        {
            var metadata = Metadata(
                decodedWidth: 6000,
                decodedHeight: 4000,
                orientation: 6,
                exposure: new ExifRational(1, 250));
            var filter = Prepare(
                new NumberFilterCondition(FilterField.Width, 4000, 4000),
                new NumberFilterCondition(FilterField.Height, 6000, 6000),
                new NumberFilterCondition(FilterField.ShutterSpeed, 0.004m, 0.004m));

            Assert.True(filter.Matches(CreateCandidate(metadataResult: metadata)));
        }

        [Fact]
        public void IncompleteDimensionPairIsUnknown()
        {
            var filter = Prepare(new NumberFilterCondition(
                FilterField.Width, 1, null, includeUnknown: true));
            var metadata = Metadata(decodedWidth: 6000);

            Assert.True(filter.Matches(CreateCandidate(metadataResult: metadata)));
        }

        [Fact]
        public void RatingRangeCanBeOredWithRejected()
        {
            var filter = Prepare(new NumberFilterCondition(
                FilterField.Rating, 4, 5, includeRejectedRating: true));

            Assert.True(filter.Matches(CreateCandidate(metadataResult: Metadata(rating: 4))));
            Assert.True(filter.Matches(CreateCandidate(metadataResult: Metadata(rating: -1))));
            Assert.False(filter.Matches(CreateCandidate(metadataResult: Metadata(rating: 3))));
        }

        [Fact]
        public void SequenceNoneIsKnownAndCanBeOredWithRange()
        {
            var filter = Prepare(new NumberFilterCondition(
                FilterField.Sequence, 2, 3, includeNoSequence: true));

            Assert.True(filter.Matches(CreateCandidate(sequenceNumber: null)));
            Assert.True(filter.Matches(CreateCandidate(sequenceNumber: 2)));
            Assert.False(filter.Matches(CreateCandidate(sequenceNumber: 1)));
        }

        [Theory]
        [InlineData("1 B", 1L)]
        [InlineData("1.5 KiB", 1536L)]
        [InlineData("2mib", 2097152L)]
        [InlineData("1 GiB", 1073741824L)]
        public void FileSizeParserUsesBinaryUnits(string input, long expected)
        {
            long actual;
            Assert.True(FileSizeFilterParser.TryParseBytes(input, out actual));
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("-1 B")]
        [InlineData("0.1 B")]
        [InlineData("1 MB")]
        [InlineData("79228162514264337593543950335 GiB")]
        [InlineData("")]
        public void FileSizeParserRejectsInvalidValues(string input)
        {
            long ignored;
            Assert.False(FileSizeFilterParser.TryParseBytes(input, out ignored));
        }

        [Fact]
        public void ExifStatusChoicesAreOred()
        {
            var filter = Prepare(new ChoiceFilterCondition<FilterExifReadStatus>(
                FilterField.ExifReadStatus,
                new[] { FilterExifReadStatus.NoMetadata, FilterExifReadStatus.ReadError }));

            Assert.True(filter.Matches(CreateCandidate(metadataResult: PhotoMetadataReadResult.NoMetadata())));
            Assert.True(filter.Matches(CreateCandidate(metadataResult:
                PhotoMetadataReadResult.ReadError(new InvalidOperationException()))));
            Assert.False(filter.Matches(CreateCandidate(metadataResult: Metadata(cameraMake: "Sony"))));
        }

        [Fact]
        public void NoGpsIsKnownAfterExifWasRead()
        {
            var filter = Prepare(new ChoiceFilterCondition<bool>(FilterField.HasGps, new[] { false }));

            Assert.True(filter.Matches(CreateCandidate(metadataResult: PhotoMetadataReadResult.NoMetadata())));
        }

        [Fact]
        public void SharedPairExifDoesNotShareFileSystemValues()
        {
            var sharedMetadata = Metadata(cameraMake: "Sony", rating: 5);
            var filter = Prepare(
                new StringFilterCondition(FilterField.CameraMake, "Sony", StringFilterMatchMode.Exact),
                new ChoiceFilterCondition<PhotoFileType>(FilterField.FileType, new[] { PhotoFileType.Raw }),
                new NumberFilterCondition(FilterField.FileSize, 200, 200));

            Assert.True(filter.Matches(CreateCandidate("pair.NEF", fileSize: 200, metadataResult: sharedMetadata)));
            Assert.False(filter.Matches(CreateCandidate("pair.JPG", fileSize: 100, metadataResult: sharedMetadata)));
        }

        [Fact]
        public void EveryFieldHasATypeDefinition()
        {
            foreach (FilterField field in Enum.GetValues(typeof(FilterField)))
                Assert.Equal(field, FilterFieldDefinition.Get(field).Field);

            Assert.True(FilterFieldDefinition.Get(FilterField.CameraModel).RequiresExif);
            Assert.False(FilterFieldDefinition.Get(FilterField.FileSize).RequiresExif);
            Assert.False(FilterFieldDefinition.Get(FilterField.ExifReadStatus).CanBeUnknown);
            Assert.False(FilterFieldDefinition.Get(FilterField.Extension).CanBeUnknown);
            Assert.False(FilterFieldDefinition.Get(FilterField.Protected).CanBeUnknown);
            Assert.False(FilterFieldDefinition.Get(FilterField.HasGps).CanBeUnknown);
        }

        [Fact]
        public void InvalidRangesAndEmptyChoicesCannotBePrepared()
        {
            var result = new FilterSet(new FilterCondition[]
            {
                new NumberFilterCondition(FilterField.FileSize, 2, 1),
                new ChoiceFilterCondition<FilterCopyStatus>(FilterField.CopyStatus, new FilterCopyStatus[0])
            }).Prepare();

            Assert.False(result.IsValid);
            Assert.Equal(2, result.Errors.Count);
            Assert.Equal(FilterValidationCode.MinimumExceedsMaximum, result.Errors[0].Code);
            Assert.Equal(FilterValidationCode.NoChoices, result.Errors[1].Code);
        }

        [Fact]
        public void UnknownCannotBeSelectedForFieldsThatAlwaysHaveAValue()
        {
            var result = new FilterSet(new FilterCondition[]
            {
                new ChoiceFilterCondition<bool>(
                    FilterField.HasGps, new[] { true }, includeUnknown: true)
            }).Prepare();

            Assert.False(result.IsValid);
            Assert.Equal(FilterValidationCode.OptionNotSupported, result.Errors[0].Code);
        }

        private static PreparedFilter Prepare(params FilterCondition[] conditions)
        {
            var result = new FilterSet(conditions).Prepare();
            Assert.True(result.IsValid, result.Errors.Count == 0 ? null : result.Errors[0].Message);
            return result.Filter;
        }

        private static FilterCandidate CreateCandidate(
            string originalName = "photo.jpg",
            DateTime? modifiedDate = null,
            long? fileSize = 100,
            string relativeDirectory = "DCIM",
            bool? isProtected = false,
            int? sequenceNumber = null,
            PhotoMetadataReadResult metadataResult = null)
        {
            return new FilterCandidate(
                originalName,
                modifiedDate ?? new DateTime(2026, 7, 22, 10, 0, 0),
                fileSize,
                relativeDirectory,
                isProtected,
                sequenceNumber,
                FilterCopyStatus.NotImported,
                metadataResult);
        }

        private static PhotoMetadataReadResult Metadata(
            DateTime? takenDate = null,
            TimeSpan? offset = null,
            TakenDateOffsetState offsetState = TakenDateOffsetState.Missing,
            string cameraMake = null,
            int? decodedWidth = null,
            int? decodedHeight = null,
            int? orientation = null,
            ExifRational? exposure = null,
            int? rating = null)
        {
            var metadata = new PhotoMetadata(
                takenDate,
                offset,
                offsetState,
                cameraMake,
                null,
                null,
                decodedWidth: decodedWidth,
                decodedHeight: decodedHeight,
                orientation: orientation,
                exposureTime: exposure,
                rating: rating);
            return metadata.HasValues
                ? PhotoMetadataReadResult.Success(metadata)
                : PhotoMetadataReadResult.NoMetadata();
        }
    }

    public sealed class PhotoFileClassifierTests
    {
        [Theory]
        [InlineData("photo.JPG", PhotoFileType.Jpeg, ".jpg")]
        [InlineData("photo.NEF", PhotoFileType.Raw, ".nef")]
        [InlineData("photo.HEIC", PhotoFileType.OtherImage, ".heic")]
        [InlineData("clip.MP4", PhotoFileType.Video, ".mp4")]
        [InlineData("README", PhotoFileType.Other, "")]
        [InlineData(".profile", PhotoFileType.Other, ".profile")]
        public void ClassifiesAndNormalizesExtensions(
            string path,
            PhotoFileType expectedType,
            string expectedExtension)
        {
            Assert.Equal(expectedType, PhotoFileClassifier.Classify(path));
            Assert.Equal(expectedExtension, PhotoFileClassifier.NormalizeExtension(path));
        }
    }
}
