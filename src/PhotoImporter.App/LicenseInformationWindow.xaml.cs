using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;

namespace PhotoImporter.App
{
    public partial class LicenseInformationWindow : Window
    {
        public LicenseInformationWindow()
        {
            InitializeComponent();
            var view = new ListCollectionView(new List<LicenseInformationItem>(LicenseInformationCatalog.Items));
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LicenseInformationItem.Category)));
            LicenseList.ItemsSource = view;
            LicenseList.SelectedIndex = 0;
        }
    }
}
