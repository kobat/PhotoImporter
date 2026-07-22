using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

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

        public static IReadOnlyList<FilterFieldOption> CreateAll() => new[]
        {
            Option(FilterField.FileType, "ファイル種別"),
            Option(FilterField.Extension, "{Extension} 拡張子"),
            Option(FilterField.CopyStatus, "コピー計画の状態"),
            Option(FilterField.ExifReadStatus, "Exif読込状態"),
            Option(FilterField.OriginalName, "{OriginalName} 元ファイル名"),
            Option(FilterField.FileName, "{FileName} 拡張子なしファイル名"),
            Option(FilterField.SourceRelativeDirectory, "{SourceRelativeDirectory} コピー元相対フォルダー"),
            Option(FilterField.ModifiedDate, "{ModifiedDate} 更新日時"),
            Option(FilterField.FileSize, "{FileSize} ファイルサイズ"),
            Option(FilterField.Protected, "{Protected} 読み取り専用"),
            Option(FilterField.Sequence, "{Sequence} 連番"),
            Option(FilterField.TakenDate, "{TakenDate} Exif記録日時"),
            Option(FilterField.TakenDateLocal, "{TakenDateLocal} PCタイムゾーン"),
            Option(FilterField.TakenDateInTimeZone, "{TakenDateInTimeZone} 指定タイムゾーン"),
            Option(FilterField.CameraMake, "{CameraMake} メーカー"),
            Option(FilterField.CameraModel, "{CameraModel} カメラ"),
            Option(FilterField.CameraSerial, "{CameraSerial} シリアル番号"),
            Option(FilterField.Lens, "{Lens} レンズ"),
            Option(FilterField.Width, "{Width} 向き反映後の幅"),
            Option(FilterField.Height, "{Height} 向き反映後の高さ"),
            Option(FilterField.ExifWidth, "{ExifWidth} Exif幅"),
            Option(FilterField.ExifHeight, "{ExifHeight} Exif高さ"),
            Option(FilterField.Orientation, "{Orientation} 向き"),
            Option(FilterField.Aperture, "{Aperture} 絞り"),
            Option(FilterField.ShutterSpeed, "{ShutterSpeed} シャッター秒数"),
            Option(FilterField.ExposureTime, "{ExposureTime} 露光秒数"),
            Option(FilterField.Iso, "{Iso} ISO"),
            Option(FilterField.FocalLength, "{FocalLength} 焦点距離"),
            Option(FilterField.FocalLength35mm, "{FocalLength35mm} 35mm換算焦点距離"),
            Option(FilterField.Rating, "{Rating} 評価"),
            Option(FilterField.HasGps, "{HasGps} GPS有無"),
            Option(FilterField.GpsLatitude, "{GpsLatitude} 緯度"),
            Option(FilterField.GpsLongitude, "{GpsLongitude} 経度"),
            Option(FilterField.GpsAltitude, "{GpsAltitude} 高度")
        };

        private static FilterFieldOption Option(FilterField field, string name) =>
            new FilterFieldOption(field, name);
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

        public FilterConditionEditor(IReadOnlyList<FilterFieldOption> fieldOptions)
        {
            _fieldOptions = fieldOptions ?? throw new ArgumentNullException(nameof(fieldOptions));
            StringMatchModes = new[]
            {
                new DisplayOption<StringFilterMatchMode>(StringFilterMatchMode.Exact, "完全一致"),
                new DisplayOption<StringFilterMatchMode>(StringFilterMatchMode.Contains, "部分一致"),
                new DisplayOption<StringFilterMatchMode>(StringFilterMatchMode.Wildcard, "ワイルドカード"),
                new DisplayOption<StringFilterMatchMode>(StringFilterMatchMode.RegularExpression, "正規表現")
            };
            TargetModes = new[]
            {
                new DisplayOption<bool>(true, "一致する項目を対象にする"),
                new DisplayOption<bool>(false, "一致する項目を対象から外す")
            };
            _selectedStringMatchMode = StringMatchModes[0];
            _selectedTargetMode = TargetModes[0];
            SelectedField = _fieldOptions[0];
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public IReadOnlyList<FilterFieldOption> FieldOptions => _fieldOptions;
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
                RebuildChoices();
                _includeUnknown = false;
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
        public bool CaseSensitive { get => _caseSensitive; set { if (Set(ref _caseSensitive, value)) NotifyValidation(); } }
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
        public bool CanIncludeUnknown => FilterFieldDefinition.Get(SelectedField.Field).CanBeUnknown;
        public bool IsValid { get { FilterCondition condition; string error; return TryBuild(out condition, out error); } }
        public string ValidationMessage { get { FilterCondition condition; string error; return TryBuild(out condition, out error) ? string.Empty : error; } }

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
                        condition = new StringFilterCondition(
                            field, Pattern, SelectedStringMatchMode.Value, CaseSensitive, includeMatches, IncludeUnknown);
                        break;
                    case FilterValueType.Number:
                        decimal? minimum;
                        decimal? maximum;
                        if (!TryParseNumber(MinimumText, field, out minimum) ||
                            !TryParseNumber(MaximumText, field, out maximum))
                        {
                            error = field == FilterField.FileSize
                                ? "数値を B、KiB、MiB、GiB のいずれかで入力してください。"
                                : "数値を入力してください。";
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
                            error = "時刻は HH:mm または HH:mm:ss で入力し、時刻を使う場合は日付も指定してください。";
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
                    new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" },
                    CultureInfo.InvariantCulture,
                    out time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) return false;
            value = date.Value.Date.Add(time);
            return true;
        }

        private void RebuildChoices()
        {
            foreach (var item in Choices) item.PropertyChanged -= Choice_PropertyChanged;
            Choices.Clear();
            switch (_selectedField.Field)
            {
                case FilterField.FileType:
                    AddChoice(PhotoFileType.Jpeg, "JPEG"); AddChoice(PhotoFileType.Raw, "RAW");
                    AddChoice(PhotoFileType.OtherImage, "その他の画像"); AddChoice(PhotoFileType.Video, "動画");
                    AddChoice(PhotoFileType.Other, "その他"); break;
                case FilterField.CopyStatus:
                    AddChoice(FilterCopyStatus.NotImported, "未取込"); AddChoice(FilterCopyStatus.Overwrite, "上書き対象");
                    AddChoice(FilterCopyStatus.Imported, "取込済"); AddChoice(FilterCopyStatus.Conflict, "競合");
                    AddChoice(FilterCopyStatus.ScanError, "スキャンエラー"); AddChoice(FilterCopyStatus.CopyError, "コピーエラー"); break;
                case FilterField.ExifReadStatus:
                    AddChoice(FilterExifReadStatus.Read, "読込済み"); AddChoice(FilterExifReadStatus.NoMetadata, "Exif情報なし");
                    AddChoice(FilterExifReadStatus.Unsupported, "未対応形式"); AddChoice(FilterExifReadStatus.ReadError, "読取エラー"); break;
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
                case FilterValidationCode.NoChoices: return "少なくとも1つ選択してください。";
                case FilterValidationCode.RangeIsEmpty: return "最小値・最大値・特別値のいずれかを指定してください。";
                case FilterValidationCode.MinimumExceedsMaximum: return "開始・最小値は終了・最大値以下にしてください。";
                case FilterValidationCode.InvalidRegularExpression: return "正規表現が正しくありません。";
                case FilterValidationCode.TimeZoneRequired: return "タイムゾーンを指定してください。";
                case FilterValidationCode.InvalidTimeZone: return "タイムゾーン指定が正しくありません。";
                default: return "この条件を適用できません。";
            }
        }

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(SelectedField));
            OnPropertyChanged(nameof(ValueType)); OnPropertyChanged(nameof(IsString));
            OnPropertyChanged(nameof(IsNumber)); OnPropertyChanged(nameof(IsDateTime));
            OnPropertyChanged(nameof(IsChoice)); OnPropertyChanged(nameof(IsTimeZoneDate));
            OnPropertyChanged(nameof(IsSequence)); OnPropertyChanged(nameof(IsRating));
            OnPropertyChanged(nameof(CanIncludeUnknown));
            NotifyValidation();
        }

        private void NotifyValidation()
        {
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(ValidationMessage));
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
