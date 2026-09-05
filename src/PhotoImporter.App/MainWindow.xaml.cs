using PhotoImporter.Core.Copying;
using PhotoImporter.Core.Filtering;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Settings;
using PhotoImporter.Core.Templates;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PhotoImporter.App
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private string _sourceFolder;
        private string _destinationFolder;
        private string _templateText = @"{ModifiedDate:yyyy-MM-dd}\{FileName}{Sequence}{Extension}";
        private string _message = "コピー元とコピー先を選択して、スキャンしてください。";
        private string _summary = "0 件";
        private string _progressText = string.Empty;
        private string _copyProgressSummaryText = string.Empty;
        private string _copyProgressRatesText = string.Empty;
        private string _copyProgressTimeText = string.Empty;
        private string _copyProgressPercentText = string.Empty;
        private Brush _messageBrush = Brushes.DimGray;
        private bool _isBusy;
        private bool _isCopying;
        private bool _isCancellingCopy;
        private bool _isScanningExif;
        private bool _overwriteExisting;
        private SourceFileSelectionMode _sourceFileSelectionMode = SourceFileSelectionMode.MediaOnly;
        private bool _associateSidecars;
        private string _sidecarExtensionsText = ".xmp";
        private bool _analyzeJpegOnlyForRawJpegPair = true;
        private bool _useExifCache = true;
        private bool _readExifInformation;
        private string _customExifCacheRoot;
        private readonly List<string> _previousExifCacheRoots = new List<string>();
        private int _inputHistoryLimit = PhotoImporterSettings.DefaultInputHistoryLimit;
        private readonly PhotoImporterSettingsStore _settingsStore;
        private readonly RecentInputHistoryStore _inputHistoryStore;
        private readonly PhotoImporterPresetStore _presetStore;
        private Guid? _lastAppliedPresetId;
        private PhotoImporterPreset _selectedPreset;
        private PresetSettingsSnapshot _unselectedPresetBaseline;
        private PresetUndoState _presetUndo;
        private bool _isApplyingPreset;
        private bool _suppressPresetSelection;
        private PhotoImporterPreset _selectedManagedPreset;
        private string _presetManagerSortMode = "名前順";
        private bool _previewIsCurrent;
        private double _progressPercent;
        private bool _isProgressIndeterminate;
        private int _exifCacheHits;
        private CancellationTokenSource _copyCancellation;
        private CopyPauseController _copyPauseController;
        private CopyPauseState _copyPauseState = CopyPauseState.Running;
        private CopyProgressStatistics _copyProgressStatistics;
        private DispatcherTimer _copyProgressTimer;
        private CancellationTokenSource _scanCancellation;
        private PreviewItem _selectedPreviewItem;
        private bool _showImagePreview;
        private BitmapSource _imagePreviewSource;
        private string _imagePreviewStatus = "一覧から画像を選択してください。";
        private CancellationTokenSource _imagePreviewCancellation;
        private int _imagePreviewRequestVersion;
        private readonly ImagePreviewLoader _imagePreviewLoader = new ImagePreviewLoader();
        private readonly SemaphoreSlim _imagePreviewLoadGate = new SemaphoreSlim(1, 1);
        private bool _isUpdatingSelection;
        private PreviewItemCollectionState _itemCollectionState;
        private PreparedFilter _appliedFilter;
        private int _appliedFilterCount;
        private readonly List<string> _appliedFilterConditionSummaries = new List<string>();
        private string _appliedFilterStateKey = string.Empty;
        private OverlayPanel _activeOverlay;
        private SystemMenuAboutCommand _systemMenuAboutCommand;
        private string _uiLanguage = AppLocalization.Automatic;

        private enum OverlayPanel
        {
            None,
            ExifSettings,
            Filter,
            PresetManager
        }

        public MainWindow()
        {
            _settingsStore = new PhotoImporterSettingsStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoImporter",
                "settings.xml"));
            _inputHistoryStore = new RecentInputHistoryStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoImporter",
                "history.xml"));
            _presetStore = new PhotoImporterPresetStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoImporter",
                "presets.xml"));
            string settingsWarning = null;
            try
            {
                ApplySettings(_settingsStore.Load());
            }
            catch (InvalidDataException ex)
            {
                settingsWarning = ex.Message + AppLocalization.Text(" 既定値で起動しました。", " Started with default settings.");
            }

            AppLocalization.Configure(GetCommandLineLanguageOverride() ?? _uiLanguage);
            _message = AppLocalization.Text(
                "コピー元とコピー先を選択して、スキャンしてください。",
                "Select a source and destination, then scan.");
            _summary = AppLocalization.Text("0 件", "0 items");
            _presetManagerSortMode = AppLocalization.Text("名前順", "Name");
            _imagePreviewStatus = AppLocalization.Text(
                "一覧から画像を選択してください。",
                "Select an image from the list.");
            FileSystemTokenDetails = TokenDetailItem.CreateFileSystemItems();
            ExifTokenDetails = TokenDetailItem.CreateExifItems();
            InitializeComponent();
            WpfLocalizer.Localize(this);
            _systemMenuAboutCommand = new SystemMenuAboutCommand(
                this,
                AppLocalization.Text("バージョン情報(&A)...", "About(&A)..."),
                ShowAboutWindow);
            _itemCollectionState = new PreviewItemCollectionState(Items);
            Items.CollectionChanged += (sender, args) => InvalidateSuggestions();
            FilterFieldOptions = FilterFieldOption.CreateAll();
            DataContext = this;
            string historyWarning = null;
            try
            {
                ApplyInputHistory(_inputHistoryStore.Load());
            }
            catch (InvalidDataException ex)
            {
                historyWarning = ex.Message + AppLocalization.Text(" 入力履歴なしで起動しました。", " Started without input history.");
            }
            ReloadPresets(_lastAppliedPresetId, true, true);
            Closing += MainWindow_Closing;
            ContentRendered += MainWindow_ContentRendered;
            var startupWarnings = new[] { settingsWarning, historyWarning }
                .Where(item => !string.IsNullOrWhiteSpace(item));
            var startupWarning = string.Join(" ", startupWarnings);
            if (!string.IsNullOrEmpty(startupWarning))
                SetMessage(startupWarning, Brushes.DarkGoldenrod);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void ShowAboutWindow()
        {
            new AboutWindow(_uiLanguage, language => _uiLanguage = language) { Owner = this }.ShowDialog();
        }

        internal static string GetCommandLineLanguageOverride()
        {
            const string prefix = "--language=";
            var argument = Environment.GetCommandLineArgs()
                .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (argument == null) return null;
            var value = argument.Substring(prefix.Length);
            if (string.Equals(value, AppLocalization.Japanese, StringComparison.OrdinalIgnoreCase))
                return AppLocalization.Japanese;
            if (string.Equals(value, AppLocalization.English, StringComparison.OrdinalIgnoreCase))
                return AppLocalization.English;
            return null;
        }

        public ObservableCollection<PreviewItem> Items { get; } = new ObservableCollection<PreviewItem>();
        public ObservableCollection<FilterConditionEditor> FilterConditions { get; } = new ObservableCollection<FilterConditionEditor>();
        public ObservableCollection<PhotoImporterPreset> Presets { get; } = new ObservableCollection<PhotoImporterPreset>();
        public ObservableCollection<PhotoImporterPreset> ManagedPresets { get; } = new ObservableCollection<PhotoImporterPreset>();
        public ObservableCollection<string> SourceFolderHistory { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> DestinationFolderHistory { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> TemplateHistory { get; } = new ObservableCollection<string>();
        public IReadOnlyList<string> PresetManagerSortModes { get; } = new[]
        {
            AppLocalization.Text("名前順", "Name"),
            AppLocalization.Text("最終利用日順", "Last used")
        };
        internal IReadOnlyList<FilterFieldOption> FilterFieldOptions { get; private set; }
        public ICollectionView ItemsView => _itemCollectionState.View;
        public IReadOnlyList<TokenDetailItem> FileSystemTokenDetails { get; }
        public IReadOnlyList<TokenDetailItem> ExifTokenDetails { get; }

        public string SourceFolder
        {
            get => _sourceFolder;
            set
            {
                if (!Set(ref _sourceFolder, value)) return;
                ResetImagePreviewForSourceChange();
                SettingsChanged();
            }
        }

        public string DestinationFolder
        {
            get => _destinationFolder;
            set { if (Set(ref _destinationFolder, value)) SettingsChanged(); }
        }

        public string TemplateText
        {
            get => _templateText;
            set { if (Set(ref _templateText, value)) SettingsChanged(); }
        }

        public bool OverwriteExisting
        {
            get => _overwriteExisting;
            set { if (Set(ref _overwriteExisting, value)) SettingsChanged(); }
        }

        public bool IncludeOtherFiles
        {
            get => _sourceFileSelectionMode == SourceFileSelectionMode.AllFiles;
            set
            {
                var mode = value
                    ? SourceFileSelectionMode.AllFiles
                    : SourceFileSelectionMode.MediaOnly;
                if (!Set(ref _sourceFileSelectionMode, mode)) return;
                SettingsChanged();
            }
        }

        public bool AnalyzeJpegOnlyForRawJpegPair
        {
            get => _analyzeJpegOnlyForRawJpegPair;
            set { if (Set(ref _analyzeJpegOnlyForRawJpegPair, value)) SettingsChanged(); }
        }

        public bool UseExifCache
        {
            get => _useExifCache;
            set
            {
                if (!Set(ref _useExifCache, value)) return;
                NotifyExifSettingsSummaryChanged();
                SettingsChanged(false);
            }
        }

        public bool AssociateSidecars
        {
            get => _associateSidecars;
            set { if (Set(ref _associateSidecars, value)) SettingsChanged(); }
        }

        public string SidecarExtensionsText
        {
            get => _sidecarExtensionsText;
            set
            {
                if (!Set(ref _sidecarExtensionsText, value)) return;
                OnPropertyChanged(nameof(SidecarExtensionsError));
                OnPropertyChanged(nameof(SidecarExtensionsErrorVisibility));
                SettingsChanged();
            }
        }

        public string SidecarExtensionsError
        {
            get
            {
                try
                {
                    CreateSidecarPolicy(true, SidecarExtensionsText);
                    return string.Empty;
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
            }
        }

        public Visibility SidecarExtensionsErrorVisibility =>
            string.IsNullOrEmpty(SidecarExtensionsError)
                ? Visibility.Collapsed
                : Visibility.Visible;

        public bool ReadExifInformation
        {
            get => _readExifInformation;
            set
            {
                if (!Set(ref _readExifInformation, value)) return;
                NotifyExifSettingsSummaryChanged();
                SettingsChanged();
            }
        }

        public bool ShowImagePreview
        {
            get => _showImagePreview;
            set
            {
                if (!Set(ref _showImagePreview, value)) return;
                OnPropertyChanged(nameof(ImagePreviewVisibility));
                QueueImagePreview(SelectedPreviewItem);
            }
        }

        public BitmapSource ImagePreviewSource => _imagePreviewSource;
        public string ImagePreviewStatus => _imagePreviewStatus;
        public Visibility ImagePreviewVisibility => ShowImagePreview
            ? Visibility.Visible
            : Visibility.Collapsed;
        public Visibility ImagePreviewPlaceholderVisibility => _imagePreviewSource == null
            ? Visibility.Visible
            : Visibility.Collapsed;

        public PreviewItem SelectedPreviewItem
        {
            get => _selectedPreviewItem;
            set
            {
                if (!Set(ref _selectedPreviewItem, value)) return;
                foreach (var item in FileSystemTokenDetails) item.SetPreviewItem(value);
                foreach (var item in ExifTokenDetails) item.SetPreviewItem(value);
                OnPropertyChanged(nameof(SelectedSourcePath));
                OnPropertyChanged(nameof(ExifReadStatus));
                QueueImagePreview(value);
            }
        }

        public string SelectedSourcePath => SelectedPreviewItem == null
            ? AppLocalization.Text("一覧からファイルを選択してください。", "Select a file from the list.")
            : SelectedPreviewItem.SourcePath;

        public string ExifReadStatus
        {
            get
            {
                if (SelectedPreviewItem == null) return string.Empty;
                if (SelectedPreviewItem.IsScanError) return AppLocalization.Text("ファイル情報を取得できません。", "File information is unavailable.");
                var result = SelectedPreviewItem.MetadataResult;
                if (result == null) return AppLocalization.Text("Exif情報は読み込まれていません。", "Exif data has not been read.");
                var source = string.IsNullOrWhiteSpace(SelectedPreviewItem.MetadataSourcePath)
                    ? string.Empty
                    : AppLocalization.Text(" / 解析元: ", " / Source: ") + SelectedPreviewItem.MetadataSourcePath;
                switch (result.Status)
                {
                    case PhotoMetadataReadStatus.Success: return AppLocalization.Text("Exif読込済み", "Exif loaded") + source;
                    case PhotoMetadataReadStatus.NoMetadata: return AppLocalization.Text("Exif情報なし", "No Exif data") + source;
                    case PhotoMetadataReadStatus.Unsupported: return AppLocalization.Text("Exif未対応形式", "Unsupported Exif format") + source;
                    default: return AppLocalization.Text("Exif読取エラー: ", "Exif read error: ") + result.Error.Message + source;
                }
            }
        }

        public string ExifCacheRoot => string.IsNullOrWhiteSpace(_customExifCacheRoot)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ExifCache"))
            : Path.GetFullPath(_customExifCacheRoot);
        public string ExifSettingsSummary => AppLocalization.Format(
            "Exif: {0} / キャッシュ {1} / 保存先: {2}",
            "Exif: {0} / Cache {1} / Location: {2}",
            ReadExifInformation ? AppLocalization.Text("常に読込", "Always") : AppLocalization.Text("必要時のみ読込", "When needed"),
            UseExifCache ? "ON" : "OFF",
            string.IsNullOrWhiteSpace(_customExifCacheRoot) ? AppLocalization.Text("既定", "Default") : ExifCacheRoot);
        public string ExifSettingsToolTip => ExifSettingsSummary + Environment.NewLine + ExifCacheRoot;

        public string Message { get => _message; private set => Set(ref _message, value); }
        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
        public string CopyProgressSummaryText
        {
            get => _copyProgressSummaryText;
            private set => Set(ref _copyProgressSummaryText, value);
        }
        public string CopyProgressRatesText
        {
            get => _copyProgressRatesText;
            private set => Set(ref _copyProgressRatesText, value);
        }
        public string CopyProgressTimeText
        {
            get => _copyProgressTimeText;
            private set => Set(ref _copyProgressTimeText, value);
        }
        public string CopyProgressPercentText
        {
            get => _copyProgressPercentText;
            private set => Set(ref _copyProgressPercentText, value);
        }
        public Brush MessageBrush { get => _messageBrush; private set => Set(ref _messageBrush, value); }
        public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            private set => Set(ref _isProgressIndeterminate, value);
        }
        public Visibility ProgressVisibility => _isCopying || _isScanningExif ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CopyProgressDetailsVisibility => _isCopying
            ? Visibility.Visible
            : Visibility.Collapsed;
        public Visibility SimpleProgressTextVisibility => _isScanningExif
            ? Visibility.Visible
            : Visibility.Collapsed;
        public bool CanEditSettings => !_isBusy;
        public bool CanSelectItems => !_isBusy;
        public bool CanSelectAll => !_isBusy && _itemCollectionState.VisibleItems.Any(item => item.CanCopy);
        public bool? SelectAllState => _itemCollectionState.GetVisibleSelectAllState();
        public bool CanCancel => (_isCopying && !_isCancellingCopy) || _isScanningExif;
        public bool CanScan => !_isBusy && !string.IsNullOrWhiteSpace(SourceFolder) &&
                               !string.IsNullOrWhiteSpace(DestinationFolder) &&
                               !string.IsNullOrWhiteSpace(TemplateText) &&
                               string.IsNullOrEmpty(SidecarExtensionsError);
        public bool CanCopy => _isCopying
            ? !_isCancellingCopy
            : !_isBusy && _previewIsCurrent && _itemCollectionState.CopyTargets.Any();
        public bool CanEditFilters => !_isBusy && _previewIsCurrent;
        public bool CanApplyFilter => CanEditFilters && FilterConditions.All(item => item.IsValid);
        public PhotoImporterPreset SelectedPreset
        {
            get => _selectedPreset;
            private set
            {
                if (!Set(ref _selectedPreset, value)) return;
                _lastAppliedPresetId = value?.Id;
                NotifyPresetStateChanged();
            }
        }
        public bool HasPresetChanges
        {
            get
            {
                var current = CapturePresetSnapshot();
                return SelectedPreset != null
                    ? !current.Matches(SelectedPreset)
                    : _unselectedPresetBaseline != null && !current.EquivalentTo(_unselectedPresetBaseline);
            }
        }
        public string PresetStatusText => SelectedPreset == null
            ? HasPresetChanges
                ? AppLocalization.Text("(プリセットなし) ● 未保存の変更", "(No preset) ● Unsaved changes")
                : AppLocalization.Text("(プリセットなし)", "(No preset)")
            : HasPresetChanges ? AppLocalization.Text("● 未保存の変更", "● Unsaved changes") : string.Empty;
        public Visibility PresetStatusVisibility => string.IsNullOrEmpty(PresetStatusText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        public bool CanUndoPresetApply => _presetUndo != null && !_isBusy;
        public Visibility PresetUndoVisibility => _presetUndo == null
            ? Visibility.Collapsed
            : Visibility.Visible;
        public int AppliedFilterCount
        {
            get => _appliedFilterCount;
            private set
            {
                if (_appliedFilterCount == value) return;
                _appliedFilterCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilterSummary));
                OnPropertyChanged(nameof(FilterSummaryToolTip));
            }
        }
        public string FilterSummary
        {
            get
            {
                if (_appliedFilterConditionSummaries.Count == 0) return AppLocalization.Text("フィルター: 条件なし", "Filter: no conditions");
                var shown = string.Join(" / ", _appliedFilterConditionSummaries.Take(2));
                var remainder = _appliedFilterConditionSummaries.Count - 2;
                return AppLocalization.Text("フィルター: ", "Filter: ") + shown +
                       (remainder > 0 ? AppLocalization.Format(" / ほか{0}件", " / {0} more", remainder) : string.Empty);
            }
        }
        public string FilterSummaryToolTip
        {
            get
            {
                var applied = _appliedFilterConditionSummaries.Count == 0
                    ? AppLocalization.Text("適用中の条件はありません。", "No conditions are applied.")
                    : AppLocalization.Text("適用中:", "Applied:") + Environment.NewLine +
                      string.Join(Environment.NewLine, _appliedFilterConditionSummaries.Select((item, index) =>
                          string.Format("{0}. {1}", index + 1, item)));
                if (!HasUnappliedFilterChanges) return applied;
                var draft = FilterConditions.Count == 0
                    ? AppLocalization.Text("条件なし", "No conditions")
                    : string.Join(Environment.NewLine, FilterConditions.Select((item, index) =>
                        string.Format("{0}. {1}", index + 1, item.Summary)));
                return applied + Environment.NewLine + Environment.NewLine +
                       AppLocalization.Text("未適用の編集:", "Unapplied edits:") + Environment.NewLine + draft;
            }
        }
        public bool HasUnappliedFilterChanges => !string.Equals(
            _appliedFilterStateKey,
            BuildCurrentFilterStateKey(),
            StringComparison.Ordinal);
        public string FilterEditStatus => HasUnappliedFilterChanges
            ? AppLocalization.Format("未適用の変更あり（編集中 {0} 件）", "Unapplied changes ({0} editing)", FilterConditions.Count)
            : AppLocalization.Format("適用済み {0} 件", "{0} applied", AppliedFilterCount);
        public Visibility FilterEditStatusVisibility => HasUnappliedFilterChanges
            ? Visibility.Visible
            : Visibility.Collapsed;
        public Visibility ExifSettingsOverlayVisibility => _activeOverlay == OverlayPanel.ExifSettings
            ? Visibility.Visible
            : Visibility.Collapsed;
        public Visibility FilterOverlayVisibility => _activeOverlay == OverlayPanel.Filter
            ? Visibility.Visible
            : Visibility.Collapsed;
        public Visibility PresetManagerOverlayVisibility => _activeOverlay == OverlayPanel.PresetManager
            ? Visibility.Visible
            : Visibility.Collapsed;
        public PhotoImporterPreset SelectedManagedPreset
        {
            get => _selectedManagedPreset;
            set
            {
                if (!Set(ref _selectedManagedPreset, value)) return;
                OnPropertyChanged(nameof(CanManageSelectedPreset));
                OnPropertyChanged(nameof(ManagedPresetSettingRows));
                OnPropertyChanged(nameof(ManagedPresetInformationRows));
                OnPropertyChanged(nameof(ManagedPresetWarning));
                OnPropertyChanged(nameof(ManagedPresetWarningVisibility));
            }
        }
        public string PresetManagerSortMode
        {
            get => _presetManagerSortMode;
            set
            {
                if (!Set(ref _presetManagerSortMode, value)) return;
                RefreshManagedPresets(SelectedManagedPreset?.Id);
            }
        }
        public bool CanManageSelectedPreset => SelectedManagedPreset != null;
        public IReadOnlyList<PresetDetailRow> ManagedPresetSettingRows =>
            PresetDetailRows.CreateSettings(SelectedManagedPreset);
        public IReadOnlyList<PresetDetailRow> ManagedPresetInformationRows =>
            PresetDetailRows.CreateInformation(SelectedManagedPreset);
        public string ManagedPresetWarning => GetPresetValidationWarning(SelectedManagedPreset);
        public Visibility ManagedPresetWarningVisibility => string.IsNullOrEmpty(ManagedPresetWarning)
            ? Visibility.Collapsed
            : Visibility.Visible;
        public string CopyButtonText
        {
            get
            {
                if (_isCopying)
                    return _copyPauseController != null && _copyPauseController.IsPauseRequested
                        ? AppLocalization.Text("再開", "Resume")
                        : AppLocalization.Text("一時停止", "Pause");
                return AppLocalization.Format("コピー ({0})", "Copy ({0})", _itemCollectionState.GetCounts().Selected);
            }
        }
        public string ViewSelectionSummary
        {
            get
            {
                var counts = _itemCollectionState.GetCounts();
                return AppLocalization.Format(
                    "表示 {0} / 全 {1}　チェック {2}（表示外 {3}）",
                    "Visible {0} / Total {1}  Selected {2} ({3} hidden)",
                    counts.Visible,
                    counts.Total,
                    counts.Selected,
                    counts.HiddenSelected);
            }
        }

        public bool UncheckHiddenItems
        {
            get => _itemCollectionState.UncheckHiddenItems;
            set
            {
                if (_itemCollectionState.UncheckHiddenItems == value) return;
                _isUpdatingSelection = true;
                try
                {
                    _itemCollectionState.SetUncheckHiddenItems(value);
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
                OnPropertyChanged();
                UpdateSummary();
            }
        }

        private void SelectSource_Click(object sender, RoutedEventArgs e) =>
            SourceFolder = SelectFolder(SourceFolder, AppLocalization.Text("コピー元フォルダーを選択してください", "Select the source folder")) ?? SourceFolder;

        private void SelectDestination_Click(object sender, RoutedEventArgs e) =>
            DestinationFolder = SelectFolder(DestinationFolder, AppLocalization.Text("コピー先フォルダーを選択してください", "Select the destination folder")) ?? DestinationFolder;

        private void SelectExifCacheRoot_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectFolder(ExifCacheRoot, AppLocalization.Text("Exif キャッシュの保存先を選択してください", "Select the Exif cache location"));
            if (selected != null) ChangeExifCacheRoot(Path.GetFullPath(selected), false);
        }

        private void ResetExifCacheRoot_Click(object sender, RoutedEventArgs e) =>
            ChangeExifCacheRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ExifCache")), true);

        private void ManageExifCache_Click(object sender, RoutedEventArgs e)
        {
            var manager = new ExifCacheManagerWindow(
                this,
                ExifCacheRoot,
                _previousExifCacheRoots,
                SourceFolder);
            manager.ShowDialog();

            var remaining = manager.RemainingPreviousRoots;
            if (_previousExifCacheRoots.SequenceEqual(remaining, StringComparer.OrdinalIgnoreCase)) return;
            _previousExifCacheRoots.Clear();
            _previousExifCacheRoots.AddRange(remaining);
        }

        private void ShowExifSettingsOverlay_Click(object sender, RoutedEventArgs e) =>
            SetActiveOverlay(OverlayPanel.ExifSettings);

        private void ShowFilterOverlay_Click(object sender, RoutedEventArgs e) =>
            SetActiveOverlay(OverlayPanel.Filter);

        private void ShowPresetManager_Click(object sender, RoutedEventArgs e)
        {
            ReloadPresets(SelectedPreset?.Id, true);
            RefreshManagedPresets(SelectedPreset?.Id);
            SetActiveOverlay(OverlayPanel.PresetManager);
        }

        private void CloseOverlay_Click(object sender, RoutedEventArgs e) =>
            CloseOverlay(true);

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _activeOverlay == OverlayPanel.None) return;
            CloseOverlay(true);
            e.Handled = true;
        }

        private void QueueImagePreview(PreviewItem item)
        {
            CancelImagePreviewRequest();
            SetImagePreviewState(null, string.Empty);
            if (!ShowImagePreview) return;
            if (item == null)
            {
                SetImagePreviewState(null, AppLocalization.Text("一覧から画像を選択してください。", "Select an image from the list."));
                return;
            }
            if (item.IsScanError)
            {
                SetImagePreviewState(null, AppLocalization.Text("スキャンエラーの項目はプレビューできません。", "Items with scan errors cannot be previewed."));
                return;
            }
            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                SetImagePreviewState(null, AppLocalization.Text("コピー元フォルダーを指定してください。", "Specify the source folder."));
                return;
            }

            string previewPath;
            try
            {
                var sourceRoot = Path.GetFullPath(SourceFolder);
                previewPath = Path.GetFullPath(Path.Combine(sourceRoot, item.ImagePreviewSourcePath));
                if (!IsSameOrUnder(previewPath, sourceRoot))
                    throw new InvalidOperationException(AppLocalization.Text("コピー元フォルダー外の画像はプレビューできません。", "Images outside the source folder cannot be previewed."));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException ||
                                       ex is InvalidOperationException)
            {
                SetImagePreviewState(null, AppLocalization.Text("プレビュー元を確認できません: ", "Unable to access the preview source: ") + ex.Message);
                return;
            }

            var requestVersion = _imagePreviewRequestVersion;
            var cancellation = new CancellationTokenSource();
            _imagePreviewCancellation = cancellation;
            SetImagePreviewState(null, AppLocalization.Text("読込中...", "Loading..."));
            _ = LoadImagePreviewAsync(
                item,
                previewPath,
                PhotoFileClassifier.Classify(item.SourcePath),
                requestVersion,
                cancellation);
        }

        private async Task LoadImagePreviewAsync(
            PreviewItem item,
            string previewPath,
            PhotoFileType originalFileType,
            int requestVersion,
            CancellationTokenSource cancellation)
        {
            var gateEntered = false;
            try
            {
                await Task.Delay(200, cancellation.Token);
                await _imagePreviewLoadGate.WaitAsync(cancellation.Token);
                gateEntered = true;
                if (!IsCurrentImagePreviewRequest(item, requestVersion)) return;

                var token = cancellation.Token;
                var result = await Task.Run(() => _imagePreviewLoader.Load(
                    previewPath,
                    originalFileType,
                    ImagePreviewLoader.DefaultMaximumWidth,
                    ImagePreviewLoader.DefaultMaximumHeight,
                    token), token);
                if (!IsCurrentImagePreviewRequest(item, requestVersion)) return;

                SetImagePreviewState(
                    result.Image,
                    result.IsSuccess ? string.Empty : result.Message);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrentImagePreviewRequest(item, requestVersion))
                    SetImagePreviewState(null, AppLocalization.Text("画像プレビューを読み込めません: ", "Unable to load the image preview: ") + ex.Message);
            }
            finally
            {
                if (gateEntered) _imagePreviewLoadGate.Release();
                if (ReferenceEquals(_imagePreviewCancellation, cancellation))
                    _imagePreviewCancellation = null;
                cancellation.Dispose();
            }
        }

        private bool IsCurrentImagePreviewRequest(PreviewItem item, int requestVersion) =>
            ShowImagePreview &&
            requestVersion == _imagePreviewRequestVersion &&
            ReferenceEquals(SelectedPreviewItem, item);

        private void CancelImagePreviewRequest()
        {
            unchecked { _imagePreviewRequestVersion++; }
            var cancellation = _imagePreviewCancellation;
            _imagePreviewCancellation = null;
            cancellation?.Cancel();
        }

        private void ResetImagePreviewForSourceChange()
        {
            CancelImagePreviewRequest();
            SetImagePreviewState(
                null,
                ShowImagePreview
                    ? AppLocalization.Text("コピー元が変更されました。再スキャンして画像を選択してください。", "The source changed. Scan again and select an image.")
                    : string.Empty);
        }

        private void SetImagePreviewState(BitmapSource source, string status)
        {
            _imagePreviewSource = source;
            _imagePreviewStatus = status ?? string.Empty;
            OnPropertyChanged(nameof(ImagePreviewSource));
            OnPropertyChanged(nameof(ImagePreviewStatus));
            OnPropertyChanged(nameof(ImagePreviewPlaceholderVisibility));
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            await ScanAsync();
        }

        private async Task<bool> ScanAsync()
        {
            CancellationTokenSource scanCancellation = null;
            string inputHistoryWarning = null;
            _presetUndo = null;
            NotifyPresetStateChanged();
            SetBusy(true, false);
            SelectedPreviewItem = null;
            Items.Clear();
            _previewIsCurrent = false;
            Summary = AppLocalization.Text("スキャン中...", "Scanning...");
            SetMessage(AppLocalization.Text("ファイルを調べています...", "Inspecting files..."), Brushes.DimGray);

            try
            {
                var sourceRoot = Path.GetFullPath(SourceFolder);
                var destinationRoot = Path.GetFullPath(DestinationFolder);
                ValidateRoots(sourceRoot, destinationRoot);

                var parseResult = TemplateParser.Parse(TemplateText);
                if (!parseResult.IsValid)
                {
                    ShowTemplateError(parseResult.Error);
                    return false;
                }
                var overwrite = OverwriteExisting;
                var sourceFileSelectionMode = _sourceFileSelectionMode;
                var sidecarPolicy = CreateSidecarPolicy(
                    AssociateSidecars,
                    SidecarExtensionsText);
                var rawJpegAnalysisMode = AnalyzeJpegOnlyForRawJpegPair
                    ? RawJpegAnalysisMode.JpegOnlyForPair
                    : RawJpegAnalysisMode.AnalyzeBoth;
                var useExifCache = UseExifCache;
                var readExifInformation = ReadExifInformation;
                var exifCacheRoot = ExifCacheRoot;
                var shouldReadExif = parseResult.Template.RequiresExif || readExifInformation ||
                                     (_appliedFilter != null && _appliedFilter.RequiresExif);
                inputHistoryWarning = await RecordInputHistoryAsync(
                    sourceRoot,
                    destinationRoot,
                    TemplateText);
                IProgress<PhotoMetadataScanProgress> exifProgress = null;
                if (shouldReadExif)
                {
                    scanCancellation = new CancellationTokenSource();
                    _scanCancellation = scanCancellation;
                    SetScanningExif(true);
                    _exifCacheHits = 0;
                    ProgressPercent = 0;
                    IsProgressIndeterminate = true;
                    ProgressText = AppLocalization.Text("対象ファイルを検索しています...", "Searching for files...");
                    SetMessage(AppLocalization.Text("Exif情報を読み取っています...", "Reading Exif data..."), Brushes.DimGray);
                    exifProgress = new Progress<PhotoMetadataScanProgress>(UpdateExifScanProgress);
                }
                var cancellationToken = scanCancellation == null
                    ? CancellationToken.None
                    : scanCancellation.Token;
                var preview = await Task.Run(() => BuildPreview(
                    sourceRoot,
                    destinationRoot,
                    parseResult.Template,
                    overwrite,
                    sourceFileSelectionMode,
                    sidecarPolicy,
                    rawJpegAnalysisMode,
                    useExifCache,
                    shouldReadExif,
                    exifCacheRoot,
                    exifProgress,
                    cancellationToken), cancellationToken);
                foreach (var row in preview.Items)
                {
                    row.PropertyChanged += PreviewItem_PropertyChanged;
                    Items.Add(row);
                }
                RefreshItemsView();

                _previewIsCurrent = true;
                UpdateSummary();
                if (!string.IsNullOrEmpty(inputHistoryWarning))
                    preview.Warnings.Insert(0, inputHistoryWarning);
                if (preview.Warnings.Count > 0)
                    SetMessage(string.Join(" ", preview.Warnings), Brushes.DarkGoldenrod);
                else
                    SetMessage(preview.Items.Count == 0
                        ? AppLocalization.Text("コピー元にファイルがありません。", "No files were found in the source folder.")
                        : _exifCacheHits > 0
                            ? AppLocalization.Format("プレビューを更新しました（Exif キャッシュ {0} 件）。", "Preview updated ({0} Exif cache hits).", _exifCacheHits)
                            : AppLocalization.Text("プレビューを更新しました。", "Preview updated."), Brushes.DimGray);
                return true;
            }
            catch (OperationCanceledException) when (scanCancellation != null && scanCancellation.IsCancellationRequested)
            {
                SetMessage(AppLocalization.Text("Exifスキャンを停止しました。解析済みのExifデータはキャッシュへ保存しました。", "Exif scanning stopped. Exif data already analyzed was saved to the cache."), Brushes.DimGray);
            }
            catch (TemplateException ex) { ShowTemplateError(ex.Error); }
            catch (UnauthorizedAccessException) { SetMessage(AppLocalization.Text("アクセスできないフォルダーがあります。権限を確認してください。", "A folder could not be accessed. Check its permissions."), Brushes.Firebrick); }
            catch (Exception ex) { SetMessage(ex.Message, Brushes.Firebrick); }
            finally
            {
                SetScanningExif(false);
                if (ReferenceEquals(_scanCancellation, scanCancellation)) _scanCancellation = null;
                scanCancellation?.Dispose();
                SetBusy(false, false);
                if (Summary == AppLocalization.Text("スキャン中...", "Scanning...")) Summary = AppLocalization.Text("0 件", "0 items");
            }
            return false;
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_isCopying)
            {
                ToggleCopyPause();
                return;
            }

            var selected = _itemCollectionState.CopyTargets.ToList();
            if (selected.Count == 0) return;
            var selectionBeforeCopy = PreviewSelectionState.Capture(Items);
            var orderedPlan = selected
                .OrderBy(item => item.IsAssociatedSidecar ? 1 : 0)
                .Select(item => item.CopyPlan)
                .ToList();

            var copyCancellation = new CancellationTokenSource();
            CopyPauseController pauseController = null;
            var pauseProgress = new Progress<CopyPauseState>(state =>
                UpdateCopyPauseState(pauseController));
            pauseController = new CopyPauseController(TimeSpan.FromSeconds(3), pauseProgress);
            _copyCancellation = copyCancellation;
            _copyPauseController = pauseController;
            _copyPauseState = CopyPauseState.Running;
            _isCancellingCopy = false;
            SetBusy(true, true);
            SetMessage(AppLocalization.Text("コピーしています...", "Copying..."), Brushes.DimGray);
            IsProgressIndeterminate = false;
            ProgressPercent = 0;
            var progressStatistics = new CopyProgressStatistics();
            StartCopyProgressTracking(
                progressStatistics,
                orderedPlan.Count,
                orderedPlan.Sum(item => item.SourceSnapshot.FileSize));

            CopyBatchResult result = null;
            try
            {
                result = await Task.Run(() => new CopyEngine().Execute(
                    orderedPlan,
                    Path.GetFullPath(SourceFolder),
                    progressStatistics,
                    copyCancellation.Token,
                    pauseController));
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message, Brushes.Firebrick);
            }
            finally
            {
                if (ReferenceEquals(_copyCancellation, copyCancellation)) _copyCancellation = null;
                if (ReferenceEquals(_copyPauseController, pauseController)) _copyPauseController = null;
                copyCancellation.Dispose();
                pauseController.Dispose();
                _copyPauseState = CopyPauseState.Running;
                _isCancellingCopy = false;
                StopCopyProgressTracking(progressStatistics);
                SetBusy(false, false);
            }

            if (result == null) return;
            if (result.Aborted)
            {
                SetMessage(
                    AppLocalization.Format(
                        "{0}（中止までに成功 {1} 件）",
                        "{0} ({1} succeeded before the operation stopped)",
                        result.BatchError,
                        result.Items.Count(item => item.Status == CopyItemStatus.Copied)),
                    Brushes.Firebrick);
                return;
            }

            var copied = result.Items.Count(item => item.Status == CopyItemStatus.Copied);
            var failed = result.Items.Count(item => item.Status == CopyItemStatus.Failed);
            var errors = result.Items
                .Where(item => item.Status != CopyItemStatus.Copied)
                .ToDictionary(
                    item => MakeRelative(Path.GetFullPath(SourceFolder), item.Item.SourcePath),
                    item => item.RecoveryPath == null
                        ? item.Error
                        : item.Error + AppLocalization.Text(" 保全した一時ファイル: ", " Preserved temporary file: ") + item.RecoveryPath,
                    StringComparer.OrdinalIgnoreCase);

            var rescanned = await ScanAsync();
            if (!rescanned)
            {
                var rescanError = Message;
                SetMessage(
                    FormatCopyCompletion(copied, failed, result.Cancelled) +
                    AppLocalization.Text(
                        " コピー後の一覧を更新できませんでした。手動でスキャンしてください。",
                        " The list could not be refreshed after copying. Scan again manually.") +
                    (string.IsNullOrWhiteSpace(rescanError)
                        ? string.Empty
                        : AppLocalization.Text(" 詳細: ", " Details: ") + rescanError),
                    Brushes.Firebrick);
                return;
            }

            string postProcessingError;
            _isUpdatingSelection = true;
            try
            {
                postProcessingError = CopyPostProcessing.TryRun(
                    () => PreviewSelectionState.RestoreAfterCopy(Items, selectionBeforeCopy, errors),
                    () =>
                    {
                        _itemCollectionState.Refresh();
                        UpdateSummary();
                    });
            }
            finally
            {
                _isUpdatingSelection = false;
            }

            OnPropertyChanged(nameof(CanCopy));
            if (postProcessingError == null)
            {
                SetMessage(
                    FormatCopyCompletion(copied, failed, result.Cancelled) +
                    AppLocalization.Text(" 再スキャンしました。", " The list was rescanned."),
                    failed > 0 ? Brushes.Firebrick : Brushes.DimGray);
            }
            else
            {
                SetMessage(
                    FormatCopyCompletion(copied, failed, result.Cancelled) +
                    AppLocalization.Text(
                        " コピー後の一覧表示を更新できませんでした。手動でスキャンしてください。詳細: ",
                        " The list display could not be refreshed after copying. Scan again manually. Details: ") +
                    postProcessingError,
                    Brushes.Firebrick);
            }
        }

        private static string FormatCopyCompletion(int copied, int failed, bool cancelled) =>
            AppLocalization.Format(
                "コピー完了: 成功 {0} / エラー {1}{2}。",
                "Copy complete: {0} succeeded / {1} errors{2}.",
                copied,
                failed,
                cancelled ? AppLocalization.Text(" / キャンセル", " / cancelled") : string.Empty);

        private async void MainWindow_ContentRendered(object sender, EventArgs e)
        {
            ContentRendered -= MainWindow_ContentRendered;
            if (string.IsNullOrWhiteSpace(DestinationFolder)) return;

            try
            {
                var destinationRoot = Path.GetFullPath(DestinationFolder);
                var result = await Task.Run(() => new PartialRecoveryDetector().Scan(destinationRoot));
                if (result.Candidates.Count > 0)
                {
                    MessageBox.Show(
                        this,
                        BuildPartialRecoveryMessage(result),
                        AppLocalization.Text("残存する一時ファイル", "Remaining temporary files"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    SetMessage(
                        AppLocalization.Format(
                            "コピー先に残存する一時ファイルを {0} 件検出しました。自動削除せず保全しています。",
                            "Found {0} remaining temporary files in the destination. They were preserved and not deleted automatically.",
                            result.Candidates.Count),
                        Brushes.DarkGoldenrod);
                }
                else if (result.Warnings.Count > 0)
                {
                    SetMessage(
                        AppLocalization.Text(
                            "残存する一時ファイルを完全には検査できませんでした。コピー先の状態と権限を確認してください。",
                            "The remaining temporary files could not be inspected completely. Check the destination and its permissions."),
                        Brushes.DarkGoldenrod);
                }
            }
            catch (Exception ex)
            {
                SetMessage(
                    AppLocalization.Text(
                        "残存する一時ファイルを検査できませんでした: ",
                        "Unable to inspect remaining temporary files: ") + ex.Message,
                    Brushes.DarkGoldenrod);
            }
        }

        internal static string BuildPartialRecoveryMessage(PartialRecoveryScanResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var message = new StringBuilder()
                .AppendFormat(
                    AppLocalization.Text(
                        "コピー先に Photo Importer の命名規則に一致する一時ファイルが {0} 件残っています。",
                        "There are {0} temporary files in the destination that match the Photo Importer naming convention."),
                    result.Candidates.Count)
                .AppendLine()
                .AppendLine(AppLocalization.Text(
                    "コピーの成否や対応する正式ファイルは一時名だけでは判断できないため、自動削除・自動昇格はしていません。",
                    "The temporary names alone cannot determine whether copying succeeded or which final file they belong to, so the files were not deleted or promoted automatically."))
                .AppendLine()
                .AppendLine(AppLocalization.Text("検出したファイル:", "Detected files:"));

            const int maximumDisplayedPaths = 12;
            foreach (var candidate in result.Candidates.Take(maximumDisplayedPaths))
                message.AppendLine(candidate.Path);
            if (result.Candidates.Count > maximumDisplayedPaths)
                message.AppendFormat(
                    AppLocalization.Text("ほか {0} 件", "{0} more"),
                    result.Candidates.Count - maximumDisplayedPaths).AppendLine();

            message
                .AppendLine()
                .AppendLine(AppLocalization.Text("状態を確認して、次のように対応してください:", "Check the state and take the following action:"))
                .AppendLine("• " + AppLocalization.UserMessage(PartialRecoveryGuidance.Describe(PartialRecoveryDestinationState.Missing)))
                .AppendLine("• " + AppLocalization.UserMessage(PartialRecoveryGuidance.Describe(PartialRecoveryDestinationState.MatchesExpectedSource)))
                .AppendLine("• " + AppLocalization.UserMessage(PartialRecoveryGuidance.Describe(PartialRecoveryDestinationState.MatchesPreviousSnapshot)))
                .AppendLine("• " + AppLocalization.UserMessage(PartialRecoveryGuidance.Describe(PartialRecoveryDestinationState.RequiresComparison)))
                .AppendLine()
                .Append(AppLocalization.Text(
                    "元写真が利用できる場合は、元写真を正として手動で再スキャンしてください。",
                    "If the original photos are available, treat them as authoritative and scan again manually."));

            return message.ToString();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isCopying)
            {
                _isCancellingCopy = true;
                OnPropertyChanged(nameof(CanCopy));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CopyButtonText));
            }
            _scanCancellation?.Cancel();
            _copyCancellation?.Cancel();
            SetMessage(_isScanningExif
                ? AppLocalization.Text("Exifスキャンを停止しています。現在のファイルを完了してキャッシュを保存します...", "Stopping Exif scanning. Finishing the current file and saving the cache...")
                : AppLocalization.Text("キャンセルしています...", "Cancelling..."), Brushes.DimGray);
        }

        private void ToggleCopyPause()
        {
            var controller = _copyPauseController;
            if (controller == null || _isCancellingCopy) return;

            if (controller.IsPauseRequested)
                controller.Resume();
            else
                controller.RequestPause();

            UpdateCopyPauseState(controller);
        }

        private void UpdateCopyPauseState(CopyPauseController controller)
        {
            if (!ReferenceEquals(_copyPauseController, controller)) return;

            var previousState = _copyPauseState;
            _copyPauseState = controller.State;
            _copyProgressStatistics?.SetPaused(
                _copyPauseState == CopyPauseState.PausedBetweenFiles ||
                _copyPauseState == CopyPauseState.PausedWithinFile);
            RefreshCopyProgressDisplay();
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(CopyButtonText));
            if (_isCancellingCopy) return;

            switch (_copyPauseState)
            {
                case CopyPauseState.PausePending:
                    SetMessage(AppLocalization.Text("現在のファイル完了後に一時停止します...", "Pausing after the current file completes..."), Brushes.DimGray);
                    break;
                case CopyPauseState.NativePauseRequested:
                    SetMessage(AppLocalization.Text("3秒経過したため、現在のファイルを一時停止しています...", "Three seconds elapsed; pausing the current file..."), Brushes.DimGray);
                    break;
                case CopyPauseState.PausedBetweenFiles:
                case CopyPauseState.PausedWithinFile:
                    SetMessage(AppLocalization.Text("コピーを一時停止しました。", "Copying paused."), Brushes.DimGray);
                    break;
                case CopyPauseState.Running:
                    if (previousState != CopyPauseState.Running)
                        SetMessage(AppLocalization.Text("コピーを再開しました...", "Copying resumed..."), Brushes.DimGray);
                    break;
            }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var select = SelectAllState != true;
            _isUpdatingSelection = true;
            try
            {
                _itemCollectionState.SetAllVisibleCopyable(select);
            }
            finally
            {
                _isUpdatingSelection = false;
            }
            OnPropertyChanged(nameof(CanCopy));
            UpdateSummary();
        }

        private void StartCopyProgressTracking(
            CopyProgressStatistics statistics,
            int totalFiles,
            long totalBytes)
        {
            _copyProgressStatistics = statistics;
            CopyProgressSummaryText = AppLocalization.Format(
                "処理済み 0 / {0} 件    容量 0 B / {1}",
                "Processed 0 / {0} items    Size 0 B / {1}",
                totalFiles,
                FormatBytes(totalBytes));
            CopyProgressRatesText = AppLocalization.Text("全体平均 計算中...    直近1分 計算中...", "Overall average calculating...    Last minute calculating...");
            CopyProgressTimeText = AppLocalization.Text("経過 00:00:00    残り 計算中...", "Elapsed 00:00:00    Remaining calculating...");
            CopyProgressPercentText = "0%";

            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += CopyProgressTimer_Tick;
            _copyProgressTimer = timer;
            timer.Start();
        }

        private void StopCopyProgressTracking(CopyProgressStatistics statistics)
        {
            if (!ReferenceEquals(_copyProgressStatistics, statistics)) return;

            RefreshCopyProgressDisplay();
            var timer = _copyProgressTimer;
            _copyProgressTimer = null;
            _copyProgressStatistics = null;
            if (timer == null) return;
            timer.Stop();
            timer.Tick -= CopyProgressTimer_Tick;
        }

        private void CopyProgressTimer_Tick(object sender, EventArgs e) =>
            RefreshCopyProgressDisplay();

        private void RefreshCopyProgressDisplay()
        {
            var statistics = _copyProgressStatistics;
            if (statistics == null) return;

            var snapshot = statistics.Capture();
            var progress = snapshot.Progress;
            if (progress == null) return;

            ProgressPercent = progress.TotalBytes == 0
                ? 100
                : Math.Min(100, progress.CompletedWorkBytes * 100.0 / progress.TotalBytes);
            CopyProgressPercentText = ProgressPercent.ToString("0") + "%";
            CopyProgressSummaryText = AppLocalization.Format(
                "処理済み {0} / {1} 件    容量 {2} / {3}",
                "Processed {0} / {1} items    Size {2} / {3}",
                progress.CompletedFiles,
                progress.TotalFiles,
                FormatBytes(progress.CompletedWorkBytes),
                FormatBytes(progress.TotalBytes));

            CopyProgressRatesText = AppLocalization.Format(
                "全体平均 {0}    直近1分 {1}",
                "Overall average {0}    Last minute {1}",
                FormatCopyRates(
                    snapshot.OverallFilesPerSecond,
                    snapshot.OverallBytesPerSecond),
                FormatCopyRates(
                    snapshot.RecentFilesPerSecond,
                    snapshot.RecentBytesPerSecond));

            string remaining;
            if (!snapshot.EstimatedRemaining.HasValue)
                remaining = AppLocalization.Text("計算中...", "Calculating...");
            else if (snapshot.EstimatedRemaining.Value == TimeSpan.Zero)
                remaining = "00:00:00";
            else
                remaining = AppLocalization.Text("約 ", "About ") + FormatDuration(snapshot.EstimatedRemaining.Value);
            if (snapshot.IsPaused) remaining += AppLocalization.Text("（一時停止中）", " (paused)");

            CopyProgressTimeText = AppLocalization.Format(
                "経過 {0}    残り {1}",
                "Elapsed {0}    Remaining {1}",
                FormatDuration(snapshot.ActiveElapsed),
                remaining);
        }

        private static string FormatCopyRates(
            double? filesPerSecond,
            double? bytesPerSecond)
        {
            if (!filesPerSecond.HasValue || !bytesPerSecond.HasValue)
                return AppLocalization.Text("計算中...", "Calculating...");

            return AppLocalization.Format(
                "{0} 件/秒・{1} MB/秒",
                "{0} items/s · {1} MB/s",
                FormatRateValue(filesPerSecond.Value),
                FormatRateValue(bytesPerSecond.Value / (1024d * 1024)));
        }

        private static string FormatRateValue(double value) =>
            value < 1 ? value.ToString("0.00") : value.ToString("0.0");

        private static string FormatDuration(TimeSpan duration)
        {
            var totalHours = Math.Max(0, (long)duration.TotalHours);
            return string.Format(
                "{0:00}:{1:00}:{2:00}",
                totalHours,
                duration.Minutes,
                duration.Seconds);
        }

        private void UpdateExifScanProgress(PhotoMetadataScanProgress progress)
        {
            _exifCacheHits = progress.CacheHits;
            switch (progress.Phase)
            {
                case PhotoMetadataScanPhase.Preparing:
                    IsProgressIndeterminate = true;
                    ProgressText = AppLocalization.Format(
                        "Exifスキャンを準備しています（解析対象 {0} 件）...",
                        "Preparing Exif scan ({0} items to analyze)...",
                        progress.TotalFiles);
                    break;
                case PhotoMetadataScanPhase.Reading:
                    IsProgressIndeterminate = false;
                    ProgressPercent = progress.TotalFiles == 0
                        ? 100
                        : Math.Min(100, progress.CompletedFiles * 100.0 / progress.TotalFiles);
                    ProgressText = AppLocalization.Format(
                        "Exif {0}/{1} 件（{2:0}%、キャッシュ {3} 件）",
                        "Exif {0}/{1} ({2:0}%, {3} cache hits)",
                        progress.CompletedFiles,
                        progress.TotalFiles,
                        ProgressPercent,
                        progress.CacheHits);
                    break;
                case PhotoMetadataScanPhase.SavingCache:
                    IsProgressIndeterminate = true;
                    ProgressText = AppLocalization.Text("Exifキャッシュを保存しています...", "Saving the Exif cache...");
                    break;
                case PhotoMetadataScanPhase.Completed:
                    IsProgressIndeterminate = true;
                    ProgressText = AppLocalization.Text("Exif結果を一覧へ反映しています...", "Applying Exif results to the list...");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static PreviewBuildResult BuildPreview(
            string sourceRoot,
            string destinationRoot,
            ParsedTemplate template,
            bool overwriteExisting,
            SourceFileSelectionMode sourceFileSelectionMode,
            SidecarPolicy sidecarPolicy,
            RawJpegAnalysisMode rawJpegAnalysisMode,
            bool useExifCache,
            bool readExifInformation,
            string exifCacheRoot,
            IProgress<PhotoMetadataScanProgress> exifProgress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sidecarPolicy == null) throw new ArgumentNullException(nameof(sidecarPolicy));
            var associateSidecars = sidecarPolicy.Enabled;
            var result = new List<PreviewItem>();
            var warnings = new List<string>();
            var destinationVolume = new WindowsVolumeInfoReader().Read(destinationRoot);
            var destinationTimestampPolicy = destinationVolume.TimestampPolicy;
            if (!destinationTimestampPolicy.IsSupported)
                throw new NotSupportedException(AppLocalization.Format(
                    "コピー先のファイルシステム「{0}」には対応していません。NTFS、ReFS、exFAT、FAT、FAT32 のいずれかを使用してください。",
                    "The destination file system \"{0}\" is not supported. Use NTFS, ReFS, exFAT, FAT, or FAT32.",
                    string.IsNullOrWhiteSpace(destinationVolume.FileSystemName)
                        ? AppLocalization.Text("不明", "Unknown")
                        : destinationVolume.FileSystemName));
            var destinationLookup = new FileSystemDestinationLookup(destinationRoot);
            var allocator = new DestinationAllocator(
                template,
                destinationLookup,
                destinationTimestampPolicy,
                overwriteExisting,
                destinationRoot);
            var scan = new SourceFileEnumerator().Enumerate(
                sourceRoot,
                sourceFileSelectionMode,
                sidecarPolicy,
                cancellationToken);
            foreach (var issue in scan.Issues)
                result.Add(PreviewItem.ForScanError(issue.Path, issue.Message));

            var sidecarPlan = SidecarAssociationPlan.Create(scan.Files, sidecarPolicy);
            warnings.AddRange(sidecarPlan.Warnings);
            var files = scan.Files.Where(path =>
                sourceFileSelectionMode == SourceFileSelectionMode.AllFiles ||
                PhotoFileClassifier.IsSupported(path) ||
                (associateSidecars && IsAssociatedSidecar(sidecarPlan, path)))
                .OrderBy(
                item => MakeRelative(sourceRoot, item), StringComparer.OrdinalIgnoreCase).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            var imagePreviewPlan = RawJpegAnalysisPlan.Create(
                files,
                RawJpegAnalysisMode.JpegOnlyForPair);
            RawJpegAnalysisPlan analysisPlan = null;
            var metadataBySource = new Dictionary<string, PhotoMetadataReadResult>(StringComparer.OrdinalIgnoreCase);
            if (template.RequiresExif || readExifInformation)
            {
                analysisPlan = RawJpegAnalysisPlan.Create(files, rawJpegAnalysisMode);
                cancellationToken.ThrowIfCancellationRequested();
                ExifCacheStore cacheStore = null;
                if (useExifCache &&
                    (IsSameOrUnder(exifCacheRoot, sourceRoot) || IsSameOrUnder(sourceRoot, exifCacheRoot) ||
                     IsSameOrUnder(exifCacheRoot, destinationRoot) || IsSameOrUnder(destinationRoot, exifCacheRoot)))
                {
                    warnings.Add(string.Format(
                        AppLocalization.Text(
                            "Exif キャッシュの保存先 ({0}) がコピー元またはコピー先と重なるため、キャッシュなしで続行しました。",
                            "The Exif cache location ({0}) overlaps the source or destination, so the scan continued without the cache."),
                        exifCacheRoot));
                }
                else if (useExifCache) cacheStore = new ExifCacheStore(exifCacheRoot);

                VolumeInfo volume = null;
                if (cacheStore != null)
                {
                    try
                    {
                        volume = new WindowsVolumeInfoReader().Read(sourceRoot);
                    }
                    catch (Exception ex) when (ex is Win32Exception || ex is IOException ||
                                                   ex is UnauthorizedAccessException)
                    {
                        warnings.Add(AppLocalization.Text(
                            "コピー元のボリューム情報を取得できないため、Exif キャッシュなしで続行しました: ",
                            "The source volume information could not be read, so the scan continued without the Exif cache: ") + ex.Message);
                        cacheStore = null;
                    }
                }
                var metadataScan = new CachedPhotoMetadataScanner().Scan(
                    analysisPlan, volume, cacheStore, DateTime.UtcNow, exifProgress, cancellationToken);
                foreach (var pair in metadataScan.Results)
                    metadataBySource.Add(pair.Key, pair.Value);
                warnings.AddRange(metadataScan.Warnings);
            }

            var previewByPath = new Dictionary<string, PreviewItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files
                .OrderBy(item => IsAssociatedSidecar(sidecarPlan, item) ? 1 : 0)
                .ThenBy(item => MakeRelative(sourceRoot, item), StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var sourceSnapshot = PreviewFileSnapshot.CaptureTarget(path);
                    var sourcePath = MakeRelative(sourceRoot, path);
                    var relativeDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                    var imagePreviewSource = imagePreviewPlan.GetAnalysisSource(path);
                    var analysisSource = analysisPlan == null ? path : analysisPlan.GetAnalysisSource(path);
                    var metadataResult = analysisPlan == null ? null : metadataBySource[analysisSource];
                    if (template.RequiresExif && metadataResult != null &&
                        metadataResult.Status == PhotoMetadataReadStatus.ReadError)
                        throw metadataResult.Error;
                    var metadata = metadataResult == null ? PhotoMetadata.Empty : metadataResult.Metadata;
                    var analysisSourceSnapshot = PreviewFileSnapshot.CaptureAnalysisSource(
                        sourceSnapshot,
                        path,
                        analysisSource);
                    var context = new FileTemplateContext(
                            sourceSnapshot.Name,
                            sourceSnapshot.LastWriteTime,
                            sourceSnapshot.Length,
                            relativeDirectory,
                            metadata,
                            sourceSnapshot.LastWriteTimeUtc,
                            analysisSourceSnapshot.LastWriteTime,
                            analysisSourceSnapshot.LastWriteTimeUtc,
                            (sourceSnapshot.Attributes & FileAttributes.ReadOnly) != 0);
                    SidecarAssociation sidecarAssociation = null;
                    var isAssociatedSidecar = associateSidecars &&
                                              sidecarPlan.TryGetAssociation(path, out sidecarAssociation);
                    DestinationAllocation allocation;
                    string relatedSourcePath = null;
                    string dependencySourcePath = null;
                    if (isAssociatedSidecar)
                    {
                        PreviewItem parent;
                        if (!previewByPath.TryGetValue(sidecarAssociation.ImagePath, out parent) ||
                            parent.IsScanError ||
                            string.IsNullOrWhiteSpace(parent.DestinationPath))
                        {
                            previewByPath[path] = PreviewItem.ForScanError(
                                sourcePath,
                                AppLocalization.Text(
                                    "関連先画像のコピー先を決定できません。",
                                    "The destination for the associated image could not be determined."));
                            continue;
                        }
                        var sidecarRelativePath = SidecarDestinationPath.Derive(
                            parent.DestinationPath,
                            path,
                            sidecarAssociation.NamingStyle);
                        allocation = allocator.AllocateFixed(
                            sidecarRelativePath,
                            sourceSnapshot.Length,
                            sourceSnapshot.LastWriteTimeUtc,
                            parent.Warnings,
                            parent.SequenceNumber,
                            overwriteExisting);
                        relatedSourcePath = parent.SourcePath;
                        if (parent.CopyPlan != null)
                            dependencySourcePath = Path.GetFullPath(sidecarAssociation.ImagePath);
                    }
                    else
                    {
                        Func<string, bool> orphanSidecarBlocker = null;
                        if (associateSidecars && IsImageFile(path))
                        {
                            var sourceSidecars = sidecarPlan.GetSidecars(path);
                            orphanSidecarBlocker = candidate =>
                                IsBlockedByDestinationOnlySidecar(
                                    candidate,
                                    sourceSidecars,
                                    destinationLookup,
                                    sidecarPolicy);
                        }
                        allocation = allocator.Allocate(
                            context,
                            sourceSnapshot.LastWriteTimeUtc,
                            orphanSidecarBlocker);
                    }
                    var destinationPath = Path.Combine(destinationRoot, allocation.RelativePath);
                    var effectiveStatus = isAssociatedSidecar &&
                                          previewByPath[sidecarAssociation.ImagePath].DestinationStatus ==
                                          DestinationStatus.Conflict
                        ? DestinationStatus.Conflict
                        : allocation.Status;
                    var plan = effectiveStatus == DestinationStatus.NotImported ||
                               effectiveStatus == DestinationStatus.Overwrite
                        ? new CopyPlanItem(
                            sourceSnapshot.FullName,
                            destinationRoot,
                            destinationPath,
                            new FileSnapshot(sourceSnapshot.Length, sourceSnapshot.LastWriteTimeUtc),
                            allocation.DestinationSnapshot,
                            destinationTimestampPolicy,
                            effectiveStatus == DestinationStatus.Overwrite,
                            dependencySourcePath)
                        : null;
                    previewByPath[path] = new PreviewItem(
                        sourcePath,
                        allocation.RelativePath,
                        effectiveStatus,
                        plan,
                        allocation.Warnings,
                        context,
                        metadataResult,
                        analysisPlan == null ? null : MakeRelative(sourceRoot, analysisSource),
                        allocation.SequenceNumber,
                        MakeRelative(sourceRoot, imagePreviewSource),
                        relatedSourcePath);
                }
                catch (UnauthorizedAccessException ex) { previewByPath[path] = PreviewItem.ForScanError(MakeRelative(sourceRoot, path), ex.Message); }
                catch (IOException ex) { previewByPath[path] = PreviewItem.ForScanError(MakeRelative(sourceRoot, path), ex.Message); }
                catch (TemplateException ex) { previewByPath[path] = PreviewItem.ForScanError(MakeRelative(sourceRoot, path), ex.Error.Code.ToString()); }
            }

            if (associateSidecars)
                ApplySidecarGroupConflicts(sidecarPlan, previewByPath);
            foreach (var path in files)
            {
                PreviewItem item;
                if (previewByPath.TryGetValue(path, out item)) result.Add(item);
            }
            return new PreviewBuildResult(result, warnings);
        }

        private static bool IsAssociatedSidecar(SidecarAssociationPlan plan, string path)
        {
            SidecarAssociation ignored;
            return plan.TryGetAssociation(path, out ignored);
        }

        private static bool IsImageFile(string path)
        {
            var type = PhotoFileClassifier.Classify(path);
            return type == PhotoFileType.Jpeg ||
                   type == PhotoFileType.Raw ||
                   type == PhotoFileType.OtherImage;
        }

        private static bool IsBlockedByDestinationOnlySidecar(
            string imageRelativePath,
            IReadOnlyList<SidecarAssociation> sourceSidecars,
            IDestinationFileLookup destinationLookup,
            SidecarPolicy sidecarPolicy)
        {
            var representedPaths = new HashSet<string>(
                sourceSidecars.Select(sidecar => SidecarDestinationPath.Derive(
                    imageRelativePath,
                    sidecar.SidecarPath,
                    sidecar.NamingStyle)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var potentialPath in SidecarDestinationPath.GetPotentialSidecarPaths(
                         imageRelativePath,
                         sidecarPolicy))
            {
                if (representedPaths.Contains(potentialPath)) continue;
                DestinationFileSnapshot ignored;
                if (destinationLookup.TryGetFile(potentialPath, out ignored)) return true;
            }
            return false;
        }

        private static void ApplySidecarGroupConflicts(
            SidecarAssociationPlan plan,
            IReadOnlyDictionary<string, PreviewItem> previewByPath)
        {
            foreach (var imagePair in previewByPath.Where(pair => IsImageFile(pair.Key)))
            {
                var associations = plan.GetSidecars(imagePair.Key);
                if (associations.Count == 0) continue;
                var parent = imagePair.Value;
                var children = associations
                    .Select(association =>
                    {
                        PreviewItem child;
                        return previewByPath.TryGetValue(association.SidecarPath, out child) ? child : null;
                    })
                    .Where(child => child != null)
                    .ToList();

                if (parent.DestinationStatus == DestinationStatus.Conflict)
                {
                    foreach (var child in children.Where(child => child.CanCopy))
                        child.BlockByRelatedConflict(AppLocalization.Text(
                            "関連先画像が競合しています。",
                            "The associated image has a conflict."));
                    continue;
                }

                if (parent.CopyPlan != null &&
                    children.Any(child => child.IsScanError ||
                                          child.DestinationStatus == DestinationStatus.Conflict))
                {
                    parent.BlockByRelatedConflict(AppLocalization.Text(
                        "関連サイドカーが競合または読取エラーです。",
                        "An associated sidecar has a conflict or read error."));
                    foreach (var child in children.Where(child => child.CanCopy))
                        child.BlockByRelatedConflict(AppLocalization.Text(
                            "関連先画像と同時にコピーできません。",
                            "This file cannot be copied together with the associated image."));
                }
            }
        }

        private void PreviewItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PreviewItem.IsSelected)) InvalidateSuggestions();
            if (e.PropertyName == nameof(PreviewItem.IsSelected))
            {
                if (_isUpdatingSelection) return;
                var changed = sender as PreviewItem;
                _isUpdatingSelection = true;
                try
                {
                    if (changed != null && changed.IsAssociatedSidecar && changed.IsSelected)
                    {
                        var parent = Items.FirstOrDefault(item => string.Equals(
                            item.SourcePath,
                            changed.RelatedSourcePath,
                            StringComparison.OrdinalIgnoreCase));
                        if (parent != null && parent.CanCopy) parent.IsSelected = true;
                        else if (parent != null && parent.DestinationStatus != DestinationStatus.Imported)
                            changed.IsSelected = false;
                    }
                    else if (changed != null && !changed.IsAssociatedSidecar && !changed.IsSelected)
                    {
                        foreach (var child in Items.Where(item =>
                            item.IsAssociatedSidecar &&
                            string.Equals(
                                item.RelatedSourcePath,
                                changed.SourcePath,
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            child.IsSelected = false;
                        }
                    }
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
                OnPropertyChanged(nameof(CanCopy));
                UpdateSummary();
            }
            else if (ReferenceEquals(sender, SelectedPreviewItem) &&
                     (e.PropertyName == nameof(PreviewItem.MetadataResult) ||
                      e.PropertyName == nameof(PreviewItem.MetadataSourcePath)))
            {
                OnPropertyChanged(nameof(ExifReadStatus));
                foreach (var item in ExifTokenDetails) item.SetPreviewItem(SelectedPreviewItem);
            }
        }

        internal void ApplyPreviewFilter(Predicate<PreviewItem> filter)
        {
            _isUpdatingSelection = true;
            try
            {
                _itemCollectionState.ApplyFilter(filter);
            }
            finally
            {
                _isUpdatingSelection = false;
            }

            if (SelectedPreviewItem != null &&
                !_itemCollectionState.VisibleItems.Contains(SelectedPreviewItem))
                SelectedPreviewItem = null;
            UpdateSummary();
        }

        private void AddFilterCondition_Click(object sender, RoutedEventArgs e)
        {
            var editor = new FilterConditionEditor(FilterFieldOptions);
            editor.PropertyChanged += FilterCondition_PropertyChanged;
            FilterConditions.Add(editor);
            NotifyFilterEditorStateChanged();
        }

        private void RemoveFilterCondition_Click(object sender, RoutedEventArgs e)
        {
            var editor = (sender as FrameworkElement)?.DataContext as FilterConditionEditor;
            if (editor == null) return;
            editor.PropertyChanged -= FilterCondition_PropertyChanged;
            FilterConditions.Remove(editor);
            NotifyFilterEditorStateChanged();
        }

        private async void ApplyFilter_Click(object sender, RoutedEventArgs e)
        {
            var conditions = new List<FilterCondition>();
            foreach (var editor in FilterConditions)
            {
                FilterCondition condition;
                string error;
                if (!editor.TryBuild(out condition, out error))
                {
                    OnPropertyChanged(nameof(CanApplyFilter));
                    return;
                }
                conditions.Add(condition);
            }

            var preparation = new FilterSet(conditions).Prepare();
            if (!preparation.IsValid) return;
            var prepared = preparation.Filter;

            if (prepared.RequiresExif && Items.Any(item => !item.IsScanError && item.MetadataResult == null))
            {
                if (!await LoadExifForFilterAsync(prepared, conditions.Count)) return;
            }
            else
            {
                TryCommitFilter(prepared, conditions.Count);
            }
        }

        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            foreach (var editor in FilterConditions) editor.PropertyChanged -= FilterCondition_PropertyChanged;
            FilterConditions.Clear();
            _appliedFilter = null;
            CommitAppliedFilterEditorState(0);
            ApplyPreviewFilter(null);
            SetMessage(AppLocalization.Text("一覧フィルターをクリアしました。", "List filter cleared."), Brushes.DimGray);
        }

        private void FilterCondition_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FilterConditionEditor.IsValid))
                NotifyFilterEditorStateChanged();
        }

        private bool TryCommitFilter(PreparedFilter prepared, int conditionCount)
        {
            try
            {
                foreach (var item in Items) prepared.Matches(item.CreateFilterCandidate());
                _appliedFilter = conditionCount == 0 ? null : prepared;
                CommitAppliedFilterEditorState(conditionCount);
                ApplyPreviewFilter(_appliedFilter == null
                    ? (Predicate<PreviewItem>)null
                    : item => _appliedFilter.Matches(item.CreateFilterCandidate()));
                SetMessage(conditionCount == 0
                    ? AppLocalization.Text("条件なしで全項目を表示しました。", "All items are shown with no conditions.")
                    : AppLocalization.Format("一覧フィルターを適用しました（{0} 条件）。", "List filter applied ({0} conditions).", conditionCount), Brushes.DimGray);
                return true;
            }
            catch (FilterEvaluationException ex)
            {
                SetMessage(AppLocalization.Text("フィルター評価エラー: ", "Filter evaluation error: ") + ex.Message, Brushes.Firebrick);
                return false;
            }
        }

        private async Task<bool> LoadExifForFilterAsync(PreparedFilter prepared, int conditionCount, bool applyFilter = true)
        {
            CancellationTokenSource scanCancellation = null;
            SetBusy(true, false);
            try
            {
                var sourceRoot = Path.GetFullPath(SourceFolder);
                var destinationRoot = Path.GetFullPath(DestinationFolder);
                ValidateRoots(sourceRoot, destinationRoot);
                var parseResult = TemplateParser.Parse(TemplateText);
                if (!parseResult.IsValid)
                {
                    ShowTemplateError(parseResult.Error);
                    return false;
                }

                scanCancellation = new CancellationTokenSource();
                _scanCancellation = scanCancellation;
                SetScanningExif(true);
                _exifCacheHits = 0;
                ProgressPercent = 0;
                IsProgressIndeterminate = true;
                ProgressText = AppLocalization.Text("Exifスキャン準備中...", "Preparing Exif scan...");
                SetMessage(AppLocalization.Text("フィルターに必要なExif情報を読み取っています。現在の一覧は完了まで維持されます...", "Reading Exif data required by the filter. The current list remains visible until completion..."), Brushes.DimGray);
                var progress = new Progress<PhotoMetadataScanProgress>(UpdateExifScanProgress);
                var token = scanCancellation.Token;
                var loadPlan = LazyExifPreviewLoadPlan.Capture(
                    sourceRoot,
                    destinationRoot,
                    Items,
                    AnalyzeJpegOnlyForRawJpegPair ? RawJpegAnalysisMode.JpegOnlyForPair : RawJpegAnalysisMode.AnalyzeBoth);
                var loadResult = await Task.Run(() => loadPlan.Load(
                    UseExifCache,
                    ExifCacheRoot,
                    progress,
                    token), token);

                var commit = loadResult.PrepareCommit(Items, prepared);
                commit.Apply();
                _exifCacheHits = loadResult.CacheHits;
                if (applyFilter)
                {
                    _appliedFilter = conditionCount == 0 ? null : prepared;
                    CommitAppliedFilterEditorState(conditionCount);
                }
                ApplyPreviewFilter(_appliedFilter == null
                    ? (Predicate<PreviewItem>)null
                    : item => _appliedFilter.Matches(item.CreateFilterCandidate()));
                SetMessage(loadResult.Warnings.Count == 0
                    ? !applyFilter ? AppLocalization.Text("候補に使うExif情報を読み込みました。", "Exif data is ready for candidates.")
                    : AppLocalization.Format("Exif情報を読み込み、一覧フィルターを適用しました（{0} 条件）。", "Exif data loaded and list filter applied ({0} conditions).", conditionCount)
                    : string.Join(" ", loadResult.Warnings),
                    loadResult.Warnings.Count == 0 ? Brushes.DimGray : Brushes.DarkGoldenrod);
                return true;
            }
            catch (OperationCanceledException) when (scanCancellation != null && scanCancellation.IsCancellationRequested)
            {
                SetMessage(AppLocalization.Text("Exifスキャンを停止しました。直前の一覧とフィルターを維持しています。", "Exif scanning stopped. The previous list and filter are unchanged."), Brushes.DimGray);
            }
            catch (FilterEvaluationException ex)
            {
                SetMessage(AppLocalization.Text("フィルター評価エラー: ", "Filter evaluation error: ") + ex.Message + AppLocalization.Text(" 直前の一覧を維持しています。", " The previous list is unchanged."), Brushes.Firebrick);
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message + AppLocalization.Text(" 直前の一覧とフィルターを維持しています。", " The previous list and filter are unchanged."), Brushes.Firebrick);
            }
            finally
            {
                SetScanningExif(false);
                if (ReferenceEquals(_scanCancellation, scanCancellation)) _scanCancellation = null;
                scanCancellation?.Dispose();
                SetBusy(false, false);
            }
            return false;
        }

        private void ReplacePreviewItems(
            IEnumerable<PreviewItem> replacement,
            IReadOnlyDictionary<string, bool> selection = null)
        {
            SelectedPreviewItem = null;
            Items.Clear();
            foreach (var row in replacement)
            {
                bool isSelected;
                if (selection != null && selection.TryGetValue(row.SourcePath, out isSelected))
                    row.IsSelected = isSelected;
                row.PropertyChanged += PreviewItem_PropertyChanged;
                Items.Add(row);
            }
            RefreshItemsView();
            UpdateSummary();
        }

        private void RefreshItemsView()
        {
            _isUpdatingSelection = true;
            try
            {
                _itemCollectionState.Refresh();
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        private void UpdateSummary()
        {
            var rows = Items.Where(item => !item.IsScanError).ToList();
            Summary = AppLocalization.Format(
                "{0} 件（対象 {1} / 未取込 {2} / 上書き {3} / 取込済 {4} / 競合・エラー {5}）",
                "{0} items (selected {1} / not imported {2} / overwrite {3} / imported {4} / conflicts or errors {5})",
                rows.Count,
                rows.Count(item => item.IsSelected && item.CanCopy),
                rows.Count(item => item.DestinationStatus == DestinationStatus.NotImported),
                rows.Count(item => item.DestinationStatus == DestinationStatus.Overwrite),
                rows.Count(item => item.DestinationStatus == DestinationStatus.Imported),
                Items.Count(item => item.IsScanError || item.DestinationStatus == DestinationStatus.Conflict));
            OnPropertyChanged(nameof(SelectAllState));
            OnPropertyChanged(nameof(CanSelectAll));
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(CopyButtonText));
            OnPropertyChanged(nameof(ViewSelectionSummary));
        }

        private void SetActiveOverlay(OverlayPanel overlay)
        {
            if (_activeOverlay == overlay) return;
            _activeOverlay = overlay;
            OnPropertyChanged(nameof(ExifSettingsOverlayVisibility));
            OnPropertyChanged(nameof(FilterOverlayVisibility));
            OnPropertyChanged(nameof(PresetManagerOverlayVisibility));
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_activeOverlay == OverlayPanel.ExifSettings) ExifOverlayCloseButton.Focus();
                else if (_activeOverlay == OverlayPanel.Filter) FilterOverlayCloseButton.Focus();
                else if (_activeOverlay == OverlayPanel.PresetManager) PresetManagerCloseButton.Focus();
            }), DispatcherPriority.Input);
        }

        private void CloseOverlay(bool restoreFocus)
        {
            var closing = _activeOverlay;
            if (closing == OverlayPanel.None) return;
            _activeOverlay = OverlayPanel.None;
            OnPropertyChanged(nameof(ExifSettingsOverlayVisibility));
            OnPropertyChanged(nameof(FilterOverlayVisibility));
            OnPropertyChanged(nameof(PresetManagerOverlayVisibility));
            if (!restoreFocus) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closing == OverlayPanel.ExifSettings) ExifSettingsButton.Focus();
                else if (closing == OverlayPanel.Filter) FilterSettingsButton.Focus();
                else PresetActionsButton.Focus();
            }), DispatcherPriority.Input);
        }

        private string BuildCurrentFilterStateKey() =>
            string.Join("\u001e", FilterConditions.Select(item => item.StateKey));

        private void CommitAppliedFilterEditorState(int conditionCount)
        {
            _appliedFilterConditionSummaries.Clear();
            if (conditionCount > 0)
                _appliedFilterConditionSummaries.AddRange(FilterConditions.Select(item => item.Summary));
            _appliedFilterStateKey = BuildCurrentFilterStateKey();
            AppliedFilterCount = conditionCount;
            NotifyFilterEditorStateChanged();
        }

        private void NotifyFilterEditorStateChanged()
        {
            OnPropertyChanged(nameof(CanApplyFilter));
            OnPropertyChanged(nameof(FilterSummary));
            OnPropertyChanged(nameof(FilterSummaryToolTip));
            OnPropertyChanged(nameof(HasUnappliedFilterChanges));
            OnPropertyChanged(nameof(FilterEditStatus));
            OnPropertyChanged(nameof(FilterEditStatusVisibility));
        }

        private void NotifyExifSettingsSummaryChanged()
        {
            OnPropertyChanged(nameof(ExifSettingsSummary));
            OnPropertyChanged(nameof(ExifSettingsToolTip));
        }

        private void PresetSelector_DropDownOpened(object sender, EventArgs e) =>
            ReloadPresets(SelectedPreset?.Id, true);

        private void PresetSelector_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
            e.Handled = true;

        private void HistoryComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
            e.Handled = true;

        private void PresetSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressPresetSelection) return;
            var requested = PresetSelector.SelectedItem as PhotoImporterPreset;
            if (requested == null || (SelectedPreset != null && requested.Id == SelectedPreset.Id)) return;
            ApplyPresetSelection(requested);
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e) => SaveCurrentPreset();

        private void SavePresetAs_Click(object sender, RoutedEventArgs e) => SaveCurrentPresetAs();

        private void PresetActions_Click(object sender, RoutedEventArgs e)
        {
            var menu = PresetActionsButton.ContextMenu;
            if (menu == null) return;
            menu.PlacementTarget = PresetActionsButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void UndoPresetApply_Click(object sender, RoutedEventArgs e)
        {
            var undo = _presetUndo;
            if (undo == null || _isBusy) return;
            _isApplyingPreset = true;
            try
            {
                ApplyPresetSnapshot(undo.Settings);
                var restored = undo.SelectedPresetId.HasValue
                    ? Presets.FirstOrDefault(item => item.Id == undo.SelectedPresetId.Value)
                    : null;
                SetSelectedPresetWithoutApplying(restored);
                _unselectedPresetBaseline = undo.UnselectedBaseline;
            }
            finally
            {
                _isApplyingPreset = false;
                _presetUndo = null;
                NotifyPresetStateChanged();
            }
            ValidateCurrentSettingsAfterPresetApply();
        }

        private void ApplyPresetSelection(PhotoImporterPreset requested)
        {
            if (requested == null) return;
            if (HasPresetChanges)
            {
                var choice = PresetDialogs.ConfirmApply(this, requested.Name);
                if (choice == PresetApplyChoice.Cancel)
                {
                    SetSelectedPresetWithoutApplying(SelectedPreset);
                    return;
                }
                if (choice == PresetApplyChoice.SaveThenApply && !SaveCurrentPreset())
                {
                    SetSelectedPresetWithoutApplying(SelectedPreset);
                    return;
                }
                requested = Presets.FirstOrDefault(item => item.Id == requested.Id) ?? requested;
            }

            var undo = new PresetUndoState(
                CapturePresetSnapshot(),
                SelectedPreset?.Id,
                _unselectedPresetBaseline);
            _isApplyingPreset = true;
            try
            {
                if (requested.SaveSourceFolder) SourceFolder = requested.SourceFolder;
                DestinationFolder = requested.DestinationFolder;
                TemplateText = requested.TemplateText;
                OverwriteExisting = requested.OverwriteExisting;
                IncludeOtherFiles = requested.SourceFileSelectionMode == SourceFileSelectionMode.AllFiles;
                AssociateSidecars = requested.AssociateSidecars;
                SidecarExtensionsText = string.Join("; ", requested.SidecarExtensions);
                AnalyzeJpegOnlyForRawJpegPair = requested.AnalyzeJpegOnlyForRawJpegPair;
                ReadExifInformation = requested.ReadExifInformation;
                SetSelectedPresetWithoutApplying(requested);
            }
            finally
            {
                _isApplyingPreset = false;
            }
            _presetUndo = undo;
            NotifyPresetStateChanged();
            ValidateCurrentSettingsAfterPresetApply();

            try
            {
                var refreshed = _presetStore.TouchLastUsed(requested.Id, DateTime.UtcNow);
                ReplacePresets(refreshed, requested.Id, false);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                SetMessage(AppLocalization.Text("プリセットは適用しましたが、最終利用日時を保存できませんでした。 ", "The preset was applied, but its last-used time could not be saved. ") + ex.Message,
                    Brushes.DarkGoldenrod);
            }
        }

        private bool SaveCurrentPreset()
        {
            if (_isBusy) return false;
            if (SelectedPreset == null) return SaveCurrentPresetAs();
            PhotoImporterPreset updated;
            if (!TryCreatePresetFromCurrent(
                SelectedPreset.Id,
                SelectedPreset.Name,
                SelectedPreset.CreatedUtc,
                DateTime.UtcNow,
                SelectedPreset.LastUsedUtc,
                SelectedPreset.SaveSourceFolder,
                out updated)) return false;
            try
            {
                var refreshed = _presetStore.Update(updated);
                ReplacePresets(refreshed, updated.Id, false);
                _presetUndo = null;
                NotifyPresetStateChanged();
                SetMessage(AppLocalization.IsEnglish ? "Saved preset \"" + updated.Name + "\"." : "プリセット「" + updated.Name + "」を保存しました。", Brushes.DimGray);
                return true;
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError(AppLocalization.Text("プリセットを保存できませんでした。", "Unable to save the preset."), ex);
                ReloadPresets(SelectedPreset?.Id, false);
                return false;
            }
        }

        private bool SaveCurrentPresetAs()
        {
            if (_isBusy) return false;
            var input = PresetDialogs.PromptForName(this, AppLocalization.Text("名前を付けて保存", "Save preset as"), string.Empty, false, true);
            if (input == null) return false;
            string name;
            try
            {
                name = PhotoImporterPresetStore.NormalizeName(input.Name);
            }
            catch (ArgumentException ex)
            {
                ShowPresetError(AppLocalization.Text("プリセット名が正しくありません。", "The preset name is invalid."), ex);
                return false;
            }

            ReloadPresets(SelectedPreset?.Id, true);
            var existing = Presets.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var confirmation = MessageBox.Show(
                    this,
                    AppLocalization.IsEnglish
                        ? "A preset with the same name exists.\nOverwrite \"" + existing.Name + "\"?"
                        : "同じ名前のプリセットがあります。\n「" + existing.Name + "」を上書きしますか？",
                    AppLocalization.Text("プリセットを上書き", "Overwrite preset"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes) return false;
            }

            var now = DateTime.UtcNow;
            PhotoImporterPreset preset;
            if (!TryCreatePresetFromCurrent(
                existing?.Id ?? Guid.NewGuid(),
                name,
                existing?.CreatedUtc ?? now,
                now,
                now,
                input.SaveSourceFolder,
                out preset)) return false;
            try
            {
                var refreshed = existing == null
                    ? _presetStore.Add(preset)
                    : _presetStore.Update(preset);
                ReplacePresets(refreshed, preset.Id, false);
                _presetUndo = null;
                NotifyPresetStateChanged();
                SetMessage(AppLocalization.IsEnglish ? "Saved preset \"" + preset.Name + "\"." : "プリセット「" + preset.Name + "」を保存しました。", Brushes.DimGray);
                return true;
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError(AppLocalization.Text("プリセットを保存できませんでした。", "Unable to save the preset."), ex);
                ReloadPresets(SelectedPreset?.Id, false);
                return false;
            }
        }

        private void RenamePreset_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedManagedPreset;
            if (selected == null) return;
            var input = PresetDialogs.PromptForName(
                this, AppLocalization.Text("プリセット名を変更", "Rename preset"), selected.Name, selected.SaveSourceFolder, false);
            if (input == null) return;
            try
            {
                var renamed = selected.Clone();
                renamed.Name = PhotoImporterPresetStore.NormalizeName(input.Name);
                renamed.UpdatedUtc = DateTime.UtcNow;
                var refreshed = _presetStore.Update(renamed);
                ReplacePresets(refreshed, SelectedPreset?.Id, false);
                RefreshManagedPresets(renamed.Id);
                SetMessage(AppLocalization.IsEnglish ? "Renamed the preset to \"" + renamed.Name + "\"." : "プリセット名を「" + renamed.Name + "」へ変更しました。", Brushes.DimGray);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError(AppLocalization.Text("プリセット名を変更できませんでした。", "Unable to rename the preset."), ex);
                ReloadPresets(SelectedPreset?.Id, false);
                RefreshManagedPresets(selected.Id);
            }
        }

        private void DuplicatePreset_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedManagedPreset;
            if (selected == null) return;
            var input = PresetDialogs.PromptForName(
                this, AppLocalization.Text("プリセットを複製", "Duplicate preset"), selected.Name + AppLocalization.Text(" - コピー", " - Copy"), selected.SaveSourceFolder, false);
            if (input == null) return;
            try
            {
                var now = DateTime.UtcNow;
                var duplicate = selected.Clone();
                duplicate.Id = Guid.NewGuid();
                duplicate.Name = PhotoImporterPresetStore.NormalizeName(input.Name);
                duplicate.CreatedUtc = now;
                duplicate.UpdatedUtc = now;
                duplicate.LastUsedUtc = null;
                var refreshed = _presetStore.Add(duplicate);
                ReplacePresets(refreshed, SelectedPreset?.Id, false);
                RefreshManagedPresets(duplicate.Id);
                SetMessage(AppLocalization.IsEnglish ? "Created preset \"" + duplicate.Name + "\"." : "プリセット「" + duplicate.Name + "」を作成しました。", Brushes.DimGray);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError(AppLocalization.Text("プリセットを複製できませんでした。", "Unable to duplicate the preset."), ex);
                ReloadPresets(SelectedPreset?.Id, false);
                RefreshManagedPresets(selected.Id);
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedManagedPreset;
            if (selected == null) return;
            var confirmation = MessageBox.Show(
                this,
                AppLocalization.IsEnglish
                    ? "Delete preset \"" + selected.Name + "\"?\n\nDeleting it does not change copied photos or the current setting values."
                    : "プリセット「" + selected.Name + "」を削除しますか？\n\n削除しても、コピー済みの写真と設定ファイルの現在値は変わりません。",
                AppLocalization.Text("プリセットを削除", "Delete preset"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;
            try
            {
                var deletesCurrent = SelectedPreset != null && SelectedPreset.Id == selected.Id;
                var refreshed = _presetStore.Delete(selected.Id);
                ReplacePresets(refreshed, deletesCurrent ? null : SelectedPreset?.Id, deletesCurrent);
                if (deletesCurrent)
                {
                    _presetUndo = null;
                    _unselectedPresetBaseline = CapturePresetSnapshot();
                    NotifyPresetStateChanged();
                }
                RefreshManagedPresets(null);
                SetMessage(AppLocalization.IsEnglish ? "Deleted preset \"" + selected.Name + "\"." : "プリセット「" + selected.Name + "」を削除しました。", Brushes.DimGray);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError(AppLocalization.Text("プリセットを削除できませんでした。", "Unable to delete the preset."), ex);
                ReloadPresets(SelectedPreset?.Id, false);
                RefreshManagedPresets(null);
            }
        }

        private void ExportPreset_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedManagedPreset;
            if (selected == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = AppLocalization.Text("プリセットをエクスポート", "Export preset"),
                Filter = AppLocalization.Text("PhotoImporter プリセット (*.xml)|*.xml|すべてのファイル (*.*)|*.*", "PhotoImporter preset (*.xml)|*.xml|All files (*.*)|*.*"),
                DefaultExt = ".xml",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = CreateSafePresetFileName(selected.Name) + ".xml"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _presetStore.WriteExportFile(dialog.FileName, selected);
                SetMessage(AppLocalization.Text("プリセットをエクスポートしました。 ", "Preset exported. ") + dialog.FileName, Brushes.DimGray);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError(AppLocalization.Text("プリセットをエクスポートできませんでした。", "Unable to export the preset."), ex);
            }
        }

        private void ImportPreset_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppLocalization.Text("プリセットをインポート", "Import preset"),
                Filter = AppLocalization.Text("PhotoImporter プリセット (*.xml)|*.xml|すべてのファイル (*.*)|*.*", "PhotoImporter preset (*.xml)|*.xml|All files (*.*)|*.*"),
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var imported = _presetStore.ReadExportFile(dialog.FileName);
                ReloadPresets(SelectedPreset?.Id, false);
                var idMatch = Presets.FirstOrDefault(item => item.Id == imported.Id);
                var nameMatch = Presets.FirstOrDefault(item =>
                    string.Equals(item.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
                if (idMatch != null && nameMatch != null && idMatch.Id != nameMatch.Id)
                    throw new InvalidOperationException(
                        AppLocalization.Text("インポートするプリセットの id と名前が、それぞれ別の既存プリセットと重複しています。", "The imported preset ID and name conflict with two different existing presets."));

                var conflict = idMatch ?? nameMatch;
                IReadOnlyList<PhotoImporterPreset> refreshed;
                Guid importedId;
                if (conflict == null)
                {
                    imported.LastUsedUtc = null;
                    refreshed = _presetStore.Add(imported);
                    importedId = imported.Id;
                }
                else
                {
                    var choice = PresetDialogs.ConfirmImportConflict(this, imported.Name, conflict.Name);
                    if (choice == PresetImportChoice.Skip) return;
                    if (choice == PresetImportChoice.Overwrite)
                    {
                        imported.Id = conflict.Id;
                        imported.CreatedUtc = conflict.CreatedUtc;
                        imported.UpdatedUtc = DateTime.UtcNow;
                        imported.LastUsedUtc = conflict.LastUsedUtc;
                        refreshed = _presetStore.Update(imported);
                        importedId = imported.Id;
                    }
                    else
                    {
                        var nameInput = PresetDialogs.PromptForName(
                            this, AppLocalization.Text("別名でインポート", "Import with another name"), imported.Name + AppLocalization.Text(" - コピー", " - Copy"), imported.SaveSourceFolder, false);
                        if (nameInput == null) return;
                        var now = DateTime.UtcNow;
                        imported.Id = Guid.NewGuid();
                        imported.Name = PhotoImporterPresetStore.NormalizeName(nameInput.Name);
                        imported.CreatedUtc = now;
                        imported.UpdatedUtc = now;
                        imported.LastUsedUtc = null;
                        refreshed = _presetStore.Add(imported);
                        importedId = imported.Id;
                    }
                }
                ReplacePresets(refreshed, SelectedPreset?.Id, false);
                RefreshManagedPresets(importedId);
                var warning = GetPresetValidationWarning(imported);
                SetMessage(
                    string.IsNullOrEmpty(warning)
                        ? AppLocalization.IsEnglish ? "Imported preset \"" + imported.Name + "\"." : "プリセット「" + imported.Name + "」をインポートしました。"
                        : AppLocalization.Text("プリセットをインポートしましたが、設定に警告があります。 ", "The preset was imported with setting warnings. ") + warning,
                    string.IsNullOrEmpty(warning) ? Brushes.DimGray : Brushes.DarkGoldenrod);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError(AppLocalization.Text("プリセットをインポートできませんでした。", "Unable to import the preset."), ex);
                ReloadPresets(SelectedPreset?.Id, false);
                RefreshManagedPresets(null);
            }
        }

        private void ApplyManagedTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedManagedPreset == null || _isBusy) return;
            TemplateText = SelectedManagedPreset.TemplateText;
            SetMessage(AppLocalization.IsEnglish
                    ? "Applied only the template from preset \"" + SelectedManagedPreset.Name + "\"."
                    : "プリセット「" + SelectedManagedPreset.Name + "」からテンプレートだけを取り込みました。",
                Brushes.DimGray);
        }

        private void RefreshManagedPresets(Guid? selectedId)
        {
            if (ManagedPresets == null) return;
            var ordered = string.Equals(
                PresetManagerSortMode,
                AppLocalization.Text("最終利用日順", "Last used"),
                StringComparison.Ordinal)
                ? Presets.OrderByDescending(item => item.LastUsedUtc.HasValue)
                    .ThenByDescending(item => item.LastUsedUtc)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : Presets.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
            ManagedPresets.Clear();
            foreach (var preset in ordered) ManagedPresets.Add(preset);
            SelectedManagedPreset = selectedId.HasValue
                ? ManagedPresets.FirstOrDefault(item => item.Id == selectedId.Value)
                : null;
        }

        private static string CreateSafePresetFileName(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var value = new string((name ?? "preset").Select(character =>
                invalid.Contains(character) ? '_' : character).ToArray()).Trim();
            return string.IsNullOrEmpty(value) ? "preset" : value;
        }

        private static string GetPresetValidationWarning(PhotoImporterPreset preset)
        {
            if (preset == null) return string.Empty;
            var warnings = new List<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(preset.DestinationFolder) ||
                    !Path.IsPathRooted(preset.DestinationFolder))
                    warnings.Add(AppLocalization.Text("コピー先が絶対パスではありません。", "The destination is not an absolute path."));
                else if (!Directory.Exists(preset.DestinationFolder))
                    warnings.Add(AppLocalization.Text("コピー先が存在しません。", "The destination does not exist."));
                if (preset.SaveSourceFolder)
                {
                    if (string.IsNullOrWhiteSpace(preset.SourceFolder) || !Path.IsPathRooted(preset.SourceFolder))
                        warnings.Add(AppLocalization.Text("コピー元が絶対パスではありません。", "The source is not an absolute path."));
                    else if (!Directory.Exists(preset.SourceFolder))
                        warnings.Add(AppLocalization.Text("コピー元が存在しません。", "The source does not exist."));
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                warnings.Add(AppLocalization.Text("フォルダーパスが正しくありません。", "A folder path is invalid."));
            }
            var parsed = TemplateParser.Parse(preset.TemplateText ?? string.Empty);
            if (!parsed.IsValid) warnings.Add(AppLocalization.Text("テンプレートが正しくありません。", "The template is invalid."));
            try
            {
                SidecarPolicy.Create(preset.AssociateSidecars, preset.SidecarExtensions);
            }
            catch (ArgumentException ex)
            {
                warnings.Add(ex.Message);
            }
            return string.Join(" ", warnings.Distinct());
        }

        private bool TryCreatePresetFromCurrent(
            Guid id,
            string name,
            DateTime createdUtc,
            DateTime updatedUtc,
            DateTime? lastUsedUtc,
            bool saveSourceFolder,
            out PhotoImporterPreset preset)
        {
            preset = null;
            try
            {
                if (string.IsNullOrWhiteSpace(DestinationFolder) || !Path.IsPathRooted(DestinationFolder))
                    throw new ArgumentException(AppLocalization.Text("コピー先には絶対パスを指定してください。", "Specify an absolute destination path."));
                if (saveSourceFolder &&
                    (string.IsNullOrWhiteSpace(SourceFolder) || !Path.IsPathRooted(SourceFolder)))
                    throw new ArgumentException(AppLocalization.Text("保存するコピー元には絶対パスを指定してください。", "Specify an absolute source path to save."));
                var destination = NormalizePath(DestinationFolder);
                var source = saveSourceFolder ? NormalizePath(SourceFolder) : null;
                if (saveSourceFolder &&
                    (IsSameOrUnder(source, destination) || IsSameOrUnder(destination, source)))
                    throw new InvalidOperationException(
                        AppLocalization.Text("コピー元とコピー先には、同一または互いの配下ではないフォルダーを指定してください。", "The source and destination must be different folders and neither may be inside the other."));
                var parsed = TemplateParser.Parse(TemplateText);
                if (!parsed.IsValid)
                    throw new ArgumentException(AppLocalization.Format(
                        "テンプレートエラー: {0}（位置 {1}）",
                        "Template error: {0} (position {1})",
                        parsed.Error.Code,
                        parsed.Error.Position + 1));
                var sidecarPolicy = CreateSidecarPolicy(AssociateSidecars, SidecarExtensionsText);
                preset = new PhotoImporterPreset
                {
                    Id = id,
                    Name = PhotoImporterPresetStore.NormalizeName(name),
                    CreatedUtc = createdUtc,
                    UpdatedUtc = updatedUtc,
                    LastUsedUtc = lastUsedUtc,
                    SaveSourceFolder = saveSourceFolder,
                    SourceFolder = source,
                    DestinationFolder = destination,
                    TemplateText = TemplateText,
                    OverwriteExisting = OverwriteExisting,
                    SourceFileSelectionMode = _sourceFileSelectionMode,
                    AssociateSidecars = AssociateSidecars,
                    AnalyzeJpegOnlyForRawJpegPair = AnalyzeJpegOnlyForRawJpegPair,
                    ReadExifInformation = ReadExifInformation
                };
                foreach (var extension in sidecarPolicy.Extensions) preset.SidecarExtensions.Add(extension);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException ||
                                       ex is NotSupportedException || ex is PathTooLongException)
            {
                ShowPresetError(AppLocalization.Text("現在の設定をプリセットへ保存できません。", "Unable to save the current settings as a preset."), ex);
                return false;
            }
        }

        private PresetSettingsSnapshot CapturePresetSnapshot() => new PresetSettingsSnapshot(
            SourceFolder,
            DestinationFolder,
            TemplateText,
            OverwriteExisting,
            _sourceFileSelectionMode,
            AssociateSidecars,
            SplitSidecarExtensions(SidecarExtensionsText),
            AnalyzeJpegOnlyForRawJpegPair,
            ReadExifInformation);

        private void ApplyPresetSnapshot(PresetSettingsSnapshot snapshot)
        {
            SourceFolder = snapshot.SourceFolder;
            DestinationFolder = snapshot.DestinationFolder;
            TemplateText = snapshot.TemplateText;
            OverwriteExisting = snapshot.OverwriteExisting;
            IncludeOtherFiles = snapshot.SourceFileSelectionMode == SourceFileSelectionMode.AllFiles;
            AssociateSidecars = snapshot.AssociateSidecars;
            SidecarExtensionsText = string.Join("; ", snapshot.SidecarExtensions);
            AnalyzeJpegOnlyForRawJpegPair = snapshot.AnalyzeJpegOnlyForRawJpegPair;
            ReadExifInformation = snapshot.ReadExifInformation;
        }

        private static IEnumerable<string> SplitSidecarExtensions(string text) =>
            (text ?? string.Empty).Split(
                new[] { ';', ',', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

        private void ValidateCurrentSettingsAfterPresetApply()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SourceFolder) || !Path.IsPathRooted(SourceFolder))
                    throw new ArgumentException(AppLocalization.Text("コピー元には絶対パスを指定してください。", "Specify an absolute source path."));
                if (string.IsNullOrWhiteSpace(DestinationFolder) || !Path.IsPathRooted(DestinationFolder))
                    throw new ArgumentException(AppLocalization.Text("コピー先には絶対パスを指定してください。", "Specify an absolute destination path."));
                var source = Path.GetFullPath(SourceFolder ?? string.Empty);
                var destination = Path.GetFullPath(DestinationFolder ?? string.Empty);
                ValidateRoots(source, destination);
                var parsed = TemplateParser.Parse(TemplateText);
                if (!parsed.IsValid)
                {
                    ShowTemplateError(parsed.Error);
                    return;
                }
                CreateSidecarPolicy(AssociateSidecars, SidecarExtensionsText);
                SetMessage(AppLocalization.Text("プリセットを適用しました。再スキャンしてください。", "Preset applied. Scan again."), Brushes.DimGray);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is InvalidOperationException ||
                                       ex is NotSupportedException)
            {
                SetMessage(AppLocalization.Text("プリセットを適用しましたが、設定にエラーがあります。 ", "The preset was applied, but the settings contain an error. ") + ex.Message,
                    Brushes.Firebrick);
            }
        }

        private void ReloadPresets(Guid? selectedId, bool showWarning, bool resetUnselectedBaseline = false)
        {
            var result = _presetStore.Load();
            var selectedWasRemoved = selectedId.HasValue && result.Presets.All(item => item.Id != selectedId.Value);
            ReplacePresets(result.Presets, selectedId, resetUnselectedBaseline || selectedWasRemoved);
            if (showWarning && !string.IsNullOrEmpty(result.Warning))
                SetMessage(result.Warning, Brushes.DarkGoldenrod);
        }

        private void ReplacePresets(
            IEnumerable<PhotoImporterPreset> presets,
            Guid? selectedId,
            bool resetUnselectedBaseline)
        {
            _suppressPresetSelection = true;
            try
            {
                Presets.Clear();
                foreach (var preset in presets.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                    Presets.Add(preset);
                var selected = selectedId.HasValue
                    ? Presets.FirstOrDefault(item => item.Id == selectedId.Value)
                    : null;
                SelectedPreset = selected;
                if (selected == null && resetUnselectedBaseline)
                    _unselectedPresetBaseline = CapturePresetSnapshot();
                if (PresetSelector != null) PresetSelector.SelectedItem = selected;
            }
            finally
            {
                _suppressPresetSelection = false;
            }
            NotifyPresetStateChanged();
        }

        private void SetSelectedPresetWithoutApplying(PhotoImporterPreset preset)
        {
            _suppressPresetSelection = true;
            try
            {
                SelectedPreset = preset;
                if (PresetSelector != null) PresetSelector.SelectedItem = preset;
            }
            finally
            {
                _suppressPresetSelection = false;
            }
        }

        private void NotifyPresetStateChanged()
        {
            OnPropertyChanged(nameof(HasPresetChanges));
            OnPropertyChanged(nameof(PresetStatusText));
            OnPropertyChanged(nameof(PresetStatusVisibility));
            OnPropertyChanged(nameof(CanUndoPresetApply));
            OnPropertyChanged(nameof(PresetUndoVisibility));
        }

        private void ShowPresetError(string title, Exception ex)
        {
            var message = AppLocalization.UserMessage(ex.Message);
            SetMessage(title + " " + message, Brushes.Firebrick);
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static bool IsPresetStoreFailure(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException ||
            ex is InvalidOperationException || ex is ArgumentException || ex is TimeoutException;

        private void SettingsChanged(bool presetOwnedSetting = true)
        {
            _previewIsCurrent = false;
            if (!_isApplyingPreset && presetOwnedSetting) _presetUndo = null;
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(CanSelectAll));
            OnPropertyChanged(nameof(CanEditFilters));
            OnPropertyChanged(nameof(CanApplyFilter));
            NotifyPresetStateChanged();
        }

        private void ChangeExifCacheRoot(string newRoot, bool useDefault)
        {
            var oldRoot = ExifCacheRoot;
            var normalizedNewRoot = Path.GetFullPath(newRoot);
            if (string.Equals(oldRoot, normalizedNewRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (useDefault && !string.IsNullOrWhiteSpace(_customExifCacheRoot))
                {
                    _customExifCacheRoot = null;
                    OnPropertyChanged(nameof(ExifCacheRoot));
                    NotifyExifSettingsSummaryChanged();
                    SettingsChanged(false);
                    SetMessage(AppLocalization.Text("Exif キャッシュの保存先を既定値へ戻しました。", "The Exif cache location was restored to the default."), Brushes.DimGray);
                }
                return;
            }

            try
            {
                VerifyDirectoryWritable(normalizedNewRoot);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                SetMessage(AppLocalization.Format(
                    "Exif キャッシュの保存先を使用できません ({0}): {1}",
                    "The Exif cache location cannot be used ({0}): {1}",
                    normalizedNewRoot,
                    ex.Message),
                    Brushes.Firebrick);
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                AppLocalization.IsEnglish
                    ? "Change the Exif cache location.\n\nCurrent: " + oldRoot + "\nNew: " + normalizedNewRoot + "\n\nCaches in the previous location will remain and will no longer be used during normal scans."
                    : "Exif キャッシュの保存先を変更します。\n\n現在: " + oldRoot + "\n変更後: " + normalizedNewRoot + "\n\n以前の保存先にあるキャッシュは残り、通常のスキャンでは使われなくなります。",
                AppLocalization.Text("Exif キャッシュの保存先を変更", "Change Exif cache location"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);
            if (confirmation != MessageBoxResult.OK) return;

            RememberPreviousCacheRoot(oldRoot);
            _previousExifCacheRoots.RemoveAll(
                path => string.Equals(path, normalizedNewRoot, StringComparison.OrdinalIgnoreCase));
            _customExifCacheRoot = useDefault ? null : normalizedNewRoot;
            OnPropertyChanged(nameof(ExifCacheRoot));
            NotifyExifSettingsSummaryChanged();
            SettingsChanged(false);
            SetMessage(AppLocalization.Text("Exif キャッシュの保存先を変更しました。", "The Exif cache location was changed."), Brushes.DimGray);
        }

        private void RememberPreviousCacheRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                _previousExifCacheRoots.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase))) return;
            _previousExifCacheRoots.Add(Path.GetFullPath(path));
        }

        private static void VerifyDirectoryWritable(string path)
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".PhotoImporter_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough)) { }
            }
            finally
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
        }

        private void ApplySettings(PhotoImporterSettings settings)
        {
            _sourceFolder = settings.SourceFolder;
            _destinationFolder = settings.DestinationFolder;
            _templateText = string.IsNullOrWhiteSpace(settings.TemplateText)
                ? PhotoImporterSettings.DefaultTemplate
                : settings.TemplateText;
            _overwriteExisting = settings.OverwriteExisting;
            _sourceFileSelectionMode = settings.SourceFileSelectionMode;
            _associateSidecars = settings.AssociateSidecars;
            _sidecarExtensionsText = string.Join("; ", settings.SidecarExtensions);
            _analyzeJpegOnlyForRawJpegPair = settings.AnalyzeJpegOnlyForRawJpegPair;
            _useExifCache = settings.UseExifCache;
            _readExifInformation = settings.ReadExifInformation;
            _showImagePreview = settings.ShowImagePreview;
            _inputHistoryLimit = settings.InputHistoryLimit;
            _customExifCacheRoot = settings.CustomExifCacheRoot;
            _lastAppliedPresetId = settings.LastAppliedPresetId;
            _uiLanguage = AppLocalization.NormalizePreference(settings.UiLanguage);
            _previousExifCacheRoots.Clear();
            _previousExifCacheRoots.AddRange(settings.PreviousExifCacheRoots);
            _previousExifCacheRoots.RemoveAll(
                path => string.Equals(path, ExifCacheRoot, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<string> RecordInputHistoryAsync(
            string sourceFolder,
            string destinationFolder,
            string templateText)
        {
            try
            {
                var history = await Task.Run(() => _inputHistoryStore.Record(
                    sourceFolder,
                    destinationFolder,
                    templateText,
                    _inputHistoryLimit));
                ApplyInputHistory(history);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidDataException || ex is InvalidOperationException ||
                                       ex is ArgumentException || ex is NotSupportedException ||
                                       ex is TimeoutException)
            {
                return AppLocalization.Text("入力履歴を保存できませんでした: ", "Unable to save input history: ") + ex.Message;
            }
        }

        private void ApplyInputHistory(RecentInputHistory history)
        {
            SynchronizeCollection(
                SourceFolderHistory,
                history.SourceFolders.Take(_inputHistoryLimit),
                StringComparer.OrdinalIgnoreCase);
            SynchronizeCollection(
                DestinationFolderHistory,
                history.DestinationFolders.Take(_inputHistoryLimit),
                StringComparer.OrdinalIgnoreCase);
            SynchronizeCollection(
                TemplateHistory,
                history.Templates.Take(_inputHistoryLimit),
                StringComparer.Ordinal);
        }

        private static void SynchronizeCollection(
            ObservableCollection<string> destination,
            IEnumerable<string> values,
            StringComparer comparer)
        {
            var expected = values.ToList();
            for (var expectedIndex = 0; expectedIndex < expected.Count; expectedIndex++)
            {
                var value = expected[expectedIndex];
                var existingIndex = -1;
                for (var index = expectedIndex; index < destination.Count; index++)
                {
                    if (!comparer.Equals(destination[index], value)) continue;
                    existingIndex = index;
                    break;
                }

                if (existingIndex < 0) destination.Insert(expectedIndex, value);
                else if (existingIndex != expectedIndex) destination.Move(existingIndex, expectedIndex);
            }

            while (destination.Count > expected.Count)
                destination.RemoveAt(destination.Count - 1);
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            CancelImagePreviewRequest();
            SidecarPolicy sidecarPolicy;
            try
            {
                sidecarPolicy = CreateSidecarPolicy(
                    AssociateSidecars,
                    SidecarExtensionsText);
            }
            catch (ArgumentException ex)
            {
                if (PresetDialogs.ConfirmExitWithoutSaving(this, ex.Message)) return;
                e.Cancel = true;
                return;
            }

            var settings = new PhotoImporterSettings
            {
                SourceFolder = SourceFolder,
                DestinationFolder = DestinationFolder,
                TemplateText = TemplateText,
                OverwriteExisting = OverwriteExisting,
                SourceFileSelectionMode = _sourceFileSelectionMode,
                AssociateSidecars = AssociateSidecars,
                AnalyzeJpegOnlyForRawJpegPair = AnalyzeJpegOnlyForRawJpegPair,
                UseExifCache = UseExifCache,
                ReadExifInformation = ReadExifInformation,
                ShowImagePreview = ShowImagePreview,
                InputHistoryLimit = _inputHistoryLimit,
                CustomExifCacheRoot = _customExifCacheRoot,
                LastAppliedPresetId = SelectedPreset?.Id,
                UiLanguage = _uiLanguage
            };
            settings.SidecarExtensions.Clear();
            foreach (var extension in sidecarPolicy.Extensions)
                settings.SidecarExtensions.Add(extension);
            foreach (var path in _previousExifCacheRoots) settings.PreviousExifCacheRoots.Add(path);

            try
            {
                _settingsStore.Save(settings);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException || ex is ArgumentException)
            {
                MessageBox.Show(
                    this,
                    AppLocalization.Text("設定を保存できませんでした。\n", "Unable to save settings.\n") + _settingsStore.SettingsPath + "\n\n" + ex.Message,
                    "Photo Importer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void SetBusy(bool busy, bool copying)
        {
            _isBusy = busy;
            _isCopying = copying;
            OnPropertyChanged(nameof(CanEditSettings));
            OnPropertyChanged(nameof(CanSelectItems));
            OnPropertyChanged(nameof(CanSelectAll));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(CopyButtonText));
            OnPropertyChanged(nameof(ProgressVisibility));
            OnPropertyChanged(nameof(CopyProgressDetailsVisibility));
            OnPropertyChanged(nameof(SimpleProgressTextVisibility));
            OnPropertyChanged(nameof(CanEditFilters));
            OnPropertyChanged(nameof(CanApplyFilter));
            OnPropertyChanged(nameof(CanUndoPresetApply));
            if (!copying && !_isScanningExif) ProgressText = string.Empty;
        }

        private void SetScanningExif(bool scanning)
        {
            if (_isScanningExif == scanning) return;
            _isScanningExif = scanning;
            OnPropertyChanged(nameof(ProgressVisibility));
            OnPropertyChanged(nameof(SimpleProgressTextVisibility));
            OnPropertyChanged(nameof(CanCancel));
        }

        private static void ValidateRoots(string sourceRoot, string destinationRoot)
        {
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(AppLocalization.Text("コピー元フォルダーが見つかりません。", "The source folder was not found."));
            if (!Directory.Exists(destinationRoot)) throw new DirectoryNotFoundException(AppLocalization.Text("コピー先フォルダーが見つかりません。", "The destination folder was not found."));
            if (IsSameOrUnder(sourceRoot, destinationRoot) || IsSameOrUnder(destinationRoot, sourceRoot))
                throw new InvalidOperationException(AppLocalization.Text("コピー元とコピー先には、同一または互いの配下ではないフォルダーを指定してください。", "The source and destination must be different folders and neither may be inside the other."));
        }

        private static SidecarPolicy CreateSidecarPolicy(bool enabled, string extensionText)
        {
            var extensions = (extensionText ?? string.Empty).Split(
                new[] { ';', ',', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            return SidecarPolicy.Create(enabled, extensions);
        }

        private static string SelectFolder(string initialPath, string description)
        {
            using (var dialog = new Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = true })
            {
                if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)) dialog.SelectedPath = initialPath;
                return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        private void ShowTemplateError(TemplateError error) =>
            SetMessage(AppLocalization.Format("テンプレートエラー: {0}（位置 {1}）", "Template error: {0} (position {1})", error.Code, error.Position + 1), Brushes.Firebrick);
        private void SetMessage(string value, Brush brush) { Message = AppLocalization.UserMessage(value); MessageBrush = brush; }

        private static bool IsSameOrUnder(string path, string root)
        {
            var candidate = NormalizePath(path);
            var normalizedRoot = NormalizePath(root);
            return string.Equals(candidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(EnsureTrailingSeparator(normalizedRoot), StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeRelative(string root, string path)
        {
            var normalizedRoot = NormalizePath(root);
            var normalizedPath = NormalizePath(path);
            if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            var prefix = EnsureTrailingSeparator(normalizedRoot);
            if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(AppLocalization.Text(
                    "コピー元フォルダー外のパスは処理できません。",
                    "Paths outside the source folder cannot be processed."));
            return normalizedPath.Substring(prefix.Length);
        }

        private static string NormalizePath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024d * 1024 * 1024)).ToString("0.0") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / (1024d * 1024)).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            return bytes + " B";
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

        private sealed class PresetUndoState
        {
            public PresetUndoState(
                PresetSettingsSnapshot settings,
                Guid? selectedPresetId,
                PresetSettingsSnapshot unselectedBaseline)
            {
                Settings = settings;
                SelectedPresetId = selectedPresetId;
                UnselectedBaseline = unselectedBaseline;
            }

            public PresetSettingsSnapshot Settings { get; }
            public Guid? SelectedPresetId { get; }
            public PresetSettingsSnapshot UnselectedBaseline { get; }
        }
    }

    public sealed class TokenDetailItem : INotifyPropertyChanged
    {
        private readonly TemplateTokenKind _token;
        private readonly bool _isExif;
        private string _format;
        private string _value = "—";
        private bool _isFormatEditorVisible;
        private PreviewItem _previewItem;

        private TokenDetailItem(
            TemplateTokenKind token,
            bool isExif,
            string description,
            string formatDescription = null,
            string defaultFormat = null)
        {
            _token = token;
            _isExif = isExif;
            Description = description;
            FormatDescription = formatDescription;
            SupportsFormat = formatDescription != null;
            _format = defaultFormat ?? string.Empty;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public string Token => "{" + _token + "}";
        public bool SupportsFormat { get; }
        public string Description { get; }
        public string FormatDescription { get; }
        public bool IsFormatEditorVisible
        {
            get => _isFormatEditorVisible;
            set
            {
                if (_isFormatEditorVisible == value) return;
                _isFormatEditorVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFormatEditorVisible)));
            }
        }
        public string Format
        {
            get => _format;
            set
            {
                if (_format == value) return;
                _format = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Format)));
                Recalculate();
            }
        }
        public string Value
        {
            get => _value;
            private set
            {
                if (_value == value) return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public void SetPreviewItem(PreviewItem previewItem)
        {
            _previewItem = previewItem;
            Recalculate();
        }

        private void Recalculate()
        {
            if (_previewItem == null || _previewItem.TemplateContext == null)
            {
                Value = "—";
                return;
            }
            if (_isExif && _previewItem.MetadataResult == null)
            {
                Value = AppLocalization.Text("未読み込み", "Not loaded");
                return;
            }
            if (_isExif && _previewItem.MetadataResult.Status == PhotoMetadataReadStatus.ReadError)
            {
                Value = AppLocalization.Text("読取エラー", "Read error");
                return;
            }

            var format = SupportsFormat && !string.IsNullOrEmpty(Format) ? ":" + Format : string.Empty;
            var source = "x{" + _token + format + "}";
            var parsed = TemplateParser.Parse(source);
            if (!parsed.IsValid)
            {
                Value = AppLocalization.Text("書式エラー: ", "Format error: ") + parsed.Error.Code;
                return;
            }

            try
            {
                var evaluated = TemplateEvaluator.Evaluate(
                    parsed.Template,
                    _previewItem.TemplateContext,
                    _token == TemplateTokenKind.Sequence ? _previewItem.SequenceNumber : null);
                var tokenValue = evaluated.Length == 0 ? string.Empty : evaluated.Substring(1);
                Value = tokenValue.Length == 0 ? AppLocalization.Text("（空文字）", "(Empty string)") : tokenValue;
            }
            catch (TemplateException ex)
            {
                Value = AppLocalization.Text("書式エラー: ", "Format error: ") + ex.Error.Code;
            }
        }

        public static IReadOnlyList<TokenDetailItem> CreateFileSystemItems() => new[]
        {
            Item(TemplateTokenKind.OriginalName,
                D("元ファイル名。最後の拡張子を含みます（例: DSC_0101.NEF）。", "The original file name, including the final extension (example: DSC_0101.NEF).")),
            Item(TemplateTokenKind.FileName,
                D("元ファイル名から最後の拡張子を除いた部分です（例: DSC_0101）。", "The original file name without its final extension (example: DSC_0101).")),
            Item(TemplateTokenKind.Extension,
                D("最後の拡張子。先頭のピリオドを含み、拡張子がなければ空文字になります（例: .NEF）。", "The final extension, including its leading period. Empty when there is no extension (example: .NEF).")),
            Item(TemplateTokenKind.SourceRelativeDirectory,
                D("コピー元ルートからファイル格納フォルダーまでの相対パス。コピー元ルート直下では空文字になります。", "The path from the source root to the file's folder. Empty for files directly under the source root."),
                SourceDirectoryFormatDescription),
            Item(TemplateTokenKind.ModifiedDate,
                D("元ファイルの最終更新日時です。", "The original file's last modified date and time."),
                DateFormatDescription),
            Item(TemplateTokenKind.FileSize,
                D("元ファイルのバイト数を10進整数で表示します。", "The original file size in bytes as a decimal integer.")),
            Item(TemplateTokenKind.Protected,
                D("読み取り専用属性があれば Protected、なければ Unprotected を表示します。", "Displays Protected for a read-only file, otherwise Unprotected.")),
            Item(TemplateTokenKind.Sequence,
                D("宛先の競合を避ける連番。競合がなければ空文字、競合時はアンダースコア付きの連番になります。", "A sequence number used to avoid destination conflicts. Empty when there is no conflict; otherwise an underscore-prefixed number."),
                SequenceFormatDescription)
        };

        public static IReadOnlyList<TokenDetailItem> CreateExifItems() => new[]
        {
            ExifItem(TemplateTokenKind.TakenDate,
                D("Exifの撮影日時をタイムゾーン変換せず、記録された壁時計値のまま表示します。サブ秒も使用できます。", "Displays the recorded Exif capture time without time-zone conversion. Subseconds are available."),
                DateFormatDescription),
            ExifItem(TemplateTokenKind.TakenDateLocal,
                D("Exifの撮影日時を、このPCの現在のタイムゾーンへ変換して表示します。", "Displays the Exif capture time converted to this PC's current time zone."),
                DateFormatDescription),
            ExifItem(TemplateTokenKind.TakenDateInTimeZone,
                D("Exifの撮影日時を、指定したタイムゾーンへ変換して表示します。", "Displays the Exif capture time converted to the specified time zone."),
                TimeZoneFormatDescription,
                "JST|yyyy-MM-dd HH-mm-ss"),
            ExifItem(TemplateTokenKind.CameraMake,
                D("カメラのメーカー名。値がなければ Unknown になります。", "Camera manufacturer. Displays Unknown when unavailable.")),
            ExifItem(TemplateTokenKind.CameraModel,
                D("カメラのモデル名。値がなければ Unknown になります。", "Camera model. Displays Unknown when unavailable.")),
            ExifItem(TemplateTokenKind.CameraSerial,
                D("カメラボディのシリアル番号。値がなければ Unknown になります。", "Camera body serial number. Displays Unknown when unavailable.")),
            ExifItem(TemplateTokenKind.Lens,
                D("レンズのモデル名。値がなければ Unknown になります。", "Lens model. Displays Unknown when unavailable.")),
            ExifItem(TemplateTokenKind.Width,
                D("Exifの向きを反映した画像の幅（ピクセル）です。", "Image width in pixels after applying Exif orientation."),
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.Height,
                D("Exifの向きを反映した画像の高さ（ピクセル）です。", "Image height in pixels after applying Exif orientation."),
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.ExifWidth,
                D("Exifに記録された向き反映前の幅（ピクセル）です。", "Width in pixels recorded in Exif before applying orientation."),
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.ExifHeight,
                D("Exifに記録された向き反映前の高さ（ピクセル）です。", "Height in pixels recorded in Exif before applying orientation."),
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.Orientation,
                D("Exif Orientation の値（1～8）です。", "Exif Orientation value (1–8)."),
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.Aperture,
                D("絞り値。書式を省略すると F2.8 のように表示します。", "Aperture. Without a format, displayed like F2.8."),
                NumberWithoutUnitFormatDescription),
            ExifItem(TemplateTokenKind.ShutterSpeed,
                D("シャッタースピード。書式を省略すると 1-250s のように表示します。", "Shutter speed. Without a format, displayed like 1-250s."),
                ShutterSpeedFormatDescription),
            ExifItem(TemplateTokenKind.ExposureTime,
                D("露光時間を秒単位の10進数で表示します。既定は小数最大6桁で末尾のゼロを省略します。", "Exposure time in seconds as a decimal. By default, shows up to six decimal places and removes trailing zeros."),
                DecimalNumberFormatDescription),
            ExifItem(TemplateTokenKind.Iso,
                D("ISO感度を整数で表示します。", "ISO speed as an integer."),
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.FocalLength,
                D("焦点距離。書式を省略すると 35mm または 23.5mm のように表示します。", "Focal length. Without a format, displayed like 35mm or 23.5mm."),
                NumberWithoutUnitFormatDescription),
            ExifItem(TemplateTokenKind.FocalLength35mm,
                D("35mm判換算焦点距離。書式を省略すると 35mm のように表示します。", "35mm-equivalent focal length. Without a format, displayed like 35mm."),
                NumberWithoutUnitFormatDescription),
            ExifItem(TemplateTokenKind.Rating,
                D("評価（スター）を1～5で表示します。除外は Rejected、未評価は Unknown になります。", "Rating (stars) from 1 to 5. Rejected means excluded and Unknown means unrated."),
                RatingFormatDescription),
            ExifItem(TemplateTokenKind.HasGps,
                D("有効なGPS位置情報があれば GPS、なければ NoGPS を表示します。", "Displays GPS when valid GPS coordinates exist; otherwise NoGPS.")),
            ExifItem(TemplateTokenKind.GpsLatitude,
                D("緯度。書式を省略すると符号付き10進度・小数6桁で表示します。", "Latitude. Without a format, displays signed decimal degrees with six decimal places."),
                GpsFormatDescription),
            ExifItem(TemplateTokenKind.GpsLongitude,
                D("経度。書式を省略すると符号付き10進度・小数6桁で表示します。", "Longitude. Without a format, displays signed decimal degrees with six decimal places."),
                GpsFormatDescription),
            ExifItem(TemplateTokenKind.GpsAltitude,
                D("高度（メートル）。書式を省略すると 34.5m のように表示し、海面下は負値になります。", "Altitude in meters. Without a format, displayed like 34.5m; values below sea level are negative."),
                NumberWithoutUnitFormatDescription)
        };

        private static readonly string DateFormatDescription = D(
            "指定できる書式: .NETカスタム日時書式（InvariantCulture）。既定は yyyyMMdd_HHmmss。指定子は d/dd/ddd/dddd（日）、f～fffffff・F～FFFFFFF（秒の小数部）、g（紀元）、h/hh（12時間）、H/HH（24時間）、K（タイムゾーン）、m/mm（分）、M/MM/MMM/MMMM（月）、s/ss（秒）、t/tt（午前/午後）、y/yy/yyy以上（年）、z/zz/zzz（UTC差）、引用符付きリテラル、%（単独指定子）、\\（エスケープ）です。: と / は日時区切り指定子ですが、結果にコロン、スラッシュなどWindowsパスで使用できない文字を含む書式は指定できません。",
            "Allowed format: .NET custom date/time format using InvariantCulture. Default: yyyyMMdd_HHmmss. Supported specifiers include day, fractional second, era, 12/24-hour, time zone, minute, month, second, AM/PM, year, UTC offset, quoted literals, %, and escaped characters. Formats whose result contains characters invalid in Windows paths, such as colons or slashes, are not allowed.");
        private static readonly string TimeZoneFormatDescription = D(
            "指定できる書式: タイムゾーン指定子、または タイムゾーン指定子|日時書式。指定子は UTC、JST、PST、MST、CST、EST、GMT、CET、または UTC±H / UTC±HH:MM（UTC-14:00～UTC+14:00）。日時書式を省略した場合は yyyyMMdd_HHmmss。",
            "Allowed format: a time-zone specifier, optionally followed by | and a date/time format. Use UTC, JST, PST, MST, CST, EST, GMT, CET, UTC±H, or UTC±HH:MM (UTC-14:00 to UTC+14:00). The default date/time format is yyyyMMdd_HHmmss.") + DateFormatDescription;
        private static readonly string IntegerNumberFormatDescription = D(
            "指定できる書式: .NET整数書式（InvariantCulture）。標準指定子は C、D、E、F、G、N、P、X（後ろに精度数字を指定可能）。カスタム指定子は 0、#、小数点、桁区切り・スケーリングのコンマ、%、‰、指数 E0/E+0/E-0、\\（エスケープ）、引用符付きリテラル、;（正・負・ゼロのセクション）で、組み合わせて指定できます。結果にWindowsパスで使用できない文字を含む書式は指定できません。値がなければ書式にかかわらず Unknown になります。",
            "Allowed format: a .NET integer format using InvariantCulture. Formats whose result contains characters invalid in Windows paths are not allowed. Unknown is displayed when no value exists.");
        private static readonly string DecimalNumberFormatDescription = D(
            "指定できる書式: .NET小数書式（InvariantCulture）。標準指定子は C、E、F、G、N、P（後ろに精度数字を指定可能）。カスタム指定子は 0、#、小数点、桁区切り・スケーリングのコンマ、%、‰、指数 E0/E+0/E-0、\\（エスケープ）、引用符付きリテラル、;（正・負・ゼロのセクション）で、組み合わせて指定できます。結果にWindowsパスで使用できない文字を含む書式は指定できません。値がなければ書式にかかわらず Unknown になります。",
            "Allowed format: a .NET decimal format using InvariantCulture. Formats whose result contains characters invalid in Windows paths are not allowed. Unknown is displayed when no value exists.");
        private static readonly string NumberWithoutUnitFormatDescription =
            DecimalNumberFormatDescription + D(" 書式を指定した場合、F、mm、mなどの接頭辞・単位は付かず、数値だけを表示します。", " When a format is specified, only the number is shown without prefixes or units such as F, mm, or m.");
        private static readonly string RatingFormatDescription =
            IntegerNumberFormatDescription + D(" 書式は評価1～5だけに適用され、Rejected と Unknown には適用されません。", " The format applies only to ratings 1–5, not Rejected or Unknown.");
        private static readonly string SourceDirectoryFormatDescription = D(
            "指定できる書式: 末尾から残す階層数を、先頭ゼロや符号のない1以上の10進整数で指定します。省略時は全階層を表示します。",
            "Allowed format: a positive decimal integer without a sign or leading zeros indicating how many trailing folder levels to keep. All levels are shown when omitted.");
        private static readonly string SequenceFormatDescription = D(
            "指定できる書式: 1～9の桁数。省略時は3桁です（例: 4を指定すると _0001）。",
            "Allowed format: a width from 1 to 9 digits. The default is 3 digits (for example, 4 produces _0001).");
        private static readonly string ShutterSpeedFormatDescription = D(
            "指定できる書式: 1-250s、1-250、1_250s、1_250 の4種類。省略時は1-250sです。",
            "Allowed formats: 1-250s, 1-250, 1_250s, or 1_250. The default is 1-250s.");
        private static readonly string GpsFormatDescription = D(
            "指定できる書式: dms（度-分-秒・小数1桁・半球記号）または dm（度-10進分・小数3桁・半球記号）。省略時は符号付き10進度・小数6桁です。",
            "Allowed formats: dms (degrees-minutes-seconds with one decimal and hemisphere) or dm (degrees and decimal minutes with three decimals and hemisphere). The default is signed decimal degrees with six decimals.");

        private static string D(string japanese, string english) => AppLocalization.Text(japanese, english);

        private static TokenDetailItem Item(
            TemplateTokenKind token,
            string description,
            string formatDescription = null) =>
            new TokenDetailItem(token, false, description, formatDescription);

        private static TokenDetailItem ExifItem(
            TemplateTokenKind token,
            string description,
            string formatDescription = null,
            string defaultFormat = null) =>
            new TokenDetailItem(token, true, description, formatDescription, defaultFormat);
    }

    public sealed class PreviewItem : INotifyPropertyChanged
    {
        private static readonly PhotoMetadataReadResult ScanErrorMetadataResult =
            PhotoMetadataReadResult.ReadError(new IOException("File information could not be read during scanning."));
        private bool _isSelected;
        private string _copyError;
        private string _relatedConflictMessage;

        public PreviewItem(
            string sourcePath,
            string destinationPath,
            DestinationStatus destinationStatus,
            CopyPlanItem copyPlan,
            IReadOnlyList<TemplateWarningCode> warnings = null,
            FileTemplateContext templateContext = null,
            PhotoMetadataReadResult metadataResult = null,
            string metadataSourcePath = null,
            int? sequenceNumber = null,
            string imagePreviewSourcePath = null,
            string relatedSourcePath = null)
        {
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
            DestinationStatus = destinationStatus;
            CopyPlan = copyPlan;
            Warnings = warnings ?? new TemplateWarningCode[0];
            TemplateContext = templateContext;
            MetadataResult = metadataResult;
            MetadataSourcePath = metadataSourcePath;
            SequenceNumber = sequenceNumber;
            ImagePreviewSourcePath = string.IsNullOrWhiteSpace(imagePreviewSourcePath)
                ? sourcePath
                : imagePreviewSourcePath;
            RelatedSourcePath = relatedSourcePath;
            _isSelected = CanCopy;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public string SourcePath { get; }
        public string DestinationPath { get; }
        public DestinationStatus DestinationStatus { get; private set; }
        public CopyPlanItem CopyPlan { get; private set; }
        public IReadOnlyList<TemplateWarningCode> Warnings { get; }
        public FileTemplateContext TemplateContext { get; private set; }
        public PhotoMetadataReadResult MetadataResult { get; private set; }
        public string MetadataSourcePath { get; private set; }
        public int? SequenceNumber { get; }
        public string ImagePreviewSourcePath { get; }
        public string RelatedSourcePath { get; }
        public bool IsAssociatedSidecar => !string.IsNullOrWhiteSpace(RelatedSourcePath);
        public bool CanCopy => CopyPlan != null && !IsScanError && _copyError == null;
        public bool IsScanError { get; private set; }
        public string ErrorMessage { get; private set; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                var next = CanCopy && value;
                if (_isSelected == next)
                {
                    if (value != next)
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    return;
                }
                _isSelected = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string Status
        {
            get
            {
                if (_copyError != null) return AppLocalization.Text("コピーエラー: ", "Copy error: ") + AppLocalization.UserMessage(_copyError);
                if (IsScanError) return AppLocalization.Text("スキャンエラー: ", "Scan error: ") + AppLocalization.UserMessage(ErrorMessage);
                if (_relatedConflictMessage != null)
                    return AppLocalization.Text("関連ファイル競合: ", "Related-file conflict: ") + _relatedConflictMessage;
                string status;
                switch (DestinationStatus)
                {
                    case DestinationStatus.Imported: status = AppLocalization.Text("取込済", "Imported"); break;
                    case DestinationStatus.Overwrite: status = AppLocalization.Text("上書き対象", "Overwrite"); break;
                    case DestinationStatus.Conflict: status = AppLocalization.Text("競合", "Conflict"); break;
                    default: status = AppLocalization.Text("未取込", "Not imported"); break;
                }
                if (IsAssociatedSidecar) status = AppLocalization.Text("サイドカー", "Sidecar: ") + status;
                if (Warnings.Contains(TemplateWarningCode.TakenDateFallbackToModifiedDate))
                    status += AppLocalization.Text("（撮影日時なし: 更新日時を使用）", " (no capture date: using modified date)");
                else if (Warnings.Contains(TemplateWarningCode.TakenDateOffsetMissing))
                    status += AppLocalization.Text("（Exif時差なし）", " (no Exif UTC offset)");
                if (Warnings.Contains(TemplateWarningCode.OrphanSidecarForcedSequence))
                    status += AppLocalization.Text("（孤立サイドカーを避けて連番を使用）", " (sequence used to avoid orphaned sidecar)");
                return status;
            }
        }

        internal void BlockByRelatedConflict(string message)
        {
            _relatedConflictMessage = string.IsNullOrWhiteSpace(message)
                ? AppLocalization.Text("関連ファイルを安全にコピーできません。", "The related file cannot be copied safely.")
                : message;
            DestinationStatus = DestinationStatus.Conflict;
            CopyPlan = null;
            _isSelected = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCopy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }

        public void SetCopyError(string error)
        {
            _copyError = string.IsNullOrWhiteSpace(error)
                ? AppLocalization.Text("不明なコピーエラー", "Unknown copy error")
                : error;
            _isSelected = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCopy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }

        internal FilterCandidate CreateFilterCandidate()
        {
            return CreateFilterCandidate(MetadataResult);
        }

        internal FilterCandidate CreateFilterCandidate(PhotoMetadataReadResult metadataResult)
        {
            var context = TemplateContext;
            return new FilterCandidate(
                context == null ? null : context.OriginalName,
                context == null ? (DateTime?)null : context.ModifiedDate,
                context == null ? (long?)null : context.FileSize,
                context == null ? null : context.SourceRelativeDirectory,
                context == null ? (bool?)null : context.IsReadOnly,
                SequenceNumber,
                GetFilterCopyStatus(),
                metadataResult ?? (IsScanError ? ScanErrorMetadataResult : null),
                !IsScanError);
        }

        internal void AttachMetadata(
            PhotoMetadataReadResult metadataResult,
            string metadataSourcePath,
            DateTime metadataSourceModifiedDate,
            DateTime metadataSourceModifiedDateUtc)
        {
            if (metadataResult == null) throw new ArgumentNullException(nameof(metadataResult));
            if (TemplateContext == null)
                throw new InvalidOperationException("A template context is required to attach metadata.");
            TemplateContext = new FileTemplateContext(
                TemplateContext.OriginalName,
                TemplateContext.ModifiedDate,
                TemplateContext.FileSize,
                TemplateContext.SourceRelativeDirectory,
                metadataResult.Metadata,
                TemplateContext.ModifiedDateUtc,
                metadataSourceModifiedDate,
                metadataSourceModifiedDateUtc,
                TemplateContext.IsReadOnly);
            MetadataResult = metadataResult;
            MetadataSourcePath = metadataSourcePath;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TemplateContext)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataResult)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataSourcePath)));
        }

        private FilterCopyStatus GetFilterCopyStatus()
        {
            if (IsScanError) return FilterCopyStatus.ScanError;
            if (_copyError != null) return FilterCopyStatus.CopyError;
            switch (DestinationStatus)
            {
                case DestinationStatus.Overwrite: return FilterCopyStatus.Overwrite;
                case DestinationStatus.Imported: return FilterCopyStatus.Imported;
                case DestinationStatus.Conflict: return FilterCopyStatus.Conflict;
                default: return FilterCopyStatus.NotImported;
            }
        }

        public static PreviewItem ForScanError(string sourcePath, string message) =>
            new PreviewItem(sourcePath, string.Empty, DestinationStatus.Conflict, null)
            { IsScanError = true, ErrorMessage = message };
    }

    internal static class PreviewSelectionState
    {
        public static bool? GetSelectAllState(IEnumerable<PreviewItem> items)
        {
            var copyableItems = items.Where(item => item.CanCopy).ToList();
            if (copyableItems.Count == 0 || copyableItems.All(item => !item.IsSelected)) return false;
            return copyableItems.All(item => item.IsSelected) ? true : (bool?)null;
        }

        public static void SetAllCopyable(IEnumerable<PreviewItem> items, bool isSelected)
        {
            foreach (var item in items.Where(item => item.CanCopy))
                item.IsSelected = isSelected;
        }

        public static IReadOnlyDictionary<string, bool> Capture(IEnumerable<PreviewItem> items) =>
            items.ToDictionary(
                item => item.SourcePath,
                item => item.IsSelected,
                StringComparer.OrdinalIgnoreCase);

        public static void RestoreAfterCopy(
            IEnumerable<PreviewItem> items,
            IReadOnlyDictionary<string, bool> previousSelection,
            IReadOnlyDictionary<string, string> copyErrors)
        {
            foreach (var item in items)
            {
                string error;
                if (copyErrors.TryGetValue(item.SourcePath, out error))
                {
                    item.SetCopyError(error);
                    continue;
                }

                bool wasSelected;
                if (previousSelection.TryGetValue(item.SourcePath, out wasSelected))
                    item.IsSelected = wasSelected;
            }
        }
    }

    internal sealed class PreviewBuildResult
    {
        public PreviewBuildResult(List<PreviewItem> items, List<string> warnings)
        {
            Items = items;
            Warnings = warnings;
        }

        public List<PreviewItem> Items { get; }
        public List<string> Warnings { get; }
    }

    internal sealed class FileSystemDestinationLookup : IDestinationFileLookup
    {
        private readonly string _root;
        public FileSystemDestinationLookup(string root) { _root = root; }

        public bool TryGetFile(string relativePath, out DestinationFileSnapshot snapshot)
        {
            var path = Path.Combine(_root, relativePath);
            if (!File.Exists(path)) { snapshot = null; return false; }
            var info = new FileInfo(path);
            snapshot = new DestinationFileSnapshot(info.Length, info.LastWriteTimeUtc);
            return true;
        }
    }
}
