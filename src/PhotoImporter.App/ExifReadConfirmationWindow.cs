using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PhotoImporter.App
{
    internal sealed class ExifReadConfirmationWindow : Window
    {
        internal ExifReadConfirmationWindow(int fileCount)
        {
            Title = AppLocalization.Text("画像からExif情報を読み込み", "Read Exif from images");
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
            SetResourceReference(ForegroundProperty, SystemColors.WindowTextBrushKey);
            AutomationProperties.SetAutomationId(this, "ConfirmExifFileReads");

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.Text("画像からExif情報を読み込みますか？", "Read Exif information from images?"),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.Format(
                    "キャッシュで取得できない{0}ファイルから、Exif情報を読み込む必要があります。処理に時間がかかる場合があります。",
                    "Exif information must be read from {0} files that could not be retrieved from the cache. This may take some time.",
                    fileCount),
                Margin = new Thickness(0, 12, 0, 20),
                TextWrapping = TextWrapping.Wrap
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var read = new Button
            {
                Content = AppLocalization.Text("読み込む", "Read"), IsDefault = true,
                Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0)
            };
            var cancel = new Button
            {
                Content = AppLocalization.Text("キャンセル", "Cancel"), IsCancel = true,
                Padding = new Thickness(16, 6, 16, 6)
            };
            AutomationProperties.SetAutomationId(read, "ReadExifFiles");
            AutomationProperties.SetAutomationId(cancel, "CancelExifFileReads");
            read.Click += (sender, args) => DialogResult = true;
            cancel.Click += (sender, args) => DialogResult = false;
            buttons.Children.Add(read);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;
        }
    }
}
