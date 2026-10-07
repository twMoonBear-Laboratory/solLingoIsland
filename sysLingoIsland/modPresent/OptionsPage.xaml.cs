using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using RoutedEventArgs = System.Windows.RoutedEventArgs;

namespace LingoIsland.Present;

/// <summary>
/// 選項分頁（Issue #34；原 SettingsWindow 內容移入為 UserControl）：金鑰／朗讀語音／查詢模型等偏好。
/// 儲存後 raise <see cref="SettingsChanged"/>；情境提示改由「情境」分頁管理、喚起快捷鍵改由「螢幕擷取」
/// 分頁管理（#133），本頁不呈現該二者，但 <see cref="Gather"/> 保留既有 Context／HistoryMax／Hotkey、不重置。
/// </summary>
[EditablePage]
[NoThemeConsumption]
public partial class OptionsPage : UserControl, IUnsavedGuardPage
{
    private const string DefaultVoiceTag = "";
    private ISpeechService? _testSvc;

    /// <summary>目前組態（供 Gather 保留未在本頁呈現之欄位）。</summary>
    public AppConfig Config { get; private set; }

    /// <summary>按「儲存」後觸發，帶新組態；呼叫端據此重建服務、更新狀態、重註冊熱鍵。</summary>
    public event Action<AppConfig>? SettingsChanged;

    public OptionsPage(AppConfig current)
    {
        InitializeComponent();
        Config = current;

        VoiceBox.Items.Add(new ComboBoxItem { Content = "（系統預設英文語音）", Tag = DefaultVoiceTag });
        foreach (var v in SpeechService.InstalledVoiceNames())
        {
            VoiceBox.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        }

        SaveBtn.Click += OnSave;
        TestBtn.Click += OnTest;
        // 發音及格門檻：滑桿↔數值框雙向同步（spec#10）
        PronThresholdSlider.ValueChanged += (_, e) => PronThresholdBox.Text = ((int)e.NewValue).ToString();
        PronThresholdBox.LostFocus += (_, _) => SyncThresholdFromBox();
        // 條目字級：滑桿↔數值框雙向同步（#複查）
        EntryFontSlider.ValueChanged += (_, e) => EntryFontBox.Text = ((int)e.NewValue).ToString();
        EntryFontBox.LostFocus += (_, _) => SyncEntryFontFromBox();
        // 條目卡底色透明度：滑桿↔數值框雙向同步（v1.0.1）
        EntryOpacitySlider.ValueChanged += (_, e) => EntryOpacityBox.Text = ((int)e.NewValue).ToString();
        EntryOpacityBox.LostFocus += (_, _) => SyncEntryOpacityFromBox();
        // 查詢視窗字級：滑桿↔數值框雙向同步（#複查）
        ResultFontSlider.ValueChanged += (_, e) => ResultFontBox.Text = ((int)e.NewValue).ToString();
        ResultFontBox.LostFocus += (_, _) => SyncResultFontFromBox();
        // 影片頁字幕帶字級：滑桿↔數值框雙向同步（比照筆記）
        SubtitleFontSlider.ValueChanged += (_, e) => SubtitleFontBox.Text = ((int)e.NewValue).ToString();
        SubtitleFontBox.LostFocus += (_, _) => SyncSubtitleFontFromBox();
        // 影片搜尋縮圖高度：滑桿↔數值框雙向同步（#複查）
        ThumbSizeSlider.ValueChanged += (_, e) => ThumbSizeBox.Text = ((int)e.NewValue).ToString();
        ThumbSizeBox.LostFocus += (_, _) => SyncThumbSizeFromBox();
        // 資料備份與搬遷（#206）：即時動作、不走頁尾儲存
        ExportDataBtn.Click += (_, _) => OnExportData();
        ImportDataBtn.Click += (_, _) => OnImportData();

        SetConfig(current);
    }

    /// <summary>匯出資料（#206）：整包 %APPDATA%\LingoIsland → 使用者指定之 zip；成功顯路徑、失敗明訊不當機。</summary>
    private void OnExportData()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "匯出 LingoIsland 資料",
            Filter = "備份檔 (*.zip)|*.zip",
            FileName = LingoIsland.Query.BackupService.DefaultBackupFileName(DateTimeOffset.Now),
        };
        if (dlg.ShowDialog(System.Windows.Window.GetWindow(this)) != true) { return; }
        try
        {
            LingoIsland.Query.BackupService.CreateBackup(LingoIsland.Query.BackupService.DefaultDataDir, dlg.FileName);
            System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this),
                $"已匯出至：\n{dlg.FileName}\n\n提醒：備份不含 OpenAI API 金鑰（在環境變數）——新電腦需再設定一次。",
                "匯出完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this),
                $"匯出失敗：{ex.Message}", "匯出資料", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>匯入資料（#206）：確認 → 驗備份 → 先解壓暫存再搬入（解壓失敗不動本機資料）→ 提示並關閉程式（重啟後套用）；還原走背景執行緒、執行中禁用雙鈕。</summary>
    /// <summary>匯入清單背景執行中時回提示文案（#322；App 注入）：備份還原與匯入清單雙向互斥。</summary>
    public Func<string?>? RestoreBlockedReason { get; set; }

    /// <summary>備份還原進行中（#322）：自確認還原至程式結束；還原失敗即解除。供 App 擋匯入清單。</summary>
    public bool RestoreRunning { get; private set; }

    private async void OnImportData()
    {
        if (RestoreBlockedReason?.Invoke() is { } why)
        {
            System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this), why, "資料備份與搬遷",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information); // #322：匯入清單進行中不還原
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "匯入 LingoIsland 資料",
            Filter = "備份檔 (*.zip)|*.zip",
        };
        if (dlg.ShowDialog(System.Windows.Window.GetWindow(this)) != true) { return; }
        var go = System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this),
            "匯入會以備份內容覆蓋本機同名資料（筆記／歷史／主題／截圖／影片與設定），其餘檔案保留。\n建議先「匯出資料…」留存現況。\n\n匯入完成後程式會關閉，請再重新開啟。要繼續嗎？",
            "匯入資料", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (go != System.Windows.MessageBoxResult.OK) { return; }
        if (RestoreBlockedReason?.Invoke() is { } why2) // 確認框開著期間可能剛開始匯入清單
        {
            System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this), why2, "資料備份與搬遷",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        RestoreRunning = true; // #322：還原進行中擋匯入清單（至程式結束；失敗即解除）
        ExportDataBtn.IsEnabled = ImportDataBtn.IsEnabled = false; // 審查修：還原走背景、大 zip 不凍 UI、防重入
        try
        {
            await Task.Run(() => LingoIsland.Query.BackupService.RestoreBackup(dlg.FileName, LingoIsland.Query.BackupService.DefaultDataDir));
            System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this),
                "匯入完成。程式即將關閉，請重新開啟 LingoIsland。\n\n提醒：OpenAI API 金鑰不在備份內——新電腦請照「快速開始」再設定 OPENAI_API_KEY。",
                "匯入完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this),
                $"匯入失敗：{ex.Message}\n本機資料未被變更（還原於解壓暫存階段即中止）。", "匯入資料", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            RestoreRunning = false;
        }
        finally { ExportDataBtn.IsEnabled = ImportDataBtn.IsEnabled = true; }
    }

    /// <summary>以指定組態刷新欄位（啟動與外部變更後呼叫）。</summary>
    public void SetConfig(AppConfig c)
    {
        Config = c;
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        KeyStatus.Text = string.IsNullOrWhiteSpace(key) ? "狀態：○ 未設定" : "狀態：● 已設定";
        SelectByTag(VoiceBox, c.Voice ?? DefaultVoiceTag);
        QueryModelBox.Text = c.Model;
        PronThresholdSlider.Value = c.PronPassThreshold; // ValueChanged 同步數值框
        PronThresholdBox.Text = c.PronPassThreshold.ToString();
        PronModelBox.Text = c.PronModel;
        EntryFontSlider.Value = c.EntryFontSize; // ValueChanged 同步數值框（#複查）
        EntryFontBox.Text = ((int)c.EntryFontSize).ToString();
        EntryBoldChk.IsChecked = c.EntryBold;
        EntryWrapChk.IsChecked = c.EntryWrap;
        EntryOpacitySlider.Value = c.EntryCardOpacity; // 條目卡底色透明度（v1.0.1；ValueChanged 同步數值框）
        EntryOpacityBox.Text = c.EntryCardOpacity.ToString();
        ResultFontSlider.Value = c.ResultFontSize; // ValueChanged 同步數值框（#複查）
        ResultFontBox.Text = ((int)c.ResultFontSize).ToString();
        SubtitleFontSlider.Value = c.SubtitleFontSize; // 影片頁字幕帶字級（比照筆記；ValueChanged 同步數值框）
        SubtitleFontBox.Text = ((int)c.SubtitleFontSize).ToString();
        SubtitleBoldChk.IsChecked = c.SubtitleBold;
        ThumbSizeSlider.Value = c.SearchThumbHeight; // 影片搜尋縮圖高度（ValueChanged 同步數值框）
        ThumbSizeBox.Text = ((int)c.SearchThumbHeight).ToString();
    }

    /// <summary>
    /// 是否有未儲存變更（#複查）：任一欄位值異於上次儲存的 <see cref="Config"/>，或已輸入尚未套用的金鑰。
    /// 供主視窗於離開選項頁前提示。以 <see cref="Gather"/> 對上次儲存快照做記錄式結構比對。
    /// </summary>
    /// <summary>對話文案用之頁面顯示名（spec#11 四成員契約）。</summary>
    public string PageDisplayName => "選項頁";

    public bool IsDirty => !string.IsNullOrEmpty(KeyBox.Password) || Gather() != Config;

    /// <summary>捨棄未儲存變更、還原為上次儲存值（#複查：離開頁時選「確定離開」則還原）。</summary>
    public void RevertChanges()
    {
        KeyBox.Clear();
        SetConfig(Config); // 以上次儲存快照重填所有欄位
    }

    /// <summary>條目字級數值框 → 滑桿同步（鉗制 12–32；空/非數字回預設）。</summary>
    private void SyncEntryFontFromBox()
    {
        var v = int.TryParse(EntryFontBox.Text?.Trim(), out var n) ? Math.Clamp(n, 12, 32) : (int)AppConfig.DefaultEntryFontSize;
        EntryFontSlider.Value = v;
    }

    /// <summary>條目卡底色透明度數值框 → 滑桿同步（鉗制 0–100；空/非數字回預設）。</summary>
    private void SyncEntryOpacityFromBox()
    {
        var v = int.TryParse(EntryOpacityBox.Text?.Trim(), out var n) ? Math.Clamp(n, 0, 100) : AppConfig.DefaultEntryCardOpacity;
        EntryOpacitySlider.Value = v;
    }

    /// <summary>查詢視窗字級數值框 → 滑桿同步（鉗制 16–40；空/非數字回預設）。</summary>
    private void SyncResultFontFromBox()
    {
        var v = int.TryParse(ResultFontBox.Text?.Trim(), out var n) ? Math.Clamp(n, 16, 40) : (int)AppConfig.DefaultResultFontSize;
        ResultFontSlider.Value = v;
    }

    /// <summary>字幕帶字級數值框 → 滑桿同步（鉗制 12–48；空/非數字回預設）。</summary>
    private void SyncSubtitleFontFromBox()
    {
        var v = int.TryParse(SubtitleFontBox.Text?.Trim(), out var n) ? Math.Clamp(n, 12, 48) : (int)AppConfig.DefaultSubtitleFontSize;
        SubtitleFontSlider.Value = v;
    }

    /// <summary>搜尋縮圖高度數值框 → 滑桿同步（鉗制 28–120；空/非數字回預設）。</summary>
    private void SyncThumbSizeFromBox()
    {
        var v = int.TryParse(ThumbSizeBox.Text?.Trim(), out var n) ? Math.Clamp(n, 28, 120) : (int)AppConfig.DefaultSearchThumbHeight;
        ThumbSizeSlider.Value = v;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if ((string?)item.Tag == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static string TagOf(ComboBox box) => (string?)((ComboBoxItem?)box.SelectedItem)?.Tag ?? "";

    private AppConfig Gather() => new(
        string.IsNullOrWhiteSpace(QueryModelBox.Text) ? "gpt-4o-mini" : QueryModelBox.Text.Trim(),
        Config.TimeoutSec,
        TagOf(VoiceBox),
        Config.MaxRetries,
        Config.Hotkey,       // 保留：喚起快捷鍵改由「螢幕擷取」分頁管理（#133），本頁不重置
        Config.HistoryMax,   // 保留（#13）
        Config.Context,      // 保留情境（由情境分頁管理，本頁不重置）
        (int)PronThresholdSlider.Value, // 發音及格門檻（spec#10）
        string.IsNullOrWhiteSpace(PronModelBox.Text) ? AppConfig.DefaultPronModel : PronModelBox.Text.Trim(),
        EntryFontSlider.Value,          // 條目字級（#複查）
        EntryBoldChk.IsChecked == true, // 條目粗體
        EntryWrapChk.IsChecked == true, // 條目自動換行
        ResultFontSlider.Value,          // Dictionary 分頁結果基準字級（#複查）
        Config.ResultHideOnBlur, // #135：失焦自動隱藏已移除（浮窗移除）——保留 AppConfig 欄位、UI 不再呈現
        (int)EntryOpacitySlider.Value, // 條目卡底色透明度（v1.0.1，0–100）
        SubtitleFontSlider.Value,       // 影片頁字幕帶字級（比照筆記）
        SubtitleBoldChk.IsChecked == true, // 影片頁字幕帶粗體
        ThumbSizeSlider.Value); // 影片搜尋縮圖高度

    /// <summary>數值框 → 滑桿同步（鉗制 0–100；空/非數字回門檻預設）。</summary>
    private void SyncThresholdFromBox()
    {
        var v = int.TryParse(PronThresholdBox.Text?.Trim(), out var n) ? Math.Clamp(n, 0, 100) : AppConfig.DefaultPronThreshold;
        PronThresholdSlider.Value = v; // ValueChanged 會把數值框回寫為整數
    }

    private void ApplyKeyIfProvided()
    {
        var newKey = KeyBox.Password;
        if (!string.IsNullOrWhiteSpace(newKey))
        {
            newKey = newKey.Trim();
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", newKey, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", newKey, EnvironmentVariableTarget.Process);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => TrySave();

    /// <summary>
    /// 執行儲存（#125）：成功回傳 <c>true</c>——**不彈成功模態**，成功回饋改由呼叫端經
    /// <see cref="SettingsChanged"/>→App→主視窗狀態列輕量閃示「Saved ✓」；失敗回傳 <c>false</c>
    /// 並以**持續性**模態報錯（非數秒閃示、免一閃即逝）、留在頁上。供「儲存」鈕與「存後離開」守衛共用。
    /// </summary>
    public bool TrySave()
    {
        try
        {
            ApplyKeyIfProvided();
            Config = Gather();
            Config.Save(AppConfig.SettingsPath); // %APPDATA%（Issue #51 遷居；exe 旁不再寫）
            KeyBox.Clear();
            SetConfig(Config);
            SettingsChanged?.Invoke(Config); // 成功回饋走狀態列閃示（#125，取代原「Saved.」模態框）
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("儲存失敗：" + ex.Message, "LingoIsland 選項");
            return false;
        }
    }

    private void OnTest(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = Gather();
            (_testSvc as IDisposable)?.Dispose();
            _testSvc = new SpeechService(cfg.Voice);
            _testSvc.Speak("Hello, this is LingoIsland.", "en-US", stopPrevious: true);
            _testSvc.Speak("Hello, this is the screen English lookup tool.", "en-US", stopPrevious: false);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("測試失敗：" + ex.Message, "LingoIsland 選項");
        }
    }
}
