using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using UEEtc.Core.Reporting;

namespace UEEtc.Wpf.Preview;

public partial class CodeChangePreview : UserControl
{
    private readonly List<Paragraph> changes = [];
    public string Text { get; private set; } = "";
    public IReadOnlyList<int> ChangedLines { get; private set; } = [];
    public int CurrentChangeIndex { get; private set; } = -1;
    public int ChangeCount => changes.Count;
    public CodeChangePreview() => InitializeComponent();

    public void Clear() => ShowComparison("", "");
    public void ShowComparison(string original, string proposed)
    {
        Text = proposed;
        ChangedLines = ChangedLineMap.Compare(original, proposed);
        var marked = ChangedLines.ToHashSet();
        var lines = proposed.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var document = new FlowDocument { PagePadding = new Thickness(4), PageWidth = Math.Max(1000, lines.Max(l => l.Replace("\t", "        ", StringComparison.Ordinal).Length) * 9.0 + 100) };
        changes.Clear(); CurrentChangeIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            bool isChanged = marked.Contains(i + 1);
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
            paragraph.Inlines.Add(new Run($"{i + 1,5} {(isChanged ? '+' : ' ')} ") { Foreground = Brushes.Gray });
            paragraph.Inlines.Add(new Run(lines[i]));
            if (isChanged)
            {
                paragraph.Background = Brushes.Honeydew;
                paragraph.Foreground = Brushes.Black;
                if (i == 0 || !marked.Contains(i)) changes.Add(paragraph);
            }
            document.Blocks.Add(paragraph);
        }
        Editor.Document = document;
        NextButton.IsEnabled = PreviousButton.IsEnabled = changes.Count > 0;
        PositionText.Text = changes.Count == 0 ? "変更なし" : $"変更 {changes.Count} 箇所 / 緑色と + が変更行";
        Editor.ScrollToHome();
    }
    public void MoveChange(int direction)
    {
        if (changes.Count == 0) return;
        CurrentChangeIndex = CurrentChangeIndex < 0 ? (direction < 0 ? changes.Count - 1 : 0) :
            (CurrentChangeIndex + direction + changes.Count) % changes.Count;
        var paragraph = changes[CurrentChangeIndex];
        Editor.Selection.Select(paragraph.ContentStart, paragraph.ContentStart);
        paragraph.BringIntoView();
        PositionText.Text = $"変更 {CurrentChangeIndex + 1} / {changes.Count}（緑色と + が変更行）";
    }
    private void Previous_Click(object sender, RoutedEventArgs e) => MoveChange(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => MoveChange(1);
}
