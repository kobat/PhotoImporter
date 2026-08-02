using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace PhotoImporter.App
{
    public partial class AboutWindow : Window
    {
        private const string RepositoryUrl = "https://github.com/kobat/PhotoImporter";
        private const string ReleasesUrl = RepositoryUrl + "/releases";
        private const string IssuesUrl = RepositoryUrl + "/issues";

        public AboutWindow()
        {
            InitializeComponent();
            VersionText = "バージョン " + GetProductVersion();
            DataContext = this;
        }

        public string VersionText { get; }

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

        private void OpenSource_Click(object sender, RoutedEventArgs e) => OpenUrl(RepositoryUrl);
        private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(ReleasesUrl);
        private void OpenIssues_Click(object sender, RoutedEventArgs e) => OpenUrl(IssuesUrl);

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
                    "ブラウザーでページを開けませんでした。\n\n" + ex.Message,
                    "Photo Importer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
