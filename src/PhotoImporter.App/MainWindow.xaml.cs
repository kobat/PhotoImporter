using PhotoImporter.Core.Templates;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
                if (PathsEqual(sourceRoot, destinationRoot)) throw new InvalidOperationException("コピー元とコピー先には異なるフォルダーを指定してください。");

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
                foreach (var row in rows) if (row.IsImported) imported++;
                Summary = string.Format("{0} 件（未取込 {1} / 取込済 {2}）", rows.Count, rows.Count - imported, imported);
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
            foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (IsUnder(path, destinationRoot)) continue;
                var info = new FileInfo(path);
                var allocation = allocator.Allocate(new FileTemplateContext(info.Name, info.LastWriteTime, info.Length));
                result.Add(new PreviewItem(
                    MakeRelative(sourceRoot, path),
                    allocation.RelativePath,
                    allocation.Status == DestinationStatus.Imported));
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
        private static bool PathsEqual(string first, string second) => string.Equals(TrimPath(first), TrimPath(second), StringComparison.OrdinalIgnoreCase);
        private static string TrimPath(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(TrimPath(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string MakeRelative(string root, string path) => new Uri(TrimPath(root) + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString().Replace('/', '\\');

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
        public string Status => IsImported ? "取込済" : "未取込";
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
