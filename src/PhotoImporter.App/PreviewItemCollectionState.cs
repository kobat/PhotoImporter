using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;

namespace PhotoImporter.App
{
    internal sealed class PreviewItemCollectionState
    {
        private Predicate<PreviewItem> _appliedFilter;

        public PreviewItemCollectionState(ObservableCollection<PreviewItem> items)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            View = CollectionViewSource.GetDefaultView(items);
        }

        public ObservableCollection<PreviewItem> Items { get; }
        public System.ComponentModel.ICollectionView View { get; }
        public bool UncheckHiddenItems { get; private set; } = true;
        public bool HasActiveFilter => _appliedFilter != null;

        public IEnumerable<PreviewItem> VisibleItems => View.Cast<PreviewItem>();
        public IEnumerable<PreviewItem> CopyTargets =>
            Items.Where(item => item.CanCopy && item.IsSelected);

        public void ApplyFilter(Predicate<PreviewItem> filter)
        {
            _appliedFilter = filter;
            View.Filter = filter == null
                ? (Predicate<object>)null
                : item => filter((PreviewItem)item);
            Refresh();
        }

        public void Refresh()
        {
            View.Refresh();
            if (UncheckHiddenItems) UncheckHiddenCopyableItems();
        }

        public void SetUncheckHiddenItems(bool value)
        {
            if (UncheckHiddenItems == value) return;
            UncheckHiddenItems = value;
            if (value && HasActiveFilter) UncheckHiddenCopyableItems();
        }

        public void UncheckHiddenCopyableItems()
        {
            var visible = new HashSet<PreviewItem>(VisibleItems);
            foreach (var item in Items.Where(item => item.CanCopy && !visible.Contains(item)))
                item.IsSelected = false;
        }

        public bool? GetVisibleSelectAllState() =>
            PreviewSelectionState.GetSelectAllState(VisibleItems);

        public void SetAllVisibleCopyable(bool isSelected) =>
            PreviewSelectionState.SetAllCopyable(VisibleItems, isSelected);

        public PreviewItemCounts GetCounts()
        {
            var visible = new HashSet<PreviewItem>(VisibleItems);
            var checkedItems = CopyTargets.ToList();
            return new PreviewItemCounts(
                visible.Count,
                Items.Count,
                checkedItems.Count,
                checkedItems.Count(item => !visible.Contains(item)));
        }
    }

    internal sealed class PreviewItemCounts
    {
        public PreviewItemCounts(int visible, int total, int selected, int hiddenSelected)
        {
            Visible = visible;
            Total = total;
            Selected = selected;
            HiddenSelected = hiddenSelected;
        }

        public int Visible { get; }
        public int Total { get; }
        public int Selected { get; }
        public int HiddenSelected { get; }
    }
}
