using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LingoIsland.Capture;
using LingoIsland.Present;
using LingoIsland.Query;
using LingoIsland.Video;
using WinForms = System.Windows.Forms;

namespace LingoIsland;

/// <summary>
/// 應用進入點：系統匣常駐（無主視窗自動啟動），全域喚起快捷鍵喚起 capture→query→present 主動線。
/// 維運/檢視整合於單一 Office 式主視窗 <see cref="MainWindow"/>（筆記／歷史／選項／關於分頁，Issue #34），
/// 取代原 DockWindow／HistoryWindow／NotesWindow／SettingsWindow。
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instanceGuard;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _keyStatusItem;
    private MainWindow? _main;
    private VideoCapturePage? _videoPage; // 影片頁（設定變更後即時套用字幕帶字級/粗體）
    private EbookPage? _ebookPage;        // 電子書頁（#229，spec#4/#5/#6：匯入書櫃）
    private NotesPage? _notesPage;
    private HistoryPage? _historyPage;
    private ThemeManagementPage? _themePage;
    private ScreenCapturePage? _capturePage;
    private OptionsPage? _optionsPage;
    private HotKeyService? _hotkey;
    private HotkeyListenGuard? _listenGuard; // 指定快捷鍵監聽期間暫停/恢復全域熱鍵（Issue #89）
    private ISpeechService? _speech;
    private IPronunciationAssessor? _assessor;   // 發音評分（spec#10；金鑰於呼叫時讀、隨設定重建）
    private AppConfig _config = new("gpt-4o-mini", 15, "");
    private bool _busy;
    private DictionaryWindow? _dictionaryWindow; // 獨立字典視窗（v1.0.1：取代 #135 併入主視窗之 Dictionary 分頁；修筆記練習被打斷）
    private readonly HistoryStore _historyStore = new();
    private readonly NotesStore _notesStore = new();
    private readonly ThemeStore _themeStore = new();
    private readonly ScreenshotStore _screenshotStore = new(); // epic #145 增量3：截圖持久化
    private readonly VideoStore _videoStore = new();           // epic #145 增量4：影片清單
    private readonly EbookStore _ebookStore = new();           // #229：電子書書櫃（spec#5/#6；比照 VideoStore）
    private readonly INotificationService _notify = new WinToastNotificationService(); // 發音回饋系統通知（#101）
    private UpdateService? _updates;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "LingoIsland-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _instanceGuard = SingleInstanceGuard.Acquire();
        if (!_instanceGuard.IsFirstInstance)
        {
            System.Windows.MessageBox.Show("LingoIsland 已在執行中（請見系統匣圖示）。", "LingoIsland");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;

        // 設定檔在 %APPDATA%（Issue #51 遷居：Velopack 更新換置版本目錄，存 exe 旁會失）；exe 旁舊檔一次性遷移
        _config = AppConfig.Load(AppConfig.ResolveSettingsPath(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"), AppConfig.SettingsPath));
        NoteDefaults.Load(); // 筆記加入預設（資料夾/底色/智能配色規則，Issue #55）
        EntryDisplaySettings.SyncFrom(_config); // #複查：條目顯示偏好（字級/粗體/換行）自 config 同步
        ResultDisplaySettings.SyncFrom(_config); // #複查：查詢結果視窗基準字級自 config 同步
        SubtitleDisplaySettings.SyncFrom(_config); // 影片頁字幕帶字級/粗體自 config 同步（比照筆記）
        var keyReady = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        _speech = new SpeechService(_config.Voice);

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(WinForms.SystemInformation.SmallIconSize),
            Visible = true,
            Text = TrayText(),
        };
        var menu = new WinForms.ContextMenuStrip();
        _keyStatusItem = new WinForms.ToolStripMenuItem(AppStatusText.KeyStatus(keyReady)) { Enabled = false };
        menu.Items.Add(_keyStatusItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("開啟主視窗", null, (_, _) => OpenMain(MainTab.Notes));
        menu.Items.Add("字典", null, (_, _) => SummonResult()); // 喚出獨立字典視窗（顯示最近查詢；v1.0.1）
        menu.Items.Add("查詢歷史", null, (_, _) => OpenMain(MainTab.History));
        menu.Items.Add("我的筆記", null, (_, _) => OpenMain(MainTab.Notes));
        menu.Items.Add("擷取", null, (_, _) => OpenMain(MainTab.Capture)); // 系統匣「Capture」→螢幕截圖頁（epic #145 增量2）
        menu.Items.Add("選項", null, (_, _) => OpenMain(MainTab.Options));
        menu.Items.Add("關於", null, (_, _) => OpenMain(MainTab.About));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenMain(MainTab.Notes);

        // 分頁（UserControl）＋統一主視窗
        _assessor = new PronunciationService(_config.PronModel, _config.TimeoutSec, _config.MaxRetries); // 發音評分（spec#10）
        _notesPage = new NotesPage(_notesStore, () => _speech,
            () => _assessor, () => new NaudioRecorder(), () => _config.PronPassThreshold, _notify);
        _notesPage.ViewRequested += entry => ShowDetail(entry.ToResult());
        _notesPage.EntryEditRequested += (id, text) => _ = EditNoteEntryAsync(id, text); // 複查回饋：筆記編輯→重譯
        _notesPage.ImportConfirmed += RunNotesImport; // spec#14／#309：確認頁勾選之清單→逐字既有查詢→寫入目前選取夾
        _historyPage = new HistoryPage(_historyStore, () => _speech);
        _historyPage.ViewRequested += entry => ShowDetail(entry.ToResult());
        _historyPage.EntryEditRequested += (id, text) => _ = EditHistoryEntryAsync(id, text); // 複查回饋：歷史編輯→重譯
        _historyPage.AddToNotesRequested += entry => // 歷史「＋筆記」：套目前預設資料夾/底色（#55）
            AddToNotes(new NoteAddRequest(entry.ToResult(), NoteDefaults.FolderName, NoteDefaults.ColorHex));
        _optionsPage = new OptionsPage(_config);
        _optionsPage.SettingsChanged += ApplySettings;
        // 指定快捷鍵監聽期間暫停全域熱鍵、結束後依現行組態恢復（Issue #89）：
        // 避免監聽中按下現行鍵誤觸喚起，並使鍵盤組合不被 RegisterHotKey 攔截吞鍵而得正確擷取。
        _listenGuard = new HotkeyListenGuard(
            suspend: () => _hotkey?.Unregister(),
            resume: RegisterHotkeyOrWarn);
        _themeStore.LoadMigrated(_config.Context); // #14 單一主題提示相容遷移為一則命名主題
        _themePage = new ThemeManagementPage(_themeStore,
            bytes => new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries).DescribeImageAsync(bytes));
        _capturePage = new ScreenCapturePage(_config.Hotkey, _screenshotStore, _themeStore); // 快捷鍵初值（#133）＋截圖儲存（增量3）＋依 theme 篩選（B）
        // 喚起快捷鍵設定＋監聽暫停守衛＋手動擷取皆由螢幕截圖頁承載（#133／#5；epic #145 增量2 自主題頁拆出）
        _capturePage.ListeningChanged += _listenGuard.OnListeningChanged;
        _capturePage.HotkeyChanged += OnHotkeyChanged;
        _capturePage.CaptureRequested += TriggerManualCapture;

        _updates = new UpdateService();
        _updates.UpdateReady += v => Dispatcher.BeginInvoke(() => _main?.ShowUpdateReady(v));

        // 獨立字典視窗（v1.0.1）：查詢結果/查字典改回獨立視窗（取代 #135 併入主視窗之分頁），查詢/檢視/查單字/重譯皆導向本視窗之 Page。
        _dictionaryWindow = new DictionaryWindow();
        _dictionaryWindow.Page.AddToNotesRequested += AddToNotes;
        _dictionaryWindow.Page.WordQueryRequested += word => _ = LookupWordAsync(word);   // 雙擊單字＝查該字
        _dictionaryWindow.Page.TextReQueryRequested += text => _ = ReTranslateAsync(text); // 編輯原文→重譯
        _dictionaryWindow.Page.ManualQueryRequested += text => _ = ManualLookupAsync(text); // 頂部手動輸入查詢
        _dictionaryWindow.Page.HistoryRequested += RefreshDictionaryHistory; // 下拉開啟→以查詢歷史填入

        // 影片擷取分頁（#139，spec#2）：獲得（增量6′：單一輸入框貼影片＋字幕檔網址）→ 取字幕檔、自帶時間＋說話人直接解析建立字幕 → WebView2 導引播放到句暫停 → 暫停句點字沿用既有查詢、加入既有筆記。
        // 增量6′「輸入 pivot」：砍 finder／結果表後，已無 finder（ITranscriptVideoFinder）／關鍵字搜尋（IVideoSearcher）／內嵌探測（ISubtitleFetcher）／網路字幕欄（IWebTranscriptProbe）之注入。
        _videoPage = new VideoCapturePage(_videoStore,
            _themeStore, // 影片清單＋加入時記錄使用中主題（增量4）＋依 theme 篩選（B）
            new SubtitleStore(), // 字幕存檔：重開/重選同片還原、免重抓、保留說話人與 YAML 編修（#174）
            // epic #178 增量6′-B「時間 pivot」定案：字幕檔自帶時間＋說話人、免費直接解析載入；版面五花八門、免費解析讀不到時間時,以 AI 直接抽取（照網頁原有時間戳、非對齊/Whisper,故不亂序）。
            new OpenAiTranscriptAligner("gpt-4.1-mini", _config.TimeoutSec),
            () => _speech); // 字幕帶播音（USR）：委派取現行語音服務
        _videoPage.WordLookupRequested += LookupWordFromVideo;
        _videoPage.AddToNotesRequested += text => _ = AddVideoNoteAsync(text);
        _videoPage.AddSpeakerNotesRequested += AddSpeakerNotesToFolder; // 某說話人所有台詞原文→〔影片-說話人〕資料夾（免 AI，#189-checklist）
        _videoPage.ApplyThumbSize(_config.SearchThumbHeight); // 搜尋結果縮圖高度自 config 套用（選項頁可調，#複查）

        // 電子書擷取分頁（#229/#231，spec#4–#10）：獲得（選/拖 .epub → 逐檔 EbookReader.ParseAsync 預解析 → 批次 EbookStore.Add 匯入書櫃）＋
        // 書櫃（封面縮圖／主題篩選／四鍵排序／右鍵標記主題或刪除／清空）＋【內容】三欄逐段導讀閱讀器（切片2）。資料層（切片1）之
        // EbookReader/EbookContentReader（靜態）與 EbookStore（持久化，已納入 BackupService 整包備份）已就緒，此處注入 store／主題／語音服務並接線查詞/筆記（與影片頁共用既有後段）。
        _ebookPage = new EbookPage(_ebookStore, _themeStore, () => _speech); // 逐段 TTS：委派取現行語音服務（換聲後仍取到新實例）
        _ebookPage.WordLookupRequested += LookupWordFromVideo;               // 段落逐字點選→查該字（沿用影片頁同一動線：獨立字典視窗）
        _ebookPage.AddToNotesRequested += text => _ = AddVideoNoteAsync(text); // 當前段原文→重譯後入既有 NotesStore
        _ebookPage.AddSpeakerNotesRequested += AddSpeakerNotesToFolder;      // 某說話人全書段落原文→〔書名-說話人〕資料夾（App 端確認費用後逐句翻譯）

        // spec#12（#290）：主題存放區一併注入主視窗——主題變更之訂閱與派送只有主視窗一個落點，消費頁不自行訂閱。
        _main = new MainWindow(_themePage, _capturePage, _videoPage, _ebookPage, _notesPage, _historyPage, _optionsPage, new AboutPage(_updates), _themeStore);
        _main.RefreshStatus(keyReady, HotkeyDisplay());
        _main.ResultRequested += SummonResult; // 功能列「Dictionary」鈕→喚出獨立字典視窗（v1.0.1 恢復）
        _main.ExitRequested += ExitApp;        // 主視窗關閉(✕)→結束整個程式（v1.0.1：移除原「關閉＝收合」防關閉行為，USR 回饋）
        // 主視窗取得焦點不關結果卡片（Issue #105：與主視窗共存，關閉時機僅限使用者關閉／新查詢或檢視取代／選項儲存重建）
        // 啟動即顯示主視窗於影片頁（原為 Minimized 常駐；USR 回饋「開好可用」——縮匣不易察覺、易誤以為沒開）。仍可最小化(_)保留背景熱鍵、✕ 結束。
        _main.Show();
        _main.ShowTab(MainTab.Video);

        _hotkey = new HotKeyService();
        _hotkey.HotKeyPressed += OnHotKey;
        RegisterHotkeyOrWarn();

        // 啟動即背景檢查更新（Issue #51）：靜默下載、就緒才提示；未安裝形態/失敗皆靜默跳過
        _ = _updates.CheckAndDownloadAsync();
    }

    /// <summary>
    /// 明確結束常駐（主視窗 ✕／<c>Alt+F4</c>／系統匣「結束」三入口之匯流點）。
    /// <para>
    /// spec#11：**先過未存變更離開守衛**，取消即不結束。守衛必須掛在此處而非
    /// <c>MainWindow.OnClosing</c>——本方法先呼叫 <c>AllowClose()</c> 令 <c>_exiting</c> 為真，
    /// <c>OnClosing</c> 屆時會直接放行，掛在那裡會漏掉系統匣「結束」。
    /// </para>
    /// </summary>
    private void ExitApp()
    {
        if (_main is not null && !_main.ConfirmLeaveCurrentPage()) { return; }
        _main?.AllowClose();
        Shutdown();
    }

    private void RegisterHotkeyOrWarn()
    {
        var failed = new List<string>();
        if (_hotkey is not null && !_hotkey.Register(HotKeyBinding.Parse(_config.Hotkey)))
        {
            failed.Add(HotkeyDisplay());
        }
        if (failed.Count > 0)
        {
            System.Windows.MessageBox.Show(
                $"無法註冊快捷鍵「{string.Join("」、「", failed)}」（可能已被其他應用程式占用）。LingoIsland 仍會繼續執行；你可以在「選項」中變更快捷鍵。",
                "LingoIsland");
        }
    }

    private string HotkeyDisplay() => HotKeyBinding.Parse(_config.Hotkey).DisplayName;

    /// <summary>
    /// 擷取頁改定喚起快捷鍵後（#133：#3）：立即持久化並重註冊全域熱鍵、同步狀態列與系統匣。
    /// 以 <see cref="OptionsPage.SetConfig"/> 把新組態灌回選項頁快照——App 為 AppConfig 單一擁有者，
    /// 令選項頁 Gather 保留新快捷鍵、避免兩頁各自 Save 互相覆寫（單一 config 擁有者防覆寫）。
    /// </summary>
    private void OnHotkeyChanged(HotKeyBinding binding)
    {
        _config = _config with { Hotkey = binding.Serialize() }; // AppConfig 為 record，with 複製改單一欄位
        _config.Save(AppConfig.SettingsPath);
        RegisterHotkeyOrWarn();
        _optionsPage?.SetConfig(_config); // resync 選項頁快照，令其 Gather 保留新快捷鍵、避免兩頁各存 AppConfig 互相覆寫
        var keyReady = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        if (_tray is not null)
        {
            _tray.Text = TrayText(); // 系統匣提示同步新快捷鍵
        }
        _main?.RefreshStatus(keyReady, HotkeyDisplay());
    }

    private string TrayText() => AppStatusText.TrayTip(HotkeyDisplay());

    private static Icon LoadAppIcon(System.Drawing.Size size)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/assets/app.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                return new Icon(stream, size);
            }
        }
        catch { /* 資源缺失退回預設 */ }
        return SystemIcons.Application;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        try { File.WriteAllText(LogPath, DateTime.Now + "\n" + args.Exception); }
        catch { /* log 寫入失敗不致命 */ }
        System.Windows.MessageBox.Show(
            "發生錯誤（已記錄至 " + LogPath + "）：\n\n" + args.Exception.Message, "LingoIsland 錯誤");
        args.Handled = true;
    }

    /// <summary>手動觸發擷取（#133：#5 擷取頁「Capture Screen」鈕）：查詢進行中則忽略；否則先收合主視窗、
    /// <b>待其真正淡出後</b>再走既有喚起主動線——同步立即擷取會在最小化動畫／DWM 重組完成前就凍結桌面，
    /// 把主視窗自身烙進畫格、使用者反而選不到它原本遮住的區域（業界審查 #133 MAJOR）。</summary>
    private async void TriggerManualCapture()
    {
        if (_busy)
        {
            return; // 查詢進行中：不收合（否則視窗消失卻無擷取、無回饋），忽略本次手動觸發（審查 NIT③）
        }
        if (_main is not null)
        {
            _main.WindowState = WindowState.Minimized; // 先最小化（留工作列可還原、不致「消失」）
        }
        await Task.Delay(200); // 待最小化動畫／DWM 重組完成、視窗真正離開畫面後再擷取，免截到主視窗自身
        OnHotKey();            // 既有喚起主動線（遮罩擷取→查詢→結果）
    }

    /// <summary>喚起（第一熱鍵）：遮罩選區/雙擊點選截圖 → vision 查詢 → 結果視窗＋朗讀＋歷史留存。</summary>
    private async void OnHotKey()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            _dictionaryWindow?.Hide(); // 擷取前隱藏字典視窗，免遮罩凍結畫格截到它
            var mask = new MaskWindow();
            mask.ShowDialog();
            if (mask.Result is null)
            {
                return;
            }
            await RunQueryAsync(mask.Result);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("查詢錯誤：\n" + ex.Message, "LingoIsland");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>查詢主動線：Dictionary 分頁 loading → vision 查詢（依 <c>IsPointMode</c>）→ 結果/錯誤＋歷史留存（#135）。</summary>
    private async Task RunQueryAsync(CaptureResult capture)
    {
        _dictionaryWindow!.Page.SetNoteTargets(TopFolderNames(), ActiveThemeName()); // #55「加入至」下拉來源
        _dictionaryWindow.Page.ShowLoading();
        _dictionaryWindow.ShowAndActivate(); // 顯示獨立字典視窗（Topmost 疊於無邊框遊戲）

        // epic #145 增量3：保存本次截圖（不論查詢成敗），記錄擷取當下使用中主題（跨媒體主題歸屬）
        var capturedTheme = ThemeStore.GetActive(_themeStore.Load());
        _screenshotStore.Add(capture.PngBytes, capturedTheme?.Id, capturedTheme?.Name, DateTimeOffset.Now);
        _capturePage?.RefreshScreenshots();

        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries,
                _themeStore.ActiveText(), _themeStore.ActiveColorRules()); // 配色規則＝使用中情境各色描述（#69）
            var result = await query.QueryAsync(capture.PngBytes, capture.IsPointMode); // #54/#86 點選自動判斷
            if (!result.IsEmpty)
            {
                _historyStore.Append(result, _config.HistoryMax, DateTimeOffset.Now);
                _historyPage?.Reload();
            }
            _dictionaryWindow.Page.ShowResult(result, _speech!);
        }
        catch (QueryException ex)
        {
            _dictionaryWindow.Page.ShowError(ex.Message);
        }
    }

    /// <summary>單字查詢（Dictionary 分頁點單字，#135）：文字查該字義→推入分頁導航堆疊（可往前返回原句）；失敗以 toast 明訊、不破壞現有結果。</summary>
    private async Task LookupWordAsync(string word)
    {
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryWordAsync(word);
            _dictionaryWindow?.Page.PushWordResult(result); // 內含結束等待游標
        }
        catch (QueryException ex)
        {
            _dictionaryWindow?.Page.WordLookupFailed(); // 清等待游標＋忙碌旗標
            ToastNotifier.Show("單字查詢失敗：" + ex.Message);
        }
    }

    /// <summary>影片擷取頁點字幕單字（#139，spec#2）：顯示獨立字典視窗 loading，再沿用既有單字查詢主動線（來源改字幕文字）。</summary>
    private void LookupWordFromVideo(string word)
    {
        _dictionaryWindow?.Page.SetNoteTargets(TopFolderNames(), ActiveThemeName());
        _dictionaryWindow?.Page.ShowLoading();
        _dictionaryWindow?.ShowAndActivate();
        _ = LookupWordAsync(word);
    }

    /// <summary>影片擷取頁「加入我的筆記」（#139，spec#2）：整句重譯後入既有 NotesStore（沿用去重／資料夾／發音練習、共用不另造）。</summary>
    private async Task AddVideoNoteAsync(string english)
    {
        var t = (english ?? "").Trim();
        if (t.Length == 0)
        {
            return;
        }
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryTextAsync(t);
            AddToNotes(new NoteAddRequest(result, NoteDefaults.FolderName, NoteDefaults.ColorHex));
        }
        catch (QueryException ex)
        {
            ToastNotifier.Show("加入筆記失敗：" + ex.Message);
        }
    }

    /// <summary>
    /// 某說話人所有台詞批次翻譯後收藏至〔影片-說話人〕資料夾（#189-checklist USR 修：提醒費用＋逐句 AI 翻譯）：
    /// 先跨全樹去重（免對已收錄者重複付費）→ 費用確認對話框（顯查詢數＝AI 呼叫數）→ 逐句 QueryTextAsync 翻譯、加入、存檔 → 更新筆記頁與下拉、toast 回報。
    /// </summary>
    private void AddSpeakerNotesToFolder(string folder, IReadOnlyList<string> lines)
    {
        var fresh = _notesStore.NewOriginals(lines); // 先去重,只譯尚未收錄者
        if (fresh.Count == 0) { ToastNotifier.Show($"所有台詞皆已在筆記「{folder}」中。"); return; }
        var ok = System.Windows.MessageBox.Show(
            $"要將 {fresh.Count} 句台詞加入「{folder}」嗎？\n\n每句台詞會以一次 AI 查詢進行翻譯——共 {fresh.Count} 次查詢，將使用你的 OpenAI 金鑰。是否繼續？",
            "將說話人台詞加入筆記", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.OK) { return; }

        // 進度表單（USR）：AiActionWindow 逐句 report 進度、可 Cancel 中止；同一視窗模態呈現、其訊息迴圈續泵故 async 照跑。
        var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
        int added = 0, failed = 0;
        AiActionWindow.RunAndShow(_main, $"正在將 {fresh.Count} 句台詞加入「{folder}」", async (report, ct) =>
        {
            for (var i = 0; i < fresh.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                report($"翻譯中 {i + 1}/{fresh.Count}：{Ellipsis(fresh[i], 42)}");
                try
                {
                    var r = await query.QueryTextAsync(fresh[i], ct);
                    if (_notesStore.AddToNamedFolderAndSave(r, folder, NoteDefaults.ColorHex, DateTimeOffset.Now) == NoteAddResult.Added) { added++; }
                }
                catch (QueryException) { failed++; }
            }
            report($"完成——已加入 {added}" + (failed > 0 ? $"，{failed} 句失敗" : ""));
            return null; // 無 token 用量回傳→不顯費用（前置對話框已提醒）
        }, autoCloseOnSuccess: false, showCost: false);

        _notesPage?.Reload();
        _dictionaryWindow?.Page.SetNoteTargets(TopFolderNames(), ActiveThemeName());
        if (added > 0) { ToastNotifier.Show($"✓ 已將 {added} 句翻譯後的台詞加入「{folder}」" + (failed > 0 ? $"（{failed} 句失敗）" : "")); }
    }

    private static string Ellipsis(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    /// <summary>
    /// 匯入清單之批次執行（spec#14／#309）：確認頁已揭露費用、此處不再問；以 <see cref="AiActionWindow"/> 逐字顯示進度可取消、
    /// <see cref="NotesImportRunner"/> 接既有 <see cref="QueryService"/>（單字查字義／片語整句翻譯，與字典頁手動查詢同規則）
    /// 寫入目前選取夾（含子夾）一字一存；結束顯示結果表（成功／略過／失敗逐字原因）、重載筆記頁與字典「加入至」下拉。
    /// </summary>
    private void RunNotesImport(string folderId, string folderName, IReadOnlyList<string> words)
    {
        if (words.Count == 0) { return; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            // 起跑前預檢（與字典頁「金鑰未設定時顯示明確錯誤與設定指引」同基準）：每個字都會失敗，不讓使用者等逾時
            System.Windows.MessageBox.Show(_main, "尚未設定 OPENAI_API_KEY，無法線上查詢。請先在系統匣選單「設定…」或環境變數設定金鑰，再匯入一次（清單檔不會有任何變動）。",
                "匯入清單", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
        var runner = new NotesImportRunner(_notesStore, NotesImportRunner.MakeLookup(query));
        NotesImportOutcome? outcome = null;
        AiActionWindow.RunAndShow(_main, $"正在匯入 {words.Count} 字到「{folderName}」", async (report, ct) =>
        {
            outcome = await runner.RunAsync(words, folderId, NoteDefaults.ColorHex, report, ct);
            var shownFolder = outcome.TargetFolderMissing ? NotesStore.DefaultFolderName : folderName;
            report(NotesImport.ResultText(outcome.Added, outcome.Updated, outcome.Skipped, outcome.Failed, shownFolder, outcome.AddedWords)
                   + (outcome.TargetFolderMissing ? $"\n（目標資料夾「{folderName}」在匯入途中已不存在，已改加到第一個資料夾「{NotesStore.DefaultFolderName}」。）" : "")
                   + (outcome.Cancelled ? "\n（已取消——已加入的字保留，其餘未查詢。）" : ""));
            return null; // 費用已於確認頁前置揭露；不顯用量
        }, autoCloseOnSuccess: false, showCost: false);

        _notesPage?.Reload();
        _dictionaryWindow?.Page.SetNoteTargets(TopFolderNames(), ActiveThemeName());
        if (outcome is not null && (outcome.Added > 0 || outcome.Updated > 0))
        {
            var shownFolder = outcome.TargetFolderMissing ? NotesStore.DefaultFolderName : folderName;
            ToastNotifier.Show($"✓ 已匯入 {outcome.Added} 字到「{shownFolder}」" + (outcome.Updated > 0 ? $"、更新 {outcome.Updated} 字" : "")
                               + (outcome.Failed.Count > 0 ? $"（{outcome.Failed.Count} 字失敗）" : ""));
        }
    }

    /// <summary>編輯筆記條目原文後重譯（複查回饋）：文字重查→更新該筆三欄（練習分數歸零）、存檔並重載筆記頁。空字串/失敗以 toast。</summary>
    private async Task EditNoteEntryAsync(string id, string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            _notesPage?.Reload();
            return;
        }
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryTextAsync(t);
            var data = _notesStore.LoadEnsured();
            if (NotesStore.UpdateEntryContent(data, id, result))
            {
                _notesStore.Save(data);
            }
        }
        catch (QueryException ex)
        {
            ToastNotifier.Show("重新翻譯失敗：" + ex.Message);
        }
        finally
        {
            _notesPage?.Reload(); // 成功以新內容重建、失敗還原原內容
        }
    }

    /// <summary>編輯歷史條目原文後重譯（複查回饋）：文字重查→更新該筆三欄、存檔並重載歷史頁。</summary>
    private async Task EditHistoryEntryAsync(string id, string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            _historyPage?.Reload();
            return;
        }
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryTextAsync(t);
            _historyStore.UpdateContent(id, result);
        }
        catch (QueryException ex)
        {
            ToastNotifier.Show("重新翻譯失敗：" + ex.Message);
        }
        finally
        {
            _historyPage?.Reload();
        }
    }

    /// <summary>編輯原文後重譯（辨識有誤時校正，#135）：文字重查→取代 Dictionary 分頁目前結果；失敗 toast、清游標。</summary>
    private async Task ReTranslateAsync(string text)
    {
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryTextAsync(text);
            _dictionaryWindow?.Page.ReplaceCurrentResult(result);
        }
        catch (QueryException ex)
        {
            _dictionaryWindow?.Page.WordLookupFailed();
            ToastNotifier.Show("重新翻譯失敗：" + ex.Message);
        }
    }

    /// <summary>Dictionary 分頁手動輸入查詢（#135）：單一 token（無空白）→查該字字義、否則整句翻譯；結果顯示於本頁。</summary>
    private async Task ManualLookupAsync(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            return;
        }
        _dictionaryWindow!.Page.SetNoteTargets(TopFolderNames(), ActiveThemeName());
        _dictionaryWindow.Page.ShowLoading();
        _dictionaryWindow.ShowAndActivate();
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            bool single = !t.Any(char.IsWhiteSpace);
            var result = single ? await query.QueryWordAsync(t) : await query.QueryTextAsync(t);
            _dictionaryWindow.Page.ShowResult(result, _speech!);
        }
        catch (QueryException ex)
        {
            _dictionaryWindow.Page.ShowError(ex.Message);
        }
    }

    /// <summary>刷新 Dictionary 分頁輸入下拉之查詢歷史（英文原文、新在前、去重；#135 回饋，下拉開啟時呼叫）。</summary>
    private void RefreshDictionaryHistory()
        => _dictionaryWindow?.Page.SetHistory(_historyStore.Load()
            .Select(h => h.ToResult().Original)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList());

    /// <summary>目前頂層資料夾名清單（供結果視窗「加入至」下拉，#55）。</summary>
    private List<string> TopFolderNames() => _notesStore.LoadEnsured().Folders.Select(f => f.Name).ToList();

    /// <summary>使用中情境名（空＝無使用中情境；供「加入至」預設夾解析與標籤，#55）。</summary>
    private string ActiveThemeName() => ThemeStore.GetActive(_themeStore.Load())?.Name ?? "";

    /// <summary>開啟統一主視窗並切到指定分頁（tray／入口）；結果卡片保留不關（Issue #105 與主視窗共存）。</summary>
    private void OpenMain(MainTab tab)
    {
        _main?.ShowTab(tab);
    }

    /// <summary>
    /// 加入我的筆記（去重）：結果視窗或自動加入觸發，加入至請求指定之資料夾並套底色（#55），右下角 toast 回饋（spec#7）。
    /// 資料夾名空 → 依使用中情境名（無情境則預設夾）解析。
    /// </summary>
    private void AddToNotes(NoteAddRequest req)
    {
        var folder = ResolveFolderName(req.FolderName);
        var msg = _notesStore.AddToNamedFolderAndSave(req.Result, folder, req.ColorHex, DateTimeOffset.Now) switch
        {
            NoteAddResult.Added => folder == NotesStore.DefaultFolderName ? "✓ 已加入我的筆記" : $"✓ 已加入「{folder}」",
            NoteAddResult.AlreadyExists => "已在筆記中",
            _ => "沒有可儲存的內容",
        };
        ToastNotifier.Show(msg);
        _notesPage?.Reload();
    }

    /// <summary>解析目標資料夾名（#55）：非空即固定夾；空則使用中情境名、無情境則預設夾。</summary>
    private string ResolveFolderName(string chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return chosen.Trim();
        }
        var ctx = ActiveThemeName();
        return ctx.Length > 0 ? ctx : NotesStore.DefaultFolderName;
    }

    /// <summary>「檢視」：於獨立字典視窗顯示三欄詳情（重用共用 ResultView；v1.0.1 改獨立視窗、**不動主視窗當前分頁**——修 #135 筆記練習被打斷）。</summary>
    private void ShowDetail(QueryResult r)
    {
        _dictionaryWindow!.Page.SetNoteTargets(TopFolderNames(), ActiveThemeName());
        _dictionaryWindow.Page.ShowResult(r, _speech!);
        _dictionaryWindow.ShowAndActivate();
    }

    /// <summary>
    /// 喚回查詢結果視窗（Issue #107；主視窗 Result 鈕與 tray「Result」項兩入口鏡像）三態：
    /// 現有卡（含最小化中）→先還原再帶前景（不新開）；無卡且有查詢歷史→以最新一筆走「檢視」路徑重開（單一守衛）；
    /// 無任何歷史（含清除全部後、歷史檔毀損退空）→toast 提示、不開卡。喚回語意＝重開最新查詢、非最後顯示內容。
    /// </summary>
    private void SummonResult()
    {
        if (_dictionaryWindow?.Page.HasResult == true)
        {
            _dictionaryWindow.ShowAndActivate(); // 已有結果 → 喚出獨立視窗帶前景
            return;
        }
        var latest = _historyStore.Load().FirstOrDefault();
        if (latest is null)
        {
            ToastNotifier.Show("尚無查詢結果");
            return;
        }
        ShowDetail(latest.ToResult());
    }

    /// <summary>選項分頁儲存後套用：重建語音服務、重註冊熱鍵、更新狀態；並把新語音注入 Dictionary 分頁（不續用已釋放服務，#135）。</summary>
    private void ApplySettings(AppConfig cfg)
    {
        _config = cfg;
        (_speech as IDisposable)?.Dispose();
        _speech = new SpeechService(_config.Voice);
        _dictionaryWindow?.Page.UpdateSpeech(_speech); // 換語音服務後同步字典視窗（播放鈕讀欄位、免用已釋放服務）
        _assessor = new PronunciationService(_config.PronModel, _config.TimeoutSec, _config.MaxRetries); // 隨模型/逾時重建（spec#10）
        EntryDisplaySettings.SyncFrom(_config); // #複查：條目字級/粗體/換行偏好同步後重建兩頁
        ResultDisplaySettings.SyncFrom(_config); // #複查/#135：Dictionary 分頁結果基準字級同步（下次渲染套用）
        SubtitleDisplaySettings.SyncFrom(_config); // 影片頁字幕帶字級/粗體同步
        _videoPage?.ApplySubtitleDisplay();        // 立即套用到字幕帶（即使當前句已顯示）
        _videoPage?.ApplyThumbSize(_config.SearchThumbHeight); // 搜尋結果縮圖高度即時套用（選項頁調整後，#複查）
        _notesPage?.Reload(); // 門檻/條目顯示改動 → 重建卡片（intTest#36）
        _historyPage?.Reload(); // #複查：條目顯示改動同步套用歷史頁
        RegisterHotkeyOrWarn();
        var keyReady = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        if (_tray is not null)
        {
            _tray.Text = TrayText();
        }
        if (_keyStatusItem is not null)
        {
            _keyStatusItem.Text = AppStatusText.KeyStatus(keyReady);
        }
        _main?.RefreshStatus(keyReady, HotkeyDisplay());
        _main?.FlashSaved(); // #125：儲存成功於狀態列輕量閃示「Saved ✓」（取代原「Saved.」模態框；含「存後離開」路徑）
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updates?.ApplyOnExit(); // 新版已就緒者結束時掛起套用（下次啟動即新版；無則 no-op）
        _main?.AllowClose();
        _dictionaryWindow?.AllowClose(); // 允許獨立字典視窗真正關閉（否則 OnClosing 攔為隱藏）
        _dictionaryWindow?.Close();
        _hotkey?.Dispose();
        (_speech as IDisposable)?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}
