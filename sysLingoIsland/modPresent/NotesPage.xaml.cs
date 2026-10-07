using LingoIsland.Query;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Border = System.Windows.Controls.Border;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using Orientation = System.Windows.Controls.Orientation;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using Thickness = System.Windows.Thickness;
using CornerRadius = System.Windows.CornerRadius;
using Visibility = System.Windows.Visibility;
using RoutedPropertyChangedEventArgs = System.Windows.RoutedPropertyChangedEventArgs<object>;
using TextTrimming = System.Windows.TextTrimming;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FrameworkElement = System.Windows.FrameworkElement;
using UIElement = System.Windows.UIElement;
using Point = System.Windows.Point;
using DataObject = System.Windows.DataObject;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Adorner = System.Windows.Documents.Adorner;
using AdornerLayer = System.Windows.Documents.AdornerLayer;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using DrawingContext = System.Windows.Media.DrawingContext;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;

namespace LingoIsland.Present;

/// <summary>
/// 我的筆記分頁（Issue #34／#38 Windows 慣例收斂）：左側**多層資料夾樹如檔案總管**——頂部工具列僅
/// [建立資料夾]（建立即進原地更名）、標準樹節點＋右鍵選單（新增子資料夾／更名 `F2` 原地編輯／刪除 `Del`）、
/// 拖曳移動節點（目標夾高亮、防成環由 <see cref="NotesStore"/> 保證）；右側選取夾之條目拖曳排序
/// （**插入位置指示線**即時回饋）、**右鍵選單**（播音／檢視／底色粉彩盤／刪除，Issue #44）＋**雙擊＝檢視**、
/// 卡片底色套 <see cref="NoteEntry.Color"/>。每次變更即落地 notes.json；語音以 provider 委派取用。
/// </summary>
[UnsavedStateOutOfScope("F2 原地更名之暫態編輯，#285 範圍外")]
[ThemeConsumptionOutOfScope("卡片底色一軌（ColorMath.LightenForBackground）未納入 spec#12 推送，底色於查詢當下決定並隨筆記落地")]
public partial class NotesPage : UserControl
{
    private const string FmtFolder = "LingoIsland.NoteFolderId";
    private const string FmtEntry = "LingoIsland.NoteEntryId";

    private readonly NotesStore _store;
    private readonly Func<ISpeechService?> _speech;
    private readonly Func<IPronunciationAssessor?> _assessor;   // 發音評分（spec#10）
    private readonly Func<IAudioRecorder> _recorderFactory;     // 麥克風錄音工廠（spec#10）
    private readonly Func<int> _threshold;                      // 發音及格門檻（設定頁可調）
    private readonly INotificationService _notify;              // 發音回饋系統通知（#101；未安裝降級浮層）
    private IAudioRecorder? _recording;                         // 按住期間之錄音器（同時至多一個）
    private PracticeCell? _activeCell;                          // 錄音中之卡片繫結（供樹重建時收束、釋放擷取）
    private Action<double>? _levelHandler;                      // 錄音期間即時音量訂閱（收束時退訂）
    private bool _practiceBusy;                                 // 評分中守衛：不重入
    private System.Windows.Threading.DispatcherTimer? _maxRecTimer; // 錄音上限守衛
    private NotesData _data;

    public event Action<NoteEntry>? ViewRequested;

    /// <summary>目前選取資料夾之條目數（#132，供狀態列顯示）。</summary>
    public int CurrentEntryCount => Selected?.Entries.Count ?? 0;

    /// <summary>目前檢視條目數變更（#132）：切夾/增/刪/重繪後觸發，供主視窗狀態列即時更新。</summary>
    public event Action<int>? EntryCountChanged;

    private TreeViewItem? _pressItem;
    private Point _pressPoint;
    private NoteEntry? _entryDrag;
    private Point _entryStart;
    private TreeViewItem? _dropHighlight;   // 目前拖曳滑過之目標夾（高亮回饋，Issue #38）
    private InsertionAdorner? _insertLine;  // 條目拖曳之插入位置指示線（Issue #38）
    private System.Windows.Threading.DispatcherTimer? _edgeScrollTimer; // #128：拖曳邊緣自動捲動計時器
    private double _edgeScrollDelta;                                    // 每 tick 垂直捲動量（含方向）；0＝不捲

    public NotesPage(NotesStore store, Func<ISpeechService?> speechProvider,
        Func<IPronunciationAssessor?> assessorProvider, Func<IAudioRecorder> recorderFactory, Func<int> passThreshold,
        INotificationService notify)
    {
        InitializeComponent();
        _store = store;
        _speech = speechProvider;
        _assessor = assessorProvider;
        _recorderFactory = recorderFactory;
        _threshold = passThreshold;
        _notify = notify;
        _data = _store.LoadEnsured();

        NewFolderBtn.Click += (_, _) => CreateFolder(parent: null); // 一律建頂層；子資料夾走節點右鍵選單（檔案總管慣例）
        ImportListBtn.Click += (_, _) => BeginImportList();           // spec#14／#309：選檔→預掃描→確認頁→交 App 逐字查詢加入
        PreviewDragEnter += OnFileDragEnter;                          // #320：整頁接受 .txt／.csv 檔案拖放（Preview 穿隧先攔 FileDrop，非 FileDrop 不碰）
        PreviewDragOver += OnFileDragOver;
        PreviewDragLeave += OnFileDragLeave;
        PreviewDrop += OnFileDrop;
        AlphaSortBtn.Click += (_, _) => ToggleSort(NoteSortMode.Alpha);   // 字母（#126：同鈕再點翻方向）
        TimeSortBtn.Click += (_, _) => ToggleSort(NoteSortMode.Time);     // 日期
        ManualSortBtn.Click += (_, _) => ToggleSort(NoteSortMode.Manual); // 自訂順序（拖曳序 正/反）
        ClearPracticeBtn.Click += (_, _) => OnClearPractice();
        FolderTree.SelectedItemChanged += OnFolderSelected;
        FolderTree.PreviewMouseLeftButtonUp += (_, _) => _pressItem = null; // 放開即清，防殘留按壓被後續拖曳劫持
        FolderTree.KeyDown += OnTreeKeyDown;                // F2＝更名、Del＝刪除（檔案總管慣例）
        FolderTree.Drop += OnTreeBackgroundDrop;            // 拖到空白處 → 移到頂層
        FolderTree.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
        FolderTree.DragLeave += (_, _) => SetDropHighlight(null);

        EntryPanel.AllowDrop = true;
        EntryPanel.DragOver += OnEntryAreaDragOver;
        EntryPanel.DragLeave += (_, _) => { HideInsertLine(); StopEdgeScroll(); }; // #128：離開清單即停自動捲動
        EntryPanel.Drop += OnEntryAreaDrop;

        BuildTree();
    }

    public void Reload()
    {
        _data = _store.LoadEnsured();
        BuildTree();
    }

    // ---- 匯入清單（spec#14／#309）----

    /// <summary>
    /// 確認頁按下主鈕（「查詢並加入 N 字」或 #321 之「加入 N 字（…）」）後觸發：(目標資料夾 Id, 目標資料夾名, 勾選之字＋自備中譯清單〔#321；自備中譯空＝線上查詢〕)。
    /// 本頁只負責選檔→預掃描→確認（確認前零 AI 呼叫）；逐字線上查詢與寫入由 App 以 <see cref="NotesImportRunner"/> 執行後 <see cref="Reload"/>。
    /// </summary>
    public event Action<string, string, IReadOnlyList<NotesImportItem>>? ImportConfirmed;

    /// <summary>匯入流程進行中（#320）：自按鈕／放下起至 <see cref="BeginImportFiles"/> 返回止；一律以 try/finally 解除，期間不受理新的拖放與按鈕。</summary>
    private bool _importBusy;

    /// <summary>「匯入清單」鈕：金鑰預檢在選檔之前；對話框可多選（#320），選完交 <see cref="BeginImportFiles"/>（與拖放同一條路）。</summary>
    private void BeginImportList()
    {
        if (_importBusy) { return; }
        _importBusy = true;
        try
        {
            if (Selected is null) { ToastNotifier.Show("請先在左側選一個資料夾，再匯入清單。"); return; } // 守備性：樹恆預選首夾，平時不會到此
            if (!CheckApiKey()) { return; } // 預檢在選檔之前（不讓使用者選完檔、勾完表才被告知）
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = NotesImport.DialogFilter,
                Multiselect = true,
                Title = NotesImport.DialogTitle,
            };
            if (dlg.ShowDialog() != true) { return; }
            BeginImportFiles(dlg.FileNames);
        }
        finally { _importBusy = false; }
    }

    /// <summary>OPENAI_API_KEY 預檢：未設即指引至「選項」分頁並回 false（App 端於執行前再檢一次為守備）。</summary>
    private bool CheckApiKey()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))) { return true; }
        System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this),
            "尚未設定 OPENAI_API_KEY，匯入後無法線上查詢。請先到主視窗「選項」分頁設定金鑰（或設定同名環境變數），再按「匯入清單」或拖入清單檔。",
            "匯入清單", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        return false;
    }

    /// <summary>
    /// 兩入口（按鈕多選、檔案拖放）之共用段（#320）：副檔名分類→多檔載入→合併預掃描→確認頁→<see cref="ImportConfirmed"/>。
    /// 呼叫端負責 <see cref="_importBusy"/>；本函式不在 OLE 放下回呼內執行（拖放以 Dispatcher 延後呼叫）。
    /// </summary>
    private void BeginImportFiles(IReadOnlyList<string> paths)
    {
        var folder = Selected;
        if (folder is null) { ToastNotifier.Show("請先在左側選一個資料夾，再匯入清單。"); return; }
        var owner = System.Windows.Window.GetWindow(this);
        var split = NotesImport.SplitByExtension(paths, System.IO.Directory.Exists);
        var load = NotesImport.LoadSources(split.Accepted, p => new System.IO.FileInfo(p).Length, NotesImport.ReadAllText);
        if (!load.IsOk)
        {
            System.Windows.MessageBox.Show(owner, load.Error, "匯入清單", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        var excluded = split.Rejected.Concat(load.Excluded).ToList(); // 未納入：分類被拒者在前
        if (load.Sources.Count == 0)
        {
            System.Windows.MessageBox.Show(owner, NotesImport.AllUnusableText(excluded, split.Accepted.Count), "匯入清單",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var data = _store.LoadEnsured(); // 以磁碟現況判「已在筆記」（他處可能剛加入）
        var scan = NotesImport.ScanSources(load.Sources,
            key => NotesStore.FolderOfKey(data, key) is { } f ? NotesStore.FolderPath(data, f.Id) : null); // 已在筆記者附所在夾（B-3）
        if (!scan.IsOk)
        {
            System.Windows.MessageBox.Show(owner, scan.Error, "匯入清單",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        var folderPath = NotesStore.FolderPath(data, folder.Id) is { Length: > 0 } fp ? fp : folder.Name;
        var win = new NotesImportWindow(load.Sources, excluded, folderPath, scan.Entries) { Owner = owner };
        if (win.ShowDialog() != true || win.SelectedItems.Count == 0) { return; }
        ImportConfirmed?.Invoke(folder.Id, folderPath, win.SelectedItems); // 結果表與 toast 用同一個路徑名（B-13）
    }

    // ---- 檔案拖放匯入（#320）：頁層 Preview 穿隧先攔 FileDrop；非 FileDrop 一律不碰、不設 Handled（既有資料夾／條目拖曳原封走原處理器）----

    /// <summary>本次拖曳狀態：綁定該次拖曳之 <c>e.Data</c> 參照——參照不同＝新的一次拖曳才重算分類與重置提示（頁內放開不可放置、Esc 只收到 Leave，不能靠離開時清空）。</summary>
    private sealed class FileDragState
    {
        public required System.Windows.IDataObject Data { get; init; }
        public required IReadOnlyList<string> Paths { get; init; }
        public required int AcceptedCount { get; init; }
        public bool Hinted { get; set; }
    }

    private FileDragState? _fileDrag;

    private static bool IsFileDrag(DragEventArgs e) => e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);

    private FileDragState FileDragStateFor(DragEventArgs e)
    {
        if (_fileDrag is null || !ReferenceEquals(_fileDrag.Data, e.Data))
        {
            var paths = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
            _fileDrag = new FileDragState
            {
                Data = e.Data,
                Paths = paths,
                // 只在進入時算一次、且只看副檔名——OLE 回呼內不碰磁碟（離線網路磁碟之 Directory.Exists 可卡數秒）；資料夾判定留給 BeginImportFiles
                AcceptedCount = NotesImport.SplitByExtension(paths, _ => false).Accepted.Count,
            };
        }
        return _fileDrag;
    }

    private void OnFileDragEnter(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e)) { return; }
        var st = FileDragStateFor(e);
        if (!_importBusy && st.AcceptedCount == 0 && !st.Hinted)
        {
            st.Hinted = true; // 一次拖曳只提示一次（游標跨子元素時 WPF 會重發 Enter）
            ToastNotifier.Show(NotesImport.DropRejectedHint);
        }
        OnFileDragOver(sender, e);
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e)) { return; }
        var st = FileDragStateFor(e);
        var canDrop = !_importBusy && st.AcceptedCount > 0 && Selected is not null;
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (canDrop) { ShowDropBanner(); } else { HideDropBanner(); }
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e)) { return; }
        HideDropBanner(); // 任何結束方式皆不殘留；仍在頁內者下一個 DragOver 會再顯示
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e)) { return; }
        HideDropBanner();
        e.Handled = true;
        var st = FileDragStateFor(e);
        _fileDrag = null;
        if (_importBusy || st.AcceptedCount == 0) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        _importBusy = true;
        var paths = st.Paths;
        // 不得在 OLE 放下回呼內開任何模態視窗——否則拖曳來源（檔案總管）之 DoDragDrop 要等看表加查詢跑完才返回、總管卡住
        Dispatcher.BeginInvoke(new Action(() => RunDroppedImport(paths)));
    }

    /// <summary>延後執行之拖放匯入：先把主視窗帶到前景（放下當下前景仍是檔案總管）、做與按鈕相同之守備，再走共用段；finally 解除忙碌。</summary>
    private void RunDroppedImport(IReadOnlyList<string> paths)
    {
        try
        {
            System.Windows.Window.GetWindow(this)?.Activate();
            if (!CheckApiKey()) { return; }
            BeginImportFiles(paths);
        }
        finally { _importBusy = false; }
    }

    private void ShowDropBanner()
    {
        var f = Selected;
        var path = f is null ? "" : (NotesStore.FolderPath(_data, f.Id) is { Length: > 0 } p ? p : f.Name);
        DropBannerText.Text = $"放開後先開確認表——匯入到「{path}」";
        DropBanner.Visibility = Visibility.Visible;
    }

    private void HideDropBanner() => DropBanner.Visibility = Visibility.Collapsed;

    private NoteFolder? Selected => (FolderTree.SelectedItem as TreeViewItem)?.Tag as NoteFolder;

    // ---- 樹建置 ----

    private void BuildTree()
    {
        NotesStore.SortFolders(_data); // 同層一律依名稱自然排序（檔案總管慣例，Issue #42）
        var keepId = Selected?.Id;
        _dropHighlight = null;
        FolderTree.Items.Clear();
        foreach (var f in _data.Folders)
        {
            FolderTree.Items.Add(MakeItem(f));
        }
        var target = keepId is null ? null : FindItem(FolderTree.Items, keepId);
        (target ?? FirstItem())?.SetValue(TreeViewItem.IsSelectedProperty, true);
        RenderFolder();
    }

    private TreeViewItem MakeItem(NoteFolder f, int depth = 0)
    {
        var item = new TreeViewItem
        {
            Header = MakeHeader(f, depth),
            Tag = f,
            IsExpanded = true,
            AllowDrop = true,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, // USR：列內容拉伸至欄寬，使長名可折行
        };
        item.PreviewMouseLeftButtonDown += OnItemMouseDown;
        item.PreviewMouseRightButtonDown += (s, _) => ((TreeViewItem)s).IsSelected = true; // 右鍵先選取（檔案總管慣例）
        item.PreviewMouseMove += OnItemMouseMove;
        item.Drop += OnItemDrop;
        item.DragOver += (s, e) =>
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            SetDropHighlight(s as TreeViewItem); // 目標夾高亮（拖曳回饋）
        };
        item.DragLeave += (s, _) =>
        {
            if (ReferenceEquals(_dropHighlight, s))
            {
                SetDropHighlight(null);
            }
        };
        item.ContextMenu = MakeMenu(item, f);
        foreach (var sub in f.Folders)
        {
            item.Items.Add(MakeItem(sub, depth + 1));
        }
        return item;
    }

    private StackPanel MakeHeader(NoteFolder f, int depth)
    {
        // 水平 StackPanel（USR：長名自動折行且圖示/筆數**頂部對齊**（對到第一行））：名稱綁最大寬強制折行，icon/count VAlign=Center 對到第一行。
        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        sp.Children.Add(new TextBlock
        {
            Text = "", // Segoe MDL2 Assets：FolderHorizontal
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brush("#C77D9A"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 5, 0),
        });

        var name = new TextBlock
        {
            Text = f.Name,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
        };
        // WPF TreeViewItem 預設標頭欄 Auto 寬（不約束）→ 名稱不折只截。綁最大寬＝樹寬 −（圖示+筆數+縮排+捲軸+邊距）強制折行；depth 補每層縮排。
        name.SetBinding(TextBlock.MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth")
        {
            Source = FolderTree,
            Converter = NameWidthConv,
            ConverterParameter = 70.0 + depth * 19.0,
        });
        sp.Children.Add(name);

        sp.Children.Add(new TextBlock
        {
            Text = "　" + f.Entries.Count,
            FontSize = 11.5,
            Foreground = Brush("#8A5A6D"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 0, 0, 0),
        });
        return sp;
    }

    /// <summary>資料夾名折行寬度換算（USR）：樹寬 − ConverterParameter（圖示+筆數+縮排+捲軸+邊距）；下限 48。</summary>
    private sealed class WidthMinusConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, System.Type t, object parameter, System.Globalization.CultureInfo c)
            => System.Math.Max(48.0, (value is double w ? w : 200.0) - (parameter is double p ? p : 70.0));
        public object ConvertBack(object value, System.Type t, object parameter, System.Globalization.CultureInfo c)
            => throw new System.NotSupportedException();
    }
    private static readonly WidthMinusConverter NameWidthConv = new();

    private ContextMenu MakeMenu(TreeViewItem item, NoteFolder f)
    {
        var menu = new ContextMenu();
        var addSub = new MenuItem { Header = "新增子資料夾" };
        addSub.Click += (_, _) => CreateFolder(f);
        var rename = new MenuItem { Header = "更名", InputGestureText = "F2" };
        rename.Click += (_, _) => BeginRename(item);
        var delete = new MenuItem { Header = "刪除", InputGestureText = "Del", Foreground = Brush("#B23B3B") };
        delete.Click += (_, _) => DeleteFolder(f);
        menu.Items.Add(addSub);
        menu.Items.Add(new Separator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        return menu;
    }

    private TreeViewItem? FirstItem() => FolderTree.Items.Count > 0 ? (TreeViewItem)FolderTree.Items[0]! : null;

    private static TreeViewItem? FindItem(System.Windows.Controls.ItemCollection items, string id)
    {
        foreach (TreeViewItem it in items)
        {
            if (it.Tag is NoteFolder f && f.Id == id)
            {
                return it;
            }
            var sub = FindItem(it.Items, id);
            if (sub is not null)
            {
                return sub;
            }
        }
        return null;
    }

    private void OnFolderSelected(object? sender, RoutedPropertyChangedEventArgs e) => RenderFolder();

    private void RenderFolder()
    {
        CancelActivePractice(); // 重建卡片前收束進行中的錄音，免舊卡卸離後錄音器/計時器外洩、麥克風卡紅（§5 #1）
        _selector.Clear();      // 重繪/切夾即清選取（#110，不持久化）
        EntryPanel.Children.Clear();
        var f = Selected;
        bool any = f is not null && f.Entries.Count > 0;
        ImportListBtn.IsEnabled = f is not null; // spec#14：無選取夾時停用（tooltip 說明先選夾）
        EmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClearPracticeBtn.IsEnabled = any;
        AlphaSortBtn.IsEnabled = any;   // 空夾無可排序（#126）
        TimeSortBtn.IsEnabled = any;
        ManualSortBtn.IsEnabled = any;
        UpdateSortButtons(f);           // 依 f.Sort 更新作用中鈕之方向字圖/粗體/深色（#126）
        EntryCountChanged?.Invoke(f?.Entries.Count ?? 0); // #132：條目數變更通知狀態列
        if (f is null)
        {
            return;
        }
        foreach (var entry in DisplayEntries(f)) // #126：顯示為投影序（非破壞，不改 f.Entries 手動序）
        {
            EntryPanel.Children.Add(EntryRow(entry));
        }
    }

    private void Persist()
    {
        NotesStore.SortFolders(_data); // 存檔順序與顯示一致（名稱序）
        _store.Save(_data);
        BuildTree();
    }

    // ---- 排序（#126：非破壞式檢視投影＋三 toggle＋per-folder 記憶；存於 NoteFolder.Sort（notes.json）） ----

    /// <summary>目前資料夾之顯示序（投影 f.Entries，不改儲存序；#126）。</summary>
    private static IEnumerable<NoteEntry> DisplayEntries(NoteFolder f)
    {
        var s = f.Sort ?? new FolderSort(); // 唯讀預設（Manual／正向）：就地建立、不共用可變實例（審查 NIT②）
        return NotesStore.ProjectView(f.Entries, s.Mode, s.CurrentAscending);
    }

    /// <summary>
    /// 點排序鈕（#126）：同模式再點＝翻該模式方向；點他模式＝切為該模式（沿用其記住之方向）。
    /// 僅改投影狀態（<see cref="NoteFolder.Sort"/>）、不動 f.Entries 手動序；即時落地 notes.json、重繪。
    /// </summary>
    private void ToggleSort(NoteSortMode mode)
    {
        var f = Selected;
        if (f is null || f.Entries.Count == 0)
        {
            return;
        }
        var s = f.Sort ??= new FolderSort();
        if (s.Mode == mode)
        {
            switch (mode) // 同模式再點 → 翻該模式方向
            {
                case NoteSortMode.Alpha: s.AlphaAsc = !s.AlphaAsc; break;
                case NoteSortMode.Time: s.TimeAsc = !s.TimeAsc; break;
                default: s.ManualAsc = !s.ManualAsc; break;
            }
        }
        else
        {
            s.Mode = mode; // 切模式，方向沿用該模式記住值
        }
        _store.Save(_data);
        RenderFolder();
    }

    /// <summary>把目前投影序落實為 f.Entries 儲存序並切「自訂」（#126；拖曳重排前呼叫，使拖曳所見即所得、不彈回）。</summary>
    private static void MaterializeToManual(NoteFolder f)
    {
        var s = f.Sort ??= new FolderSort();
        var projected = NotesStore.ProjectView(f.Entries, s.Mode, s.CurrentAscending);
        f.Entries.Clear();
        f.Entries.AddRange(projected);
        s.Mode = NoteSortMode.Manual;
        s.ManualAsc = true; // materialize 後手動序即為正向基準
    }

    /// <summary>依 f.Sort 更新三排序鈕視覺（作用中＝方向字圖 ▲/▼＋粗體＋深色；非作用＝僅標籤。非顏色線索＝字圖存否，#126 色盲友善）。</summary>
    private void UpdateSortButtons(NoteFolder? f)
    {
        var s = f?.Sort ?? new FolderSort(); // 唯讀預設值、不共用可變實例（審查 NIT②）
        SetSortBtn(AlphaSortBtn, "字母", s.Mode == NoteSortMode.Alpha, s.AlphaAsc);
        SetSortBtn(TimeSortBtn, "日期", s.Mode == NoteSortMode.Time, s.TimeAsc);
        SetSortBtn(ManualSortBtn, "自訂", s.Mode == NoteSortMode.Manual, s.ManualAsc);
    }

    private void SetSortBtn(Button btn, string label, bool active, bool ascending)
    {
        btn.Content = active ? $"{label} {(ascending ? "▲" : "▼")}" : label;
        btn.FontWeight = active ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
        btn.Foreground = (System.Windows.Media.Brush)FindResource(active ? "PinkText" : "PinkSub");
    }

    /// <summary>右欄[Clear Practice]：清空選取夾內全部筆記之發音練習紀錄（成績框回未練；不刪筆記，spec#10）。</summary>
    private void OnClearPractice()
    {
        var f = Selected;
        if (f is null || f.Entries.Count == 0)
        {
            return;
        }
        if (MessageBox.Show($"確定要清除「{f.Name}」中全部 {f.Entries.Count} 則筆記的發音練習紀錄嗎？筆記本身會保留——僅重設成績框。",
                "清除練習紀錄", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        NotesStore.ResetFolderPractice(_data, f.Id);
        _store.Save(_data);
        RenderFolder(); // 僅重繪目前夾條目（成績框回未練），不需重建整棵樹
    }

    // ---- 資料夾操作（頂部[建立資料夾]＋右鍵選單／F2／Del，原地更名如檔案總管） ----

    private void CreateFolder(NoteFolder? parent)
    {
        var name = NotesStore.NextNewFolderName(_data);
        var created = parent is null
            ? NotesStore.AddFolder(_data, name)
            : NotesStore.AddSubFolder(_data, parent.Id, name);
        if (created is null)
        {
            return;
        }
        Persist(); // 依名稱歸位後入原地更名
        var item = FindItem(FolderTree.Items, created.Id);
        if (item is not null)
        {
            item.IsSelected = true;
            item.BringIntoView();
            BeginRename(item); // 建立即進原地更名（檔案總管慣例）
        }
    }

    /// <summary>原地更名：以 TextBox 就地編輯，Enter／失焦＝確認、Esc＝取消。</summary>
    private void BeginRename(TreeViewItem? item)
    {
        if (item?.Tag is not NoteFolder f)
        {
            return;
        }
        var box = new TextBox { Text = f.Name, MinWidth = 110, FontSize = 13, Padding = new Thickness(2, 0, 2, 0) };
        var done = false;
        void Commit(bool save)
        {
            if (done)
            {
                return;
            }
            done = true;
            if (save && !string.IsNullOrWhiteSpace(box.Text) && box.Text.Trim() != f.Name)
            {
                NotesStore.RenameFolder(_data, f.Id, box.Text);
                Persist(); // 更名後依名稱歸位
                return;
            }
            BuildTree(); // 取消或未變更亦重建，還原一般節點頭
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit(save: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Commit(save: false);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit(save: true);
        item.Header = box;
        item.Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }));
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginRename(FolderTree.SelectedItem as TreeViewItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Selected is { } f)
        {
            DeleteFolder(f);
            e.Handled = true;
        }
    }

    private void DeleteFolder(NoteFolder f)
    {
        var msg = (f.Entries.Count > 0 || f.Folders.Count > 0)
            ? $"確定要刪除資料夾「{f.Name}」及其子資料夾與 {f.Entries.Count} 則筆記嗎？此操作無法復原。"
            : $"確定要刪除資料夾「{f.Name}」嗎？";
        if (MessageBox.Show(msg, "刪除資料夾", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        NotesStore.RemoveFolder(_data, f.Id);
        Persist();
    }

    // ---- 資料夾/條目拖曳移動（節點移動如檔案總管；防環由 store；目標夾高亮回饋） ----

    private void SetDropHighlight(TreeViewItem? item)
    {
        if (ReferenceEquals(_dropHighlight, item))
        {
            return;
        }
        if (_dropHighlight is not null)
        {
            _dropHighlight.Background = Brushes.Transparent;
        }
        _dropHighlight = item;
        if (item is not null)
        {
            item.Background = Brush("#F4C2D0");
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressItem = sender as TreeViewItem;
        _pressPoint = e.GetPosition(null);
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        // 僅由被按壓的節點本身啟動拖曳：防止殘留 _pressItem 在滑鼠（因其他拖曳）持鍵掃過樹時誤啟動資料夾拖曳
        if (_pressItem is null || !ReferenceEquals(sender, _pressItem) || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _pressPoint.X) < 6 && Math.Abs(p.Y - _pressPoint.Y) < 6)
        {
            return;
        }
        if (_pressItem.Tag is NoteFolder f)
        {
            var moving = _pressItem;
            _pressItem = null;
            DragDrop.DoDragDrop(moving, new DataObject(FmtFolder, f.Id), DragDropEffects.Move);
            SetDropHighlight(null); // 拖曳結束（含取消）清除高亮
        }
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        if ((sender as TreeViewItem)?.Tag is not NoteFolder target)
        {
            return;
        }
        if (e.Data.GetDataPresent(FmtFolder) && e.Data.GetData(FmtFolder) is string fid)
        {
            NotesStore.MoveFolder(_data, fid, target.Id); // 防移入自身/子孫由 store 保證
            Persist();
        }
        else if (e.Data.GetDataPresent(FmtEntry) && e.Data.GetData(FmtEntry) is string eid)
        {
            NotesStore.MoveEntry(_data, eid, target.Id);
            Persist();
        }
        e.Handled = true;
    }

    private void OnTreeBackgroundDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        if (e.Handled)
        {
            return; // 已由某節點處理
        }
        if (e.Data.GetDataPresent(FmtFolder) && e.Data.GetData(FmtFolder) is string fid)
        {
            NotesStore.MoveFolder(_data, fid, null); // 移到頂層
            Persist();
        }
    }

    // ---- 條目版型與排序（拖曳中顯示插入位置指示線，Issue #38） ----

    // 單擊選取（Issue #110）：每頁單選、僅視覺回饋（不掛行為）；框厚恆定 2px 只換色、不跳版（共用 CardSelector）。
    private readonly CardSelector _selector = new();

    // 條目卡（Issue #44）：底色套 NoteEntry.Color；操作循 Windows 清單慣例——右鍵選單＋雙擊檢視、無常駐按鈕列。
    private UIElement EntryRow(NoteEntry entry)
    {
        var card = new Border
        {
            // #111→#118：通過卡透明底（透浮水印；導出態＝最佳分≥門檻）、未過素色；
            // 未選框＝底色×0.80 加深（#118、色彩身份延伸）——底刷/框刷單一來源 NoteCardBrush
            Background = NoteCardBrush.For(entry.Color, EntryDisplaySettings.CardOpacity), // v1.0.1：底＝該筆記色×可調透明度（筆記/歷史共用；過關不再改底）
            BorderBrush = NoteCardBrush.BorderFor(entry.Color),
            BorderThickness = new Thickness(2), // #110：框厚恆定 2px（選取只換色不跳版）
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3, 10, 3), // #複查：比照歷史條目之緊湊內距（單行、時間 inline）
            Margin = new Thickness(0, 0, 0, 5),   // #複查：比照歷史條目卡間距（原 8）
            AllowDrop = true, // 事件交由 EntryPanel 統一處理（冒泡），卡片僅作為有效放置目標
            Focusable = true,        // v1.0.1（USR 回饋）：選取後可接收 Delete 鍵刪除本條目
            FocusVisualStyle = null, // 選取已以框色表示，免預設虛線焦點框
        };
        card.MouseRightButtonDown += (_, _) => { _selector.Select(card); card.Focus(); }; // 右鍵開選單亦設選取（Windows 慣例，#110）＋取焦點
        // v1.0.1（USR 回饋）：選取後按 Delete 直接刪除本條目（比照右鍵選單 Delete、與其一致不另確認）
        card.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete)
            {
                NotesStore.RemoveEntry(_data, entry.Id);
                Persist();
                e.Handled = true;
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 0 拖曳握把
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });    // 1 原文
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 2 登記時間（#複查：比照歷史 inline 靠右、單行）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3 行尾播音鈕（Issue #56）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 4 行尾麥克風錄音鈕（spec#10）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 5 行尾發音成績框（spec#10）

        // #131 → v1.0.1（USR 回饋）：拖曳握把改用 Google Material「drag_pan」四向移動向量圖示（取代原「≡」字元）。
        // 以 Path 繪製（Material Symbols 960 網格、Stretch 縮放至握把尺寸）；外包透明底 Border 擴大命中區、窄握把也好抓。
        var handle = new System.Windows.Controls.Border
        {
            Child = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(
                    "M480-80 310-250l57-57 73 73v-206H235l73 72-58 58L80-480l169-169 57 57-72 72h206v-206l-73 73-57-57 170-170 170 170-57 57-73-73v206h205l-73-72 58-58 170 170-170 170-57-57 73-73H520v205l72-73 58 58L480-80Z"),
                Fill = Brush("#C77D9A"),
                Stretch = System.Windows.Media.Stretch.Uniform,
                Width = 19,
                Height = 19,
            },
            Background = System.Windows.Media.Brushes.Transparent, // 整塊（含留白）可命中，便於抓取
            Cursor = Cursors.SizeAll, // 四向移動：可上下排序亦可拖入左樹資料夾（Issue #46）
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 3, 2, 3),
            Margin = new Thickness(2, 0, 8, 0),
            ToolTip = "拖曳可重新排序／拖曳到資料夾可移動",
        };
        // #130：按下即擷取滑鼠——指標離開窄握把後仍收 PreviewMouseMove，直接往左橫移至左樹資料夾亦能起拖
        //（原先未擷取，往左一離開握把即收不到移動事件、門檻跨不過，須先上下拖曳才生效）。
        handle.PreviewMouseLeftButtonDown += (_, ev) => { _entryDrag = entry; _entryStart = ev.GetPosition(null); handle.CaptureMouse(); };
        handle.PreviewMouseLeftButtonUp += (_, _) => { _entryDrag = null; handle.ReleaseMouseCapture(); }; // 放開即清＋釋放擷取（對稱防殘留）
        handle.PreviewMouseMove += OnEntryHandleMove;
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Original) ? "（無內容）" : entry.Original,
            FontSize = EntryDisplaySettings.FontSize, // #複查：選項頁「條目顯示」可調字級/粗體/換行
            FontWeight = EntryDisplaySettings.Bold ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal,
            Foreground = Brush("#3A2C33"),
            TextWrapping = EntryDisplaySettings.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = EntryDisplaySettings.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // 登記時間靠右欄（Issue #104；#123：兩頁統一、日期縮小、改兩行 date/time 省寬——共用 EntryTimeCell）
        // AddedAt == default（極舊資料無值）不顯示、不佔位
        if (entry.AddedAt != default)
        {
            var time = EntryTimeCell.Build(entry.AddedAt.ToLocalTime());
            Grid.SetColumn(time, 2);
            grid.Children.Add(time);
        }

        // 行尾播音鈕（Issue #56）：最高頻動作一鍵可達，其餘（檢視/底色/刪除）仍走右鍵選單。
        // 圓鈕（RoundIcon）明示可按；Click 自處理、不冒泡至卡片（單擊播音不觸發雙擊檢視、不啟動拖曳）。
        var playBtn = RoundButton("", "播音", fg: "#2F6FED", bg: "#EAF1FE", border: "#CFE0FB"); // MDL2 Play
        playBtn.Click += (_, _) => _speech()?.Speak(entry.Original, "en-US", stopPrevious: true);
        Grid.SetColumn(playBtn, 3);
        grid.Children.Add(playBtn);

        // 行尾發音練習＝麥克風圓鈕（動作）＋成績框（狀態），取代舊燈泡（spec#10）。
        // 麥克風：按住錄音（轉紅）／放開送 AI 評分；成績框：未練/最佳分(<門檻紅框、≥門檻綠底✓)/錄音音量條/評分 spinner/得分閃分。
        // 指標事件由麥克風 Button 自處理、e.Handled 阻冒泡（不觸發播音/雙擊檢視/拖曳）。
        var scoreBox = new PracticeScoreBox();
        scoreBox.SetThreshold(_threshold());
        scoreBox.ShowBest(entry.PracticeScore);

        var micBtn = RoundButton("", // MDL2 Microphone
            "按住錄音，放開即檢查發音", fg: MicFg, bg: MicBg, border: MicBorder);
        var cell = new PracticeCell { Entry = entry, Mic = micBtn, Box = scoreBox, Card = card };
        micBtn.Tag = cell;
        micBtn.PreviewMouseLeftButtonDown += OnMicDown;
        micBtn.PreviewMouseLeftButtonUp += OnMicUp;
        micBtn.LostMouseCapture += OnMicLostCapture; // 擷取被搶走（alt-tab／他處彈窗）亦收束錄音、不卡在上限
        Grid.SetColumn(micBtn, 4);
        grid.Children.Add(micBtn);

        Grid.SetColumn(scoreBox, 5);
        grid.Children.Add(scoreBox);

        card.Child = grid;
        card.Tag = entry;
        card.ContextMenu = MakeEntryMenu(entry, card);
        card.ToolTip = "雙擊檢視；右鍵可設定底色／刪除"; // View 項移除後之可發現性提示（#106 §G）
        card.MouseLeftButtonDown += (_, e) =>
        {
            _selector.Select(card); // 單擊即選取（#110；不設 Handled——雙擊首擊選取、再擊開檢視）
            card.Focus();           // v1.0.1：取鍵盤焦點，使 Delete 鍵路由至本卡（刪除本條目）
            if (e.ClickCount == 2) // 雙擊＝檢視（Windows 清單慣例）
            {
                ViewRequested?.Invoke(entry);
                e.Handled = true;
            }
        };
        return card;
    }

    // 麥克風圓鈕配色（閒置＝與播音同款藍／錄音中轉紅）
    private const string MicFg = "#2F6FED", MicBg = "#EAF1FE", MicBorder = "#CFE0FB";
    private const string MicRecFg = "#D64545", MicRecBg = "#FCE4E4", MicRecBorder = "#D64545";

    /// <summary>一張筆記卡之發音練習元件繫結（麥克風鈕＋成績框＋條目＋卡片）；供指標事件回查對應成績框，
    /// 卡片參照供評分達標當下就地點亮（#111 星紋→#118 透明底）。</summary>
    private sealed class PracticeCell
    {
        public required NoteEntry Entry { get; init; }
        public required Button Mic { get; init; }
        public required PracticeScoreBox Box { get; init; }
        public required Border Card { get; init; }
    }

    /// <summary>建立卡片行尾圓形圖示鈕（RoundIcon 樣式；播音／麥克風同款、明示可按）。</summary>
    private Button RoundButton(string glyph, string tooltip, string fg, string bg, string border) => new()
    {
        Style = (System.Windows.Style)FindResource("RoundIcon"),
        Content = glyph,
        Foreground = Brush(fg),
        Background = Brush(bg),
        BorderBrush = Brush(border),
        Margin = new Thickness(1, 0, 1, 0),
        ToolTip = tooltip,
    };

    /// <summary>依錄音態設定麥克風圓鈕外觀（錄音中轉紅）。</summary>
    private static void SetMic(Button mic, bool recording)
    {
        mic.Foreground = Brush(recording ? MicRecFg : MicFg);
        mic.Background = Brush(recording ? MicRecBg : MicBg);
        mic.BorderBrush = Brush(recording ? MicRecBorder : MicBorder);
    }

    /// <summary>目前 _data 中該條目之最佳分（-1＝未練）；用於評分後回落與失敗復原。</summary>
    private int CurrentScore(string entryId)
    {
        foreach (var f in NotesStore.AllFolders(_data))
        {
            var e = f.Entries.FirstOrDefault(x => x.Id == entryId);
            if (e is not null)
            {
                return e.PracticeScore;
            }
        }
        return -1;
    }

    /// <summary>按住麥克風＝開始錄音（spec#10）；無麥克風／未授權／忙碌各自處理。e.Handled 阻冒泡。錄音期間即時音量→成績框音量條。</summary>
    private void OnMicDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // 不冒泡至卡片（不觸發雙擊檢視、不啟動拖曳）
        if (_practiceBusy || _recording is not null || sender is not Button btn || btn.Tag is not PracticeCell cell)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(cell.Entry.Original))
        {
            ToastNotifier.Show("沒有可練習的內容");
            return;
        }
        var rec = _recorderFactory();
        var start = rec.Start();
        if (start != RecordStart.Ok)
        {
            (rec as IDisposable)?.Dispose();
            NotifyFail(cell, start switch
            {
                RecordStart.NoDevice => "找不到麥克風",
                RecordStart.PermissionDenied => "請在 Windows 隱私權設定中允許麥克風存取",
                _ => "無法開始錄音",
            });
            return;
        }
        _recording = rec;
        _levelHandler = v => Dispatcher.BeginInvoke(new Action(() => cell.Box.SetLevel(v)));
        rec.LevelChanged += _levelHandler; // 即時音量→藍色音量條（marshal 回 UI 執行緒）
        btn.CaptureMouse(); // 放開若移出按鈕仍能收到 up
        SetMic(btn, recording: true);
        cell.Box.ShowRecording();
        _activeCell = cell; // 記錄進行中之卡片，供樹重建時收束（§5 #1）
        _maxRecTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(NaudioRecorder.MaxRecordMs) };
        _maxRecTimer.Tick += (_, _) => FinishRecording(cell);
        _maxRecTimer.Start();
    }

    /// <summary>放開麥克風＝停止錄音並送評分（spec#10）。</summary>
    private void OnMicUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is PracticeCell cell)
        {
            FinishRecording(cell); // 擷取於 FinishRecording 內釋放
        }
    }

    /// <summary>擷取意外遺失（alt-tab／他處彈窗奪焦）→ 視同放開、收束錄音，避免卡在錄音態直到上限（spec#10）。</summary>
    private void OnMicLostCapture(object sender, MouseEventArgs e)
    {
        if (_recording is not null && sender is Button btn && btn.Tag is PracticeCell cell)
        {
            FinishRecording(cell);
        }
    }

    /// <summary>結束錄音 → 送 AI 評分 → 成績框閃該次分再回落最佳分、toast 明示分數/門檻或失敗訊息（spec#10）。太短/無音/失敗各自降級回前態。</summary>
    private async void FinishRecording(PracticeCell cell)
    {
        _maxRecTimer?.Stop();
        _maxRecTimer = null;
        var rec = _recording;
        if (rec is null)
        {
            return; // 已結束（上限守衛與放開競態時只跑一次）
        }
        _recording = null;
        _activeCell = null;
        if (_levelHandler is not null)
        {
            rec.LevelChanged -= _levelHandler; // 退訂即時音量
            _levelHandler = null;
        }
        if (cell.Mic.IsMouseCaptured) { cell.Mic.ReleaseMouseCapture(); }
        var wav = rec.Stop(out var tooShort);
        (rec as IDisposable)?.Dispose();
        SetMic(cell.Mic, recording: false);
        if (wav is null)
        {
            RestoreBox(cell); // 太短/無音 → 成績框回前態（含 <MinRecordMs 之放開）
            if (tooShort)
            {
                NotifyFail(cell, "錄音太短");
            }
            else
            {
                NotifyFail(cell, "沒有可評分的錄音。");
            }
            return;
        }
        var assessor = _assessor();
        if (assessor is null)
        {
            RestoreBox(cell);
            NotifyFail(cell, "請設定 OpenAI 金鑰以評分發音");
            return;
        }
        _practiceBusy = true;
        cell.Box.ShowScoring(); // 評分中 spinner 轉圈
        try
        {
            var result = await assessor.AssessAsync(wav, cell.Entry.Original);
            var threshold = _threshold();
            NotesStore.SetPracticeScore(_data, cell.Entry.Id, result.Score); // 取最佳分
            _store.Save(_data);
            if (IsBoxLive(cell.Box))
            {
                cell.Box.FlashScore(result.Score, CurrentScore(cell.Entry.Id)); // 閃這次分 → 回落最佳分
                // v1.0.1（USR 回饋）：條目卡底色改為統一「可調透明度」、與過關無關 → 達標不再改底（過關仍由綠色成績框表示）
            }
            else
            {
                RenderFolder(); // 樹於評分間被重建（§5 #1）→ 以最新最佳分重繪目前夾
            }
            NotifyResult(cell, result.Score, threshold, result.Note); // 系統通知：標題含目標句、內文分數/門檻/建議
        }
        catch (QueryException ex)
        {
            RestoreBox(cell);
            var offline = ex.Message.Contains("Network", StringComparison.OrdinalIgnoreCase)
                          || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
            NotifyFail(cell, offline ? "沒有網路——發音評分需要連線" : "評分失敗：" + ex.Message);
        }
        catch (Exception)
        {
            // 非預期例外（含 async void）→ 復原並提示，不使 UI 執行緒崩潰
            RestoreBox(cell);
            NotifyFail(cell, "評分失敗，請再試一次");
        }
        finally
        {
            _practiceBusy = false;
        }
    }

    /// <summary>
    /// 中止進行中的錄音（樹將重建時呼叫，§5 #1）：停上限計時器、退訂即時音量、停並棄置錄音器、釋放滑鼠擷取、
    /// 麥克風復位。評分中之 <c>await</c> 無法中止，但其分數以條目 Id 寫回仍正確；此處只確保錄音硬體與計時器不外洩、
    /// 麥克風不卡在紅色。無進行中錄音時為無為。
    /// </summary>
    private void CancelActivePractice()
    {
        _maxRecTimer?.Stop();
        _maxRecTimer = null;
        var rec = _recording;
        if (rec is not null)
        {
            _recording = null;
            if (_levelHandler is not null)
            {
                rec.LevelChanged -= _levelHandler;
                _levelHandler = null;
            }
            try { rec.Stop(out _); } catch { /* 停止失敗不致命 */ }
            (rec as IDisposable)?.Dispose();
        }
        if (_activeCell is not null)
        {
            if (_activeCell.Mic.IsMouseCaptured) { _activeCell.Mic.ReleaseMouseCapture(); }
            SetMic(_activeCell.Mic, recording: false);
            _activeCell = null;
        }
    }

    /// <summary>成績框仍在畫面上（未因樹重建而卸離）才回前態；已卸離則跳過，重建之新框已反映正確狀態。</summary>
    private void RestoreBox(PracticeCell cell)
    {
        if (IsBoxLive(cell.Box))
        {
            cell.Box.ShowBest(CurrentScore(cell.Entry.Id));
        }
    }

    /// <summary>成績框是否仍連於呈現中的視覺樹（樹重建後舊框卸離 → 回 false）。</summary>
    private static bool IsBoxLive(PracticeScoreBox box) => System.Windows.PresentationSource.FromVisual(box) is not null;

    /// <summary>發音評分結果之系統通知（#101）：標題含目標句、內文含分數/門檻/過不過＋建議。</summary>
    private void NotifyResult(PracticeCell cell, int score, int threshold, string note)
    {
        var (title, body) = PronNotify.Result(cell.Entry.Original, score, threshold, note);
        _notify.Show(title, body);
    }

    /// <summary>發音練習失敗態之系統通知（#101）：標題含目標句、內文為明確訊息。</summary>
    private void NotifyFail(PracticeCell cell, string message)
    {
        var (title, body) = PronNotify.Failure(cell.Entry.Original, message);
        _notify.Show(title, body);
    }

    /// <summary>
    /// 條目右鍵選單（Issue #106 收斂為單層）：色塊選項平鋪（No Color＋粉彩盤、目前色打勾）＋分隔線＋ Delete。
    /// 播音走行尾鈕、檢視走雙擊，不再入選單（原 ▶ Play／View／Color ▸ 子選單制廢止）。
    /// </summary>
    private ContextMenu MakeEntryMenu(NoteEntry entry, Border card)
    {
        var menu = new ContextMenu();

        // 編輯原文（複查回饋：辨識有誤時校正、自動重譯更新中文）
        // 直接捕捉 card；MenuItem.Parent 對 ContextMenu 頂層項常回 null，不可靠。
        var edit = new MenuItem { Header = "編輯文字" };
        edit.Click += (_, _) => BeginEntryEdit(card, entry);
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());

        menu.Items.Add(ColorItem(entry, "無底色", ""));
        foreach (var (name, hex) in NoteColors.Palette) // 集中色盤（Issue #55）
        {
            menu.Items.Add(ColorItem(entry, name, hex));
        }

        var delete = new MenuItem { Header = "刪除", Foreground = Brush("#B23B3B") };
        delete.Click += (_, _) => { NotesStore.RemoveEntry(_data, entry.Id); Persist(); };

        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>編輯條目原文後自動重譯（複查回饋）：Save 交 App 重查更新、Cancel 還原。</summary>
    public event Action<string, string>? EntryEditRequested;

    private void BeginEntryEdit(Border card, NoteEntry entry)
    {
        var box = new TextBox
        {
            Text = entry.Original,
            FontSize = EntryDisplaySettings.FontSize,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(4),
        };
        var save = new Button
        {
            Content = "儲存並重新翻譯", Padding = new Thickness(10, 4, 10, 4),
            Background = Brush("#F4C2D0"), Foreground = Brush("#6D3A4D"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        save.Click += (_, _) => EntryEditRequested?.Invoke(entry.Id, box.Text); // App 重譯後 Reload 重建卡片
        var cancel = new Button
        {
            Content = "取消", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4),
            Background = Brush("#66FFFFFF"), Foreground = Brush("#6D3A4D"),
            BorderBrush = Brush("#E4B7C6"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        cancel.Click += (_, _) => RenderFolder(); // 取消：重建卡片還原
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(save);
        row.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(row);
        card.Child = panel;
        box.Focus();
        box.SelectAll();
    }

    private MenuItem ColorItem(NoteEntry entry, string name, string hex)
    {
        var item = new MenuItem
        {
            Header = NoteColors.DisplayName(name),
            IsCheckable = false,
            Icon = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                Background = SafeBrush(hex, "#FFFFFF"),
                BorderBrush = Brush("#D8B4C2"),
                BorderThickness = new Thickness(1),
            },
        };
        if ((entry.Color ?? "") == hex)
        {
            item.FontWeight = System.Windows.FontWeights.SemiBold;
            item.Header = NoteColors.DisplayName(name) + "　✓"; // 目前色標記
        }
        item.Click += (_, _) =>
        {
            NotesStore.SetEntryColor(_data, entry.Id, hex);
            Persist();
        };
        return item;
    }

    /// <summary>hex 轉筆刷；空或無效值回退預設色（舊檔容錯）。</summary>
    private static SolidColorBrush SafeBrush(string? hex, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { return Brush(hex!); }
            catch { /* 無效色值回退 */ }
        }
        return Brush(fallback);
    }

    private void OnEntryHandleMove(object sender, MouseEventArgs e)
    {
        if (_entryDrag is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _entryStart.X) < 6 && Math.Abs(p.Y - _entryStart.Y) < 6)
        {
            return;
        }
        var moving = _entryDrag;
        _entryDrag = null;
        (sender as UIElement)?.ReleaseMouseCapture(); // #130：起拖前釋放握把擷取，讓 DoDragDrop 自行接管拖放
        DragDrop.DoDragDrop(EntryPanel, new DataObject(FmtEntry, moving.Id), DragDropEffects.Move);
        HideInsertLine();  // 拖曳結束（含取消／落在樹側）清除指示線
        StopEdgeScroll();  // #128：拖曳結束（放開/Esc/取消）即停自動捲動
    }

    /// <summary>條目拖曳滑過右側清單 → 於預定落點顯示插入位置指示線（標準拖放回饋）。</summary>
    private void OnEntryAreaDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(FmtEntry))
        {
            e.Effects = DragDropEffects.None; // 資料夾拖入右側無意義 → 標準「不可放置」回饋
            e.Handled = true;
            StopEdgeScroll();
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ShowInsertLine(SlotIndex(e.GetPosition(EntryPanel).Y));
        UpdateEdgeScroll(e.GetPosition(EntryScroll).Y); // #128：以視口 Y 判上/下緣感應帶、驅動自動捲動
    }

    // 條目落下 → 同夾排序（插入槽位所見即所得；來自他夾之條目交由左側資料夾 drop 處理移動）
    private void OnEntryAreaDrop(object sender, DragEventArgs e)
    {
        HideInsertLine();
        var f = Selected;
        if (f is null || !e.Data.GetDataPresent(FmtEntry) || e.Data.GetData(FmtEntry) is not string eid)
        {
            return;
        }
        int slot = SlotIndex(e.GetPosition(EntryPanel).Y); // 顯示槽位（依 EntryPanel 子項＝目前投影序）
        // #126：投影模式（字母/日期或反向自訂）下拖曳 → 先把目前顯示序 materialize 入 f.Entries 並切「自訂」，
        // 使儲存序＝顯示序、from/slot 索引一致、拖曳所見即所得（檔案總管慣例、不彈回）。
        MaterializeToManual(f);
        int from = f.Entries.FindIndex(x => x.Id == eid);
        if (from < 0)
        {
            return;
        }
        int to = slot > from ? slot - 1 : slot; // 插入槽位 → 最終索引（移除自身後前移一位）
        NotesStore.Reorder(f, from, to);
        _store.Save(_data);
        RenderFolder();
        e.Handled = true;
    }

    // ---- #128：拖曳邊緣自動捲動（指標近清單上/下緣時 ScrollViewer 自動捲動、長清單重排免中斷） ----

    /// <summary>
    /// 依指標於 <see cref="EntryScroll"/> 視口之 Y 判上/下緣感應帶（≈16px），設每 tick 捲動量（速度隨貼近遞增）並驅動計時器；
    /// 另立即捲一步（`DragOver` 驅動、不依賴計時器於拖放 modal 迴圈是否 tick）。以 `DragOver` 視口 Y 判定、非靠 `DragLeave` 邊緣抖動。
    /// </summary>
    private void UpdateEdgeScroll(double viewportY)
    {
        const double band = 16;    // 感應帶厚（px）
        const double maxStep = 22; // 最大每 tick 捲動量（px；約 1–2 行高）
        double h = EntryScroll.ActualHeight;
        double delta = 0;
        if (viewportY < band)
        {
            delta = -maxStep * (1 - viewportY / band);      // 近頂 → 上捲，越近越快
        }
        else if (viewportY > h - band)
        {
            delta = maxStep * (1 - (h - viewportY) / band); // 近底 → 下捲
        }
        _edgeScrollDelta = delta;
        if (delta == 0)
        {
            StopEdgeScroll();
            return;
        }
        EntryScroll.ScrollToVerticalOffset(EntryScroll.VerticalOffset + delta); // 立即捲一步（自動鉗制 0..ScrollableHeight）
        if (_edgeScrollTimer is null)
        {
            _edgeScrollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _edgeScrollTimer.Tick += OnEdgeScrollTick;
        }
        _edgeScrollTimer.Start();
    }

    private void OnEdgeScrollTick(object? sender, EventArgs e)
    {
        if (_edgeScrollDelta == 0)
        {
            StopEdgeScroll();
            return;
        }
        EntryScroll.ScrollToVerticalOffset(EntryScroll.VerticalOffset + _edgeScrollDelta); // 到頂/底 ScrollViewer 自動鉗制不再捲
    }

    /// <summary>停止拖曳邊緣自動捲動（放開/離開清單/Esc/離開感應帶；不殘留計時器）。</summary>
    private void StopEdgeScroll()
    {
        _edgeScrollTimer?.Stop();
        _edgeScrollDelta = 0;
    }

    /// <summary>以 Y 座標求插入槽位（0..Count）：每列以中線分上下半。</summary>
    private int SlotIndex(double y)
    {
        for (int i = 0; i < EntryPanel.Children.Count; i++)
        {
            var c = (FrameworkElement)EntryPanel.Children[i];
            double top = c.TranslatePoint(new Point(0, 0), EntryPanel).Y;
            if (y < top + c.ActualHeight / 2)
            {
                return i;
            }
        }
        return EntryPanel.Children.Count;
    }

    private void ShowInsertLine(int slot)
    {
        if (_insertLine is null)
        {
            var layer = AdornerLayer.GetAdornerLayer(EntryPanel);
            if (layer is null)
            {
                return;
            }
            _insertLine = new InsertionAdorner(EntryPanel);
            layer.Add(_insertLine);
        }
        double y = 2;
        int n = EntryPanel.Children.Count;
        if (n > 0)
        {
            if (slot < n)
            {
                var c = (FrameworkElement)EntryPanel.Children[slot];
                y = c.TranslatePoint(new Point(0, 0), EntryPanel).Y - 4; // 落點列上緣（含列距中線）
            }
            else
            {
                var last = (FrameworkElement)EntryPanel.Children[n - 1];
                y = last.TranslatePoint(new Point(0, 0), EntryPanel).Y + last.ActualHeight + 4;
            }
        }
        _insertLine.SetY(y);
    }

    private void HideInsertLine()
    {
        if (_insertLine is not null)
        {
            AdornerLayer.GetAdornerLayer(EntryPanel)?.Remove(_insertLine);
            _insertLine = null;
        }
    }

    /// <summary>插入位置指示線（WPF 標準拖放回饋模式：Adorner 疊加水平線＋左端圓點）。</summary>
    private sealed class InsertionAdorner : Adorner
    {
        private static readonly SolidColorBrush LineBrush = new(Color.FromRgb(0x2F, 0x6F, 0xED));
        private static readonly Pen LinePen = new(LineBrush, 2.5);
        private double _y;

        static InsertionAdorner()
        {
            LineBrush.Freeze();
            LinePen.Freeze();
        }

        public InsertionAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

        public void SetY(double y)
        {
            if (Math.Abs(_y - y) > 0.5)
            {
                _y = y;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var width = ((FrameworkElement)AdornedElement).ActualWidth;
            dc.DrawEllipse(LineBrush, null, new Point(3, _y), 3.5, 3.5);
            dc.DrawLine(LinePen, new Point(6, _y), new Point(width, _y));
        }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
