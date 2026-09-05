using PhotoImporter.App;
using PhotoImporter.Core.Filtering;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    [Collection(JapaneseLocalizationCollection.Name)]
    public sealed class FilterSuggestionWindowTests
    {
        [Theory]
        [InlineData("strings")]
        [InlineData("dates")]
        [InlineData("dates-latest")]
        [InlineData("dates-period")]
        [InlineData("numbers")]
        [InlineData("unread")]
        [InlineData("from-file")]
        public void PickerLoadsSearchesAndCommitsWithoutFilesystemReads(string scenario)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                FilterSuggestionWindow window = null;
                try
                {
                    var fields = FilterFieldOption.CreateAll();
                    var field = scenario.StartsWith("dates", StringComparison.Ordinal) ? FilterField.ModifiedDate : scenario == "numbers" ? FilterField.FileSize :
                        scenario == "unread" ? FilterField.CameraModel : FilterField.Extension;
                    var editor = new FilterConditionEditor(fields) { SelectedField = fields.Single(item => item.Field == field) };
                    var catalog = new FilterSuggestionCatalog(new[]
                    {
                        new FilterCandidate("a.jpg", new DateTime(2026, 9, 5, 12, 0, 0), 100, "", false, null, FilterCopyStatus.NotImported),
                        new FilterCandidate("b.nef", new DateTime(2026, 9, 6, 13, 0, 0), 200, "", false, null, FilterCopyStatus.NotImported)
                    });
                    window = new FilterSuggestionWindow(editor, () => Task.FromResult(catalog), true, scenario == "from-file")
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, ShowActivated = false
                    };
                    var current = window;
                    window.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            var status = Control<TextBlock>(current, "StatusText");
                            await Until(() => status.Text.Contains("候補 /"));
                            var list = Control<ListBox>(current, "ValuesList");
                            await Until(() => scenario == "unread" || list.Items.Count == 2);
                            Assert.Equal(scenario == "from-file", Control<ComboBox>(current, "FieldBox").IsEnabled);
                            Capture(current, scenario);
                            if (scenario == "unread")
                            {
                                Assert.Contains("Exif未読 2件", status.Text);
                                Assert.Empty(list.Items.Cast<object>());
                                Assert.False(Control<Button>(current, "UseButton").IsEnabled);
                                Click(current, "LoadExifButton");
                                return;
                            }
                            if (scenario == "dates")
                            {
                                list.SelectedIndex = 0;
                                Control<CheckBox>(current, "ExactTimeBox").IsChecked = true;
                                await Until(() => list.Items.Count == 1 && list.Items[0].GetType().GetProperty("Label").GetValue(list.Items[0]).ToString().Contains("13:00"));
                                list.SelectedIndex = 0;
                                Capture(current, "exact-time");
                                Click(current, "UseButton");
                                return;
                            }
                            if (scenario == "dates-latest" || scenario == "dates-period")
                            {
                                Control<TextBox>(current, "SearchBox").Text = "2026/09/05";
                                await Until(() => list.Items.Count == 1);
                                Click(current, scenario == "dates-latest" ? "LatestButton" : "PeriodButton");
                                return;
                            }
                            if (scenario == "numbers")
                            {
                                list.SelectedIndex = 1;
                                Control<ComboBox>(current, "BoundaryBox").SelectedIndex = 1;
                                Click(current, "UseButton");
                                return;
                            }
                            var first = list.Items[0];
                            first.GetType().GetProperty("IsSelected").SetValue(first, true);
                            Control<TextBox>(current, "SearchBox").Text = "nef";
                            await Until(() => list.Items.Count == 1);
                            Assert.True(Control<Button>(current, "UseButton").IsEnabled);
                            list.Items[0].GetType().GetProperty("IsSelected").SetValue(list.Items[0], true);
                            Click(current, "UseButton");
                        }
                        catch (Exception ex) { failure = ex; current.Close(); }
                    }), DispatcherPriority.ApplicationIdle);
                    var result = window.ShowDialog();
                    if (failure != null) return;
                    if (scenario == "unread") { Assert.True(window.RequestsExif); Assert.False(result); }
                    else
                    {
                        Assert.True(result);
                        Assert.True(editor.IsValid, editor.ValidationMessage);
                        if (scenario == "dates") Assert.Equal("13:00:00", editor.StartTimeText);
                        else if (scenario.StartsWith("dates", StringComparison.Ordinal))
                        {
                            Assert.Equal(new DateTime(2026, 9, scenario == "dates-latest" ? 6 : 5), editor.StartDate);
                            Assert.Equal(new DateTime(2026, 9, 6), editor.EndDate);
                            Assert.Empty(editor.EndTimeText);
                        }
                        else if (scenario == "numbers") { Assert.Equal("200", editor.MinimumText); Assert.Empty(editor.MaximumText); }
                        else Assert.Equal(2, editor.SelectedValues.Count);
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Picker test timed out.");
            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        private static T Control<T>(Window window, string name) where T : class => (T)window.FindName(name);
        private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        private static async Task Until(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!predicate())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Picker did not reach the expected state.");
                await Task.Delay(20);
            }
        }
        private static void Capture(Window window, string name)
        {
            var directory = Environment.GetEnvironmentVariable("PHOTOIMPORTER_UI_TEST_OUTPUT");
            if (string.IsNullOrEmpty(directory)) return;
            var content = (FrameworkElement)window.Content;
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                drawing.DrawRectangle(window.Background, null, bounds);
                drawing.DrawRectangle(new VisualBrush(content), null, bounds);
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(directory);
            using (var stream = File.Create(Path.Combine(directory, "filter-" + name + ".png"))) encoder.Save(stream);
        }
    }
}
