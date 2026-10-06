using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace LingoIsland;

/// <summary>檢查更新結果（關於分頁手動檢查回報用；自動檢查僅關心 Ready）。#123：失敗細分不同因、各給對應訊息。</summary>
public enum UpdateCheckResult
{
    /// <summary>新版已下載就緒。</summary>
    Ready,
    /// <summary>已是最新版本。</summary>
    UpToDate,
    /// <summary>連不上更新伺服器（離線／DNS／連線中斷）——非「已是最新」。</summary>
    FailedOffline,
    /// <summary>更新來源限流（GitHub API 403/429，查詢過於頻繁）——稍後再試。</summary>
    FailedRateLimited,
    /// <summary>伺服器暫時性錯誤（5xx／逾時），重試後仍失敗——稍後再試。</summary>
    FailedTransient,
    /// <summary>更新來源異常（feed 解析失敗／資產缺失／設定錯誤）。</summary>
    FailedSource,
}

/// <summary>更新失敗之內部分類（#122；供 <see cref="UpdateService.Classify"/> 純函式判定與重試決策）。</summary>
internal enum UpdateFailureKind
{
    Offline,
    RateLimited,
    ServerError,
    Timeout,
    SourceError,
    Unknown,
}

/// <summary>
/// Velopack 自動更新封裝（Issue #51；#122 強化）：背景檢查更新源→靜默下載→就緒後由 UI 顯示提示，
/// 「立即重啟更新」即時套用、否則結束時掛起套用（下次啟動即新版）。
/// #122：失敗**細分類**（離線／限流／伺服器暫時性／來源異常）不再一律報「連線失敗」；**暫時性錯誤重試＋退避**
/// （比照 QueryService）吸收瞬間抖動；下載階段以事件對外報「下載中／進度」。
/// 檢查/下載失敗一律不影響查詢主動線；dev 裸跑（非 Velopack 安裝形態）整段跳過。UI 顯示字串歸 AppStatusText。
/// </summary>
public sealed class UpdateService
{
    /// <summary>預設更新源：本專案之 GitHub Releases（資產含 releases.win.json 與 nupkg）。</summary>
    public const string RepoUrl = "https://github.com/twMoonBear-Laboratory/solLingoIsland";

    /// <summary>更新源覆寫環境變數（測試縫／自訂佈署）：URL 或本地路徑 feed；不設＝GitHub Releases。</summary>
    public const string FeedOverrideEnv = "LINGOISLAND_UPDATE_URL";

    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    public UpdateService()
    {
        var feed = Environment.GetEnvironmentVariable(FeedOverrideEnv);
        _mgr = string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(RepoUrl, null, false))
            : new UpdateManager(feed);
    }

    /// <summary>是否為 Velopack 安裝形態；false（dev 裸跑／publish 夾直跑）時更新流程整段跳過。</summary>
    public bool IsSupported => _mgr.IsInstalled;

    /// <summary>已靜默下載就緒之新版版號（無＝null）。</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>新版下載就緒（參數＝版號字串）。於背景執行緒觸發，接收端自行切 Dispatcher。</summary>
    public event Action<string>? UpdateReady;

    /// <summary>進入下載階段（檢查完成、開始下載）。背景執行緒觸發、接收端自切 Dispatcher。#122：與「確認中」區分。</summary>
    public event Action? DownloadStarted;

    /// <summary>下載進度百分比（0–100）。背景執行緒觸發、接收端自切 Dispatcher。</summary>
    public event Action<int>? DownloadProgress;

    /// <summary>
    /// 檢查更新源並靜默下載新版（已就緒者直接回 Ready、不重查）。未安裝形態視為已是最新。
    /// #122：失敗細分類（<see cref="UpdateCheckResult"/>）；暫時性錯誤（離線/逾時/5xx）以指數退避重試、
    /// 限流(403/429)與來源異常不重試（重試無益）。
    /// </summary>
    /// <remarks><paramref name="maxRetries"/>／<paramref name="backoff"/> internal 供測試（免真等待）。</remarks>
    public async Task<UpdateCheckResult> CheckAndDownloadAsync(int maxRetries = 2, Func<int, Task>? backoff = null)
    {
        if (!IsSupported)
        {
            return UpdateCheckResult.UpToDate;
        }
        if (ReadyVersion is not null)
        {
            return UpdateCheckResult.Ready;
        }
        backoff ??= i => Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));

        var kind = UpdateFailureKind.Unknown;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await CheckAndDownloadOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                kind = Classify(ex);
                if (!IsRetriable(kind) || attempt == maxRetries)
                {
                    break;
                }
                await backoff(attempt).ConfigureAwait(false); // 暫時性：退避 2^attempt 秒後重試
            }
        }
        return ToResult(kind);
    }

    /// <summary>單次檢查＋下載（成功回 Ready/UpToDate；任何失敗擲例外交由呼叫端分類/重試）。</summary>
    private async Task<UpdateCheckResult> CheckAndDownloadOnceAsync()
    {
        var info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
        {
            return UpdateCheckResult.UpToDate;
        }
        DownloadStarted?.Invoke(); // 檢查完成、進入下載階段（UI 由「確認中」轉「下載中」）
        await _mgr.DownloadUpdatesAsync(info, p => DownloadProgress?.Invoke(p)).ConfigureAwait(false);
        _pending = info;
        ReadyVersion = info.TargetFullRelease.Version.ToString();
        UpdateReady?.Invoke(ReadyVersion);
        return UpdateCheckResult.Ready;
    }

    /// <summary>更新失敗分類（#122 純函式，可測）：走例外鏈依型別／狀態碼／訊息判因。</summary>
    internal static UpdateFailureKind Classify(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException he when he.StatusCode is { } sc:
                    var code = (int)sc;
                    if (code is 403 or 429) return UpdateFailureKind.RateLimited; // GitHub API 限流
                    if (code >= 500) return UpdateFailureKind.ServerError;
                    return UpdateFailureKind.SourceError; // 其他 4xx（含 404 找不到 release/資產）
                case TaskCanceledException:
                case TimeoutException:
                    return UpdateFailureKind.Timeout;
                case SocketException:
                    return UpdateFailureKind.Offline;
                case HttpRequestException: // 無 StatusCode＝連線層失敗（DNS/連線中斷）
                    return UpdateFailureKind.Offline;
                case JsonException:
                case FormatException:
                    return UpdateFailureKind.SourceError;
            }
            var m = (e.Message ?? string.Empty).ToLowerInvariant();
            if (m.Contains("rate limit")) return UpdateFailureKind.RateLimited;
            if (m.Contains("timed out") || m.Contains("timeout")) return UpdateFailureKind.Timeout;
            if (m.Contains("no such host") || m.Contains("could not be resolved")
                || m.Contains("network is unreachable") || m.Contains("connection refused")
                || m.Contains("failed to connect")) return UpdateFailureKind.Offline;
        }
        return UpdateFailureKind.Unknown;
    }

    /// <summary>是否值得重試（#122）：離線/逾時/伺服器 5xx＝暫時性可重試；限流/來源異常/未知＝不重試。</summary>
    internal static bool IsRetriable(UpdateFailureKind kind) => kind is
        UpdateFailureKind.Offline or UpdateFailureKind.Timeout or UpdateFailureKind.ServerError;

    /// <summary>分類 → 對外結果（#122）。</summary>
    internal static UpdateCheckResult ToResult(UpdateFailureKind kind) => kind switch
    {
        UpdateFailureKind.Offline => UpdateCheckResult.FailedOffline,
        UpdateFailureKind.RateLimited => UpdateCheckResult.FailedRateLimited,
        UpdateFailureKind.ServerError => UpdateCheckResult.FailedTransient,
        UpdateFailureKind.Timeout => UpdateCheckResult.FailedTransient,
        UpdateFailureKind.SourceError => UpdateCheckResult.FailedSource,
        _ => UpdateCheckResult.FailedTransient, // 未知＝以「暫時性、請再試」保守呈現
    };

    /// <summary>立即重啟套用已就緒之新版（關於頁「立即重啟更新」）；無就緒新版＝no-op。</summary>
    public void RestartToApply()
    {
        if (_pending is not null)
        {
            _mgr.ApplyUpdatesAndRestart(_pending);
        }
    }

    /// <summary>結束時掛起套用（不重啟；下次啟動即新版）；無就緒新版＝no-op、失敗不阻斷結束。</summary>
    public void ApplyOnExit()
    {
        if (_pending is null)
        {
            return;
        }
        try
        {
            _mgr.WaitExitThenApplyUpdates(_pending, silent: true, restart: false);
        }
        catch
        {
            // 套用排程失敗不阻斷程式結束；新版仍留在本機、下次檢查直接就緒
        }
    }
}
