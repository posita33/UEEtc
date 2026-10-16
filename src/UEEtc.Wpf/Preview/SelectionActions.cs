using System.Windows.Controls;
using System.Windows.Input;

namespace UEEtc.Wpf.Preview;

public interface ISelectionRow
{
    bool Selected { get; set; }
    bool CanSelect { get; }
}

public static class SelectionActions
{
    public static void Set(IEnumerable<ISelectionRow> rows, bool selected)
    { foreach (var row in rows.Where(r => r.CanSelect).ToArray()) row.Selected = selected; }

    public static void Attach(DataGrid grid)
    {
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
            var rows = grid.SelectedItems.OfType<ISelectionRow>().Where(r => r.CanSelect).ToArray();
            if (rows.Length == 0) return;
            Set(rows, !rows.All(r => r.Selected)); e.Handled = true;
        };
    }
}
