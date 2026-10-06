namespace LingoIsland.Present;

/// <summary>
/// 維運狀態顯示文字之單一來源（Issue #25）：常駐主控頁與系統匣選單為同一組維運資訊之兩個入口鏡像，
/// 顯示字串一律取自本類、不各寫一份（design ＜II/III.C.(C)＞「動作來源單一」）。純函式、可單元測試。
/// </summary>
public static class AppStatusText
{
    /// <summary>金鑰狀態列（tray 選單與主控頁共用）。</summary>
    public static string KeyStatus(bool keyReady) =>
        keyReady ? "● 金鑰已就緒（OPENAI_API_KEY）" : "○ 金鑰未設定（OPENAI_API_KEY）";

    /// <summary>主控頁的喚起快捷鍵列。</summary>
    public static string HotkeyLine(string hotkeyDisplay) => $"快捷鍵：{hotkeyDisplay}";

    /// <summary>系統匣停留提示（滑鼠移到圖示上顯示）。</summary>
    public static string TrayTip(string hotkeyDisplay) =>
        $"LingoIsland — 遊戲畫面英文查詢（{hotkeyDisplay}）";

    /// <summary>新版下載就緒（底部狀態列與關於分頁共用，Issue #51）。</summary>
    public static string UpdateReady(string version) => $"新版 v{version} 已就緒——重啟以套用";

    /// <summary>新版就緒時之主視窗標題（OS 標題列＝工作列按鈕同步可見；USR 回饋）。</summary>
    public static string TitleUpdateReady(string version) => $"LingoIsland — 新版 v{version} 已就緒";

    /// <summary>手動檢查更新：已是最新（關於分頁）。</summary>
    public const string UpdateUpToDate = "已是最新版本";

    /// <summary>手動檢查更新：確認中（關於分頁）。</summary>
    public const string UpdateChecking = "檢查更新中…";

    /// <summary>手動更新：下載中（#122：與「確認中」區分）。</summary>
    public const string UpdateDownloading = "下載更新中…";

    /// <summary>手動更新：下載中含進度（#122）。</summary>
    public static string UpdateDownloadingPercent(int percent) => $"下載更新中… {percent}%";

    // #122：更新失敗細分類——各給對應訊息與下一步，不再一律「檢查你的網路」。

    /// <summary>失敗：連不上更新伺服器（離線／DNS）。</summary>
    public const string UpdateFailedOffline = "無法連上更新伺服器，請檢查你的網路連線。";

    /// <summary>失敗：更新來源限流（GitHub API 403/429，查詢過於頻繁）。</summary>
    public const string UpdateFailedRateLimited = "更新檢查過於頻繁，請稍後再試。";

    /// <summary>失敗：伺服器暫時性錯誤（5xx／逾時），重試後仍失敗。</summary>
    public const string UpdateFailedTransient = "更新伺服器暫時異常，請稍後再試。";

    /// <summary>失敗：更新來源異常（feed 解析／資產缺失／設定錯誤）。</summary>
    public const string UpdateFailedSource = "無法讀取更新資訊，更新來源可能暫時無法使用。";

    /// <summary>使用手冊開啟失敗（#311）：說明發生什麼、附完整網址，並提示以 Ctrl+C 複製（WPF MessageBox 文字不可選取）。</summary>
    public static string ManualOpenFailed(string url) =>
        "無法開啟瀏覽器顯示使用手冊。\n\n請將下列網址貼到瀏覽器開啟：\n" + url +
        "\n\n（按 Ctrl+C 可複製本訊息，內含上列網址。）";

    /// <summary>使用手冊開啟前判定離線（#311）：先告知可能載入不了並附網址，按確定後才開瀏覽器。</summary>
    public static string ManualOffline(string url) =>
        "目前沒有網路連線，瀏覽器可能無法載入使用手冊。\n按「確定」後仍會開啟瀏覽器；連上網路後在瀏覽器重新整理即可。\n\n網址：\n" + url +
        "\n\n（按 Ctrl+C 可複製本訊息，內含上列網址。）";

    /// <summary>失敗結果 → 對應訊息（#122）。</summary>
    public static string UpdateFailureMessage(UpdateCheckResult result) => result switch
    {
        UpdateCheckResult.FailedOffline => UpdateFailedOffline,
        UpdateCheckResult.FailedRateLimited => UpdateFailedRateLimited,
        UpdateCheckResult.FailedSource => UpdateFailedSource,
        _ => UpdateFailedTransient,
    };
}
