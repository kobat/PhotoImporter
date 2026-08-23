using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PhotoImporter.App
{
    internal enum PresetApplyChoice
    {
        Cancel,
        Apply,
        SaveThenApply
    }

    internal enum PresetImportChoice
    {
        Skip,
        Overwrite,
        AddWithAnotherName
    }

    internal sealed class PresetNameResult
    {
        public string Name { get; set; }
        public bool SaveSourceFolder { get; set; }
    }

    internal static class PresetDialogs
    {
        public static PresetApplyChoice ConfirmApply(Window owner, string presetName)
        {
            var choice = PresetApplyChoice.Cancel;
            var window = CreateDialog(owner, AppLocalization.Text("プリセットを適用", "Apply preset"), 520);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.IsEnglish
                    ? "The current settings have unsaved changes.\nApply \"" + presetName + "\"?"
                    : "現在の設定には未保存の変更があります。\n「" + presetName + "」を適用しますか？",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(CreateButton(AppLocalization.Text("適用", "Apply"), true, () => { choice = PresetApplyChoice.Apply; window.DialogResult = true; }));
            buttons.Children.Add(CreateButton(AppLocalization.Text("保存してから適用", "Save and apply"), false, () => { choice = PresetApplyChoice.SaveThenApply; window.DialogResult = true; }));
            buttons.Children.Add(CreateButton(AppLocalization.Text("キャンセル", "Cancel"), false, () => window.DialogResult = false));
            panel.Children.Add(buttons);
            window.Content = panel;
            window.ShowDialog();
            return choice;
        }

        public static PresetNameResult PromptForName(
            Window owner,
            string title,
            string initialName,
            bool initialSaveSourceFolder,
            bool showSourceOption)
        {
            PresetNameResult result = null;
            var window = CreateDialog(owner, title, 460);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = AppLocalization.Text("プリセット名", "Preset name"), Margin = new Thickness(0, 0, 0, 5) });
            var nameBox = new TextBox { Text = initialName ?? string.Empty, MinWidth = 360, Padding = new Thickness(4, 2, 4, 2) };
            AutomationProperties.SetAutomationId(nameBox, "PresetName");
            panel.Children.Add(nameBox);
            var sourceCheck = new CheckBox
            {
                Content = AppLocalization.Text("コピー元フォルダーも保存する", "Save the source folder too"),
                IsChecked = initialSaveSourceFolder,
                Margin = new Thickness(0, 12, 0, 0),
                Visibility = showSourceOption ? Visibility.Visible : Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(sourceCheck, "SavePresetSourceFolder");
            panel.Children.Add(sourceCheck);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            buttons.Children.Add(CreateButton(AppLocalization.Text("保存", "Save"), true, () =>
            {
                result = new PresetNameResult
                {
                    Name = nameBox.Text,
                    SaveSourceFolder = sourceCheck.IsChecked == true
                };
                window.DialogResult = true;
            }));
            buttons.Children.Add(CreateButton(AppLocalization.Text("キャンセル", "Cancel"), false, () => window.DialogResult = false));
            panel.Children.Add(buttons);
            window.Content = panel;
            window.Loaded += (sender, args) =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            };
            window.ShowDialog();
            return result;
        }

        public static PresetImportChoice ConfirmImportConflict(Window owner, string importedName, string existingName)
        {
            var choice = PresetImportChoice.Skip;
            var window = CreateDialog(owner, AppLocalization.Text("プリセットをインポート", "Import preset"), 540);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.IsEnglish
                    ? "The imported preset \"" + importedName + "\" conflicts with the existing preset \"" + existingName + "\". Choose an action."
                    : "インポートする「" + importedName + "」は既存の「" + existingName +
                      "」と重複します。処理を選んでください。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(CreateButton(AppLocalization.Text("上書き", "Overwrite"), true, () => { choice = PresetImportChoice.Overwrite; window.DialogResult = true; }));
            buttons.Children.Add(CreateButton(AppLocalization.Text("別名で追加", "Add with another name"), false, () => { choice = PresetImportChoice.AddWithAnotherName; window.DialogResult = true; }));
            buttons.Children.Add(CreateButton(AppLocalization.Text("取り込まない", "Skip"), false, () => window.DialogResult = false));
            panel.Children.Add(buttons);
            window.Content = panel;
            window.ShowDialog();
            return choice;
        }

        public static bool ConfirmExitWithoutSaving(Window owner, string error)
        {
            var exit = false;
            var window = CreateDialog(owner, AppLocalization.Text("設定を保存できません", "Unable to save settings"), 520);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.UserMessage(error) + AppLocalization.Text(
                    "\n\n現在の状態を settings.xml へ保存せず終了しますか？",
                    "\n\nExit without saving the current state to settings.xml?"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(CreateButton(AppLocalization.Text("保存せず終了", "Exit without saving"), false, () => { exit = true; window.DialogResult = true; }));
            buttons.Children.Add(CreateButton(AppLocalization.Text("修正する", "Go back"), true, () => window.DialogResult = false));
            panel.Children.Add(buttons);
            window.Content = panel;
            window.ShowDialog();
            return exit;
        }

        private static Window CreateDialog(Window owner, string title, double width) => new Window
        {
            Owner = owner,
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        private static Button CreateButton(string text, bool isDefault, System.Action action)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 90,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = text == AppLocalization.Text("キャンセル", "Cancel")
            };
            button.Click += (sender, args) => action();
            AutomationProperties.SetAutomationId(button,
                "PresetDialog" + text.Replace("...", string.Empty).Replace(" ", string.Empty));
            return button;
        }
    }
}
