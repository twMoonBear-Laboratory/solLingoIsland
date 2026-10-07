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

    /// <summary>以自備中譯實際寫入之數（加入＋更新；不含失敗與取消後未寫者；#321）。</summary>
    public int OwnTranslationUsed { get; init; }
}

/// <summary>
/// 【匯入清單】批次執行器（[modPresent模組] 筆記清單匯入契約，spec#14／#309）：逐字以**注入之查詢委派**取得三欄結果、
/// 以 <see cref="NotesStore.AddToFolderAndSave"/> 寫入指定資料夾（含子夾）並**一字一存**；<see cref="QueryException"/>（與其他非取消例外）
/// 計失敗、<b>不中斷其餘</b>；取消時已加者保留。查詢層以委派抽象，使單元／整合測試得以 fake 覆蓋全路徑、零 OpenAI 額度（USR 常設裁定）；
/// 正式路徑由 <see cref="MakeLookup"/> 接上 <see cref="QueryService"/>（單字→字義、片語→整句翻譯，與字典頁手動查詢同規則）。
/// #321：帶自備中譯（csv 第二欄）之字<b>不呼叫查詢委派</b>，連續者每 <see cref="OwnSegmentSize"/> 字一段一讀一寫（<see cref="NotesStore.AddOrRefreshOwnTranslationsAndSave"/>）；
/// 全部自備時改走同步之 <see cref="RunOwnOnly"/>（一次載入、一次存檔）。
/// </summary>
public sealed class NotesImportRunner
{
    /// <summary>系統性失敗早停門檻：連續線上查詢失敗達此數且尚無任何線上查詢成功即中止（金鑰未設、離線、逾時——每字都會失敗，不讓使用者等完整份清單）。</summary>
    public const int SystemicFailureStreak = 3;

    /// <summary>早停後其餘線上查詢之字之失敗原因（前綴；後接首次之實際錯誤訊息）。#321 起只計要上網查的字。</summary>
    public const string NotQueriedReason = "未查詢——連續 3 個要上網查的字失敗，疑金鑰或網路問題";

    /// <summary>混合時連續自備中譯字之每段字數上限（#321）：一段一讀一寫，段與段之間讓出 UI 執行緒。</summary>
    public const int OwnSegmentSize = 50;

    /// <summary>全自備時之守衛查詢委派（#321）：被呼叫即擲例外——自備中譯不得觸發線上查詢。</summary>
    public static readonly Func<string, CancellationToken, Task<QueryResult>> NoLookup =
        (_, _) => throw new InvalidOperationException("自備中譯之匯入不得呼叫線上查詢");

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

    /// <summary>字串清單（全部線上查詢）之多載——同 v4.18.0。</summary>
    public Task<NotesImportOutcome> RunAsync(IReadOnlyList<string> words, string folderId, string? colorHex,
        Action<string>? report, CancellationToken ct)
        => RunAsync(words.Select(w => new NotesImportItem(w)).ToList(), folderId, colorHex, report, ct);

    /// <summary>
    /// 逐字查詢並加入。<paramref name="report"/> 每字前回報 `查詢中 i/N：字`（自備段每段一則 `加入中 i–j/N：自備中譯 m 字`）；<paramref name="ct"/> 取消時立即停、已加者保留、
    /// 回傳 <see cref="NotesImportOutcome.Cancelled"/>＝true（不丟例外，使呼叫端得以顯示部分結果）。
    /// </summary>
    public async Task<NotesImportOutcome> RunAsync(IReadOnlyList<NotesImportItem> items, string folderId, string? colorHex,
        Action<string>? report, CancellationToken ct)
    {
        var acc = new Acc();
        int onlineOk = 0, streak = 0;
        string firstError = "";
        var cancelled = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var w = (items[i].Text ?? "").Trim();
            if (w.Length == 0) { continue; }
            if (items[i].IsOwn)
            {
                // #321：連續之自備中譯字一段（至多 OwnSegmentSize 字）一讀一寫，不呼叫查詢委派；早停不連坐
                var seg = new List<(int Index, string Text, string Own)>();
                var j = i;
                for (; j < items.Count && items[j].IsOwn && seg.Count < OwnSegmentSize; j++)
                {
                    var t = (items[j].Text ?? "").Trim();
                    if (t.Length > 0) { seg.Add((j, t, items[j].OwnTranslation.Trim())); }
                }
                report?.Invoke($"加入中 {i + 1}–{j}/{items.Count}：自備中譯 {seg.Count} 字");
                WriteOwnSegment(seg.Select(x => (x.Text, x.Own)).ToList(), folderId, colorHex, acc);
                i = j - 1;
                if (j < items.Count)
                {
                    try { await Task.Delay(1, ct).ConfigureAwait(true); } // 讓出 UI 執行緒（WPF 之 Task.Yield 讓不出）：進度與取消不凍結
                    catch (OperationCanceledException) { cancelled = true; break; }
                }
                continue;
            }
            if (streak >= SystemicFailureStreak && onlineOk == 0)
            {
                acc.Failed.Add((w, firstError.Length > 0 ? $"{NotQueriedReason}（{firstError}）" : NotQueriedReason)); // 早停：其餘線上字不查、如實計失敗
                continue;
            }
            report?.Invoke($"查詢中 {i + 1}/{items.Count}：{w}");
            try
            {
                var r = await _lookup(w, ct).ConfigureAwait(true);
                if (r is null || r.IsEmpty) { acc.Failed.Add((w, "查詢沒有回傳內容")); streak++; if (firstError.Length == 0) { firstError = "查詢沒有回傳內容"; } continue; }
                // 寫入與確認頁判定須同鍵：一律以清單上的使用者原字為 Original（AI 回之原文偶有拼寫整形，若照用會使
                // 標「新字」者被去重擋下去更新別筆、或勾「已在筆記」者找不到原筆——付費後結果偏離確認頁所示）
                var toSave = r with { Original = w };
                switch (_store.AddToFolderAndSave(toSave, folderId, colorHex, _now(), insertAt: acc.Added)) // 批次內保清單序、整批置頂
                {
                    case NoteAddResult.Added: acc.Added++; acc.AddedWords.Add(w); streak = 0; onlineOk++; break;
                    case NoteAddResult.AlreadyExists:
                        // 勾選「已在筆記」列之語意：重新查詢並更新原筆（留原夾、不重複建立、保留練習分數）；原筆已不在→略過
                        if (_store.RefreshEntryByKeyAndSave(toSave)) { acc.Updated++; onlineOk++; } else { acc.Skipped.Add(w); }
                        streak = 0;
                        break;
                    default: acc.Failed.Add((w, "沒有可儲存的內容")); streak++; if (firstError.Length == 0) { firstError = "沒有可儲存的內容"; } break;
                }
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) { acc.Failed.Add((w, ex.Message)); streak++; if (firstError.Length == 0) { firstError = ex.Message; } }   // QueryException／存檔 IOException——一字失敗不影響其他字
        }
        return Finish(acc, folderId, cancelled);
    }

    /// <summary>
    /// 全部採自備中譯時之同步執行（#321）：不呼叫查詢委派、一次載入一次存檔；存檔失敗＝整批計失敗（原因含 I/O 訊息）、筆記檔不變。
    /// 含任一線上查詢之字即擲 <see cref="ArgumentException"/>（呼叫端須先分流）。
    /// </summary>
    public NotesImportOutcome RunOwnOnly(IReadOnlyList<NotesImportItem> items, string folderId, string? colorHex)
    {
        if (items.Any(x => (x.Text ?? "").Trim().Length > 0 && !x.IsOwn)) { throw new ArgumentException("RunOwnOnly 只收全部帶自備中譯之字", nameof(items)); }
        var acc = new Acc();
        WriteOwnSegment(items.Select(x => ((x.Text ?? "").Trim(), x.OwnTranslation.Trim())).Where(x => x.Item1.Length > 0).ToList(), folderId, colorHex, acc);
        return Finish(acc, folderId, cancelled: false);
    }

    private sealed class Acc
    {
        public int Added, Updated, Own;
        public readonly List<string> AddedWords = new();
        public readonly List<string> Skipped = new();
        public readonly List<(string Word, string Reason)> Failed = new();
    }

    /// <summary>一段自備中譯字：一讀一寫；存檔失敗則該段各字計失敗（不計入早停之連續失敗）。</summary>
    private void WriteOwnSegment(IReadOnlyList<(string Text, string Own)> seg, string folderId, string? colorHex, Acc acc)
    {
        if (seg.Count == 0) { return; }
        try
        {
            var results = _store.AddOrRefreshOwnTranslationsAndSave(seg.Select(x => new QueryResult(x.Text, "", x.Own)).ToList(), folderId, colorHex, _now(), acc.Added);
            for (var k = 0; k < seg.Count; k++)
            {
                switch (results[k])
                {
                    case OwnTranslationWriteResult.Added: acc.Added++; acc.Own++; acc.AddedWords.Add(seg[k].Text); break;
                    case OwnTranslationWriteResult.Updated: acc.Updated++; acc.Own++; break;
                    default: acc.Failed.Add((seg[k].Text, "沒有可儲存的內容")); break;
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var x in seg) { acc.Failed.Add((x.Text, ex.Message)); }
        }
    }

    private NotesImportOutcome Finish(Acc acc, string folderId, bool cancelled)
    {
        var dEnd = _store.LoadEnsured();
        var missing = NotesStore.FindFolder(dEnd, folderId) is null;
        return new NotesImportOutcome(acc.Added, acc.Updated, acc.Skipped, acc.Failed)
        {
            Cancelled = cancelled,
            AddedWords = acc.AddedWords,
            TargetFolderMissing = missing,
            FallbackFolder = missing ? NotesStore.FolderPath(dEnd, dEnd.Folders[0].Id) : "",
            OwnTranslationUsed = acc.Own,
        };
    }
}
