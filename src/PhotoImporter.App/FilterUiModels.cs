using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace PhotoImporter.App
{
    public sealed class DisplayOption<T>
    {
        public DisplayOption(T value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public T Value { get; }
        public string DisplayName { get; }
    }

    public sealed class FilterFieldOption
    {
        public FilterFieldOption(FilterField field, string displayName)
        {
            Field = field;
            DisplayName = displayName;
        }

        public FilterField Field { get; }
        public string DisplayName { get; }
        public string ExifGroup => FilterFieldDefinition.Get(Field).RequiresExif
            ? AppLocalization.Text("Exif読込が必要", "Exif reading required")
            : AppLocalization.Text("Exif読込不要", "No Exif reading required");

        public static IReadOnlyList<FilterFieldOption> CreateAll() => new[]
        {
            Option(FilterField.FileType, "ファイル種別", "File type"),
            Option(FilterField.Extension, "{Extension} 拡張子", "{Extension} Extension"),
            Option(FilterField.CopyStatus, "コピー計画の状態", "Copy plan status"),
            Option(FilterField.ExifReadStatus, "Exif読込状態", "Exif read status"),
            Option(FilterField.OriginalName, "{OriginalName} 元ファイル名", "{OriginalName} Original file name"),
            Option(FilterField.FileName, "{FileName} 拡張子なしファイル名", "{FileName} File name without extension"),
            Option(FilterField.SourceRelativeDirectory, "{SourceRelativeDirectory} コピー元相対フォルダー", "{SourceRelativeDirectory} Source-relative folder"),
            Option(FilterField.ModifiedDate, "{ModifiedDate} 更新日時", "{ModifiedDate} Modified date"),
            Option(FilterField.FileSize, "{FileSize} ファイルサイズ", "{FileSize} File size"),
            Option(FilterField.Protected, "{Protected} 読み取り専用", "{Protected} Read-only"),
            Option(FilterField.Sequence, "{Sequence} 連番", "{Sequence} Sequence"),
            Option(FilterField.TakenDate, "{TakenDate} Exif記録日時", "{TakenDate} Exif recorded date"),
            Option(FilterField.TakenDateLocal, "{TakenDateLocal} PCタイムゾーン", "{TakenDateLocal} PC time zone"),
            Option(FilterField.TakenDateInTimeZone, "{TakenDateInTimeZone} 指定タイムゾーン", "{TakenDateInTimeZone} Specified time zone"),
            Option(FilterField.CameraMake, "{CameraMake} メーカー", "{CameraMake} Manufacturer"),
            Option(FilterField.CameraModel, "{CameraModel} カメラ", "{CameraModel} Camera"),
            Option(FilterField.CameraSerial, "{CameraSerial} シリアル番号", "{CameraSerial} Serial number"),
            Option(FilterField.Lens, "{Lens} レンズ", "{Lens} Lens"),
            Option(FilterField.Width, "{Width} 向き反映後の幅", "{Width} Oriented width"),
            Option(FilterField.Height, "{Height} 向き反映後の高さ", "{Height} Oriented height"),
            Option(FilterField.ExifWidth, "{ExifWidth} Exif幅", "{ExifWidth} Exif width"),
            Option(FilterField.ExifHeight, "{ExifHeight} Exif高さ", "{ExifHeight} Exif height"),
            Option(FilterField.Orientation, "{Orientation} 向き", "{Orientation} Orientation"),
            Option(FilterField.Aperture, "{Aperture} 絞り", "{Aperture} Aperture"),
            Option(FilterField.ShutterSpeed, "{ShutterSpeed} シャッター秒数", "{ShutterSpeed} Shutter speed"),
            Option(FilterField.ExposureTime, "{ExposureTime} 露光秒数", "{ExposureTime} Exposure time"),
            Option(FilterField.Iso, "{Iso} ISO", "{Iso} ISO"),
            Option(FilterField.FocalLength, "{FocalLength} 焦点距離", "{FocalLength} Focal length"),
            Option(FilterField.FocalLength35mm, "{FocalLength35mm} 35mm換算焦点距離", "{FocalLength35mm} 35mm-equivalent focal length"),
            Option(FilterField.Rating, "{Rating} 評価", "{Rating} Rating"),
            Option(FilterField.HasGps, "{HasGps} GPS有無", "{HasGps} GPS availability"),
            Option(FilterField.GpsLatitude, "{GpsLatitude} 緯度", "{GpsLatitude} Latitude"),
            Option(FilterField.GpsLongitude, "{GpsLongitude} 経度", "{GpsLongitude} Longitude"),
            Option(FilterField.GpsAltitude, "{GpsAltitude} 高度", "{GpsAltitude} Altitude")
        };

        private static FilterFieldOption Option(FilterField field, string japanese, string english) =>
            new FilterFieldOption(field, AppLocalization.Text(japanese, english));
    }

    public sealed class FilterChoiceOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public FilterChoiceOption(object value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public object Value { get; }
        public string DisplayName { get; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public sealed class FilterConditionEditor : INotifyPropertyChanged
    {
        private readonly IReadOnlyList<FilterFieldOption> _fieldOptions;
        private FilterFieldOption _selectedField;
        private DisplayOption<StringFilterMatchMode> _selectedStringMatchMode;
        private DisplayOption<bool> _selectedTargetMode;
        private string _pattern = string.Empty;
        private string _minimumText = string.Empty;
        private string _maximumText = string.Empty;
        private DateTime? _startDate;
        private DateTime? _endDate;
        private string _startTimeText = string.Empty;
        private string _endTimeText = string.Empty;
        private string _timeZoneSpecifier = "JST";
        private bool _caseSensitive;
        private bool _includeUnknown;
        private bool _includeNoSequence;
        private bool _includeRejectedRating;
        private string[] _selectedValues = new string[0];
        public IReadOnlyList<string> SelectedValues => _selectedValues;
        public bool UsesSelectedValues => _selectedValues.Length != 0;
        public bool IsStringInput => IsString && !UsesSelectedValues;
        public string SuggestionButtonText => UsesSelectedValues
            ? AppLocalization.Text("候補を変更...", "Change choices...")
            : AppLocalization.Text("候補から選択...", "Choose from scan...");
        public string ManualInputButtonText => AppLocalization.Text("文字列入力に戻す", "Use text input");
        public string SelectedValuesSummary => string.Join(AppLocalization.Text(" または ", " or "), _selectedValues);

        public FilterConditionEditor Clone()
        {
            var copy = new FilterConditionEditor(_fieldOptions);
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(FilterConditionEditor source)
        {
            SelectedField = source.SelectedField;
            SelectedStringMatchMode = StringMatchModes.Single(option => option.Value == source.SelectedStringMatchMode.Value);
            SelectedTargetMode = TargetModes.Single(option => option.Value == source.SelectedTargetMode.Value);
            Pattern = source.Pattern;
            MinimumText = source.MinimumText; MaximumText = source.MaximumText;
            StartDate = source.StartDate; EndDate = source.EndDate;
            StartTimeText = source.StartTimeText; EndTimeText = source.EndTimeText;
            TimeZoneSpecifier = source.TimeZoneSpecifier;
            CaseSensitive = source.CaseSensitive;
            IncludeUnknown = source.IncludeUnknown;
            IncludeNoSequence = source.IncludeNoSequence;
            IncludeRejectedRating = source.IncludeRejectedRating;
            foreach (var choice in Choices)
                choice.IsSelected = source.Choices.Any(item => Equals(item.Value, choice.Value) && item.IsSelected);
            SetSelectedValues(source.SelectedValues);
        }

        public void SetSelectedValues(IEnumerable<string> values)
        {
            _selectedValues = values.Distinct(StringComparer.Ordinal).ToArray();
            OnPropertyChanged(nameof(UsesSelectedValues));
            OnPropertyChanged(nameof(IsStringInput));
            OnPropertyChanged(nameof(SelectedValuesSummary));
            OnPropertyChanged(nameof(SuggestionButtonText));
            NotifyValidation();
        }

        public void UseSuggestion(object value, string boundary = "Equal", bool exactTime = false)
        {
            if (IsString) { SetSelectedValues(new[] { (string)value }); return; }
            if (IsChoice)
            {
                foreach (var choice in Choices) choice.IsSelected = Equals(choice.Value, value);
                return;
            }
            if (IsDateTime)
            {
                var date = (DateTime)value;
                var time = exactTime ? date.ToString(date.Ticks % TimeSpan.TicksPerSecond == 0 ? "HH:mm:ss" : "HH:mm:ss.fffffff", CultureInfo.InvariantCulture) : string.Empty;
                if (boundary != "Maximum") { StartDate = date.Date; StartTimeText = time; }
                if (boundary != "Minimum") { EndDate = date.Date; EndTimeText = time; }
                return;
            }
            IncludeNoSequence = value is FilterSpecialValue;
            IncludeRejectedRating = IsRating && Equals(value, -1m);
            if (IncludeNoSequence || IncludeRejectedRating)
            {
                MinimumText = MaximumText = string.Empty;
                return;
            }
            var text = Convert.ToString(value, SelectedField.Field == FilterField.FileSize ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture);
            if (boundary != "Maximum") MinimumText = text;
            if (boundary != "Minimum") MaximumText = text;
        }

        public FilterConditionEditor(IReadOnlyList<FilterFieldOption> fieldOptions)
        {
            _fieldOptions = fieldOptions ?? throw new ArgumentNullException(nameof(fieldOptions));
            GroupedFieldOptions = new ListCollectionView(fieldOptions.ToList());
            GroupedFieldOptions.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FilterFieldOption.ExifGroup)));
            StringMatchModes = new[]
            {
                new DisplayOption<StringFilterMatchMode>(StringFilterMatchMode.Exact, AppLocalization.Text("完全一致", "Exact match")),
                new DisplayOption<StringFilterMatchMode>(StringFilterMatchMode.Contains, AppLocalization.Text("部分一致", "Contains")),
                new DisplayOption<StringFilterMatchMode>(StringFilterMatchMode.Wildcard, AppLocalization.Text("ワイルドカード", "Wildcard")),
                new DisplayOption<StringFilterMatchMode>(StringFilterMatchMode.RegularExpression, AppLocalization.Text("正規表現", "Regular expression"))
            };
            TargetModes = new[]
            {
                new DisplayOption<bool>(true, AppLocalization.Text("対象にする", "Include")),
                new DisplayOption<bool>(false, AppLocalization.Text("対象から外す", "Exclude"))
            };
            _selectedStringMatchMode = StringMatchModes[0];
            _selectedTargetMode = TargetModes[0];
            SelectedField = _fieldOptions[0];
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public IReadOnlyList<FilterFieldOption> FieldOptions => _fieldOptions;
        public ListCollectionView GroupedFieldOptions { get; }
        public IReadOnlyList<DisplayOption<StringFilterMatchMode>> StringMatchModes { get; }
        public IReadOnlyList<DisplayOption<bool>> TargetModes { get; }
        public ObservableCollection<FilterChoiceOption> Choices { get; } = new ObservableCollection<FilterChoiceOption>();

        public FilterFieldOption SelectedField
        {
            get => _selectedField;
            set
            {
                if (_selectedField == value || value == null) return;
                _selectedField = value;
                _selectedValues = new string[0];

                var includeUnknownChanged = _includeUnknown;
                var includeNoSequenceChanged = _includeNoSequence;
                var includeRejectedRatingChanged = _includeRejectedRating;
                var caseSensitiveChanged = _caseSensitive && !CanUseCaseSensitivity;

                _includeUnknown = false;
                _includeNoSequence = false;
                _includeRejectedRating = false;
                if (caseSensitiveChanged) _caseSensitive = false;

                RebuildChoices();
                if (includeUnknownChanged) OnPropertyChanged(nameof(IncludeUnknown));
                if (includeNoSequenceChanged) OnPropertyChanged(nameof(IncludeNoSequence));
                if (includeRejectedRatingChanged) OnPropertyChanged(nameof(IncludeRejectedRating));
                if (caseSensitiveChanged) OnPropertyChanged(nameof(CaseSensitive));
                NotifyAll();
            }
        }

        public DisplayOption<StringFilterMatchMode> SelectedStringMatchMode
        {
            get => _selectedStringMatchMode;
            set { if (Set(ref _selectedStringMatchMode, value)) NotifyValidation(); }
        }

        public DisplayOption<bool> SelectedTargetMode
        {
            get => _selectedTargetMode;
            set { if (Set(ref _selectedTargetMode, value)) NotifyValidation(); }
        }

        public string Pattern { get => _pattern; set { if (Set(ref _pattern, value ?? string.Empty)) NotifyValidation(); } }
        public string MinimumText { get => _minimumText; set { if (Set(ref _minimumText, value ?? string.Empty)) NotifyValidation(); } }
        public string MaximumText { get => _maximumText; set { if (Set(ref _maximumText, value ?? string.Empty)) NotifyValidation(); } }
        public DateTime? StartDate { get => _startDate; set { if (Set(ref _startDate, value)) NotifyValidation(); } }
        public DateTime? EndDate { get => _endDate; set { if (Set(ref _endDate, value)) NotifyValidation(); } }
        public string StartTimeText { get => _startTimeText; set { if (Set(ref _startTimeText, value ?? string.Empty)) NotifyValidation(); } }
        public string EndTimeText { get => _endTimeText; set { if (Set(ref _endTimeText, value ?? string.Empty)) NotifyValidation(); } }
        public string TimeZoneSpecifier { get => _timeZoneSpecifier; set { if (Set(ref _timeZoneSpecifier, value ?? string.Empty)) NotifyValidation(); } }
        public bool CaseSensitive
        {
            get => _caseSensitive;
            set
            {
                var supportedValue = CanUseCaseSensitivity && value;
                if (Set(ref _caseSensitive, supportedValue)) NotifyValidation();
            }
        }
        public bool IncludeUnknown { get => _includeUnknown; set { if (Set(ref _includeUnknown, value)) NotifyValidation(); } }
        public bool IncludeNoSequence { get => _includeNoSequence; set { if (Set(ref _includeNoSequence, value)) NotifyValidation(); } }
        public bool IncludeRejectedRating { get => _includeRejectedRating; set { if (Set(ref _includeRejectedRating, value)) NotifyValidation(); } }

        public FilterValueType ValueType => FilterFieldDefinition.Get(SelectedField.Field).ValueType;
        public bool IsString => ValueType == FilterValueType.String;
        public bool IsNumber => ValueType == FilterValueType.Number;
        public bool IsDateTime => ValueType == FilterValueType.DateTime;
        public bool IsChoice => ValueType == FilterValueType.Choice || ValueType == FilterValueType.Boolean;
        public bool IsTimeZoneDate => SelectedField.Field == FilterField.TakenDateInTimeZone;
        public bool IsSequence => SelectedField.Field == FilterField.Sequence;
        public bool IsRating => SelectedField.Field == FilterField.Rating;
        public bool CanUseCaseSensitivity => IsString && SelectedField.Field != FilterField.Extension;
        public bool CanIncludeUnknown => FilterFieldDefinition.Get(SelectedField.Field).CanBeUnknown;
        public bool IsValid { get { FilterCondition condition; string error; return TryBuild(out condition, out error); } }
        public string ValidationMessage { get { FilterCondition condition; string error; return TryBuild(out condition, out error) ? string.Empty : error; } }
        public string Summary
        {
            get
            {
                var value = BuildValueSummary();
                var options = new List<string>();
                if (IncludeUnknown) options.Add(AppLocalization.Text("Unknownを含む", "include Unknown"));
                if (CaseSensitive && CanUseCaseSensitivity) options.Add(AppLocalization.Text("大文字・小文字を区別", "match case"));
                if (SelectedTargetMode != null && !SelectedTargetMode.Value) options.Add(AppLocalization.Text("一致項目を除外", "exclude matches"));
                return SelectedField.DisplayName + ": " + value +
                       (options.Count == 0 ? string.Empty : AppLocalization.IsEnglish
                           ? " (" + string.Join(", ", options) + ")"
                           : "（" + string.Join("、", options) + "）");
            }
        }

        internal string StateKey => string.Join("\u001f", new[]
        {
            SelectedField.Field.ToString(),
            SelectedStringMatchMode?.Value.ToString() ?? string.Empty,
            SelectedTargetMode?.Value.ToString() ?? string.Empty,
            Pattern,
            string.Join("", _selectedValues.Select(value => value.Length + ":" + value)),
            MinimumText,
            MaximumText,
            StartDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            EndDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            StartTimeText,
            EndTimeText,
            TimeZoneSpecifier,
            CaseSensitive.ToString(),
            IncludeUnknown.ToString(),
            IncludeNoSequence.ToString(),
            IncludeRejectedRating.ToString(),
            string.Join(",", Choices.Select(item =>
                Convert.ToString(item.Value, CultureInfo.InvariantCulture) + "=" + item.IsSelected))
        });

        public bool TryBuild(out FilterCondition condition, out string error)
        {
            condition = null;
            error = null;
            var field = SelectedField.Field;
            var includeMatches = SelectedTargetMode == null || SelectedTargetMode.Value;
            try
            {
                switch (ValueType)
                {
                    case FilterValueType.String:
                        if (UsesSelectedValues)
                        {
                            condition = new ChoiceFilterCondition<string>(field, _selectedValues,
                                includeMatches, IncludeUnknown, field != FilterField.Extension && CaseSensitive);
                            break;
                        }
                        condition = new StringFilterCondition(
                            field, Pattern, SelectedStringMatchMode.Value,
                            field != FilterField.Extension && CaseSensitive,
                            includeMatches, IncludeUnknown);
                        break;
                    case FilterValueType.Number:
                        decimal? minimum;
                        decimal? maximum;
                        if (!TryParseNumber(MinimumText, field, out minimum) ||
                            !TryParseNumber(MaximumText, field, out maximum))
                        {
                            error = field == FilterField.FileSize
                                ? AppLocalization.Text("数値を B、KiB、MiB、GiB のいずれかで入力してください。", "Enter a number in B, KiB, MiB, or GiB.")
                                : AppLocalization.Text("数値を入力してください。", "Enter a number.");
                            return false;
                        }
                        condition = new NumberFilterCondition(
                            field, minimum, maximum, includeMatches, IncludeUnknown,
                            IncludeNoSequence, IncludeRejectedRating);
                        break;
                    case FilterValueType.DateTime:
                        DateTime? start;
                        DateTime? end;
                        if (!TryCombineDateAndTime(StartDate, StartTimeText, out start) ||
                            !TryCombineDateAndTime(EndDate, EndTimeText, out end))
                        {
                            error = AppLocalization.Text(
                                "時刻は HH:mm または HH:mm:ss で入力し、時刻を使う場合は日付も指定してください。",
                                "Enter time as HH:mm or HH:mm:ss, and specify a date when using a time.");
                            return false;
                        }
                        var endHasTime = !string.IsNullOrWhiteSpace(EndTimeText);
                        var dateMaximum = endHasTime || !end.HasValue ? end : end.Value.Date.AddDays(1);
                        condition = new DateTimeFilterCondition(
                            field, start, dateMaximum, !endHasTime && dateMaximum.HasValue,
                            IsTimeZoneDate ? TimeZoneSpecifier : null,
                            includeMatches, IncludeUnknown);
                        break;
                    case FilterValueType.Boolean:
                        condition = new ChoiceFilterCondition<bool>(
                            field, Choices.Where(item => item.IsSelected).Select(item => (bool)item.Value),
                            includeMatches, IncludeUnknown);
                        break;
                    case FilterValueType.Choice:
                        condition = BuildChoiceCondition(field, includeMatches);
                        break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                error = ex.Message;
                return false;
            }

            var prepared = new FilterSet(new[] { condition }).Prepare();
            if (prepared.IsValid) return true;
            error = TranslateValidation(prepared.Errors[0].Code);
            return false;
        }

        private FilterCondition BuildChoiceCondition(FilterField field, bool includeMatches)
        {
            var selected = Choices.Where(item => item.IsSelected).Select(item => item.Value).ToList();
            if (field == FilterField.FileType)
                return new ChoiceFilterCondition<PhotoFileType>(field, selected.Cast<PhotoFileType>(), includeMatches, IncludeUnknown);
            if (field == FilterField.CopyStatus)
                return new ChoiceFilterCondition<FilterCopyStatus>(field, selected.Cast<FilterCopyStatus>(), includeMatches, IncludeUnknown);
            return new ChoiceFilterCondition<FilterExifReadStatus>(field, selected.Cast<FilterExifReadStatus>(), includeMatches, IncludeUnknown);
        }

        private static bool TryParseNumber(string text, FilterField field, out decimal? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (field == FilterField.FileSize)
            {
                long bytes;
                if (!FileSizeFilterParser.TryParseBytes(text, out bytes)) return false;
                value = bytes;
                return true;
            }
            decimal number;
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out number) &&
                !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return false;
            value = number;
            return true;
        }

        private static bool TryCombineDateAndTime(DateTime? date, string timeText, out DateTime? value)
        {
            value = date?.Date;
            if (string.IsNullOrWhiteSpace(timeText)) return true;
            if (!date.HasValue) return false;
            TimeSpan time;
            if (!TimeSpan.TryParseExact(
                    timeText.Trim(),
                    new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss", @"hh\:mm\:ss\.fffffff" },
                    CultureInfo.InvariantCulture,
                    out time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) return false;
            value = date.Value.Date.Add(time);
            return true;
        }

        private string BuildValueSummary()
        {
            switch (ValueType)
            {
                case FilterValueType.String:
                    if (UsesSelectedValues) return AppLocalization.Text("候補と完全一致: ", "Exact choices: ") + SelectedValuesSummary;
                    return AppLocalization.IsEnglish
                        ? (SelectedStringMatchMode?.DisplayName ?? "Text") + " \"" + Pattern + "\""
                        : (SelectedStringMatchMode?.DisplayName ?? "文字列") + "「" + Pattern + "」";
                case FilterValueType.Number:
                    var numberParts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(MinimumText) && !string.IsNullOrWhiteSpace(MaximumText))
                        numberParts.Add(MinimumText.Trim() + AppLocalization.Text("～", " to ") + MaximumText.Trim());
                    else if (!string.IsNullOrWhiteSpace(MinimumText))
                        numberParts.Add(MinimumText.Trim() + AppLocalization.Text("以上", " or more"));
                    else if (!string.IsNullOrWhiteSpace(MaximumText))
                        numberParts.Add(MaximumText.Trim() + AppLocalization.Text("以下", " or less"));
                    if (IncludeNoSequence) numberParts.Add(AppLocalization.Text("連番なし", "no sequence number"));
                    if (IncludeRejectedRating) numberParts.Add("Rejected");
                    return numberParts.Count == 0
                        ? AppLocalization.Text("値未指定", "No value")
                        : string.Join(AppLocalization.Text(" または ", " or "), numberParts);
                case FilterValueType.DateTime:
                    var start = FormatDateBoundary(StartDate, StartTimeText);
                    var end = FormatDateBoundary(EndDate, EndTimeText);
                    var range = !string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end)
                        ? start + AppLocalization.Text("～", " to ") + end
                        : !string.IsNullOrEmpty(start) ? start + AppLocalization.Text("以降", " or later")
                        : !string.IsNullOrEmpty(end) ? end + AppLocalization.Text("以前", " or earlier")
                        : AppLocalization.Text("日時未指定", "No date/time");
                    return IsTimeZoneDate && !string.IsNullOrWhiteSpace(TimeZoneSpecifier)
                        ? range + " [" + TimeZoneSpecifier.Trim() + "]"
                        : range;
                default:
                    var selected = Choices.Where(item => item.IsSelected)
                        .Select(item => item.DisplayName)
                        .ToList();
                    return selected.Count == 0
                        ? AppLocalization.Text("選択なし", "None selected")
                        : string.Join(AppLocalization.Text(" または ", " or "), selected);
            }
        }

        private static string FormatDateBoundary(DateTime? date, string timeText)
        {
            if (!date.HasValue) return string.Empty;
            var result = date.Value.ToString("yyyy/M/d", CultureInfo.CurrentCulture);
            return string.IsNullOrWhiteSpace(timeText) ? result : result + " " + timeText.Trim();
        }

        private void RebuildChoices()
        {
            foreach (var item in Choices) item.PropertyChanged -= Choice_PropertyChanged;
            Choices.Clear();
            switch (_selectedField.Field)
            {
                case FilterField.FileType:
                    AddChoice(PhotoFileType.Jpeg, "JPEG"); AddChoice(PhotoFileType.Raw, "RAW");
                    AddChoice(PhotoFileType.OtherImage, AppLocalization.Text("その他の画像", "Other image")); AddChoice(PhotoFileType.Video, AppLocalization.Text("動画", "Video"));
                    AddChoice(PhotoFileType.Other, AppLocalization.Text("その他", "Other")); break;
                case FilterField.CopyStatus:
                    AddChoice(FilterCopyStatus.NotImported, AppLocalization.Text("未取込", "Not imported")); AddChoice(FilterCopyStatus.Overwrite, AppLocalization.Text("上書き対象", "Overwrite"));
                    AddChoice(FilterCopyStatus.Imported, AppLocalization.Text("取込済", "Imported")); AddChoice(FilterCopyStatus.Conflict, AppLocalization.Text("競合", "Conflict"));
                    AddChoice(FilterCopyStatus.ScanError, AppLocalization.Text("スキャンエラー", "Scan error")); AddChoice(FilterCopyStatus.CopyError, AppLocalization.Text("コピーエラー", "Copy error")); break;
                case FilterField.ExifReadStatus:
                    AddChoice(FilterExifReadStatus.Read, AppLocalization.Text("読込済み", "Loaded")); AddChoice(FilterExifReadStatus.NoMetadata, AppLocalization.Text("Exif情報なし", "No Exif data"));
                    AddChoice(FilterExifReadStatus.Unsupported, AppLocalization.Text("未対応形式", "Unsupported format")); AddChoice(FilterExifReadStatus.ReadError, AppLocalization.Text("読取エラー", "Read error")); break;
                case FilterField.Protected:
                    AddChoice(true, "Protected"); AddChoice(false, "Unprotected"); break;
                case FilterField.HasGps:
                    AddChoice(true, "GPS"); AddChoice(false, "NoGPS"); break;
            }
        }

        private void AddChoice(object value, string name)
        {
            var item = new FilterChoiceOption(value, name);
            item.PropertyChanged += Choice_PropertyChanged;
            Choices.Add(item);
        }

        private void Choice_PropertyChanged(object sender, PropertyChangedEventArgs e) => NotifyValidation();

        private static string TranslateValidation(FilterValidationCode code)
        {
            switch (code)
            {
                case FilterValidationCode.NoChoices: return AppLocalization.Text("少なくとも1つ選択してください。", "Select at least one option.");
                case FilterValidationCode.RangeIsEmpty: return AppLocalization.Text("最小値・最大値・特別値のいずれかを指定してください。", "Specify a minimum, maximum, or special value.");
                case FilterValidationCode.MinimumExceedsMaximum: return AppLocalization.Text("開始・最小値は終了・最大値以下にしてください。", "The start or minimum must not exceed the end or maximum.");
                case FilterValidationCode.InvalidRegularExpression: return AppLocalization.Text("正規表現が正しくありません。", "The regular expression is invalid.");
                case FilterValidationCode.TimeZoneRequired: return AppLocalization.Text("タイムゾーンを指定してください。", "Specify a time zone.");
                case FilterValidationCode.InvalidTimeZone: return AppLocalization.Text("タイムゾーン指定が正しくありません。", "The time zone specification is invalid.");
                case FilterValidationCode.OptionNotSupported:
                    return AppLocalization.Text(
                        "「連番なし」と「Rejected」は、それぞれ {Sequence} と {Rating} でのみ使用できます。",
                        "No sequence number and Rejected are available only for {Sequence} and {Rating}, respectively.");
                default: return AppLocalization.Text("この条件を適用できません。", "This condition cannot be applied.");
            }
        }

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(SelectedField));
            OnPropertyChanged(nameof(UsesSelectedValues));
            OnPropertyChanged(nameof(IsStringInput));
            OnPropertyChanged(nameof(SelectedValuesSummary));
            OnPropertyChanged(nameof(SuggestionButtonText));
            OnPropertyChanged(nameof(ValueType)); OnPropertyChanged(nameof(IsString));
            OnPropertyChanged(nameof(IsNumber)); OnPropertyChanged(nameof(IsDateTime));
            OnPropertyChanged(nameof(IsChoice)); OnPropertyChanged(nameof(IsTimeZoneDate));
            OnPropertyChanged(nameof(IsSequence)); OnPropertyChanged(nameof(IsRating));
            OnPropertyChanged(nameof(CanUseCaseSensitivity));
            OnPropertyChanged(nameof(CanIncludeUnknown));
            NotifyValidation();
        }

        private void NotifyValidation()
        {
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(StateKey));
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
