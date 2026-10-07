using System.Reflection;
using System.Windows;
using LingoIsland.Query;
using CancelEventArgs = System.ComponentModel.CancelEventArgs;

namespace LingoIsland.Present;

/// <summary>統一主視窗之分頁（電子書分頁 #229 置於影片與筆記之間）。</summary>
public enum MainTab { Themes, Capture, Video, Ebook, Notes, History, Options, About }

/// <summary>
/// 統一 Office 式主視窗（Issue #34）：頂部功能列分頁（圖示＋文字）＋下方對應功能頁，取代原
/// DockWindow／HistoryWindow／NotesWindow／SettingsWindow 各獨立視窗。標準工作列視窗；
/// <b>關閉（✕）＝結束整個程式</b>（v1.0.1，USR 回饋，取代原「關閉＝收合」防關閉行為）；
/// 背景常駐／熱鍵請改用「最小化（_）」保留；淺粉底＋logo 背景。
/// </summary>
public partial class MainWindow : Window
{
    private bool _exiting; // true＝允許真正關閉。使用者按 ✕ 先觸發 ExitRequested→App 統一結束（AllowClose 設此旗標後 Shutdown）。

    /// <summary>功能列右上「Dictionary」鈕按下（v1.0.1 恢復）：本視窗僅發事件，喚出獨立字典視窗之決策在 App 組合根。</summary>
    public event Action? ResultRequested;

    /// <summary>使用者按主視窗關閉（✕）：請求結束整個常駐程式（v1.0.1，取代原「關閉＝收合」；由 App 走統一結束流程）。</summary>
    public event Action? ExitRequested;

    /// <summary>匯入進度列「取消」（#322）：按下即取消、不另確認（已加入者保留）。</summary>
    public event Action? ImportCancelRequested;

    /// <summary>已提示過「缺此語言語音」之 culture（#252）——每種語言只擾民一次。</summary>
    private readonly HashSet<string> _missingVoiceNotified = new(StringComparer.OrdinalIgnoreCase);

    private readonly ThemeManagementPage _themes;
    private readonly ScreenCapturePage _capture;
    private readonly VideoCapturePage _video;
    private readonly EbookPage _ebook;
    private readonly NotesPage _notes;
    private readonly HistoryPage _history;
    private readonly OptionsPage _options;
    private readonly AboutPage _about;

    public MainWindow(ThemeManagementPage themes, ScreenCapturePage capture, VideoCapturePage video, EbookPage ebook, NotesPage notes, HistoryPage history, OptionsPage options, AboutPage about, ThemeStore themeStore)
    {
        InitializeComponent();
        ImportProgressCancel.Click += (_, _) => ImportCancelRequested?.Invoke();
        _themes = themes;
        _capture = capture;
        _video = video;
        _ebook = ebook;
        _notes = notes;
        _history = history;
        _options = options;
        _about = about;

        // spec#12（#290）：主題變更之**唯一訂閱點**。#286 已把模型改為推送，但訂閱仍寫在各消費頁自己的
        // 建構式——新頁面漏訂閱不會報錯、只會靜默顯示舊配色，與 spec#11 所修之守衛同形。改為由本視窗
        // 單點訂閱、對所承載之頁反射枚舉後逐一派送；消費頁一律不自行訂閱。
        themeStore.Changed += () => Dispatcher.BeginInvoke(new Action(DispatchThemesChanged));

        // 缺語言語音之一次性告知（#252）：朗讀遇中文段而系統未裝中文語音時，該段以預設語音念（不略過），
        // 並在此提示一次加裝路徑——去抖責任在訂閱端（服務端每段都會通知、不自行記狀態）。
        SpeechService.MissingVoiceCulture += (_, culture) =>
        {
            if (!_missingVoiceNotified.Add(culture)) { return; }
            Dispatcher.BeginInvoke(() => ToastNotifier.Show(
                $"系統未安裝 {culture} 語音，該語言段落改以預設語音朗讀。\n可到「設定 → 語言 → 語音」加裝。"));
        };

        // 條目列數即時更新（#132）：頁內切夾/切日/增刪時，若該頁為當前分頁即更新底部狀態列。
        _notes.EntryCountChanged += n => { if (Host.Content == _notes) ShowEntryCount(n); };
        _history.EntryCountChanged += n => { if (Host.Content == _history) ShowEntryCount(n); };

        // 各分頁切換前先過「離開選項頁」守衛（#複查）：選項頁有未存變更時提示，取消則留在選項頁。
        // 切至筆記/歷史時於狀態列顯目前檢視條目數，其餘分頁隱藏（#132）。
        TabNotes.Checked += (_, _) => { if (_reselecting) { return; } if (!ConfirmLeaveCurrentPage()) { ReselectCurrentTab(); return; } _notes.Reload(); Host.Content = _notes; ShowEntryCount(_notes.CurrentEntryCount); };
        TabHistory.Checked += (_, _) => { if (_reselecting) { return; } if (!ConfirmLeaveCurrentPage()) { ReselectCurrentTab(); return; } _history.Reload(); Host.Content = _history; ShowEntryCount(_history.CurrentEntryCount); };
        TabThemes.Checked += (_, _) => { if (_reselecting) { return; } if (!ConfirmLeaveCurrentPage()) { ReselectCurrentTab(); return; } _themes.Reload(preferActive: true); Host.Content = _themes; ShowEntryCount(null); }; // USR：切到本頁預設選使用中主題
        TabCapture.Checked += (_, _) => { if (_reselecting) { return; } if (!ConfirmLeaveCurrentPage()) { ReselectCurrentTab(); return; } Host.Content = _capture; ShowEntryCount(null); };
        TabVideo.Checked += (_, _) => { if (_reselecting) { return; } if (!ConfirmLeaveCurrentPage()) { ReselectCurrentTab(); return; } Host.Content = _video; ShowEntryCount(null); };
        TabEbook.Checked += (_, _) => { if (_reselecting) { return; } if (!ConfirmLeaveCurrentPage()) { ReselectCurrentTab(); return; } Host.Content = _ebook; ShowEntryCount(null); }; // 電子書分頁（#229）：切入即由 EbookPage.IsVisibleChanged 重填主題篩選並重整書櫃
        TabOptions.Checked += (_, _) => { if (_reselecting) { return; } if (!ConfirmLeaveCurrentPage()) { ReselectCurrentTab(); return; } Host.Content = _options; ShowEntryCount(null); }; // spec#11：一般化後不再豁免；目標頁＝目前頁時守衛自然放行
        TabAbout.Checked += (_, _) => { if (_reselecting) { return; } if (!ConfirmLeaveCurrentPage()) { ReselectCurrentTab(); return; } Host.Content = _about; ShowEntryCount(null); };
        ResultBtn.Click += (_, _) => ResultRequested?.Invoke();

        _themes.AttachLeaveGuard(ConfirmLeaveCurrentPage); // spec#11：頁不認識視窗型別，以 Func<bool> 注入
        Host.Content = _notes; // 預設筆記分頁（XAML IsChecked 於接線前已設，故此處明確帶入）
        ShowEntryCount(_notes.CurrentEntryCount); // #132：初始筆記分頁條目數
    }

    /// <summary>
    /// 主題變更派送（spec#12，#290）：對**所承載之全部頁**逐一派送，**不判斷任何具名頁面型別**。
    /// 頁面清單自欄位反射取得而非寫死——寫死＝新增頁面漏列，等同回到「各頁自己記得訂閱」的老問題。
    /// </summary>
    [ThemeChangeDispatch]
    internal void DispatchThemesChanged()
    {
        // 逐頁例外隔離：單頁重算擲例外不得中斷派送，否則其後各頁靜默維持舊配色＝本件所修缺陷之再生。
        foreach (var page in HostedPages().OfType<IThemeConsumerPage>())
        {
            try { page.OnThemesChanged(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[spec#12] {page.GetType().Name}.OnThemesChanged 失敗：{ex.Message}"); }
        }
    }

    /// <summary>本視窗所承載之頁面實體（枚舉來源＝頁面欄位，與 spec#11／#12 之契約測試同一口徑）。</summary>
    private IEnumerable<object> HostedPages() =>
        GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => typeof(System.Windows.Controls.UserControl).IsAssignableFrom(f.FieldType))
            .Select(f => f.GetValue(this))
            .Where(v => v is not null)!;

    /// <summary>三選提示（spec#11 斷言③ 之 seam：可注入以供單元測試；預設仍為 WPF MessageBox、畫面不變）。</summary>
    internal Func<string, MessageBoxResult> AskLeave { get; set; } = body =>
        System.Windows.MessageBox.Show(body, "未儲存的變更", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

    /// <summary>
    /// 離開守衛（spec#11）：對**目前承載之頁**求值，**不判斷任何具名頁面型別**。
    /// 目前頁未實作 <see cref="IUnsavedGuardPage"/> 或無未存變更即放行；否則三選——
    /// 存後離開（存檔成功才離開）／捨棄還原離開／取消留原處（回傳 false，由呼叫端撥回分頁選取）。
    /// </summary>
    [LeaveGuard]
    public bool ConfirmLeaveCurrentPage()
    {
        if (Host.Content is not IUnsavedGuardPage page || !page.IsDirty)
        {
            return true;
        }
        var name = page.PageDisplayName;
        // #125：兩選（OK/Cancel）改三選（Yes/No/Cancel）——存後離開／捨棄變更並離開／取消留原處。
        var r = AskLeave(
            $"{name}有未儲存的變更。\n\n" +
            $"是 — 儲存並離開\n否 — 捨棄變更並離開\n取消 — 留在{name}");
        switch (r)
        {
            case MessageBoxResult.Yes:
                return page.TrySave();     // 存檔成功才離開；失敗（實作已以持續性通道報錯）留在原頁
            case MessageBoxResult.No:
                page.RevertChanges();      // 不存離開＝還原為上次儲存值
                return true;
            default:
                return false;              // Cancel＝留在原處
        }
    }

    /// <summary>
    /// 取消離開後把分頁選取撥回**原分頁**（不寫死任一頁）。
    /// <para>
    /// spec#11「撥回不得反噬」：撥回會觸發該頁 <c>Checked</c>，其 handler 內含進場動作
    /// （如 <c>_themes.Reload(preferActive: true)</c> 會重讀檔並改選使用中主題）——**照跑就會把
    /// 使用者按「取消」保住的編輯整個覆蓋掉**。故各 handler 於 <c>_reselecting</c> 期間**整段 return**，
    /// 不只是跳過守衛（本來就已在該頁，無事可做）。
    /// </para>
    /// </summary>
    private void ReselectCurrentTab()
    {
        _reselecting = true;
        foreach (var tab in new[] { TabNotes, TabHistory, TabThemes, TabCapture, TabVideo, TabEbook, TabOptions, TabAbout })
        {
            if (ReferenceEquals(Host.Content, TabContent(tab))) { tab.IsChecked = true; break; }
        }
        _reselecting = false;
    }

    private bool _reselecting; // 撥回期間抑制守衛，免再進入

    private object? TabContent(System.Windows.Controls.Primitives.ToggleButton tab) =>
        ReferenceEquals(tab, TabNotes) ? _notes
        : ReferenceEquals(tab, TabHistory) ? _history
        : ReferenceEquals(tab, TabThemes) ? _themes
        : ReferenceEquals(tab, TabCapture) ? _capture
        : ReferenceEquals(tab, TabVideo) ? _video
        : ReferenceEquals(tab, TabEbook) ? _ebook
        : ReferenceEquals(tab, TabOptions) ? _options
        : ReferenceEquals(tab, TabAbout) ? (object?)_about : null;

    /// <summary>切到指定分頁並自收合還原（tray／入口呼叫）。</summary>
    public void ShowTab(MainTab tab)
    {
        switch (tab)
        {
            case MainTab.Notes: TabNotes.IsChecked = true; break;
            case MainTab.History: TabHistory.IsChecked = true; break;
            case MainTab.Themes: TabThemes.IsChecked = true; break;
            case MainTab.Capture: TabCapture.IsChecked = true; break;
            case MainTab.Video: TabVideo.IsChecked = true; break;
            case MainTab.Ebook: TabEbook.IsChecked = true; break;
            case MainTab.Options: TabOptions.IsChecked = true; break;
            case MainTab.About: TabAbout.IsChecked = true; break;
        }
        RestoreFromTray();
    }

    /// <summary>更新底部狀態列之金鑰狀態與快捷鍵顯示（啟動與設定變更後呼叫；Issue #38 狀態置底）。</summary>
    public void RefreshStatus(bool keyReady, string hotkeyDisplay)
    {
        KeyStatusText.Text = AppStatusText.KeyStatus(keyReady);
        KeyStatusText.Foreground = keyReady ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.Firebrick;
        HotkeyText.Text = AppStatusText.HotkeyLine(hotkeyDisplay);
    }

    private System.Windows.Threading.DispatcherTimer? _savedFlashTimer;

    /// <summary>設定儲存成功後於底部狀態列輕量閃示「Saved ✓」數秒（#125，取代原「Saved.」模態對話框）。</summary>
    public void FlashSaved()
    {
        SavedFlashText.Text = "已儲存 ✓";
        SavedFlashText.Visibility = Visibility.Visible;
        _savedFlashTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _savedFlashTimer.Stop();
        _savedFlashTimer.Tick -= OnSavedFlashElapsed; // 重按儲存時重置計時，不累加 handler
        _savedFlashTimer.Tick += OnSavedFlashElapsed;
        _savedFlashTimer.Start();
    }

    private void OnSavedFlashElapsed(object? sender, EventArgs e)
    {
        _savedFlashTimer?.Stop();
        SavedFlashText.Visibility = Visibility.Collapsed;
    }

    /// <summary>底部狀態列顯示目前檢視之條目列數（#132）；<paramref name="count"/> 為 null＝隱藏（非筆記/歷史分頁）。</summary>
    public void ShowEntryCount(int? count)
    {
        if (count is null)
        {
            CountText.Visibility = Visibility.Collapsed;
            CountSeparator.Visibility = Visibility.Collapsed;
            return;
        }
        CountText.Text = count.Value + (count.Value == 1 ? " 條" : " 條");
        CountText.Visibility = Visibility.Visible;
        CountSeparator.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 新版已靜默下載就緒 → 底部狀態列顯示提示，並於 OS 標題列標示（工作列按鈕同步可見；
    /// Issue #51＋USR 回饋；重啟後套用、新進程標題自然回復）。
    /// </summary>
    public void ShowUpdateReady(string version)
    {
        Title = AppStatusText.TitleUpdateReady(version);
        UpdateText.Text = AppStatusText.UpdateReady(version);
        UpdateSeparator.Visibility = Visibility.Visible;
        UpdateText.Visibility = Visibility.Visible;
    }

    /// <summary>顯示／更新匯入進度列（#322）：文字（過長以省略號截斷、ToolTip 全文）、進度條 0–N、取消中停用「取消」。</summary>
    public void ShowImportProgress(string text, int done, int total, bool cancelling)
    {
        ImportProgressText.Text = text;
        ImportProgressText.ToolTip = text;
        ImportProgressBar.Maximum = Math.Max(1, total);
        ImportProgressBar.Value = Math.Clamp(done, 0, Math.Max(1, total));
        ImportProgressCancel.IsEnabled = !cancelling;
        ImportProgressPanel.Visibility = Visibility.Visible;
    }

    /// <summary>隱藏匯入進度列（#322）：匯入結束即收起、不佔位。</summary>
    public void HideImportProgress() => ImportProgressPanel.Visibility = Visibility.Collapsed;

    /// <summary>從收合狀態還原並帶到前景。</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>由呼叫端在「結束」流程中設定，令下一次 Close 真正關閉（而非收合）。</summary>
    public void AllowClose() => _exiting = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            // v1.0.1（USR 回饋）：移除原「關閉(✕)＝收合」防關閉行為——✕ 改為結束整個常駐程式。
            // 先攔下本次關閉、轉交 App 走統一結束流程（ExitApp→AllowClose 設 _exiting→Shutdown→OnExit 清理，
            // 屆時再次進入本方法時 _exiting 已 true 而真正關閉）；背景常駐/熱鍵改用「最小化(_)」保留。
            e.Cancel = true;
            ExitRequested?.Invoke();
            return;
        }
        base.OnClosing(e);
    }
}
