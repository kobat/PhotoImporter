using PhotoImporter.Core.Filtering;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PhotoImporter.App
{
    public partial class FilterSuggestionWindow : Window
    {
        private readonly FilterConditionEditor _editor;
        private readonly Func<Task<FilterSuggestionCatalog>> _getCatalog;
        private readonly bool _allowLoadExif;
        public bool RequestsExif { get; private set; }
        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();
        private SuggestionRow[] _rows = new SuggestionRow[0];
        private int _request;
        private int _searchRequest;
        private bool _ready;
        private bool _loading;
        private readonly bool _fromFile;
        private CancellationTokenSource _refreshCancellation;

        internal FilterSuggestionWindow(FilterConditionEditor editor, Func<Task<FilterSuggestionCatalog>> getCatalog,
            bool allowLoadExif, bool chooseField)
        {
            _editor = editor;
            _getCatalog = getCatalog;
            _allowLoadExif = allowLoadExif;
            _fromFile = chooseField;
            InitializeComponent();
            WpfLocalizer.Localize(this);
            FieldBox.ItemsSource = editor.GroupedFieldOptions;
            FieldBox.SelectedItem = editor.SelectedField;
            FieldBox.IsEnabled = chooseField;
            _searchTimer.Tick += async (sender, args) => { _searchTimer.Stop(); await SearchAsync(); };
            Closed += (sender, args) => { _closed.Cancel(); _searchTimer.Stop(); };
            Loaded += async (sender, args) => { _ready = true; await RefreshAsync(); SearchBox.Focus(); };
        }

        private async Task RefreshAsync()
        {
            var request = ++_request;
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            var token = _refreshCancellation.Token;
            ++_searchRequest;
            _loading = true;
            _rows = new SuggestionRow[0];
            ValuesList.ItemsSource = null;
            UseButton.IsEnabled = false;
            LatestButton.IsEnabled = PeriodButton.IsEnabled = false;
            LoadExifButton.Visibility = Visibility.Collapsed;
            ExactTimeBox.Visibility = LatestButton.Visibility = PeriodButton.Visibility =
                _editor.IsDateTime ? Visibility.Visible : Visibility.Collapsed;
            BoundaryBox.Visibility = _editor.IsNumber || _editor.IsDateTime ? Visibility.Visible : Visibility.Collapsed;
            BoundaryBox.ItemsSource = new[]
            {
                new DisplayOption<string>("Equal", AppLocalization.Text(_editor.IsDateTime ? "同じ日・日時" : "この値と一致", _editor.IsDateTime ? "Same day / time" : "Equal to value")),
                new DisplayOption<string>("Minimum", AppLocalization.Text(_editor.IsDateTime ? "開始に設定" : "最小に設定", "Set minimum")),
                new DisplayOption<string>("Maximum", AppLocalization.Text(_editor.IsDateTime ? "終了に設定" : "最大に設定", "Set maximum"))
            };
            BoundaryBox.SelectedIndex = 0;
            StatusText.Text = AppLocalization.Text("候補を集計中...", "Collecting candidates...");
            var field = _editor.SelectedField.Field;
            var zone = _editor.IsTimeZoneDate ? _editor.TimeZoneSpecifier : null;
            var exact = ExactTimeBox.IsChecked == true;
            var multiple = _editor.IsString || _editor.IsChoice;
            var selected = new HashSet<object>(_editor.IsString
                ? _editor.SelectedValues.Cast<object>()
                : _editor.Choices.Where(choice => choice.IsSelected).Select(choice => choice.Value));
            var names = _editor.Choices.ToDictionary(choice => choice.Value, choice => choice.DisplayName);
            try
            {
                var catalog = await _getCatalog();
                var result = await Task.Run(() => catalog.Get(field, zone, exact, token), token);
                var rows = await Task.Run(() =>
                {
                    var list = result.Values.Select(value => new SuggestionRow(value.Value,
                        FormatValue(value.Value, names, exact, field), value.Count, multiple,
                        selected.Contains(value.Value))).ToList();
                    var present = new HashSet<object>(list.Select(row => row.Value));
                    foreach (var choice in names.Where(choice => !_fromFile && !present.Contains(choice.Key)))
                        list.Add(new SuggestionRow(choice.Key, choice.Value, 0, multiple, selected.Contains(choice.Key)));
                    foreach (var value in selected.OfType<string>().Where(value => !present.Contains(value)))
                        list.Add(new SuggestionRow(value, value, 0, true, true));
                    return list.ToArray();
                }, token);
                if (request != _request || _closed.IsCancellationRequested) return;
                _rows = rows;
                foreach (var row in rows) row.PropertyChanged += (sender, args) => UpdateUseButton();
                _loading = false;
                StatusText.Text = AppLocalization.Format(
                    _fromFile ? "選択ファイル: {0}候補 / Exif未読 {1}件 / 情報なし・取得不可 {2}件。{3}" : "スキャン結果全体: {0}候補 / Exif未読 {1}件 / 情報なし・取得不可 {2}件。{3}",
                    _fromFile ? "Selected file: {0} values / {1} Exif unread / {2} unavailable. {3}" : "All scanned files: {0} values / {1} Exif unread / {2} unavailable. {3}",
                    rows.Length, result.UnreadCount, result.UnknownCount,
                    multiple ? AppLocalization.Text("チェックした候補のいずれかと完全一致します。", "Matches any checked value exactly.") :
                    AppLocalization.Text("候補を選び、条件に設定してください。", "Select a value to use in the condition."));
                LoadExifButton.Visibility = result.UnreadCount > 0 && _allowLoadExif ? Visibility.Visible : Visibility.Collapsed;
                LatestButton.IsEnabled = PeriodButton.IsEnabled = rows.Any(row => row.Value is DateTime);
                await SearchAsync();
                UpdateUseButton();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (request != _request || _closed.IsCancellationRequested) return;
                _loading = false;
                StatusText.Text = AppLocalization.Text("候補を取得できません: ", "Cannot collect candidates: ") + ex.Message;
            }
        }

        private static string FormatValue(object value, IDictionary<object, string> names, bool exact, FilterField field)
        {
            string name;
            if (names.TryGetValue(value, out name)) return name;
            if (value is FilterSpecialValue) return AppLocalization.Text("連番なし", "No sequence number");
            if (field == FilterField.Rating && value is decimal)
                return (decimal)value == -1 ? "Rejected" : (decimal)value == 0 ? AppLocalization.Text("評価なし", "Unrated") : new string('★', (int)(decimal)value);
            if (field == FilterField.Orientation && value is decimal)
            {
                var descriptions = new[] { "", "通常", "左右反転", "180°回転", "上下反転", "転置", "90°回転", "反転転置", "270°回転" };
                var english = new[] { "", "Normal", "Mirror horizontal", "Rotate 180°", "Mirror vertical", "Transpose", "Rotate 90°", "Transverse", "Rotate 270°" };
                var orientation = (int)(decimal)value;
                if (orientation >= 1 && orientation <= 8) return orientation + ": " + AppLocalization.Text(descriptions[orientation], english[orientation]);
            }
            if (value is DateTime) return ((DateTime)value).ToString(exact ? "yyyy/MM/dd HH:mm:ss.FFFFFFF" : "yyyy/MM/dd", CultureInfo.InvariantCulture).TrimEnd('.');
            if (value is string && (string)value == string.Empty)
                return field == FilterField.SourceRelativeDirectory ? AppLocalization.Text("コピー元の直下", "Source root") :
                    field == FilterField.Extension ? AppLocalization.Text("拡張子なし", "No extension") : AppLocalization.Text("空文字", "Empty text");
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private async Task SearchAsync()
        {
            if (_closed.IsCancellationRequested || _loading) return;
            var request = ++_searchRequest;
            var rows = _rows;
            var text = SearchBox.Text.Trim();
            var filtered = await Task.Run(() => rows.Where(row => row.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToArray());
            if (request == _searchRequest && !_closed.IsCancellationRequested) ValuesList.ItemsSource = filtered;
        }

        private async void Field_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            _editor.SelectedField = (FilterFieldOption)FieldBox.SelectedItem;
            await RefreshAsync();
        }
        private async void Precision_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            var row = ValuesList.SelectedItem as SuggestionRow;
            if (ExactTimeBox.IsChecked == true && row?.Value is DateTime)
                SearchBox.Text = ((DateTime)row.Value).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
            await RefreshAsync();
        }
        private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) { _searchTimer.Stop(); _searchTimer.Start(); } }
        private void Value_Changed(object sender, SelectionChangedEventArgs e) => UpdateUseButton();
        private void UpdateUseButton()
        {
            if (UseButton != null) UseButton.IsEnabled = !_loading && (_editor.IsString || _editor.IsChoice ? _rows.Any(row => row.IsSelected) : ValuesList.SelectedItem != null);
        }
        private void Use_Click(object sender, RoutedEventArgs e)
        {
            if (!UseButton.IsEnabled) return;
            if (_editor.IsString) _editor.SetSelectedValues(_rows.Where(row => row.IsSelected).Select(row => (string)row.Value));
            else if (_editor.IsChoice)
            {
                var selected = new HashSet<object>(_rows.Where(row => row.IsSelected).Select(row => row.Value));
                foreach (var choice in _editor.Choices) choice.IsSelected = selected.Contains(choice.Value);
            }
            else
            {
                var row = ValuesList.SelectedItem as SuggestionRow;
                if (row == null) return;
                _editor.UseSuggestion(row.Value, ((DisplayOption<string>)BoundaryBox.SelectedItem).Value, ExactTimeBox.IsChecked == true);
            }
            DialogResult = true;
        }
        private void Latest_Click(object sender, RoutedEventArgs e)
        {
            var date = _rows.Where(row => row.Value is DateTime).Max(row => (DateTime)row.Value);
            _editor.UseSuggestion(date.Date);
            DialogResult = true;
        }
        private void Period_Click(object sender, RoutedEventArgs e)
        {
            var dates = _rows.Where(row => row.Value is DateTime).Select(row => (DateTime)row.Value).ToArray();
            _editor.UseSuggestion(dates.Min().Date, "Minimum");
            _editor.UseSuggestion(dates.Max().Date, "Maximum");
            DialogResult = true;
        }
        private void LoadExif_Click(object sender, RoutedEventArgs e)
        {
            RequestsExif = true;
            DialogResult = false;
        }

        private sealed class SuggestionRow : INotifyPropertyChanged
        {
            private bool _selected;
            public SuggestionRow(object value, string label, int count, bool multiple, bool selected)
            {
                Value = value;
                Label = label + AppLocalization.Format("（{0}件）", " ({0} files)", count);
                IsMultiple = multiple;
                _selected = selected;
            }
            public object Value { get; }
            public string Label { get; }
            public bool IsMultiple { get; }
            public bool IsSelected
            {
                get => _selected;
                set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
