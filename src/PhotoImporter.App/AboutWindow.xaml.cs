using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PhotoImporter.App
{
    public partial class AboutWindow : Window
    {
        private const string RepositoryUrl = "https://github.com/kobat/PhotoImporter";
        private readonly Action<string> _languageChanged;
        private bool _languageSelectionReady;

        public AboutWindow()
            : this(AppLocalization.Preference, null)
        {
        }

        internal AboutWindow(string language, Action<string> languageChanged)
        {
            InitializeComponent();
            WpfLocalizer.Localize(this);
            _languageChanged = languageChanged;
            VersionText = AppLocalization.Text("バージョン ", "Version ") + GetProductVersion();
            DataContext = this;
            LanguageOptions = new[]
            {
                new LanguageOption(
                    AppLocalization.Automatic,
                    AppLocalization.Text("自動（Windows の表示言語）", "Automatic (Windows)")),
                new LanguageOption(AppLocalization.Japanese, "日本語"),
                new LanguageOption(AppLocalization.English, "English")
            };
            LanguageSelector.ItemsSource = LanguageOptions;
            LanguageSelector.SelectedItem = LanguageOptions.First(
                option => option.Code == AppLocalization.NormalizePreference(language));
            _languageSelectionReady = true;
        }

        public string VersionText { get; }
        internal IReadOnlyList<LanguageOption> LanguageOptions { get; }

        private static string GetProductVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var attribute = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                assembly,
                typeof(AssemblyInformationalVersionAttribute));
            return string.IsNullOrWhiteSpace(attribute?.InformationalVersion)
                ? assembly.GetName().Version.ToString(3)
                : attribute.InformationalVersion;
        }

        private void OpenRepository_Click(object sender, RoutedEventArgs e) => OpenUrl(RepositoryUrl);

        private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_languageSelectionReady || !(LanguageSelector.SelectedItem is LanguageOption option)) return;
            _languageChanged?.Invoke(option.Code);
            MessageBox.Show(
                this,
                AppLocalization.Text(
                    "表示言語の変更は、次回の起動時に反映されます。",
                    "The display language change will take effect the next time you start the application."),
                "Photo Importer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OpenLicenseInformation_Click(object sender, RoutedEventArgs e)
        {
            new LicenseInformationWindow { Owner = this }.ShowDialog();
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (
                ex is Win32Exception ||
                ex is InvalidOperationException ||
                ex is NotSupportedException)
            {
                MessageBox.Show(
                    this,
                    AppLocalization.Text(
                        "ブラウザーでページを開けませんでした。\n\n",
                        "The page could not be opened in your browser.\n\n") +
                    AppLocalization.UserMessage(ex.Message),
                    "Photo Importer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
