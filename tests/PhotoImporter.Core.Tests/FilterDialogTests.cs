using PhotoImporter.App;
using PhotoImporter.Core.Filtering;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    [Collection(JapaneseLocalizationCollection.Name)]
    public sealed class FilterDialogTests
    {
        [Fact]
        public void GroupsMatchTokenDetailCategoriesAndPickersKeepIndependentSelections()
        {
            RunSta(() =>
            {
                var fields = FilterFieldOption.CreateAll();
                var first = new FilterConditionEditor(fields);
                var second = new FilterConditionEditor(fields);
                var groups = first.GroupedFieldOptions.Groups.Cast<CollectionViewGroup>().ToArray();
                Assert.Equal(new[] { "ファイルシステム系", "Exif系" }, groups.Select(group => group.Name));
                var fileSystemFields = groups[0].Items.Cast<FilterFieldOption>().ToArray();
                var exifFields = groups[1].Items.Cast<FilterFieldOption>().ToArray();
                Assert.All(fileSystemFields, field => Assert.Equal(FilterFieldCategory.FileSystem, field.Category));
                Assert.All(fileSystemFields, field => Assert.False(FilterFieldDefinition.Get(field.Field).RequiresExif));
                Assert.All(exifFields, field => Assert.Equal(FilterFieldCategory.Exif, field.Category));
                Assert.All(exifFields, field => Assert.True(FilterFieldDefinition.Get(field.Field).RequiresExif));
                Assert.Equal(
                    new[] { FilterField.FileType, FilterField.Extension, FilterField.CopyStatus },
                    fileSystemFields.Take(3).Select(field => field.Field));
                Assert.Equal(FilterField.ExifReadStatus, exifFields[0].Field);
                var catalog = new FilterSuggestionCatalog(new FilterCandidate[0]);
                var left = new FilterSuggestionWindow(first, () => Task.FromResult(catalog), true, true);
                var right = new FilterSuggestionWindow(second, () => Task.FromResult(catalog), true, true);
                try
                {
                    var leftBox = (ComboBox)left.FindName("FieldBox");
                    var rightBox = (ComboBox)right.FindName("FieldBox");
                    Assert.Single(leftBox.GroupStyle);
                    Assert.NotNull(leftBox.GroupStyle[0].HeaderTemplate);
                    Assert.False(leftBox.IsSynchronizedWithCurrentItem);
                    leftBox.SelectedItem = fields.Single(field => field.Field == FilterField.CameraMake);
                    Assert.Equal(FilterField.FileType, ((FilterFieldOption)rightBox.SelectedItem).Field);
                    Assert.Equal(FilterField.CameraMake, ((FilterFieldOption)leftBox.SelectedItem).Field);
                }
                finally { left.Close(); right.Close(); }
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ExifConfirmationReturnsTheChosenAction(bool accept)
        {
            RunSta(() =>
            {
                var dialog = new ExifReadConfirmationWindow(120)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000, Top = -10000, ShowActivated = false
                };
                Exception failure = null;
                dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var panel = (StackPanel)dialog.Content;
                        Assert.Contains("120", panel.Children.OfType<TextBlock>().Last().Text);
                        var buttons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                        Assert.True(buttons[0].IsDefault);
                        Assert.True(buttons[1].IsCancel);
                        Assert.Equal("ConfirmExifFileReads", AutomationProperties.GetAutomationId(dialog));
                        buttons[accept ? 0 : 1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                }), DispatcherPriority.ApplicationIdle);
                try { Assert.Equal(accept, dialog.ShowDialog()); }
                finally { dialog.Close(); }
                if (failure != null) throw failure;
            });
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Dialog test timed out.");
            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
