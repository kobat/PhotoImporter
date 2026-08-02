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

        private enum OverlayPanel
        {
            None,
            ExifSettings,
            Filter,
            PresetManager
        }

        public MainWindow()
        {
            FileSystemTokenDetails = TokenDetailItem.CreateFileSystemItems();
            ExifTokenDetails = TokenDetailItem.CreateExifItems();
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
                settingsWarning = ex.Message + " 既定値で起動しました。";
            }

            InitializeComponent();
            _systemMenuAboutCommand = new SystemMenuAboutCommand(
                this,
                "バージョン情報(&A)...",
                ShowAboutWindow);
            _itemCollectionState = new PreviewItemCollectionState(Items);
            FilterFieldOptions = FilterFieldOption.CreateAll();
            DataContext = this;
            string historyWarning = null;
            try
            {
                ApplyInputHistory(_inputHistoryStore.Load());
            }
            catch (InvalidDataException ex)
            {
                historyWarning = ex.Message + " 入力履歴なしで起動しました。";
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
            new AboutWindow { Owner = this }.ShowDialog();
        }

        public ObservableCollection<PreviewItem> Items { get; } = new ObservableCollection<PreviewItem>();
        public ObservableCollection<FilterConditionEditor> FilterConditions { get; } = new ObservableCollection<FilterConditionEditor>();
        public ObservableCollection<PhotoImporterPreset> Presets { get; } = new ObservableCollection<PhotoImporterPreset>();
        public ObservableCollection<PhotoImporterPreset> ManagedPresets { get; } = new ObservableCollection<PhotoImporterPreset>();
        public ObservableCollection<string> SourceFolderHistory { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> DestinationFolderHistory { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> TemplateHistory { get; } = new ObservableCollection<string>();
        public IReadOnlyList<string> PresetManagerSortModes { get; } = new[] { "名前順", "最終利用日順" };
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
            ? "一覧からファイルを選択してください。"
            : SelectedPreviewItem.SourcePath;

        public string ExifReadStatus
        {
            get
            {
                if (SelectedPreviewItem == null) return string.Empty;
                if (SelectedPreviewItem.IsScanError) return "ファイル情報を取得できません。";
                var result = SelectedPreviewItem.MetadataResult;
                if (result == null) return "Exif情報は読み込まれていません。";
                var source = string.IsNullOrWhiteSpace(SelectedPreviewItem.MetadataSourcePath)
                    ? string.Empty
                    : " / 解析元: " + SelectedPreviewItem.MetadataSourcePath;
                switch (result.Status)
                {
                    case PhotoMetadataReadStatus.Success: return "Exif読込済み" + source;
                    case PhotoMetadataReadStatus.NoMetadata: return "Exif情報なし" + source;
                    case PhotoMetadataReadStatus.Unsupported: return "Exif未対応形式" + source;
                    default: return "Exif読取エラー: " + result.Error.Message + source;
                }
            }
        }

        public string ExifCacheRoot => string.IsNullOrWhiteSpace(_customExifCacheRoot)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ExifCache"))
            : Path.GetFullPath(_customExifCacheRoot);
        public string ExifSettingsSummary => string.Format(
            "Exif: {0} / キャッシュ {1} / 保存先: {2}",
            ReadExifInformation ? "常に読込" : "必要時のみ読込",
            UseExifCache ? "ON" : "OFF",
            string.IsNullOrWhiteSpace(_customExifCacheRoot) ? "既定" : ExifCacheRoot);
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
            ? HasPresetChanges ? "(プリセットなし) ● 未保存の変更" : "(プリセットなし)"
            : HasPresetChanges ? "● 未保存の変更" : string.Empty;
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
                if (_appliedFilterConditionSummaries.Count == 0) return "フィルター: 条件なし";
                var shown = string.Join(" / ", _appliedFilterConditionSummaries.Take(2));
                var remainder = _appliedFilterConditionSummaries.Count - 2;
                return "フィルター: " + shown +
                       (remainder > 0 ? string.Format(" / ほか{0}件", remainder) : string.Empty);
            }
        }
        public string FilterSummaryToolTip
        {
            get
            {
                var applied = _appliedFilterConditionSummaries.Count == 0
                    ? "適用中の条件はありません。"
                    : "適用中:" + Environment.NewLine +
                      string.Join(Environment.NewLine, _appliedFilterConditionSummaries.Select((item, index) =>
                          string.Format("{0}. {1}", index + 1, item)));
                if (!HasUnappliedFilterChanges) return applied;
                var draft = FilterConditions.Count == 0
                    ? "条件なし"
                    : string.Join(Environment.NewLine, FilterConditions.Select((item, index) =>
                        string.Format("{0}. {1}", index + 1, item.Summary)));
                return applied + Environment.NewLine + Environment.NewLine +
                       "未適用の編集:" + Environment.NewLine + draft;
            }
        }
        public bool HasUnappliedFilterChanges => !string.Equals(
            _appliedFilterStateKey,
            BuildCurrentFilterStateKey(),
            StringComparison.Ordinal);
        public string FilterEditStatus => HasUnappliedFilterChanges
            ? string.Format("未適用の変更あり（編集中 {0} 件）", FilterConditions.Count)
            : string.Format("適用済み {0} 件", AppliedFilterCount);
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
                        ? "再開"
                        : "一時停止";
                return string.Format("コピー ({0})", _itemCollectionState.GetCounts().Selected);
            }
        }
        public string ViewSelectionSummary
        {
            get
            {
                var counts = _itemCollectionState.GetCounts();
                return string.Format(
                    "表示 {0} / 全 {1}　チェック {2}（表示外 {3}）",
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
            SourceFolder = SelectFolder(SourceFolder, "コピー元フォルダーを選択してください") ?? SourceFolder;

        private void SelectDestination_Click(object sender, RoutedEventArgs e) =>
            DestinationFolder = SelectFolder(DestinationFolder, "コピー先フォルダーを選択してください") ?? DestinationFolder;

        private void SelectExifCacheRoot_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectFolder(ExifCacheRoot, "Exif キャッシュの保存先を選択してください");
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
                SetImagePreviewState(null, "一覧から画像を選択してください。");
                return;
            }
            if (item.IsScanError)
            {
                SetImagePreviewState(null, "スキャンエラーの項目はプレビューできません。");
                return;
            }
            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                SetImagePreviewState(null, "コピー元フォルダーを指定してください。");
                return;
            }

            string previewPath;
            try
            {
                var sourceRoot = Path.GetFullPath(SourceFolder);
                previewPath = Path.GetFullPath(Path.Combine(sourceRoot, item.ImagePreviewSourcePath));
                if (!IsSameOrUnder(previewPath, sourceRoot))
                    throw new InvalidOperationException("コピー元フォルダー外の画像はプレビューできません。");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException ||
                                       ex is InvalidOperationException)
            {
                SetImagePreviewState(null, "プレビュー元を確認できません: " + ex.Message);
                return;
            }

            var requestVersion = _imagePreviewRequestVersion;
            var cancellation = new CancellationTokenSource();
            _imagePreviewCancellation = cancellation;
            SetImagePreviewState(null, "読込中...");
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
                    SetImagePreviewState(null, "画像プレビューを読み込めません: " + ex.Message);
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
                    ? "コピー元が変更されました。再スキャンして画像を選択してください。"
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
            Summary = "スキャン中...";
            SetMessage("ファイルを調べています...", Brushes.DimGray);

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
                    ProgressText = "対象ファイルを検索しています...";
                    SetMessage("Exif情報を読み取っています...", Brushes.DimGray);
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
                    SetMessage(preview.Items.Count == 0 ? "コピー元にファイルがありません。" :
                        _exifCacheHits > 0 ? string.Format("プレビューを更新しました（Exif キャッシュ {0} 件）。", _exifCacheHits) :
                        "プレビューを更新しました。", Brushes.DimGray);
                return true;
            }
            catch (OperationCanceledException) when (scanCancellation != null && scanCancellation.IsCancellationRequested)
            {
                SetMessage("Exifスキャンを停止しました。解析済みのExifデータはキャッシュへ保存しました。", Brushes.DimGray);
            }
            catch (TemplateException ex) { ShowTemplateError(ex.Error); }
            catch (UnauthorizedAccessException) { SetMessage("アクセスできないフォルダーがあります。権限を確認してください。", Brushes.Firebrick); }
            catch (Exception ex) { SetMessage(ex.Message, Brushes.Firebrick); }
            finally
            {
                SetScanningExif(false);
                if (ReferenceEquals(_scanCancellation, scanCancellation)) _scanCancellation = null;
                scanCancellation?.Dispose();
                SetBusy(false, false);
                if (Summary == "スキャン中...") Summary = "0 件";
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
            SetMessage("コピーしています...", Brushes.DimGray);
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
                    string.Format(
                        "{0}（中止までに成功 {1} 件）",
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
                        : item.Error + " 保全した一時ファイル: " + item.RecoveryPath,
                    StringComparer.OrdinalIgnoreCase);

            var rescanned = await ScanAsync();
            if (!rescanned)
            {
                var rescanError = Message;
                SetMessage(
                    FormatCopyCompletion(copied, failed, result.Cancelled) +
                    " コピー後の一覧を更新できませんでした。手動でスキャンしてください。" +
                    (string.IsNullOrWhiteSpace(rescanError) ? string.Empty : " 詳細: " + rescanError),
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
                    FormatCopyCompletion(copied, failed, result.Cancelled) + " 再スキャンしました。",
                    failed > 0 ? Brushes.Firebrick : Brushes.DimGray);
            }
            else
            {
                SetMessage(
                    FormatCopyCompletion(copied, failed, result.Cancelled) +
                    " コピー後の一覧表示を更新できませんでした。手動でスキャンしてください。詳細: " +
                    postProcessingError,
                    Brushes.Firebrick);
            }
        }

        private static string FormatCopyCompletion(int copied, int failed, bool cancelled) =>
            string.Format(
                "コピー完了: 成功 {0} / エラー {1}{2}。",
                copied,
                failed,
                cancelled ? " / キャンセル" : string.Empty);

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
                        "残存する一時ファイル",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    SetMessage(
                        string.Format(
                            "コピー先に残存する一時ファイルを {0} 件検出しました。自動削除せず保全しています。",
                            result.Candidates.Count),
                        Brushes.DarkGoldenrod);
                }
                else if (result.Warnings.Count > 0)
                {
                    SetMessage(
                        "残存する一時ファイルを完全には検査できませんでした。コピー先の状態と権限を確認してください。",
                        Brushes.DarkGoldenrod);
                }
            }
            catch (Exception ex)
            {
                SetMessage(
                    "残存する一時ファイルを検査できませんでした: " + ex.Message,
                    Brushes.DarkGoldenrod);
            }
        }

        internal static string BuildPartialRecoveryMessage(PartialRecoveryScanResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var message = new StringBuilder()
                .AppendFormat(
                    "コピー先に Photo Importer の命名規則に一致する一時ファイルが {0} 件残っています。",
                    result.Candidates.Count)
                .AppendLine()
                .AppendLine("コピーの成否や対応する正式ファイルは一時名だけでは判断できないため、自動削除・自動昇格はしていません。")
                .AppendLine()
                .AppendLine("検出したファイル:");

            const int maximumDisplayedPaths = 12;
            foreach (var candidate in result.Candidates.Take(maximumDisplayedPaths))
                message.AppendLine(candidate.Path);
            if (result.Candidates.Count > maximumDisplayedPaths)
                message.AppendFormat("ほか {0} 件", result.Candidates.Count - maximumDisplayedPaths).AppendLine();

            message
                .AppendLine()
                .AppendLine("状態を確認して、次のように対応してください:")
                .AppendLine("・" + PartialRecoveryGuidance.Describe(PartialRecoveryDestinationState.Missing))
                .AppendLine("・" + PartialRecoveryGuidance.Describe(PartialRecoveryDestinationState.MatchesExpectedSource))
                .AppendLine("・" + PartialRecoveryGuidance.Describe(PartialRecoveryDestinationState.MatchesPreviousSnapshot))
                .AppendLine("・" + PartialRecoveryGuidance.Describe(PartialRecoveryDestinationState.RequiresComparison))
                .AppendLine()
                .Append("元写真が利用できる場合は、元写真を正として手動で再スキャンしてください。");

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
                ? "Exifスキャンを停止しています。現在のファイルを完了してキャッシュを保存します..."
                : "キャンセルしています...", Brushes.DimGray);
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
                    SetMessage("現在のファイル完了後に一時停止します...", Brushes.DimGray);
                    break;
                case CopyPauseState.NativePauseRequested:
                    SetMessage("3秒経過したため、現在のファイルを一時停止しています...", Brushes.DimGray);
                    break;
                case CopyPauseState.PausedBetweenFiles:
                case CopyPauseState.PausedWithinFile:
                    SetMessage("コピーを一時停止しました。", Brushes.DimGray);
                    break;
                case CopyPauseState.Running:
                    if (previousState != CopyPauseState.Running)
                        SetMessage("コピーを再開しました...", Brushes.DimGray);
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
            CopyProgressSummaryText = string.Format(
                "処理済み 0 / {0} 件    容量 0 B / {1}",
                totalFiles,
                FormatBytes(totalBytes));
            CopyProgressRatesText = "全体平均 計算中...    直近1分 計算中...";
            CopyProgressTimeText = "経過 00:00:00    残り 計算中...";
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
            CopyProgressSummaryText = string.Format(
                "処理済み {0} / {1} 件    容量 {2} / {3}",
                progress.CompletedFiles,
                progress.TotalFiles,
                FormatBytes(progress.CompletedWorkBytes),
                FormatBytes(progress.TotalBytes));

            CopyProgressRatesText = string.Format(
                "全体平均 {0}    直近1分 {1}",
                FormatCopyRates(
                    snapshot.OverallFilesPerSecond,
                    snapshot.OverallBytesPerSecond),
                FormatCopyRates(
                    snapshot.RecentFilesPerSecond,
                    snapshot.RecentBytesPerSecond));

            string remaining;
            if (!snapshot.EstimatedRemaining.HasValue)
                remaining = "計算中...";
            else if (snapshot.EstimatedRemaining.Value == TimeSpan.Zero)
                remaining = "00:00:00";
            else
                remaining = "約 " + FormatDuration(snapshot.EstimatedRemaining.Value);
            if (snapshot.IsPaused) remaining += "（一時停止中）";

            CopyProgressTimeText = string.Format(
                "経過 {0}    残り {1}",
                FormatDuration(snapshot.ActiveElapsed),
                remaining);
        }

        private static string FormatCopyRates(
            double? filesPerSecond,
            double? bytesPerSecond)
        {
            if (!filesPerSecond.HasValue || !bytesPerSecond.HasValue)
                return "計算中...";

            return string.Format(
                "{0} 件/秒・{1} MB/秒",
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
                    ProgressText = string.Format(
                        "Exifスキャンを準備しています（解析対象 {0} 件）...",
                        progress.TotalFiles);
                    break;
                case PhotoMetadataScanPhase.Reading:
                    IsProgressIndeterminate = false;
                    ProgressPercent = progress.TotalFiles == 0
                        ? 100
                        : Math.Min(100, progress.CompletedFiles * 100.0 / progress.TotalFiles);
                    ProgressText = string.Format(
                        "Exif {0}/{1} 件（{2:0}%、キャッシュ {3} 件）",
                        progress.CompletedFiles,
                        progress.TotalFiles,
                        ProgressPercent,
                        progress.CacheHits);
                    break;
                case PhotoMetadataScanPhase.SavingCache:
                    IsProgressIndeterminate = true;
                    ProgressText = "Exifキャッシュを保存しています...";
                    break;
                case PhotoMetadataScanPhase.Completed:
                    IsProgressIndeterminate = true;
                    ProgressText = "Exif結果を一覧へ反映しています...";
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
                throw new NotSupportedException(string.Format(
                    "コピー先のファイルシステム「{0}」には対応していません。NTFS、ReFS、exFAT、FAT、FAT32 のいずれかを使用してください。",
                    string.IsNullOrWhiteSpace(destinationVolume.FileSystemName)
                        ? "不明"
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
                        "Exif キャッシュの保存先 ({0}) がコピー元またはコピー先と重なるため、キャッシュなしで続行しました。",
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
                        warnings.Add("コピー元のボリューム情報を取得できないため、Exif キャッシュなしで続行しました: " + ex.Message);
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
                                "関連先画像のコピー先を決定できません。");
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
                        child.BlockByRelatedConflict("関連先画像が競合しています。");
                    continue;
                }

                if (parent.CopyPlan != null &&
                    children.Any(child => child.IsScanError ||
                                          child.DestinationStatus == DestinationStatus.Conflict))
                {
                    parent.BlockByRelatedConflict("関連サイドカーが競合または読取エラーです。");
                    foreach (var child in children.Where(child => child.CanCopy))
                        child.BlockByRelatedConflict("関連先画像と同時にコピーできません。");
                }
            }
        }

        private void PreviewItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
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
            SetMessage("一覧フィルターをクリアしました。", Brushes.DimGray);
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
                    ? "条件なしで全項目を表示しました。"
                    : string.Format("一覧フィルターを適用しました（{0} 条件）。", conditionCount), Brushes.DimGray);
                return true;
            }
            catch (FilterEvaluationException ex)
            {
                SetMessage("フィルター評価エラー: " + ex.Message, Brushes.Firebrick);
                return false;
            }
        }

        private async Task<bool> LoadExifForFilterAsync(PreparedFilter prepared, int conditionCount)
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
                ProgressText = "Exifスキャン準備中...";
                SetMessage("フィルターに必要なExif情報を読み取っています。現在の一覧は完了まで維持されます...", Brushes.DimGray);
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
                _appliedFilter = conditionCount == 0 ? null : prepared;
                CommitAppliedFilterEditorState(conditionCount);
                ApplyPreviewFilter(_appliedFilter == null
                    ? (Predicate<PreviewItem>)null
                    : item => _appliedFilter.Matches(item.CreateFilterCandidate()));
                SetMessage(loadResult.Warnings.Count == 0
                    ? string.Format("Exif情報を読み込み、一覧フィルターを適用しました（{0} 条件）。", conditionCount)
                    : string.Join(" ", loadResult.Warnings),
                    loadResult.Warnings.Count == 0 ? Brushes.DimGray : Brushes.DarkGoldenrod);
                return true;
            }
            catch (OperationCanceledException) when (scanCancellation != null && scanCancellation.IsCancellationRequested)
            {
                SetMessage("Exifスキャンを停止しました。直前の一覧とフィルターを維持しています。", Brushes.DimGray);
            }
            catch (FilterEvaluationException ex)
            {
                SetMessage("フィルター評価エラー: " + ex.Message + " 直前の一覧を維持しています。", Brushes.Firebrick);
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message + " 直前の一覧とフィルターを維持しています。", Brushes.Firebrick);
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
            Summary = string.Format(
                "{0} 件（対象 {1} / 未取込 {2} / 上書き {3} / 取込済 {4} / 競合・エラー {5}）",
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
                SetMessage("プリセットは適用しましたが、最終利用日時を保存できませんでした。 " + ex.Message,
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
                SetMessage("プリセット「" + updated.Name + "」を保存しました。", Brushes.DimGray);
                return true;
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError("プリセットを保存できませんでした。", ex);
                ReloadPresets(SelectedPreset?.Id, false);
                return false;
            }
        }

        private bool SaveCurrentPresetAs()
        {
            if (_isBusy) return false;
            var input = PresetDialogs.PromptForName(this, "名前を付けて保存", string.Empty, false, true);
            if (input == null) return false;
            string name;
            try
            {
                name = PhotoImporterPresetStore.NormalizeName(input.Name);
            }
            catch (ArgumentException ex)
            {
                ShowPresetError("プリセット名が正しくありません。", ex);
                return false;
            }

            ReloadPresets(SelectedPreset?.Id, true);
            var existing = Presets.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var confirmation = MessageBox.Show(
                    this,
                    "同じ名前のプリセットがあります。\n「" + existing.Name + "」を上書きしますか？",
                    "プリセットを上書き",
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
                SetMessage("プリセット「" + preset.Name + "」を保存しました。", Brushes.DimGray);
                return true;
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError("プリセットを保存できませんでした。", ex);
                ReloadPresets(SelectedPreset?.Id, false);
                return false;
            }
        }

        private void RenamePreset_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedManagedPreset;
            if (selected == null) return;
            var input = PresetDialogs.PromptForName(
                this, "プリセット名を変更", selected.Name, selected.SaveSourceFolder, false);
            if (input == null) return;
            try
            {
                var renamed = selected.Clone();
                renamed.Name = PhotoImporterPresetStore.NormalizeName(input.Name);
                renamed.UpdatedUtc = DateTime.UtcNow;
                var refreshed = _presetStore.Update(renamed);
                ReplacePresets(refreshed, SelectedPreset?.Id, false);
                RefreshManagedPresets(renamed.Id);
                SetMessage("プリセット名を「" + renamed.Name + "」へ変更しました。", Brushes.DimGray);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError("プリセット名を変更できませんでした。", ex);
                ReloadPresets(SelectedPreset?.Id, false);
                RefreshManagedPresets(selected.Id);
            }
        }

        private void DuplicatePreset_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedManagedPreset;
            if (selected == null) return;
            var input = PresetDialogs.PromptForName(
                this, "プリセットを複製", selected.Name + " - コピー", selected.SaveSourceFolder, false);
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
                SetMessage("プリセット「" + duplicate.Name + "」を作成しました。", Brushes.DimGray);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError("プリセットを複製できませんでした。", ex);
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
                "プリセット「" + selected.Name + "」を削除しますか？\n\n" +
                "削除しても、コピー済みの写真と設定ファイルの現在値は変わりません。",
                "プリセットを削除",
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
                SetMessage("プリセット「" + selected.Name + "」を削除しました。", Brushes.DimGray);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError("プリセットを削除できませんでした。", ex);
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
                Title = "プリセットをエクスポート",
                Filter = "PhotoImporter プリセット (*.xml)|*.xml|すべてのファイル (*.*)|*.*",
                DefaultExt = ".xml",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = CreateSafePresetFileName(selected.Name) + ".xml"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _presetStore.WriteExportFile(dialog.FileName, selected);
                SetMessage("プリセットをエクスポートしました。 " + dialog.FileName, Brushes.DimGray);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError("プリセットをエクスポートできませんでした。", ex);
            }
        }

        private void ImportPreset_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "プリセットをインポート",
                Filter = "PhotoImporter プリセット (*.xml)|*.xml|すべてのファイル (*.*)|*.*",
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
                        "インポートするプリセットの id と名前が、それぞれ別の既存プリセットと重複しています。");

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
                            this, "別名でインポート", imported.Name + " - コピー", imported.SaveSourceFolder, false);
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
                        ? "プリセット「" + imported.Name + "」をインポートしました。"
                        : "プリセットをインポートしましたが、設定に警告があります。 " + warning,
                    string.IsNullOrEmpty(warning) ? Brushes.DimGray : Brushes.DarkGoldenrod);
            }
            catch (Exception ex) when (IsPresetStoreFailure(ex))
            {
                ShowPresetError("プリセットをインポートできませんでした。", ex);
                ReloadPresets(SelectedPreset?.Id, false);
                RefreshManagedPresets(null);
            }
        }

        private void ApplyManagedTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedManagedPreset == null || _isBusy) return;
            TemplateText = SelectedManagedPreset.TemplateText;
            SetMessage("プリセット「" + SelectedManagedPreset.Name + "」からテンプレートだけを取り込みました。",
                Brushes.DimGray);
        }

        private void RefreshManagedPresets(Guid? selectedId)
        {
            if (ManagedPresets == null) return;
            var ordered = string.Equals(PresetManagerSortMode, "最終利用日順", StringComparison.Ordinal)
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
                    warnings.Add("コピー先が絶対パスではありません。");
                else if (!Directory.Exists(preset.DestinationFolder))
                    warnings.Add("コピー先が存在しません。");
                if (preset.SaveSourceFolder)
                {
                    if (string.IsNullOrWhiteSpace(preset.SourceFolder) || !Path.IsPathRooted(preset.SourceFolder))
                        warnings.Add("コピー元が絶対パスではありません。");
                    else if (!Directory.Exists(preset.SourceFolder))
                        warnings.Add("コピー元が存在しません。");
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                warnings.Add("フォルダーパスが正しくありません。");
            }
            var parsed = TemplateParser.Parse(preset.TemplateText ?? string.Empty);
            if (!parsed.IsValid) warnings.Add("テンプレートが正しくありません。");
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
                    throw new ArgumentException("コピー先には絶対パスを指定してください。");
                if (saveSourceFolder &&
                    (string.IsNullOrWhiteSpace(SourceFolder) || !Path.IsPathRooted(SourceFolder)))
                    throw new ArgumentException("保存するコピー元には絶対パスを指定してください。");
                var destination = NormalizePath(DestinationFolder);
                var source = saveSourceFolder ? NormalizePath(SourceFolder) : null;
                if (saveSourceFolder &&
                    (IsSameOrUnder(source, destination) || IsSameOrUnder(destination, source)))
                    throw new InvalidOperationException(
                        "コピー元とコピー先には、同一または互いの配下ではないフォルダーを指定してください。");
                var parsed = TemplateParser.Parse(TemplateText);
                if (!parsed.IsValid)
                    throw new ArgumentException(string.Format(
                        "テンプレートエラー: {0}（位置 {1}）", parsed.Error.Code, parsed.Error.Position + 1));
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
                ShowPresetError("現在の設定をプリセットへ保存できません。", ex);
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
                    throw new ArgumentException("コピー元には絶対パスを指定してください。");
                if (string.IsNullOrWhiteSpace(DestinationFolder) || !Path.IsPathRooted(DestinationFolder))
                    throw new ArgumentException("コピー先には絶対パスを指定してください。");
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
                SetMessage("プリセットを適用しました。再スキャンしてください。", Brushes.DimGray);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is InvalidOperationException ||
                                       ex is NotSupportedException)
            {
                SetMessage("プリセットを適用しましたが、設定にエラーがあります。 " + ex.Message,
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
            SetMessage(title + " " + ex.Message, Brushes.Firebrick);
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    SetMessage("Exif キャッシュの保存先を既定値へ戻しました。", Brushes.DimGray);
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
                SetMessage(string.Format(
                    "Exif キャッシュの保存先を使用できません ({0}): {1}", normalizedNewRoot, ex.Message),
                    Brushes.Firebrick);
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                "Exif キャッシュの保存先を変更します。\n\n" +
                "現在: " + oldRoot + "\n" +
                "変更後: " + normalizedNewRoot + "\n\n" +
                "以前の保存先にあるキャッシュは残り、通常のスキャンでは使われなくなります。",
                "Exif キャッシュの保存先を変更",
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
            SetMessage("Exif キャッシュの保存先を変更しました。", Brushes.DimGray);
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
                return "入力履歴を保存できませんでした: " + ex.Message;
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
                LastAppliedPresetId = SelectedPreset?.Id
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
                    "設定を保存できませんでした。\n" + _settingsStore.SettingsPath + "\n\n" + ex.Message,
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
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("コピー元フォルダーが見つかりません。");
            if (!Directory.Exists(destinationRoot)) throw new DirectoryNotFoundException("コピー先フォルダーが見つかりません。");
            if (IsSameOrUnder(sourceRoot, destinationRoot) || IsSameOrUnder(destinationRoot, sourceRoot))
                throw new InvalidOperationException("コピー元とコピー先には、同一または互いの配下ではないフォルダーを指定してください。");
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
            SetMessage(string.Format("テンプレートエラー: {0}（位置 {1}）", error.Code, error.Position + 1), Brushes.Firebrick);
        private void SetMessage(string value, Brush brush) { Message = value; MessageBrush = brush; }

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
                throw new InvalidOperationException("コピー元フォルダー外のパスは処理できません。");
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
                Value = "未読み込み";
                return;
            }
            if (_isExif && _previewItem.MetadataResult.Status == PhotoMetadataReadStatus.ReadError)
            {
                Value = "読取エラー";
                return;
            }

            var format = SupportsFormat && !string.IsNullOrEmpty(Format) ? ":" + Format : string.Empty;
            var source = "x{" + _token + format + "}";
            var parsed = TemplateParser.Parse(source);
            if (!parsed.IsValid)
            {
                Value = "書式エラー: " + parsed.Error.Code;
                return;
            }

            try
            {
                var evaluated = TemplateEvaluator.Evaluate(
                    parsed.Template,
                    _previewItem.TemplateContext,
                    _token == TemplateTokenKind.Sequence ? _previewItem.SequenceNumber : null);
                var tokenValue = evaluated.Length == 0 ? string.Empty : evaluated.Substring(1);
                Value = tokenValue.Length == 0 ? "（空文字）" : tokenValue;
            }
            catch (TemplateException ex)
            {
                Value = "書式エラー: " + ex.Error.Code;
            }
        }

        public static IReadOnlyList<TokenDetailItem> CreateFileSystemItems() => new[]
        {
            Item(TemplateTokenKind.OriginalName,
                "元ファイル名。最後の拡張子を含みます（例: DSC_0101.NEF）。"),
            Item(TemplateTokenKind.FileName,
                "元ファイル名から最後の拡張子を除いた部分です（例: DSC_0101）。"),
            Item(TemplateTokenKind.Extension,
                "最後の拡張子。先頭のピリオドを含み、拡張子がなければ空文字になります（例: .NEF）。"),
            Item(TemplateTokenKind.SourceRelativeDirectory,
                "コピー元ルートからファイル格納フォルダーまでの相対パス。コピー元ルート直下では空文字になります。",
                SourceDirectoryFormatDescription),
            Item(TemplateTokenKind.ModifiedDate,
                "元ファイルの最終更新日時です。",
                DateFormatDescription),
            Item(TemplateTokenKind.FileSize,
                "元ファイルのバイト数を10進整数で表示します。"),
            Item(TemplateTokenKind.Protected,
                "読み取り専用属性があれば Protected、なければ Unprotected を表示します。"),
            Item(TemplateTokenKind.Sequence,
                "宛先の競合を避ける連番。競合がなければ空文字、競合時はアンダースコア付きの連番になります。",
                SequenceFormatDescription)
        };

        public static IReadOnlyList<TokenDetailItem> CreateExifItems() => new[]
        {
            ExifItem(TemplateTokenKind.TakenDate,
                "Exifの撮影日時をタイムゾーン変換せず、記録された壁時計値のまま表示します。サブ秒も使用できます。",
                DateFormatDescription),
            ExifItem(TemplateTokenKind.TakenDateLocal,
                "Exifの撮影日時を、このPCの現在のタイムゾーンへ変換して表示します。",
                DateFormatDescription),
            ExifItem(TemplateTokenKind.TakenDateInTimeZone,
                "Exifの撮影日時を、指定したタイムゾーンへ変換して表示します。",
                TimeZoneFormatDescription,
                "JST|yyyy-MM-dd HH-mm-ss"),
            ExifItem(TemplateTokenKind.CameraMake,
                "カメラのメーカー名。値がなければ Unknown になります。"),
            ExifItem(TemplateTokenKind.CameraModel,
                "カメラのモデル名。値がなければ Unknown になります。"),
            ExifItem(TemplateTokenKind.CameraSerial,
                "カメラボディのシリアル番号。値がなければ Unknown になります。"),
            ExifItem(TemplateTokenKind.Lens,
                "レンズのモデル名。値がなければ Unknown になります。"),
            ExifItem(TemplateTokenKind.Width,
                "Exifの向きを反映した画像の幅（ピクセル）です。",
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.Height,
                "Exifの向きを反映した画像の高さ（ピクセル）です。",
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.ExifWidth,
                "Exifに記録された向き反映前の幅（ピクセル）です。",
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.ExifHeight,
                "Exifに記録された向き反映前の高さ（ピクセル）です。",
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.Orientation,
                "Exif Orientation の値（1～8）です。",
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.Aperture,
                "絞り値。書式を省略すると F2.8 のように表示します。",
                NumberWithoutUnitFormatDescription),
            ExifItem(TemplateTokenKind.ShutterSpeed,
                "シャッタースピード。書式を省略すると 1-250s のように表示します。",
                ShutterSpeedFormatDescription),
            ExifItem(TemplateTokenKind.ExposureTime,
                "露光時間を秒単位の10進数で表示します。既定は小数最大6桁で末尾のゼロを省略します。",
                DecimalNumberFormatDescription),
            ExifItem(TemplateTokenKind.Iso,
                "ISO感度を整数で表示します。",
                IntegerNumberFormatDescription),
            ExifItem(TemplateTokenKind.FocalLength,
                "焦点距離。書式を省略すると 35mm または 23.5mm のように表示します。",
                NumberWithoutUnitFormatDescription),
            ExifItem(TemplateTokenKind.FocalLength35mm,
                "35mm判換算焦点距離。書式を省略すると 35mm のように表示します。",
                NumberWithoutUnitFormatDescription),
            ExifItem(TemplateTokenKind.Rating,
                "評価（スター）を1～5で表示します。除外は Rejected、未評価は Unknown になります。",
                RatingFormatDescription),
            ExifItem(TemplateTokenKind.HasGps,
                "有効なGPS位置情報があれば GPS、なければ NoGPS を表示します。"),
            ExifItem(TemplateTokenKind.GpsLatitude,
                "緯度。書式を省略すると符号付き10進度・小数6桁で表示します。",
                GpsFormatDescription),
            ExifItem(TemplateTokenKind.GpsLongitude,
                "経度。書式を省略すると符号付き10進度・小数6桁で表示します。",
                GpsFormatDescription),
            ExifItem(TemplateTokenKind.GpsAltitude,
                "高度（メートル）。書式を省略すると 34.5m のように表示し、海面下は負値になります。",
                NumberWithoutUnitFormatDescription)
        };

        private const string DateFormatDescription =
            "指定できる書式: .NETカスタム日時書式（InvariantCulture）。既定は yyyyMMdd_HHmmss。" +
            "指定子は d/dd/ddd/dddd（日）、f～fffffff・F～FFFFFFF（秒の小数部）、g（紀元）、h/hh（12時間）、H/HH（24時間）、K（タイムゾーン）、m/mm（分）、M/MM/MMM/MMMM（月）、s/ss（秒）、t/tt（午前/午後）、y/yy/yyy以上（年）、z/zz/zzz（UTC差）、引用符付きリテラル、%（単独指定子）、\\（エスケープ）です。" +
            ": と / は日時区切り指定子ですが、結果にコロン、スラッシュなどWindowsパスで使用できない文字を含む書式は指定できません。";
        private const string TimeZoneFormatDescription =
            "指定できる書式: タイムゾーン指定子、または タイムゾーン指定子|日時書式。" +
            "指定子は UTC、JST、PST、MST、CST、EST、GMT、CET、または UTC±H / UTC±HH:MM（UTC-14:00～UTC+14:00）。" +
            "日時書式を省略した場合は yyyyMMdd_HHmmss。" + DateFormatDescription;
        private const string IntegerNumberFormatDescription =
            "指定できる書式: .NET整数書式（InvariantCulture）。標準指定子は C、D、E、F、G、N、P、X（後ろに精度数字を指定可能）。" +
            "カスタム指定子は 0、#、小数点、桁区切り・スケーリングのコンマ、%、‰、指数 E0/E+0/E-0、\\（エスケープ）、引用符付きリテラル、;（正・負・ゼロのセクション）で、組み合わせて指定できます。" +
            "結果にWindowsパスで使用できない文字を含む書式は指定できません。値がなければ書式にかかわらず Unknown になります。";
        private const string DecimalNumberFormatDescription =
            "指定できる書式: .NET小数書式（InvariantCulture）。標準指定子は C、E、F、G、N、P（後ろに精度数字を指定可能）。" +
            "カスタム指定子は 0、#、小数点、桁区切り・スケーリングのコンマ、%、‰、指数 E0/E+0/E-0、\\（エスケープ）、引用符付きリテラル、;（正・負・ゼロのセクション）で、組み合わせて指定できます。" +
            "結果にWindowsパスで使用できない文字を含む書式は指定できません。値がなければ書式にかかわらず Unknown になります。";
        private const string NumberWithoutUnitFormatDescription =
            DecimalNumberFormatDescription + " 書式を指定した場合、F、mm、mなどの接頭辞・単位は付かず、数値だけを表示します。";
        private const string RatingFormatDescription =
            IntegerNumberFormatDescription + " 書式は評価1～5だけに適用され、Rejected と Unknown には適用されません。";
        private const string SourceDirectoryFormatDescription =
            "指定できる書式: 末尾から残す階層数を、先頭ゼロや符号のない1以上の10進整数で指定します。省略時は全階層を表示します。";
        private const string SequenceFormatDescription =
            "指定できる書式: 1～9の桁数。省略時は3桁です（例: 4を指定すると _0001）。";
        private const string ShutterSpeedFormatDescription =
            "指定できる書式: 1-250s、1-250、1_250s、1_250 の4種類。省略時は1-250sです。";
        private const string GpsFormatDescription =
            "指定できる書式: dms（度-分-秒・小数1桁・半球記号）または dm（度-10進分・小数3桁・半球記号）。省略時は符号付き10進度・小数6桁です。";

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
                if (_copyError != null) return "コピーエラー: " + _copyError;
                if (IsScanError) return "スキャンエラー: " + ErrorMessage;
                if (_relatedConflictMessage != null)
                    return "関連ファイル競合: " + _relatedConflictMessage;
                string status;
                switch (DestinationStatus)
                {
                    case DestinationStatus.Imported: status = "取込済"; break;
                    case DestinationStatus.Overwrite: status = "上書き対象"; break;
                    case DestinationStatus.Conflict: status = "競合"; break;
                    default: status = "未取込"; break;
                }
                if (IsAssociatedSidecar) status = "サイドカー" + status;
                if (Warnings.Contains(TemplateWarningCode.TakenDateFallbackToModifiedDate))
                    status += "（撮影日時なし: 更新日時を使用）";
                else if (Warnings.Contains(TemplateWarningCode.TakenDateOffsetMissing))
                    status += "（Exif時差なし）";
                if (Warnings.Contains(TemplateWarningCode.OrphanSidecarForcedSequence))
                    status += "（孤立サイドカーを避けて連番を使用）";
                return status;
            }
        }

        internal void BlockByRelatedConflict(string message)
        {
            _relatedConflictMessage = string.IsNullOrWhiteSpace(message)
                ? "関連ファイルを安全にコピーできません。"
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
            _copyError = string.IsNullOrWhiteSpace(error) ? "不明なコピーエラー" : error;
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
