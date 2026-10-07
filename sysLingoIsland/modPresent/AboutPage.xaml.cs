using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace LingoIsland.Present;

/// <summary>
/// 關於分頁（Issue #34）：程式名／版本／版權／聯絡 email／logo。
/// Issue #51 增更新區：手動「檢查更新」與新版就緒後之「立即重啟更新」；
/// 僅 Velopack 安裝形態顯示（dev 裸跑隱藏）、狀態字串歸 <see cref="AppStatusText"/>。
/// </summary>
[NoUnsavedState]
[NoThemeConsumption]
public partial class AboutPage : UserControl
{
    private readonly UpdateService? _updates;

    /// <summary>「重啟以更新」前之確認（#322；App 注入）：匯入清單背景執行中即問是否停止匯入，回 false＝不重啟。</summary>
    public Func<bool>? ConfirmRestart { get; set; }

    public AboutPage(UpdateService? updates = null)
    {
        InitializeComponent();
        // #311：版號與使用手冊網址同源（AppVersion）；取不到（null／0.0.0）顯示「?」
        VersionText.Text = "版本 v" + AppVersion.Display(AppVersion.Current);
        // 更新紀錄改按鈕跳出獨立視窗（#159）——不再常駐佔頁
        ChangeLogBtn.Click += (_, _) =>
            new ChangeLogWindow { Owner = System.Windows.Window.GetWindow(this) }.ShowDialog();
        // #311：使用手冊（與系統匣同名項共用 UserManual.Open）；開啟失敗／離線以共用提示框告知（owner 規則見 ManualNoticeDialog）
        ManualBtn.Click += (_, _) => UserManual.Open(msg => ManualNoticeDialog.Show(System.Windows.Window.GetWindow(this), msg));

        _updates = updates;
        if (_updates is null || !_updates.IsSupported)
        {
            return; // 未安裝形態：更新區維持隱藏
        }
        UpdatePanel.Visibility = Visibility.Visible;
        CheckUpdateBtn.Click += async (_, _) => await CheckAsync();
        RestartUpdateBtn.Click += (_, _) => { if (ConfirmRestart?.Invoke() ?? true) { _updates.RestartToApply(); } }; // #322：匯入清單進行中先確認
        _updates.UpdateReady += v => Dispatcher.BeginInvoke(() => ShowReady(v));
        // #122：下載階段回饋（與「確認中」區分）——事件於背景執行緒觸發，切 Dispatcher 更新 UI
        _updates.DownloadStarted += () => Dispatcher.BeginInvoke(() => UpdateStatusText.Text = AppStatusText.UpdateDownloading);
        _updates.DownloadProgress += p => Dispatcher.BeginInvoke(() => UpdateStatusText.Text = AppStatusText.UpdateDownloadingPercent(p));
        if (_updates.ReadyVersion is not null)
        {
            ShowReady(_updates.ReadyVersion);
        }
    }

    /// <summary>以預設瀏覽器開啟外部連結（Issue #84 GitHub 連結）；失敗不致命。</summary>
    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* 開啟失敗不致命 */ }
        e.Handled = true;
    }

    /// <summary>手動檢查：檢查中鎖鈕；有新版轉就緒態、無新版顯示已是最新、失敗如實回報（不誤報最新）。</summary>
    private async Task CheckAsync()
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = AppStatusText.UpdateChecking;
        var result = await _updates!.CheckAndDownloadAsync();
        if (result == UpdateCheckResult.Ready && _updates.ReadyVersion is not null)
        {
            ShowReady(_updates.ReadyVersion);
        }
        else
        {
            // #122：已是最新 vs 各類失敗（離線/限流/暫時性/來源）各給對應訊息
            UpdateStatusText.Text = result == UpdateCheckResult.UpToDate
                ? AppStatusText.UpdateUpToDate
                : AppStatusText.UpdateFailureMessage(result);
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    /// <summary>新版就緒：顯示版號、換「立即重啟更新」鈕（檢查鈕收起）。</summary>
    private void ShowReady(string version)
    {
        UpdateStatusText.Text = AppStatusText.UpdateReady(version);
        CheckUpdateBtn.Visibility = Visibility.Collapsed;
        RestartUpdateBtn.Visibility = Visibility.Visible;
    }
}
