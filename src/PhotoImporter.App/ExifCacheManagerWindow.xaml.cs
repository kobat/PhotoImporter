using PhotoImporter.Core.Metadata;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoImporter.App
{
    public partial class ExifCacheManagerWindow : Window, INotifyPropertyChanged
    {
        private readonly ExifCacheManager _manager = new ExifCacheManager();
        private readonly List<string> _previousRoots;
        private readonly string _sourceFolder;
        private ExifCacheRootListItem _selectedRoot;
        private ExifCacheCardListItem _selectedCard;
        private bool _isBusy;
        private bool _inspectionCompleted;
        private string _statusText;
        private Brush _statusBrush = Brushes.DimGray;

        public ExifCacheManagerWindow(
            Window owner,
            string currentRoot,
            IEnumerable<string> previousRoots,
            string sourceFolder)
        {
            if (string.IsNullOrWhiteSpace(currentRoot))
                throw new ArgumentException("The current cache root is required.", nameof(currentRoot));
            Owner = owner;
            CurrentRoot = Path.GetFullPath(currentRoot);
            _previousRoots = (previousRoots ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !string.Equals(path, CurrentRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _sourceFolder = sourceFolder;
            InitializeComponent();
            WpfLocalizer.Localize(this);
            DataContext = this;
            Loaded += async (sender, args) => await RefreshAsync();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string CurrentRoot { get; }
        public ObservableCollection<ExifCacheRootListItem> Roots { get; } =
            new ObservableCollection<ExifCacheRootListItem>();

        public ExifCacheRootListItem SelectedRoot
        {
            get => _selectedRoot;
            set
            {
                if (ReferenceEquals(_selectedRoot, value)) return;
                _selectedRoot = value;
                OnPropertyChanged();
                SelectedCard = value?.Cards.FirstOrDefault();
            }
        }

        public ExifCacheCardListItem SelectedCard
        {
            get => _selectedCard;
            set
            {
                if (ReferenceEquals(_selectedCard, value)) return;
                _selectedCard = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanManageSelectedCard));
            }
        }

        public bool IsContentEnabled => !_isBusy;
        public bool CanManageSelectedCard => !_isBusy && SelectedCard != null;
        public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }
        public Brush StatusBrush { get => _statusBrush; private set { _statusBrush = value; OnPropertyChanged(); } }

        public IReadOnlyList<string> RemainingPreviousRoots => !_inspectionCompleted
            ? _previousRoots.ToList()
            : Roots.Where(root => !root.Info.IsCurrent &&
                                  (root.Info.Warning != null || root.Info.Exists && root.Info.Cards.Count != 0))
                .Select(root => root.Info.RootPath)
                .ToList();

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task<bool> RefreshAsync(uint? preferredSerial = null)
        {
            var previousRootPath = SelectedRoot?.Info.RootPath;
            var previousSerial = preferredSerial ?? SelectedCard?.Info.VolumeSerialNumber;
            SetBusy(true);
            SetStatus(AppLocalization.Text("Exif キャッシュを確認しています...", "Inspecting the Exif cache..."), Brushes.DimGray);
            try
            {
                var currentRoot = CurrentRoot;
                var previousRoots = _previousRoots.ToArray();
                var sourceFolder = _sourceFolder;
                var inspected = await Task.Run(() =>
                {
                    uint? sourceSerial = null;
                    if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
                    {
                        try { sourceSerial = new WindowsVolumeInfoReader().Read(sourceFolder).SerialNumber; }
                        catch (Exception ex) when (IsManagementFailure(ex)) { }
                    }
                    return _manager.Inspect(currentRoot, previousRoots, sourceSerial);
                });

                Roots.Clear();
                foreach (var info in inspected) Roots.Add(new ExifCacheRootListItem(info));
                _inspectionCompleted = true;
                SelectedRoot = Roots.FirstOrDefault(root => string.Equals(
                                   root.Info.RootPath,
                                   previousRootPath,
                                   StringComparison.OrdinalIgnoreCase)) ?? Roots.FirstOrDefault();
                if (previousSerial.HasValue && SelectedRoot != null)
                    SelectedCard = SelectedRoot.Cards.FirstOrDefault(
                        card => card.Info.VolumeSerialNumber == previousSerial.Value) ?? SelectedRoot.Cards.FirstOrDefault();

                SynchronizePreviousRoots();
                var cardCount = Roots.Sum(root => root.Cards.Count);
                SetStatus(AppLocalization.Format("{0} 件のキャッシュを確認しました。", "Inspected {0} cached cards.", cardCount), Brushes.DimGray);
                return true;
            }
            catch (Exception ex) when (IsManagementFailure(ex))
            {
                SetStatus(AppLocalization.Text("Exif キャッシュを確認できませんでした: ", "Unable to inspect the Exif cache: ") + ex.Message, Brushes.Firebrick);
                return false;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RenameCard_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedCard;
            if (selected == null) return;
            var name = PromptForText(
                AppLocalization.Text("カードの名前を変更", "Rename card"),
                AppLocalization.Text("目印となる名前を入力してください。空にすると名前を解除します。", "Enter a descriptive name. Leave it empty to remove the name."),
                selected.Info.DisplayName ?? string.Empty,
                AppLocalization.Text("保存", "Save"));
            if (name == null) return;
            if (name.Trim().Length > ExifCacheManager.MaximumDisplayNameLength)
            {
                MessageBox.Show(this,
                    AppLocalization.Format("名前は{0}文字以内で入力してください。", "Enter a name of no more than {0} characters.", ExifCacheManager.MaximumDisplayNameLength),
                    AppLocalization.Text("カードの名前を変更", "Rename card"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RunOperationAsync(
                () => _manager.RenameCard(selected.Info.CacheRoot, selected.Info.VolumeSerialNumber, name),
                AppLocalization.Text("カードの名前を変更しました。", "The card was renamed."),
                selected.Info.VolumeSerialNumber);
        }

        private async void RemoveOldEntries_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedCard;
            if (selected == null) return;
            var text = PromptForText(
                AppLocalization.Text("古いエントリを整理", "Remove old entries"),
                AppLocalization.Text("最後に使われてから何日より古いエントリを削除しますか？ 日付はUTC基準です。", "Remove entries older than how many days since last use? Dates are based on UTC."),
                "30",
                AppLocalization.Text("確認へ", "Continue"));
            int days;
            if (text == null) return;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.CurrentCulture, out days) || days < 1 || days > 36500)
            {
                MessageBox.Show(this, AppLocalization.Text("日数は1～36500の整数で入力してください。", "Enter an integer from 1 to 36500."),
                    AppLocalization.Text("古いエントリを整理", "Remove old entries"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var cutoff = DateTime.UtcNow.Date.AddDays(-days);
            var confirmation = MessageBox.Show(this,
                AppLocalization.Format(
                    "「{0}」から、最後に使われた日が {1:yyyy-MM-dd} より前のエントリを削除します。\n\nExif キャッシュを削除しても写真ファイルは削除されません。次回のスキャンで再解析されるため、処理が遅くなる場合があります。",
                    "Remove entries from \"{0}\" whose last-used date is before {1:yyyy-MM-dd}.\n\nDeleting the Exif cache does not delete photo files. The next scan may take longer because the files must be analyzed again.",
                    selected.Name,
                    cutoff),
                AppLocalization.Text("古いエントリを整理", "Remove old entries"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK) return;

            var removed = 0;
            var succeeded = await RunOperationAsync(
                () => removed = _manager.RemoveEntriesLastUsedBefore(
                    selected.Info.CacheRoot,
                    selected.Info.VolumeSerialNumber,
                    cutoff),
                null,
                selected.Info.VolumeSerialNumber);
            if (succeeded)
                SetStatus(AppLocalization.Format("{0} 件の古いエントリを削除しました。", "Removed {0} old entries.", removed), Brushes.DimGray);
        }

        private async void DeleteCard_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedCard;
            if (selected == null) return;
            var confirmation = MessageBox.Show(this,
                AppLocalization.Format(
                    "「{0}」の Exif キャッシュをすべて削除します。\n\nボリューム: {1}\nキャッシュ: {2} / {3}\n\n写真ファイルは削除されません。次回のスキャンでExif情報を再解析するため、処理が遅くなる場合があります。",
                    "Delete the entire Exif cache for \"{0}\".\n\nVolume: {1}\nCache: {2} / {3}\n\nPhoto files will not be deleted. The next scan may take longer because Exif data must be analyzed again.",
                    selected.Name,
                    selected.Info.VolumeSerialNumberHex,
                    selected.CacheSizeText,
                    selected.EntryCountText),
                AppLocalization.Text("カードのキャッシュを削除", "Delete card cache"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK) return;

            await RunOperationAsync(
                () => _manager.DeleteCard(selected.Info.CacheRoot, selected.Info.VolumeSerialNumber),
                AppLocalization.Text("カードの Exif キャッシュを削除しました。", "The card's Exif cache was deleted."),
                null);
        }

        private async Task<bool> RunOperationAsync(Action operation, string successMessage, uint? preferredSerial)
        {
            SetBusy(true);
            SetStatus(AppLocalization.Text("処理しています...", "Processing..."), Brushes.DimGray);
            try
            {
                await Task.Run(operation);
                if (!await RefreshAsync(preferredSerial)) return false;
                if (!string.IsNullOrEmpty(successMessage)) SetStatus(successMessage, Brushes.DimGray);
                return true;
            }
            catch (Exception ex) when (IsManagementFailure(ex))
            {
                SetStatus(AppLocalization.Text("操作を完了できませんでした: ", "Unable to complete the operation: ") + ex.Message, Brushes.Firebrick);
                MessageBox.Show(this, AppLocalization.UserMessage(ex.Message), AppLocalization.Text("Exif キャッシュの管理", "Manage Exif Cache"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SynchronizePreviousRoots()
        {
            var remaining = RemainingPreviousRoots;
            _previousRoots.Clear();
            _previousRoots.AddRange(remaining);
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            OnPropertyChanged(nameof(IsContentEnabled));
            OnPropertyChanged(nameof(CanManageSelectedCard));
        }

        private void SetStatus(string text, Brush brush)
        {
            StatusText = text;
            StatusBrush = brush;
        }

        private string PromptForText(string title, string prompt, string initialValue, string acceptText)
        {
            string result = null;
            var dialog = new Window
            {
                Owner = this,
                Title = title,
                Width = 500,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var textBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Padding = new Thickness(4, 2, 4, 2)
            };
            AutomationProperties.SetAutomationId(textBox, "ExifCacheManagerInput");
            panel.Children.Add(textBox);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var accept = new Button
            {
                Content = acceptText,
                MinWidth = 90,
                Padding = new Thickness(10, 4, 10, 4),
                IsDefault = true
            };
            AutomationProperties.SetAutomationId(accept, "ExifCacheManagerAcceptInput");
            accept.Click += (sender, args) =>
            {
                result = textBox.Text;
                dialog.DialogResult = true;
            };
            var cancel = new Button
            {
                Content = AppLocalization.Text("キャンセル", "Cancel"),
                MinWidth = 90,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };
            AutomationProperties.SetAutomationId(cancel, "ExifCacheManagerCancelInput");
            buttons.Children.Add(accept);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            dialog.Loaded += (sender, args) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };
            dialog.ShowDialog();
            return result;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private static bool IsManagementFailure(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException ||
            ex is ArgumentException || ex is System.ComponentModel.Win32Exception;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class ExifCacheRootListItem
    {
        public ExifCacheRootListItem(ExifCacheRootInfo info)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            Cards = info.Cards.Select(card => new ExifCacheCardListItem(card)).ToList();
        }

        public ExifCacheRootInfo Info { get; }
        public IReadOnlyList<ExifCacheCardListItem> Cards { get; }
        public string Title => Info.IsCurrent
            ? AppLocalization.Text("現在の保存先", "Current location")
            : AppLocalization.Text("以前の保存先", "Previous location");
        public string Path => Info.RootPath;
        public string Summary => Info.Exists
            ? AppLocalization.Format("{0} 件 / {1}", "{0} entries / {1}", Cards.Count, FormatSize(Info.CacheSizeBytes))
            : AppLocalization.Text("フォルダーは存在しません", "Folder does not exist");
        public string Warning => Info.Warning;
        public bool HasCards => Cards.Count != 0;

        internal static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";
            var value = (double)bytes;
            var units = new[] { "KB", "MB", "GB", "TB" };
            var index = -1;
            do
            {
                value /= 1024;
                index++;
            } while (value >= 1024 && index < units.Length - 1);
            return value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[index];
        }
    }

    public sealed class ExifCacheCardListItem
    {
        public ExifCacheCardListItem(ExifCacheCardInfo info)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
        }

        public ExifCacheCardInfo Info { get; }
        public string StateText => Info.IsCurrentSource ? AppLocalization.Text("現在のコピー元", "Current source") : string.Empty;
        public string Name => !string.IsNullOrWhiteSpace(Info.DisplayName)
            ? Info.DisplayName
            : !string.IsNullOrWhiteSpace(Info.VolumeLabel) ? Info.VolumeLabel : AppLocalization.Text("（名前なし）", "(Unnamed)");
        public string VolumeText => string.Format(CultureInfo.CurrentCulture, "{0} / {1}",
            string.IsNullOrWhiteSpace(Info.VolumeLabel) ? AppLocalization.Text("ラベルなし", "No label") : Info.VolumeLabel,
            Info.VolumeSerialNumberHex);
        public string DriveText
        {
            get
            {
                if (!Info.DriveType.HasValue) return AppLocalization.Text("不明", "Unknown");
                switch (Info.DriveType.Value)
                {
                    case DriveType.Removable: return AppLocalization.Text("リムーバブル", "Removable");
                    case DriveType.Fixed: return AppLocalization.Text("固定", "Fixed");
                    case DriveType.Network: return AppLocalization.Text("ネットワーク", "Network");
                    case DriveType.CDRom: return AppLocalization.Text("光学ドライブ", "Optical drive");
                    case DriveType.Ram: return "RAM";
                    default: return AppLocalization.Text("不明", "Unknown");
                }
            }
        }
        public string TotalSizeText => Info.TotalBytes.HasValue
            ? ExifCacheRootListItem.FormatSize(checked((long)Math.Min(Info.TotalBytes.Value, (ulong)long.MaxValue)))
            : AppLocalization.Text("不明", "Unknown");
        public string CacheSizeText => ExifCacheRootListItem.FormatSize(Info.CacheSizeBytes);
        public string EntryCountText => AppLocalization.Format("{0:N0} 件", "{0:N0} entries", Info.EntryCount);
        public string LastUsedText => Info.LastUsedUtcDate.HasValue
            ? Info.LastUsedUtcDate.Value.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)
            : AppLocalization.Text("未記録", "Not recorded");
        public string Warning => Info.Warning;
    }
}
