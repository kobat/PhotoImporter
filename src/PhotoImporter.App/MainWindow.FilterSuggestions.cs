using PhotoImporter.Core.Filtering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoImporter.App
{
    public partial class MainWindow
    {
        private FilterSuggestionCatalog _suggestionCatalog;
        private int _suggestionVersion;

        private void InvalidateSuggestions()
        {
            _suggestionVersion++;
            _suggestionCatalog = null;
        }

        private async Task<FilterSuggestionCatalog> GetSuggestionCatalogAsync()
        {
            if (_suggestionCatalog != null) return _suggestionCatalog;
            var version = _suggestionVersion;
            var snapshot = new List<FilterCandidate>(Items.Count);
            // Capture UI-owned rows in small batches before aggregating on a worker.
            for (var index = 0; index < Items.Count; index++)
            {
                snapshot.Add(Items[index].CreateFilterCandidate());
                if (index % 512 == 511)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (version != _suggestionVersion)
                        throw new InvalidOperationException(AppLocalization.Text("一覧が更新されました。候補を開き直してください。", "The list changed. Reopen candidates."));
                }
            }
            _suggestionCatalog = new FilterSuggestionCatalog(snapshot);
            return _suggestionCatalog;
        }

        private async Task<bool> ChooseSuggestionsAsync(FilterConditionEditor editor, PreviewItem source = null)
        {
            while (true)
            {
                Func<Task<FilterSuggestionCatalog>> provider = source == null
                    ? (Func<Task<FilterSuggestionCatalog>>)GetSuggestionCatalogAsync
                    : () => Task.FromResult(new FilterSuggestionCatalog(new[] { source.CreateFilterCandidate() }));
                var dialog = new FilterSuggestionWindow(editor, provider, true, source != null) { Owner = this };
                if (source != null) dialog.Title = AppLocalization.Text("選択ファイルから条件を追加: ", "Add condition from file: ") + System.IO.Path.GetFileName(source.SourcePath);
                if (dialog.ShowDialog() == true) return true;
                if (!dialog.RequestsExif) return false;
                // Keep the applied filter and unsaved edits intact while populating metadata.
                SetActiveOverlay(OverlayPanel.None);
                var filter = _appliedFilter ?? new FilterSet(new FilterCondition[0]).Prepare().Filter;
                var loaded = await LoadExifForFilterAsync(filter, _appliedFilterCount, false);
                SetActiveOverlay(OverlayPanel.Filter);
                if (!loaded) return false;
            }
        }

        private async void ShowFilterSuggestions_Click(object sender, RoutedEventArgs e)
        {
            var editor = (sender as FrameworkElement)?.DataContext as FilterConditionEditor;
            if (editor == null || !CanEditFilters) return;
            var draft = editor.Clone();
            if (await ChooseSuggestionsAsync(draft)) editor.CopyFrom(draft);
        }

        private void UseManualFilterText_Click(object sender, RoutedEventArgs e)
        {
            var editor = (sender as FrameworkElement)?.DataContext as FilterConditionEditor;
            editor?.SetSelectedValues(new string[0]);
        }

        private async void AddFilterFromFile_Click(object sender, RoutedEventArgs e)
        {
            var source = SelectedPreviewItem;
            if (source == null || !CanEditFilters) return;
            var editor = new FilterConditionEditor(FilterFieldOptions);
            editor.SelectedField = FilterFieldOptions.Single(field => field.Field == FilterField.Extension);
            if (!await ChooseSuggestionsAsync(editor, source)) return;
            editor.PropertyChanged += FilterCondition_PropertyChanged;
            FilterConditions.Add(editor);
            NotifyFilterEditorStateChanged();
            SetActiveOverlay(OverlayPanel.Filter);
        }

        private void PreviewRow_RightClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject element = e.OriginalSource as DependencyObject;
            while (element != null && !(element is DataGridRow))
                element = element is ContentElement content
                    ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                    : VisualTreeHelper.GetParent(element);
            if (element is DataGridRow row)
            {
                SelectedPreviewItem = row.Item as PreviewItem;
                row.IsSelected = true;
            }
        }

        private void FilterFromFileMenu_Opened(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)sender;
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = SelectedPreviewItem != null && CanEditFilters;
        }
    }
}
