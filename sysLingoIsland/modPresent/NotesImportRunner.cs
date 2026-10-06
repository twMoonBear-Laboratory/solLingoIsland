using LingoIsland.Query;

namespace LingoIsland.Present;

/// <summary>批次執行之結果：成功數、略過字（加入當下已在筆記）、失敗字與原因。</summary>
public sealed record NotesImportOutcome(int Added, int Updated, IReadOnlyList<string> Skipped, IReadOnlyList<(string Word, string Reason)> Failed)
{
    public bool Cancelled { get; init; }

    /// <summary>已加入之字（依寫入順序），供結果表逐字列出（取消後使用者才看得到留下了哪些）。</summary>
    public IReadOnlyList<string> AddedWords { get; init; } = Array.Empty<string>();

    /// <summary>目標夾於結束時已不存在——結果表與 toast 須如實說明，不得仍寫原夾名。</summary>
    public bool TargetFolderMissing { get; init; }

    /// <summary>目標夾不存在時字之實際落點（第一個頂層夾之路徑，依名稱排序、未必是「My Notes」）；目標夾在則為空。</summary>
    public string FallbackFolder { get; init; } = "";
}

/// <summary>
/// 【匯入清單】批次執行器（[modPresent模組] 筆記清單匯入契約，spec#14／#309）：逐字以**注入之查詢委派**取得三欄結果、
/// 以 <see cref="NotesStore.AddToFolderAndSave"/> 寫入指定資料夾（含子夾）並**一字一存**；<see cref="QueryException"/>（與其他非取消例外）
/// 計失敗、<b>不中斷其餘</b>；取消時已加者保留。查詢層以委派抽象，使單元／整合測試得以 fake 覆蓋全路徑、零 OpenAI 額度（USR 常設裁定）；
/// 正式路徑由 <see cref="MakeLookup"/> 接上 <see cref="QueryService"/>（單字→字義、片語→整句翻譯，與字典頁手動查詢同規則）。
/// </summary>
public sealed class NotesImportRunner
{
    /// <summary>系統性失敗早停門檻：起手連續失敗達此數且尚無任何成功／更新即中止（金鑰未設、離線、逾時——每字都會失敗，不讓使用者等完整份清單）。</summary>
    public const int SystemicFailureStreak = 3;

    /// <summary>早停後其餘字之失敗原因（前綴；後接首字之實際錯誤訊息）。</summary>
    public const string NotQueriedReason = "未查詢——前 3 字連續失敗，疑金鑰或網路問題";

    private readonly NotesStore _store;
    private readonly Func<string, CancellationToken, Task<QueryResult>> _lookup;
    private readonly Func<DateTimeOffset> _now;

    public NotesImportRunner(NotesStore store, Func<string, CancellationToken, Task<QueryResult>> lookup, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _lookup = lookup;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>正式查詢委派：接上既有 <see cref="QueryService"/>——不新造查詢來源。</summary>
    public static Func<string, CancellationToken, Task<QueryResult>> MakeLookup(QueryService query)
        => (text, ct) => NotesImport.IsSingleWord(text) ? query.QueryWordAsync(text, ct) : query.QueryTextAsync(text, ct);

    /// <summary>
    /// 逐字查詢並加入。<paramref name="report"/> 每字前回報 `查詢中 i/N：字`；<paramref name="ct"/> 取消時立即停、已加者保留、
    /// 回傳 <see cref="NotesImportOutcome.Cancelled"/>＝true（不丟例外，使呼叫端得以顯示部分結果）。
    /// </summary>
    public async Task<NotesImportOutcome> RunAsync(IReadOnlyList<string> words, string folderId, string? colorHex,
        Action<string>? report, CancellationToken ct)
    {
        int added = 0, updated = 0, streak = 0;
        var addedWords = new List<string>();
        string firstError = "";
        var skipped = new List<string>();
        var failed = new List<(string Word, string Reason)>();
        var cancelled = false;
        for (var i = 0; i < words.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var w = (words[i] ?? "").Trim();
            if (w.Length == 0) { continue; }
            if (streak >= SystemicFailureStreak && added + updated == 0)
            {
                failed.Add((w, firstError.Length > 0 ? $"{NotQueriedReason}（{firstError}）" : NotQueriedReason)); // 早停：其餘字不查、如實計失敗
                continue;
            }
            report?.Invoke($"查詢中 {i + 1}/{words.Count}：{w}");
            try
            {
                var r = await _lookup(w, ct).ConfigureAwait(true);
                if (r is null || r.IsEmpty) { failed.Add((w, "查詢沒有回傳內容")); streak++; if (firstError.Length == 0) { firstError = "查詢沒有回傳內容"; } continue; }
                // 寫入與確認頁判定須同鍵：一律以清單上的使用者原字為 Original（AI 回之原文偶有拼寫整形，若照用會使
                // 標「新字」者被去重擋下去更新別筆、或勾「已在筆記」者找不到原筆——付費後結果偏離確認頁所示）
                var toSave = r with { Original = w };
                switch (_store.AddToFolderAndSave(toSave, folderId, colorHex, _now(), insertAt: added)) // 批次內保清單序、整批置頂
                {
                    case NoteAddResult.Added: added++; addedWords.Add(w); streak = 0; break;
                    case NoteAddResult.AlreadyExists:
                        // 勾選「已在筆記」列之語意：重新查詢並更新原筆（留原夾、不重複建立、保留練習分數）；原筆已不在→略過
                        if (_store.RefreshEntryByKeyAndSave(toSave)) { updated++; } else { skipped.Add(w); }
                        streak = 0;
                        break;
                    default: failed.Add((w, "沒有可儲存的內容")); streak++; if (firstError.Length == 0) { firstError = "沒有可儲存的內容"; } break;
                }
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) { failed.Add((w, ex.Message)); streak++; if (firstError.Length == 0) { firstError = ex.Message; } }   // QueryException／存檔 IOException——一字失敗不影響其他字
        }
        var dEnd = _store.LoadEnsured();
        var missing = NotesStore.FindFolder(dEnd, folderId) is null;
        return new NotesImportOutcome(added, updated, skipped, failed)
        {
            Cancelled = cancelled,
            AddedWords = addedWords,
            TargetFolderMissing = missing,
            FallbackFolder = missing ? NotesStore.FolderPath(dEnd, dEnd.Folders[0].Id) : "",
        };
    }
}
