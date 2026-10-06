using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

namespace LingoIsland.Present;

/// <summary>
/// 本機版號之單一來源（#311）：關於分頁之「版本 vX.Y.Z」與使用手冊網址同源。
/// 組件版號由 <c>Directory.Build.props</c> 自根 <c>VERSION</c> 投影（dev 建置與打包成品皆然）。
/// </summary>
public static class AppVersion
{
    /// <summary>本程式組件版號；取不到時為 null。</summary>
    public static Version? Current => typeof(AppVersion).Assembly.GetName().Version;

    /// <summary>版號可用＝非 null 且三段不全為 0（0.0.0＝未設定，與 null 同視為取不到）。</summary>
    public static bool IsUsable(Version? version) =>
        version is not null && (version.Major != 0 || version.Minor != 0 || version.Build > 0);

    /// <summary>顯示用三段版號 <c>Major.Minor.Build</c>（Build 未設定之 -1 視為 0）；取不到（見 <see cref="IsUsable"/>）回「?」。</summary>
    public static string Display(Version? version) =>
        IsUsable(version) ? $"{version!.Major}.{version.Minor}.{Math.Max(version.Build, 0)}" : "?";
}

/// <summary>
/// 使用手冊入口（#311；design ＜II.C.(A).4＞ [modPresent模組] 使用手冊入口契約）：
/// 關於分頁「使用手冊」鈕與系統匣「使用手冊」項同走 <see cref="Open"/>，以預設瀏覽器開啟
/// <c>{RepoUrl}/blob/v{本機版號}/README.md</c>（與安裝版相符之手冊）；版號取不到退 <c>main</c>。
/// 開啟失敗交呼叫端以既有提示告知（附網址），本類不擲出、不當機。
/// </summary>
public static class UserManual
{
    /// <summary>提示對話框標題。</summary>
    public const string DialogTitle = "LingoIsland 使用手冊";

    /// <summary>測試縫（比照 <see cref="UpdateService.FeedOverrideEnv"/>）：設為檔案路徑時不開瀏覽器、改把網址附寫該檔。</summary>
    public const string LaunchLogEnv = "LINGOISLAND_MANUAL_LAUNCH_LOG";

    /// <summary>
    /// 組手冊網址：org／repo 只取自 <see cref="UpdateService.RepoUrl"/>（不另寫死）；
    /// 版號可用時釘該版 tag，null 或 0.0.0（未設定）退 <c>main</c>——不組出必 404 之 <c>v?</c>／<c>v0.0.0</c>。
    /// </summary>
    public static string BuildUrl(Version? version)
    {
        var gitRef = AppVersion.IsUsable(version) ? "v" + AppVersion.Display(version) : "main";
        return $"{UpdateService.RepoUrl}/blob/{gitRef}/README.md";
    }

    /// <summary>
    /// 開啟網址；成功回 null，失敗（launcher 擲任何例外）回給使用者之訊息（含網址）——不擲出。
    /// </summary>
    /// <param name="launcher">實際開啟者；null＝<see cref="DefaultLauncher"/>（測試注入用）。</param>
    public static string? TryOpen(string url, Action<string>? launcher = null)
    {
        try
        {
            (launcher ?? DefaultLauncher)(url);
            return null;
        }
        catch (Exception)
        {
            return AppStatusText.ManualOpenFailed(url);
        }
    }

    /// <summary>
    /// 兩入口之單一進入點：以本機版號組網址並開啟。**開啟前**判定離線者先以 <paramref name="notify"/> 告知
    /// （模態、使用者按確定後才開瀏覽器——免提示被隨後搶前景之瀏覽器蓋住），之後仍照常開啟（網路可能隨即恢復、可重新整理）；
    /// 開啟失敗再以 <paramref name="notify"/> 告知。每種狀況各告知一次，皆含完整網址。
    /// </summary>
    /// <param name="isOnline">網路判定；null＝<see cref="NetworkInterface.GetIsNetworkAvailable"/>（本機判定、不連網）。</param>
    /// <returns>是否成功交出開啟。</returns>
    public static bool Open(Action<string> notify, Action<string>? launcher = null, Version? version = null,
        Func<bool>? isOnline = null)
    {
        var url = BuildUrl(version ?? AppVersion.Current);
        if (!IsOnlineSafe(isOnline ?? NetworkInterface.GetIsNetworkAvailable))
        {
            notify(AppStatusText.ManualOffline(url));
        }
        var error = TryOpen(url, launcher);
        if (error is not null)
        {
            notify(error);
            return false;
        }
        return true;
    }

    /// <summary>網路判定本身失敗不得擋路：例外時視為在線（不多跳提示）。</summary>
    private static bool IsOnlineSafe(Func<bool> isOnline)
    {
        try
        {
            return isOnline();
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>預設開啟：測試縫有設即寫檔，否則交 OS 預設瀏覽器（.NET Core 起 URL 須 <c>UseShellExecute=true</c>）。</summary>
    internal static void DefaultLauncher(string url)
    {
        var log = Environment.GetEnvironmentVariable(LaunchLogEnv);
        if (!string.IsNullOrWhiteSpace(log))
        {
            File.AppendAllText(log, url + Environment.NewLine);
            return;
        }
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// 提示框之 owner 選擇（純函式、可單元測試）：主視窗存在、可見且非最小化 → 以主視窗為 owner；
    /// 否則（含最小化——被擁有視窗會隨 owner 隱藏而模態卡住 UI）→ 以暫時之置頂隱形視窗為 owner，免被遊戲或瀏覽器擋住。
    /// </summary>
    public static NoticeOwner ChooseOwner(bool hasMain, bool isVisible, bool isMinimized) =>
        hasMain && isVisible && !isMinimized ? NoticeOwner.MainWindow : NoticeOwner.TopmostHelper;
}

/// <summary>使用手冊提示框之 owner 種類（#311）。</summary>
public enum NoticeOwner
{
    /// <summary>以可見之主視窗為 owner。</summary>
    MainWindow,
    /// <summary>以暫時之置頂隱形視窗為 owner（提示關閉即關閉）。</summary>
    TopmostHelper,
}
