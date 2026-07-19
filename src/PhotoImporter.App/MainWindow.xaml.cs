using PhotoImporter.Core.Copying;
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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
        private Brush _messageBrush = Brushes.DimGray;
        private bool _isBusy;
        private bool _isCopying;
        private bool _isScanningExif;
        private bool _overwriteExisting;
        private bool _analyzeJpegOnlyForRawJpegPair = true;
        private bool _useExifCache = true;
        private bool _readExifInformation;
        private string _customExifCacheRoot;
        private readonly List<string> _previousExifCacheRoots = new List<string>();
        private readonly PhotoImporterSettingsStore _settingsStore;
        private bool _previewIsCurrent;
        private double _progressPercent;
        private int _exifCacheHits;
        private CancellationTokenSource _copyCancellation;
        private CancellationTokenSource _scanCancellation;
        private PreviewItem _selectedPreviewItem;

        public MainWindow()
        {
            FileSystemTokenDetails = TokenDetailItem.CreateFileSystemItems();
            ExifTokenDetails = TokenDetailItem.CreateExifItems();
            _settingsStore = new PhotoImporterSettingsStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoImporter",
                "settings.xml"));
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
            DataContext = this;
            Closing += MainWindow_Closing;
            if (settingsWarning != null) SetMessage(settingsWarning, Brushes.DarkGoldenrod);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<PreviewItem> Items { get; } = new ObservableCollection<PreviewItem>();
        public IReadOnlyList<TokenDetailItem> FileSystemTokenDetails { get; }
        public IReadOnlyList<TokenDetailItem> ExifTokenDetails { get; }

        public string SourceFolder
        {
            get => _sourceFolder;
            set { if (Set(ref _sourceFolder, value)) SettingsChanged(); }
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

        public bool AnalyzeJpegOnlyForRawJpegPair
        {
            get => _analyzeJpegOnlyForRawJpegPair;
            set { if (Set(ref _analyzeJpegOnlyForRawJpegPair, value)) SettingsChanged(); }
        }

        public bool UseExifCache
        {
            get => _useExifCache;
            set { if (Set(ref _useExifCache, value)) SettingsChanged(); }
        }

        public bool ReadExifInformation
        {
            get => _readExifInformation;
            set { if (Set(ref _readExifInformation, value)) SettingsChanged(); }
        }

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

        public string Message { get => _message; private set => Set(ref _message, value); }
        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
        public Brush MessageBrush { get => _messageBrush; private set => Set(ref _messageBrush, value); }
        public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
        public Visibility ProgressVisibility => _isCopying || _isScanningExif ? Visibility.Visible : Visibility.Collapsed;
        public bool CanEditSettings => !_isBusy;
        public bool CanSelectItems => !_isBusy;
        public bool CanCancel => _isCopying || _isScanningExif;
        public bool CanScan => !_isBusy && !string.IsNullOrWhiteSpace(SourceFolder) &&
                               !string.IsNullOrWhiteSpace(DestinationFolder) &&
                               !string.IsNullOrWhiteSpace(TemplateText);
        public bool CanCopy => !_isBusy && _previewIsCurrent && Items.Any(item => item.IsSelected && item.CanCopy);

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

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            await ScanAsync();
        }

        private async Task<bool> ScanAsync()
        {
            CancellationTokenSource scanCancellation = null;
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
                var rawJpegAnalysisMode = AnalyzeJpegOnlyForRawJpegPair
                    ? RawJpegAnalysisMode.JpegOnlyForPair
                    : RawJpegAnalysisMode.AnalyzeBoth;
                var useExifCache = UseExifCache;
                var readExifInformation = ReadExifInformation;
                var exifCacheRoot = ExifCacheRoot;
                var shouldReadExif = parseResult.Template.RequiresExif || readExifInformation;
                IProgress<PhotoMetadataScanProgress> exifProgress = null;
                if (shouldReadExif)
                {
                    scanCancellation = new CancellationTokenSource();
                    _scanCancellation = scanCancellation;
                    _isScanningExif = true;
                    _exifCacheHits = 0;
                    ProgressPercent = 0;
                    ProgressText = "Exifスキャン準備中...";
                    OnPropertyChanged(nameof(ProgressVisibility));
                    OnPropertyChanged(nameof(CanCancel));
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
                    rawJpegAnalysisMode,
                    useExifCache,
                    readExifInformation,
                    exifCacheRoot,
                    exifProgress,
                    cancellationToken), cancellationToken);
                foreach (var row in preview.Items)
                {
                    row.PropertyChanged += PreviewItem_PropertyChanged;
                    Items.Add(row);
                }

                _previewIsCurrent = true;
                UpdateSummary();
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
                _isScanningExif = false;
                if (ReferenceEquals(_scanCancellation, scanCancellation)) _scanCancellation = null;
                scanCancellation?.Dispose();
                SetBusy(false, false);
                if (Summary == "スキャン中...") Summary = "0 件";
            }
            return false;
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            var selected = Items.Where(item => item.IsSelected && item.CanCopy).ToList();
            if (selected.Count == 0) return;

            _copyCancellation = new CancellationTokenSource();
            SetBusy(true, true);
            SetMessage("コピーしています...", Brushes.DimGray);
            ProgressPercent = 0;

            CopyBatchResult result = null;
            try
            {
                var progress = new Progress<CopyProgress>(UpdateCopyProgress);
                result = await Task.Run(() => new CopyEngine().Execute(
                    selected.Select(item => item.CopyPlan),
                    progress,
                    _copyCancellation.Token));
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message, Brushes.Firebrick);
            }
            finally
            {
                _copyCancellation.Dispose();
                _copyCancellation = null;
                SetBusy(false, false);
            }

            if (result == null) return;

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
            if (rescanned)
            {
                foreach (var row in Items)
                {
                    string error;
                    if (errors.TryGetValue(row.SourcePath, out error)) row.SetCopyError(error);
                }
                SetMessage(
                    string.Format("コピー完了: 成功 {0} / エラー {1}{2}。再スキャンしました。",
                        copied, failed, result.Cancelled ? " / キャンセル" : string.Empty),
                    failed > 0 ? Brushes.Firebrick : Brushes.DimGray);
                UpdateSummary();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _scanCancellation?.Cancel();
            _copyCancellation?.Cancel();
            SetMessage(_isScanningExif
                ? "Exifスキャンを停止しています。現在のファイルを完了してキャッシュを保存します..."
                : "キャンセルしています...", Brushes.DimGray);
        }

        private void UpdateCopyProgress(CopyProgress progress)
        {
            ProgressPercent = progress.TotalBytes == 0
                ? 0
                : Math.Min(100, progress.TransferredBytes * 100.0 / progress.TotalBytes);
            ProgressText = string.Format(
                "{0}/{1} 件  {2}/{3}",
                progress.CompletedFiles,
                progress.TotalFiles,
                FormatBytes(progress.TransferredBytes),
                FormatBytes(progress.TotalBytes));
        }

        private void UpdateExifScanProgress(PhotoMetadataScanProgress progress)
        {
            _exifCacheHits = progress.CacheHits;
            ProgressPercent = progress.TotalFiles == 0
                ? 100
                : Math.Min(100, progress.CompletedFiles * 100.0 / progress.TotalFiles);
            ProgressText = string.Format(
                "Exifスキャン {0}/{1} 件（キャッシュ {2} 件）",
                progress.CompletedFiles,
                progress.TotalFiles,
                progress.CacheHits);
        }

        private static PreviewBuildResult BuildPreview(
            string sourceRoot,
            string destinationRoot,
            ParsedTemplate template,
            bool overwriteExisting,
            RawJpegAnalysisMode rawJpegAnalysisMode,
            bool useExifCache,
            bool readExifInformation,
            string exifCacheRoot,
            IProgress<PhotoMetadataScanProgress> exifProgress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            var allocator = new DestinationAllocator(
                template,
                new FileSystemDestinationLookup(destinationRoot),
                destinationTimestampPolicy,
                overwriteExisting);
            var scan = EnumerateSourceFiles(sourceRoot, cancellationToken);
            foreach (var issue in scan.Issues)
                result.Add(PreviewItem.ForScanError(issue.Path, issue.Message));

            var files = scan.Files.OrderBy(
                item => MakeRelative(sourceRoot, item), StringComparer.OrdinalIgnoreCase).ToList();
            cancellationToken.ThrowIfCancellationRequested();
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

            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    var sourcePath = MakeRelative(sourceRoot, path);
                    var relativeDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                    var analysisSource = analysisPlan == null ? path : analysisPlan.GetAnalysisSource(path);
                    var metadataResult = analysisPlan == null ? null : metadataBySource[analysisSource];
                    if (template.RequiresExif && metadataResult != null &&
                        metadataResult.Status == PhotoMetadataReadStatus.ReadError)
                        throw metadataResult.Error;
                    var metadata = metadataResult == null ? PhotoMetadata.Empty : metadataResult.Metadata;
                    var analysisSourceInfo = string.Equals(path, analysisSource, StringComparison.OrdinalIgnoreCase)
                        ? info
                        : new FileInfo(analysisSource);
                    var context = new FileTemplateContext(
                            info.Name,
                            info.LastWriteTime,
                            info.Length,
                            relativeDirectory,
                            metadata,
                            info.LastWriteTimeUtc,
                            analysisSourceInfo.LastWriteTime,
                            analysisSourceInfo.LastWriteTimeUtc,
                            (info.Attributes & FileAttributes.ReadOnly) != 0);
                    var allocation = allocator.Allocate(context, info.LastWriteTimeUtc);
                    var destinationPath = Path.Combine(destinationRoot, allocation.RelativePath);
                    var plan = allocation.Status == DestinationStatus.NotImported ||
                               allocation.Status == DestinationStatus.Overwrite
                        ? new CopyPlanItem(
                            info.FullName,
                            destinationRoot,
                            destinationPath,
                            new FileSnapshot(info.Length, info.LastWriteTimeUtc),
                            allocation.DestinationSnapshot,
                            destinationTimestampPolicy,
                            allocation.Status == DestinationStatus.Overwrite)
                        : null;
                    result.Add(new PreviewItem(
                        sourcePath,
                        allocation.RelativePath,
                        allocation.Status,
                        plan,
                        allocation.Warnings,
                        context,
                        metadataResult,
                        analysisPlan == null ? null : MakeRelative(sourceRoot, analysisSource),
                        allocation.SequenceNumber));
                }
                catch (UnauthorizedAccessException ex) { result.Add(PreviewItem.ForScanError(MakeRelative(sourceRoot, path), ex.Message)); }
                catch (IOException ex) { result.Add(PreviewItem.ForScanError(MakeRelative(sourceRoot, path), ex.Message)); }
                catch (TemplateException ex) { result.Add(PreviewItem.ForScanError(MakeRelative(sourceRoot, path), ex.Error.Code.ToString())); }
            }
            return new PreviewBuildResult(result, warnings);
        }

        private static SourceScanResult EnumerateSourceFiles(
            string sourceRoot,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new SourceScanResult();
            var pending = new Stack<string>();
            pending.Push(sourceRoot);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                FileSystemInfo[] entries;
                try { entries = new DirectoryInfo(directory).GetFileSystemInfos(); }
                catch (UnauthorizedAccessException ex) { result.Issues.Add(new ScanIssue(MakeRelative(sourceRoot, directory), ex.Message)); continue; }
                catch (IOException ex) { result.Issues.Add(new ScanIssue(MakeRelative(sourceRoot, directory), ex.Message)); continue; }

                foreach (var entry in entries.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var childDirectory = entry as DirectoryInfo;
                        if (childDirectory != null) pending.Push(childDirectory.FullName);
                        else if (entry is FileInfo) result.Files.Add(entry.FullName);
                    }
                    catch (UnauthorizedAccessException ex) { result.Issues.Add(new ScanIssue(MakeRelative(sourceRoot, entry.FullName), ex.Message)); }
                    catch (IOException ex) { result.Issues.Add(new ScanIssue(MakeRelative(sourceRoot, entry.FullName), ex.Message)); }
                }
            }
            return result;
        }

        private void PreviewItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PreviewItem.IsSelected))
            {
                OnPropertyChanged(nameof(CanCopy));
                UpdateSummary();
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
        }

        private void SettingsChanged()
        {
            _previewIsCurrent = false;
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanCopy));
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
                    SettingsChanged();
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
            SettingsChanged();
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
            _analyzeJpegOnlyForRawJpegPair = settings.AnalyzeJpegOnlyForRawJpegPair;
            _useExifCache = settings.UseExifCache;
            _readExifInformation = settings.ReadExifInformation;
            _customExifCacheRoot = settings.CustomExifCacheRoot;
            _previousExifCacheRoots.Clear();
            _previousExifCacheRoots.AddRange(settings.PreviousExifCacheRoots);
            _previousExifCacheRoots.RemoveAll(
                path => string.Equals(path, ExifCacheRoot, StringComparison.OrdinalIgnoreCase));
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            var settings = new PhotoImporterSettings
            {
                SourceFolder = SourceFolder,
                DestinationFolder = DestinationFolder,
                TemplateText = TemplateText,
                OverwriteExisting = OverwriteExisting,
                AnalyzeJpegOnlyForRawJpegPair = AnalyzeJpegOnlyForRawJpegPair,
                UseExifCache = UseExifCache,
                ReadExifInformation = ReadExifInformation,
                CustomExifCacheRoot = _customExifCacheRoot
            };
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
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(ProgressVisibility));
            if (!copying && !_isScanningExif) ProgressText = string.Empty;
        }

        private static void ValidateRoots(string sourceRoot, string destinationRoot)
        {
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("コピー元フォルダーが見つかりません。");
            if (!Directory.Exists(destinationRoot)) throw new DirectoryNotFoundException("コピー先フォルダーが見つかりません。");
            if (IsSameOrUnder(sourceRoot, destinationRoot) || IsSameOrUnder(destinationRoot, sourceRoot))
                throw new InvalidOperationException("コピー元とコピー先には、同一または互いの配下ではないフォルダーを指定してください。");
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
        private bool _isSelected;
        private string _copyError;

        public PreviewItem(
            string sourcePath,
            string destinationPath,
            DestinationStatus destinationStatus,
            CopyPlanItem copyPlan,
            IReadOnlyList<TemplateWarningCode> warnings = null,
            FileTemplateContext templateContext = null,
            PhotoMetadataReadResult metadataResult = null,
            string metadataSourcePath = null,
            int? sequenceNumber = null)
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
            _isSelected = copyPlan != null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public string SourcePath { get; }
        public string DestinationPath { get; }
        public DestinationStatus DestinationStatus { get; }
        public CopyPlanItem CopyPlan { get; }
        public IReadOnlyList<TemplateWarningCode> Warnings { get; }
        public FileTemplateContext TemplateContext { get; }
        public PhotoMetadataReadResult MetadataResult { get; }
        public string MetadataSourcePath { get; }
        public int? SequenceNumber { get; }
        public bool CanCopy => CopyPlan != null && !IsScanError;
        public bool IsScanError { get; private set; }
        public string ErrorMessage { get; private set; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                var next = CanCopy && value;
                if (_isSelected == next) return;
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
                string status;
                switch (DestinationStatus)
                {
                    case DestinationStatus.Imported: status = "取込済"; break;
                    case DestinationStatus.Overwrite: status = "上書き対象"; break;
                    case DestinationStatus.Conflict: status = "競合"; break;
                    default: status = "未取込"; break;
                }
                if (Warnings.Contains(TemplateWarningCode.TakenDateFallbackToModifiedDate))
                    status += "（撮影日時なし: 更新日時を使用）";
                else if (Warnings.Contains(TemplateWarningCode.TakenDateOffsetMissing))
                    status += "（Exif時差なし）";
                return status;
            }
        }

        public void SetCopyError(string error)
        {
            _copyError = error;
            _isSelected = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }

        public static PreviewItem ForScanError(string sourcePath, string message) =>
            new PreviewItem(sourcePath, string.Empty, DestinationStatus.Conflict, null)
            { IsScanError = true, ErrorMessage = message };
    }

    internal sealed class SourceScanResult
    {
        public List<string> Files { get; } = new List<string>();
        public List<ScanIssue> Issues { get; } = new List<ScanIssue>();
    }

    internal sealed class ScanIssue
    {
        public ScanIssue(string path, string message) { Path = path; Message = message; }
        public string Path { get; }
        public string Message { get; }
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
