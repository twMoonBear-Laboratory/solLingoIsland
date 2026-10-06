using System.Windows;
using System.Windows.Automation;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LingoIsland.Present;

/// <summary>
/// [modHmi筆記匯入確認頁]（spec#14，#309）：預掃描後之彙總確認表。模態（<c>ShowDialog</c>、Owner＝主視窗、CenterOwner）；
/// 每字一列（核取方塊｜原文｜狀態文字）——新字預設勾、已在筆記預設不勾可改選、檔內重複不可勾；全選新字／全不選；
/// 費用揭露與主鈕「查詢並加入 N 字」隨勾選即時更新（N＝0 停用）。本頁**只收集勾選、不發任何查詢**；
/// 確認後由呼叫端取 <see cref="SelectedWords"/> 交 <see cref="NotesImportRunner"/>。
/// </summary>
public partial class NotesImportWindow : Window
{
    /// <summary>各列核取方塊之 AutomationId 前綴（e2e 以 `NotesImportRow{i}` 定位）。</summary>
    public const string RowAutomationIdPrefix = "NotesImportRow";

    /// <summary>狀態欄固定寬（與 XAML 表頭「狀態」欄同寬，使各列對齊）。</summary>
    private const double StatusColumnWidth = 300;

    private readonly List<(NotesImportEntry Entry, CheckBox Box)> _rows = new();

    /// <summary>確認後之勾選原文（依表列順序）；取消為空。</summary>
    public IReadOnlyList<string> SelectedWords { get; private set; } = Array.Empty<string>();

    public NotesImportWindow(string fileName, string folderName, IReadOnlyList<NotesImportEntry> entries)
    {
        InitializeComponent();
        HeaderText.Inlines.Add(new System.Windows.Documents.Run("來源："));
        HeaderText.Inlines.Add(new System.Windows.Documents.Run(fileName) { FontWeight = FontWeights.SemiBold });
        HeaderText.Inlines.Add(new System.Windows.Documents.Run("　→　目標資料夾："));
        HeaderText.Inlines.Add(new System.Windows.Documents.Run(folderName) { FontWeight = FontWeights.SemiBold });
        SummaryText.Text = NotesImport.SummaryText(entries);

        for (var i = 0; i < entries.Count; i++) { RowsPanel.Children.Add(MakeRow(entries[i], i)); }

        SelectNewBtn.Click += (_, _) => { foreach (var (e, box) in _rows) { box.IsChecked = e.Status == NotesImportStatus.New; } Refresh(); };
        SelectNoneBtn.Click += (_, _) => { foreach (var (_, box) in _rows) { box.IsChecked = false; } Refresh(); };
        ConfirmBtn.Click += (_, _) =>
        {
            SelectedWords = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Entry.Text).ToList();
            if (SelectedWords.Count == 0) { return; }
            DialogResult = true;
        };
        CancelBtn.Click += (_, _) => { SelectedWords = Array.Empty<string>(); DialogResult = false; };
        Refresh();
    }

    private FrameworkElement MakeRow(NotesImportEntry e, int index)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatusColumnWidth) }); // 固定寬：各列各自成 Grid，Auto 會使狀態欄逐列錯位

        var box = new CheckBox
        {
            IsChecked = e.DefaultSelected,
            IsEnabled = e.IsSelectable,
            Margin = new Thickness(10, 4, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = e.IsSelectable ? null : "檔內重複的字只留第一筆，不能另外勾選",
        };
        AutomationProperties.SetAutomationId(box, RowAutomationIdPrefix + index);
        AutomationProperties.SetName(box, e.Text);
        box.Checked += (_, _) => Refresh();
        box.Unchecked += (_, _) => Refresh();
        grid.Children.Add(box);

        var text = new TextBlock
        {
            Text = e.Text,
            FontSize = 12.5,
            Margin = new Thickness(4, 3, 4, 3),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = e.Status == NotesImportStatus.New
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x77, 0x77, 0x77)),
            ToolTip = e.Text,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var status = new TextBlock
        {
            Text = NotesImport.StatusText(e),
            FontSize = 11,
            Margin = new Thickness(8, 3, 12, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            // 不只靠顏色——狀態文字本身即原因；暖色只是輔助標示
            Foreground = e.Status == NotesImportStatus.New
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x7A, 0x4A))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA0, 0x6A, 0x20)),
        };
        AutomationProperties.SetAutomationId(status, RowAutomationIdPrefix + index + "Status");
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        _rows.Add((e, box));
        return grid;
    }

    private void Refresh()
    {
        var n = _rows.Count(r => r.Box.IsChecked == true);
        ConfirmBtn.Content = NotesImport.ConfirmButtonText(n);
        ConfirmBtn.IsEnabled = n > 0;
        CostText.Text = NotesImport.CostText(n);
    }
}
