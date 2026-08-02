using System.Windows;

namespace PhotoImporter.App
{
    public partial class LicenseInformationWindow : Window
    {
        public LicenseInformationWindow()
        {
            InitializeComponent();
            LicenseList.ItemsSource = LicenseInformationCatalog.Items;
            LicenseList.SelectedIndex = 0;
        }
    }
}
