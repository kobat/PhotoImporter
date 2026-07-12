using PhotoImporter.Core.Templates;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
        private Brush _messageBrush = Brushes.DimGray;
        private bool _isScanning;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<PreviewItem> Items { get; } = new ObservableCollection<PreviewItem>();

        public string SourceFolder
        {
            get => _sourceFolder;
            set { if (Set(ref _sourceFolder, value)) OnPropertyChanged(nameof(CanScan)); }
        }

        public string DestinationFolder
        {
            get => _destinationFolder;
            set { if (Set(ref _destinationFolder, value)) OnPropertyChanged(nameof(CanScan)); }
        }

        public string TemplateText
        {
            get => _templateText;
            set { if (Set(ref _templateText, value)) OnPropertyChanged(nameof(CanScan)); }
        }

        public string Message { get => _message; private set => Set(ref _message, value); }
        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public Brush MessageBrush { get => _messageBrush; private set => Set(ref _messageBrush, value); }
        public bool CanScan => !_isScanning && !string.IsNullOrWhiteSpace(SourceFolder) &&
                               !string.IsNullOrWhiteSpace(DestinationFolder) &&
                               !string.IsNullOrWhiteSpace(TemplateText);

        private void SelectSource_Click(object sender, RoutedEventArgs e) =>
            SourceFolder = SelectFolder(SourceFolder, "コピー元フォルダーを選択してください") ?? SourceFolder;

        private void SelectDestination_Click(object sender, RoutedEventArgs e) =>
            DestinationFolder = SelectFolder(DestinationFolder, "コピー先フォルダーを選択してください") ?? DestinationFolder;

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            _isScanning = true;
            OnPropertyChanged(nameof(CanScan));
            Items.Clear();
            Summary = "スキャン中...";
            SetMessage("ファイルを調べています...", Brushes.DimGray);

            try
            {
                var sourceRoot = Path.GetFullPath(SourceFolder);
                var destinationRoot = Path.GetFullPath(DestinationFolder);
                if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("コピー元フォルダーが見つかりません。");
                if (!Directory.Exists(destinationRoot)) throw new DirectoryNotFoundException("コピー先フォルダーが見つかりません。");
                if (IsSameOrUnder(sourceRoot, destinationRoot) || IsSameOrUnder(destinationRoot, sourceRoot))
                    throw new InvalidOperationException("コピー元とコピー先には、同一または互いの配下ではないフォルダーを指定してください。");

                var parseResult = TemplateParser.Parse(TemplateText);
                if (!parseResult.IsValid)
                {
                    ShowTemplateError(parseResult.Error);
                    return;
                }
                if (parseResult.Template.RequiresExif)
                    throw new NotSupportedException("Exifトークンは、次の開発段階で対応します。現在はファイル名・更新日時・サイズのトークンを使用してください。");

                var rows = await Task.Run(() => BuildPreview(sourceRoot, destinationRoot, parseResult.Template));
                foreach (var row in rows) Items.Add(row);

                var imported = 0;
                var scanErrors = 0;
                foreach (var row in rows) if (row.IsImported) imported++;
                foreach (var row in rows) if (row.IsScanError) scanErrors++;
                Summary = string.Format("{0} 件（未取込 {1} / 取込済 {2} / エラー {3}）",
                    rows.Count - scanErrors, rows.Count - imported - scanErrors, imported, scanErrors);
                SetMessage(rows.Count == 0 ? "コピー元にファイルがありません。" : "プレビューを更新しました。", Brushes.DimGray);
            }
            catch (TemplateException ex) { ShowTemplateError(ex.Error); }
            catch (UnauthorizedAccessException) { SetMessage("アクセスできないフォルダーがあります。権限を確認してください。", Brushes.Firebrick); }
            catch (Exception ex) { SetMessage(ex.Message, Brushes.Firebrick); }
            finally
            {
                _isScanning = false;
                OnPropertyChanged(nameof(CanScan));
                if (Summary == "スキャン中...") Summary = "0 件";
            }
        }

        private static List<PreviewItem> BuildPreview(string sourceRoot, string destinationRoot, ParsedTemplate template)
        {
            var result = new List<PreviewItem>();
            var allocator = new DestinationAllocator(template, new FileSystemDestinationLookup(destinationRoot));
            var scan = EnumerateSourceFiles(sourceRoot);
            foreach (var issue in scan.Issues)
                result.Add(PreviewItem.ForScanError(issue.Path, issue.Message));

            foreach (var path in scan.Files.OrderBy(
                item => MakeRelative(sourceRoot, item), StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var info = new FileInfo(path);
                    var sourcePath = MakeRelative(sourceRoot, path);
                    var relativeDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                    var allocation = allocator.Allocate(new FileTemplateContext(
                        info.Name, info.LastWriteTime, info.Length, relativeDirectory));
                    result.Add(new PreviewItem(
                        sourcePath,
                        allocation.RelativePath,
                        allocation.Status == DestinationStatus.Imported));
                }
                catch (UnauthorizedAccessException ex) { result.Add(PreviewItem.ForScanError(MakeRelative(sourceRoot, path), ex.Message)); }
                catch (IOException ex) { result.Add(PreviewItem.ForScanError(MakeRelative(sourceRoot, path), ex.Message)); }
            }
            return result;
        }

        private static SourceScanResult EnumerateSourceFiles(string sourceRoot)
        {
            var result = new SourceScanResult();
            var pending = new Stack<string>();
            pending.Push(sourceRoot);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                FileSystemInfo[] entries;
                try
                {
                    entries = new DirectoryInfo(directory).GetFileSystemInfos();
                }
                catch (UnauthorizedAccessException ex) { result.Issues.Add(new ScanIssue(MakeRelative(sourceRoot, directory), ex.Message)); continue; }
                catch (IOException ex) { result.Issues.Add(new ScanIssue(MakeRelative(sourceRoot, directory), ex.Message)); continue; }

                foreach (var entry in entries.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
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

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class PreviewItem
    {
        public PreviewItem(string sourcePath, string destinationPath, bool isImported)
        {
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
            IsImported = isImported;
        }
        public string SourcePath { get; }
        public string DestinationPath { get; }
        public bool IsImported { get; }
        public bool IsScanError { get; private set; }
        public string ErrorMessage { get; private set; }
        public string Status => IsScanError ? "スキャンエラー: " + ErrorMessage : IsImported ? "取込済" : "未取込";

        public static PreviewItem ForScanError(string sourcePath, string message) =>
            new PreviewItem(sourcePath, string.Empty, false) { IsScanError = true, ErrorMessage = message };
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

    internal sealed class FileSystemDestinationLookup : IDestinationFileLookup
    {
        private readonly string _root;
        public FileSystemDestinationLookup(string root) { _root = root; }
        public bool TryGetFileSize(string relativePath, out long fileSize)
        {
            var path = Path.Combine(_root, relativePath);
            if (!File.Exists(path)) { fileSize = 0; return false; }
            fileSize = new FileInfo(path).Length;
            return true;
        }
    }
}
