using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PhotoImporter.App
{
    internal static class WpfLocalizer
    {
        public static void Localize(Window window)
        {
            if (window == null || !AppLocalization.IsEnglish) return;
            TranslateObject(window, new HashSet<object>(ReferenceEqualityComparer.Instance));
            window.Loaded += (sender, args) =>
                TranslateObject(window, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private static void TranslateObject(object value, ISet<object> visited)
        {
            if (value == null || value is string || !visited.Add(value)) return;

            var window = value as Window;
            if (window != null) window.Title = AppLocalization.Translate(window.Title);

            var textBlock = value as TextBlock;
            if (textBlock != null && textBlock.GetBindingExpression(TextBlock.TextProperty) == null)
                textBlock.Text = AppLocalization.Translate(textBlock.Text);

            var contentControl = value as ContentControl;
            if (contentControl?.Content is string)
                contentControl.Content = AppLocalization.Translate((string)contentControl.Content);

            var headered = value as HeaderedContentControl;
            if (headered?.Header is string)
                headered.Header = AppLocalization.Translate((string)headered.Header);

            var frameworkElement = value as FrameworkElement;
            if (frameworkElement != null)
            {
                if (frameworkElement.ToolTip is string)
                    frameworkElement.ToolTip = AppLocalization.Translate((string)frameworkElement.ToolTip);

                var automationName = AutomationProperties.GetName(frameworkElement);
                if (!string.IsNullOrEmpty(automationName))
                    AutomationProperties.SetName(frameworkElement, AppLocalization.Translate(automationName));

                var automationHelp = AutomationProperties.GetHelpText(frameworkElement);
                if (!string.IsNullOrEmpty(automationHelp))
                    AutomationProperties.SetHelpText(frameworkElement, AppLocalization.Translate(automationHelp));

                TranslateObject(frameworkElement.ContextMenu, visited);
            }

            var dataGrid = value as DataGrid;
            if (dataGrid != null)
            {
                foreach (var column in dataGrid.Columns)
                {
                    if (column.Header is string)
                        column.Header = AppLocalization.Translate((string)column.Header);
                }
            }

            var itemsControl = value as ItemsControl;
            if (itemsControl != null)
            {
                foreach (var item in itemsControl.Items)
                    TranslateObject(item, visited);
            }

            var dependencyObject = value as DependencyObject;
            if (dependencyObject != null)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(dependencyObject))
                    TranslateObject(child, visited);

                if (dependencyObject is Visual || dependencyObject is Visual3D)
                {
                    var count = VisualTreeHelper.GetChildrenCount(dependencyObject);
                    for (var index = 0; index < count; index++)
                        TranslateObject(VisualTreeHelper.GetChild(dependencyObject, index), visited);
                }
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
