using System.Windows;
using System.Windows.Automation;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LingoIsland.Present;

/// <summary>
/// [modHmi筆記匯入確認頁]（spec#14，#309）：預掃描後之彙總確認表。模態（<c>ShowDialog</c>、Owner＝主視窗、CenterOwner）；
/// 每字一列（核取方塊｜原文｜來源〔#320〕｜中譯來源〔#321，有自備中譯時才有〕｜狀態文字）——新字預設勾、已在筆記預設不勾可改選、檔內重複不可勾；
/// 全選新字／全不選；有自備中譯時整批切換「也改查線上」；費用揭露與主鈕隨勾選即時更新（N＝0 停用）。本頁**只收集勾選、不發任何查詢**；
/// 確認後由呼叫端取 <see cref="SelectedItems"/> 交 <see cref="NotesImportRunner"/>。
/// </summary>
public partial class NotesImportWindow : Window
{
    /// <summary>各列核取方塊之 AutomationId 前綴（e2e 以 `NotesImportRow{i}` 定位）。</summary>
    public const string RowAutomationIdPrefix = "NotesImportRow";

    /// <summary>單字欄固定寬（與 XAML 表頭同寬，使各列對齊）；狀態欄取餘寬，拉寬視窗即可看全夾路徑。</summary>
    private const double WordColumnWidth = 200;

    /// <summary>來源欄固定寬（#320；超出省略號、ToolTip 全路徑）。</summary>
    private const double SourceColumnWidth = 130;

    /// <summary>中譯來源欄寬（#321；僅有自備中譯時出現，視窗寬與 MinWidth 同步加此值＋邊距）。</summary>
    private const double TranslationColumnWidth = 150;

    /// <summary>有自備中譯時視窗加寬量（#321）。</summary>
    private const double TranslationWidthExtra = 160;

    private readonly List<(NotesImportEntry Entry, CheckBox Box, TextBlock Status, TextBlock? Translation)> _rows = new();
    private readonly IReadOnlyList<NotesImportEntry> _entries;
    private readonly bool _multiSource;
    private readonly bool _hasOwn;

    /// <summary>確認後之勾選字＋自備中譯（依表列順序；切仍查線上時自備中譯皆空；#321）；取消為空。</summary>
    public IReadOnlyList<NotesImportItem> SelectedItems { get; private set; } = Array.Empty<NotesImportItem>();

    /// <summary>整批切換「有自備中譯的字也改查線上」是否勾選（#321）。</summary>
    private bool ForceOnline => ForceOnlineBox.IsChecked == true;

    /// <summary>來源顯示名→完整路徑（來源欄 ToolTip；#320）。</summary>
    private readonly Dictionary<string, string> _sourcePaths = new(StringComparer.Ordinal);

    /// <summary>（#320 起單檔亦走此入口）可用來源（已依檔名自然排序）、未納入之檔、目標夾全路徑、合併預掃描之列。</summary>
    public NotesImportWindow(IReadOnlyList<NotesImportSource> sources, IReadOnlyList<NotesImportExcluded> excluded, string folderName, IReadOnlyList<NotesImportEntry> entries)
    {
        InitializeComponent();
        _entries = entries;
        _multiSource = sources.Count >= 2;
        _hasOwn = NotesImport.AnyOwnTranslation(entries);
        if (_hasOwn)
        {
            // #321：有自備中譯才出現中譯來源欄與整批切換；無則版面與 v4.18.0 完全相同
            Width += TranslationWidthExtra;
            MinWidth += TranslationWidthExtra;
            TranslationHeaderColumn.Width = new GridLength(TranslationColumnWidth);
            TranslationHeader.Visibility = Visibility.Visible;
            ForceOnlineBox.Visibility = Visibility.Visible;
            ForceOnlineBox.Checked += (_, _) => { RefreshRows(); Refresh(); };
            ForceOnlineBox.Unchecked += (_, _) => { RefreshRows(); Refresh(); };
        }
        foreach (var s in sources) { _sourcePaths[s.DisplayName] = s.Path; }
        var names = sources.Select(s => s.DisplayName).ToList();
        HeaderText.Inlines.Add(new System.Windows.Documents.Run("來源："));
        HeaderText.Inlines.Add(new System.Windows.Documents.Run(NotesImport.SourcesText(names)) { FontWeight = FontWeights.SemiBold });
        HeaderText.Inlines.Add(new System.Windows.Documents.Run("　→　目標資料夾："));
        HeaderText.Inlines.Add(new System.Windows.Documents.Run(folderName) { FontWeight = FontWeights.SemiBold });
        if (sources.Count > 1) { HeaderText.ToolTip = string.Join("\n", sources.Select(s => s.Path)); }
        var excludedText = NotesImport.ExcludedText(excluded);
        ExcludedText.Text = excludedText;
        ExcludedText.Visibility = excludedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (excluded.Count > NotesImport.MaxExcludedShown) { ExcludedText.ToolTip = NotesImport.ExcludedFullText(excluded); }
        // 拖放入口放下當下前景仍是檔案總管：前景鎖可能使 Activate 只閃工作列，故切一次 Topmost 保證疊在最上層可見（#320）
        Loaded += (_, _) => { Topmost = true; Topmost = false; Activate(); };

        for (var i = 0; i < entries.Count; i++) { RowsPanel.Children.Add(MakeRow(entries[i], i)); }

        SelectNewBtn.Click += (_, _) => { foreach (var r in _rows) { r.Box.IsChecked = r.Entry.Status == NotesImportStatus.New; } Refresh(); };
        SelectNoneBtn.Click += (_, _) => { foreach (var r in _rows) { r.Box.IsChecked = false; } Refresh(); };
        ConfirmBtn.Click += (_, _) =>
        {
            SelectedItems = NotesImport.ToItems(_entries, i => _rows[i].Box.IsChecked == true, ForceOnline);
            if (SelectedItems.Count == 0) { return; }
            DialogResult = true;
        };
        CancelBtn.Click += (_, _) => { SelectedItems = Array.Empty<NotesImportItem>(); DialogResult = false; };
        RefreshRows();
        Refresh();
    }

    private FrameworkElement MakeRow(NotesImportEntry e, int index)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WordColumnWidth) }); // 固定寬：各列各自成 Grid，Auto 會使欄位逐列錯位
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SourceColumnWidth) }); // #320 來源欄
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_hasOwn ? TranslationColumnWidth : 0) }); // #321 中譯來源欄
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var box = new CheckBox
        {
            IsChecked = e.DefaultSelected,
            IsEnabled = e.IsSelectable,
            Margin = new Thickness(10, 4, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = e.IsSelectable ? null : NotesImport.StatusText(e) + "——不能另外勾選",
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
            ToolTip = NotesImport.StatusText(e), // 截斷時懸停可見全文（夾路徑可能很長）
            VerticalAlignment = VerticalAlignment.Center,
            // 不只靠顏色——狀態文字本身即原因；暖色只是輔助標示
            Foreground = e.Status == NotesImportStatus.New
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x7A, 0x4A))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA0, 0x6A, 0x20)),
        };
        AutomationProperties.SetAutomationId(status, RowAutomationIdPrefix + index + "Status");
        Grid.SetColumn(status, 4);

        var source = new TextBlock
        {
            Text = e.Source,
            FontSize = 11,
            Margin = new Thickness(4, 3, 4, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x5A, 0x6D)),
            ToolTip = _sourcePaths.TryGetValue(e.Source, out var full) && full.Length > 0 ? full : (e.Source.Length > 0 ? e.Source : null),
        };
        AutomationProperties.SetAutomationId(source, RowAutomationIdPrefix + index + "Source");
        Grid.SetColumn(source, 2);
        grid.Children.Add(source);
        grid.Children.Add(status);

        TextBlock? translation = null;
        if (_hasOwn)
        {
            translation = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(4, 3, 4, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(translation, RowAutomationIdPrefix + index + "Translation");
            Grid.SetColumn(translation, 3);
            grid.Children.Add(translation);
        }

        _rows.Add((e, box, status, translation));
        return grid;
    }

    private static readonly System.Windows.Media.SolidColorBrush OwnBrush = Frozen(0x2F, 0x6A, 0x8A);
    private static readonly System.Windows.Media.SolidColorBrush OnlineBrush = Frozen(0x8A, 0x5A, 0x6D);

    private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>勾選變動時只更新計數、主鈕與費用（O(列數)、不重寫各列；#321 起各列文案只在整批切換時重寫）。</summary>
    private void Refresh()
    {
        var force = ForceOnline;
        var n = _rows.Count(r => r.Box.IsChecked == true);
        var online = _rows.Count(r => r.Box.IsChecked == true && (force || !r.Entry.HasOwnTranslation)); // #321：只計將線上查詢者
        ConfirmBtn.Content = NotesImport.ConfirmButtonText(n, online);
        ConfirmBtn.IsEnabled = n > 0;
        CostText.Text = NotesImport.CostText(n, online);
    }

    /// <summary>各列狀態／中譯來源與計數摘要（建表時與整批切換時各一次；#321）。</summary>
    private void RefreshRows()
    {
        var force = ForceOnline;
        SummaryText.Text = NotesImport.SummaryText(_entries, _multiSource, force);
        SummaryText.ToolTip = SummaryText.Text;
        foreach (var r in _rows)
        {
            var st = NotesImport.StatusText(r.Entry, force);
            r.Status.Text = st;
            r.Status.ToolTip = st;
            if (!r.Box.IsEnabled) { r.Box.ToolTip = st + "——不能另外勾選"; }
            if (r.Translation is { } t)
            {
                t.Text = NotesImport.TranslationSourceText(r.Entry, force);
                t.ToolTip = t.Text.Length > 0 ? t.Text : null;
                t.Foreground = !force && r.Entry.HasOwnTranslation ? OwnBrush : OnlineBrush;
            }
        }
    }
}
