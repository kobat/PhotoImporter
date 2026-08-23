using PhotoImporter.Core.Filtering;
using System;

namespace PhotoImporter.App
{
    internal static class CopyPostProcessing
    {
        internal static string TryRun(Action restoreSelection, Action refresh)
        {
            if (restoreSelection == null) throw new ArgumentNullException(nameof(restoreSelection));
            if (refresh == null) throw new ArgumentNullException(nameof(refresh));

            try
            {
                restoreSelection();
                refresh();
                return null;
            }
            catch (FilterEvaluationException ex)
            {
                return AppLocalization.Text("フィルター評価エラー: ", "Filter evaluation error: ") + ex.Message;
            }
            catch (Exception ex)
            {
                return AppLocalization.Text("一覧更新エラー: ", "List update error: ") + ex.Message;
            }
        }
    }
}
