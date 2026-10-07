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

    /// <summary>中斷原因（#322）：執行器於逐字處理之外擲出未預期之例外時之白話原因；空＝未中斷。至該點之逐字結果照常保留。</summary>
    public string Error { get; init; } = "";

    /// <summary>結局（#322）：中斷＞已取消＞完成（早停歸完成——其餘字以失敗列出）。</summary>
    public NotesImportEnding Ending => Error.Length > 0 ? NotesImportEnding.Interrupted : Cancelled ? NotesImportEnding.Cancelled : NotesImportEnding.Completed;
}

/// <summary>匯入之結局（#322）：決定結果視窗之標題、標頭與結果首句。</summary>
public enum NotesImportEnding { Completed, Cancelled, Interrupted }

/// <summary>
/// 背景匯入之進度（#322，結構化）：<see cref="Done"/>＝已處理完之字（加入＋更新＋略過＋失敗，含已寫入之自備中譯字）、<see cref="Total"/>＝交下之字數、
/// <see cref="Current"/>＝送出中之線上查詢字（空＝目前無查詢）、<see cref="OnlineRemaining"/>＝尚未處理完之線上查詢字數（含進行中；早停後為 0）、
/// <see cref="Remaining"/>＝預估剩餘時間（null＝樣本不足或無剩餘線上字）、<see cref="Wrote"/>＝本則緊接於一次筆記寫入之後（App 據以同步筆記頁）。
/// </summary>
public sealed record NotesImportProgress(int Done, int Total, string Current, int OnlineRemaining, TimeSpan? Remaining, bool Wrote);

/// <summary>
/// 剩餘時間估算（#322，純函式）：只計要上網查的字——每字自送出至處理完之耗時為一筆樣本，取最近至多 <see cref="Window"/> 筆之平均×剩餘線上查詢字數；
/// 未滿 <see cref="MinSamples"/> 筆不估。滑動平均比全程平均更快反映網速變化、比單筆穩定。
/// </summary>
public sealed class NotesImportEta
{
    public const int Window = 10;
    public const int MinSamples = 2;
    private readonly Queue<TimeSpan> _samples = new();

    public int SampleCount => _samples.Count;

    public void Record(TimeSpan elapsed)
    {
        _samples.Enqueue(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
        while (_samples.Count > Window) { _samples.Dequeue(); }
    }

    /// <summary>剩餘線上字數為 0 或樣本不足回 null。</summary>
    public TimeSpan? Estimate(int remainingOnline)
    {
        if (remainingOnline <= 0 || _samples.Count < MinSamples) { return null; }
        var avgTicks = _samples.Sum(t => t.Ticks) / _samples.Count;
        return TimeSpan.FromTicks(avgTicks * remainingOnline);
    }
}

/// <summary>
/// 【匯入清單】批次執行器（[modPresent模組] 筆記清單匯入契約，spec#14／#309）：逐字以**注入之查詢委派**取得三欄結果、
/// 以 <see cref="NotesStore.AddToFolderAndSave"/> 寫入指定資料夾（含子夾）並**一字一存**；<see cref="QueryException"/>（與其他非取消例外）
/// 計失敗、<b>不中斷其餘</b>；取消時已加者保留。查詢層以委派抽象，使單元／整合測試得以 fake 覆蓋全路徑、零 OpenAI 額度（USR 常設裁定）；
/// 正式路徑由 <see cref="MakeLookup"/> 接上 <see cref="QueryService"/>（單字→字義、片語→整句翻譯，與字典頁手動查詢同規則）。
/// #321：帶自備中譯（csv 第二欄）之字<b>不呼叫查詢委派</b>，連續者每 <see cref="OwnSegmentSize"/> 字一段一讀一寫（<see cref="NotesStore.AddOrRefreshOwnTranslationsAndSave"/>）；
/// 全部自備時改走同步之 <see cref="RunOwnOnly"/>（一次載入、一次存檔）。
/// #322：於 UI 執行緒以 async 執行（不 <c>Task.Run</c>）；每次寫入後於下一個 await 之前<b>同步</b>呼叫進度委派（非 <c>IProgress</c>——其 Post 排入會讓使用者事件插進寫入與筆記頁同步之間）；
/// 查詢返回後、寫入前再檢查權杖（回應已到而取消在先亦不寫入）；本批之字依清單序相連（<see cref="NotesStore.BatchInsertIndex"/>）。
/// </summary>
public sealed class NotesImportRunner
{
    /// <summary>系統性失敗早停門檻：連續線上查詢失敗達此數且尚無任何線上查詢成功即中止（金鑰未設、離線、逾時——每字都會失敗，不讓使用者等完整份清單）。</summary>
    public const int SystemicFailureStreak = 3;

    /// <summary>早停後其餘線上查詢之字之失敗原因（前綴；後接首次之實際錯誤訊息）。#321 起只計要上網查的字。</summary>
    public const string NotQueriedReason = "未查詢——連續 3 個要上網查的字失敗，疑金鑰或網路問題";

    /// <summary>混合時連續自備中譯字之每段字數上限（#321）：一段一讀一寫，段與段之間讓出 UI 執行緒。</summary>
    public const int OwnSegmentSize = 50;

    /// <summary>測試縫（#322）之環境變數名：設為 1–60000 之整數時，App 以延遲假查詢取代線上查詢（端端測試用；零網路、零額度）。</summary>
    public const string FakeLookupEnvVar = "LINGOISLAND_IMPORT_FAKE_LOOKUP_MS";

    /// <summary>測試縫假查詢寫入之中譯前綴（#322）。</summary>
    public const string FakeTranslationPrefix = "〔測試假查詢〕";

    /// <summary>全自備時之守衛查詢委派（#321）：被呼叫即擲例外——自備中譯不得觸發線上查詢。</summary>
    public static readonly Func<string, CancellationToken, Task<QueryResult>> NoLookup =
        (_, _) => throw new InvalidOperationException("自備中譯之匯入不得呼叫線上查詢");

    private readonly NotesStore _store;
    private readonly Func<string, CancellationToken, Task<QueryResult>> _lookup;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan> _clock;

    public NotesImportRunner(NotesStore store, Func<string, CancellationToken, Task<QueryResult>> lookup, Func<DateTimeOffset>? now = null, Func<TimeSpan>? clock = null)
    {
        _store = store;
        _lookup = lookup;
        _now = now ?? (() => DateTimeOffset.Now);
        if (clock is null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); // 單調時鐘（不受系統時間調整影響）
            clock = () => sw.Elapsed;
        }
        _clock = clock;
    }

    /// <summary>正式查詢委派：接上既有 <see cref="QueryService"/>——不新造查詢來源。</summary>
    public static Func<string, CancellationToken, Task<QueryResult>> MakeLookup(QueryService query)
        => (text, ct) => NotesImport.IsSingleWord(text) ? query.QueryWordAsync(text, ct) : query.QueryTextAsync(text, ct);

    /// <summary>測試縫之毫秒數（#322）：環境變數值為 1–60000 之整數才回該值，其餘（未設、空、非數字、越界）回 null＝正式行為。</summary>
    public static int? FakeLookupDelayMs(string? envValue)
        => int.TryParse((envValue ?? "").Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms is >= 1 and <= 60000 ? ms : null;

    /// <summary>測試縫之延遲假查詢（#322）：等待 <paramref name="delayMs"/> 毫秒（可隨權杖取消）後回「〔測試假查詢〕原字」——不連網、不花額度。</summary>
    public static Func<string, CancellationToken, Task<QueryResult>> MakeFakeLookup(int delayMs)
        => async (text, ct) =>
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(true);
            return new QueryResult(text, "", FakeTranslationPrefix + text);
        };

    /// <summary>字串清單（全部線上查詢）之多載——同 v4.18.0。</summary>
    public Task<NotesImportOutcome> RunAsync(IReadOnlyList<string> words, string folderId, string? colorHex,
        Action<string>? report, CancellationToken ct)
        => RunAsync(words.Select(w => new NotesImportItem(w)).ToList(), folderId, colorHex, report, ct);

    /// <summary>
    /// 逐字查詢並加入。<paramref name="report"/> 每字前回報 `查詢中 i/N：字`（自備段每段一則 `加入中 i–j/N：自備中譯 m 字`）；
    /// <paramref name="progress"/>（#322）同步回報結構化進度——每個線上字送出前一則、處理完一則，每段自備中譯寫入後一則；
    /// <paramref name="ct"/> 取消時立即停、已加者保留、回傳 <see cref="NotesImportOutcome.Cancelled"/>＝true（不丟例外，使呼叫端得以顯示部分結果）。
    /// 逐字處理之外之未預期例外不往外擲，回傳之 <see cref="NotesImportOutcome.Error"/> 帶原因、已完成逐字保留（#322）。
    /// </summary>
    public async Task<NotesImportOutcome> RunAsync(IReadOnlyList<NotesImportItem> items, string folderId, string? colorHex,
        Action<string>? report, CancellationToken ct, Action<NotesImportProgress>? progress = null)
    {
        var acc = new Acc();
        int onlineOk = 0, streak = 0, done = 0;
        string firstError = "";
        var cancelled = false;
        var error = "";
        var eta = new NotesImportEta();
        var onlineLeft = items.Count(x => (x.Text ?? "").Trim().Length > 0 && !x.IsOwn);
        var earlyStopped = false;
        void Emit(string current, bool wrote)
        {
            var left = earlyStopped ? 0 : onlineLeft;
            progress?.Invoke(new NotesImportProgress(done, items.Count, current, left, eta.Estimate(left), wrote));
        }
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var w = (items[i].Text ?? "").Trim();
                if (w.Length == 0) { done++; continue; }
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
                    done += j - i;
                    i = j - 1;
                    Emit("", wrote: true); // #322：同步回報（下一個 await 之前），App 據以同步筆記頁
                    if (j < items.Count)
                    {
                        try { await Task.Delay(1, ct).ConfigureAwait(true); } // 讓出 UI 執行緒（WPF 之 Task.Yield 讓不出）：進度與取消不凍結
                        catch (OperationCanceledException) { cancelled = true; break; }
                    }
                    continue;
                }
                if (streak >= SystemicFailureStreak && onlineOk == 0)
                {
                    earlyStopped = true;
                    acc.Failed.Add((w, firstError.Length > 0 ? $"{NotQueriedReason}（{firstError}）" : NotQueriedReason)); // 早停：其餘線上字不查、如實計失敗
                    done++;
                    Emit("", wrote: false);
                    continue;
                }
                report?.Invoke($"查詢中 {i + 1}/{items.Count}：{w}");
                Emit(w, wrote: false);
                var started = _clock();
                var wrote = false;
                try
                {
                    var r = await _lookup(w, ct).ConfigureAwait(true);
                    if (ct.IsCancellationRequested) { cancelled = true; break; } // #322：回應已到而取消在先——該字不寫入
                    if (r is null || r.IsEmpty) { acc.Failed.Add((w, "查詢沒有回傳內容")); streak++; if (firstError.Length == 0) { firstError = "查詢沒有回傳內容"; } }
                    else
                    {
                        // 寫入與確認頁判定須同鍵：一律以清單上的使用者原字為 Original（AI 回之原文偶有拼寫整形，若照用會使
                        // 標「新字」者被去重擋下去更新別筆、或勾「已在筆記」者找不到原筆——付費後結果偏離確認頁所示）
                        var toSave = r with { Original = w };
                        switch (_store.AddToFolderAndSave(toSave, folderId, colorHex, _now(), batchKeys: acc.BatchKeys)) // #322：本批之字依清單序相連
                        {
                            case NoteAddResult.Added: acc.Added++; acc.AddedWords.Add(w); acc.BatchKeys.Add(NoteEntry.KeyOf(w)); streak = 0; onlineOk++; wrote = true; break;
                            case NoteAddResult.AlreadyExists:
                                // 勾選「已在筆記」列之語意：重新查詢並更新原筆（留原夾、不重複建立、保留練習分數）；原筆已不在→略過
                                if (_store.RefreshEntryByKeyAndSave(toSave)) { acc.Updated++; onlineOk++; wrote = true; } else { acc.Skipped.Add(w); }
                                streak = 0;
                                break;
                            default: acc.Failed.Add((w, "沒有可儲存的內容")); streak++; if (firstError.Length == 0) { firstError = "沒有可儲存的內容"; } break;
                        }
                    }
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception ex) { acc.Failed.Add((w, ex.Message)); streak++; if (firstError.Length == 0) { firstError = ex.Message; } }   // QueryException／存檔 IOException——一字失敗不影響其他字
                eta.Record(_clock() - started); // 成功或失敗皆為一筆樣本（逾時與重試反映網速）
                onlineLeft--;
                done++;
                Emit("", wrote);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message; // #322：逐字之外之未預期例外——不往外擲，已完成逐字保留
        }
        return Finish(acc, folderId, cancelled) with { Error = error };
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
        public readonly List<string> BatchKeys = new(); // #322：本批已加入之字之去重鍵（寫入順序）
        public readonly List<string> Skipped = new();
        public readonly List<(string Word, string Reason)> Failed = new();
    }

    /// <summary>一段自備中譯字：一讀一寫；存檔失敗則該段各字計失敗（不計入早停之連續失敗）。</summary>
    private void WriteOwnSegment(IReadOnlyList<(string Text, string Own)> seg, string folderId, string? colorHex, Acc acc)
    {
        if (seg.Count == 0) { return; }
        try
        {
            var results = _store.AddOrRefreshOwnTranslationsAndSave(seg.Select(x => new QueryResult(x.Text, "", x.Own)).ToList(), folderId, colorHex, _now(), acc.Added, acc.BatchKeys);
            for (var k = 0; k < seg.Count; k++)
            {
                switch (results[k])
                {
                    case OwnTranslationWriteResult.Added: acc.Added++; acc.Own++; acc.AddedWords.Add(seg[k].Text); acc.BatchKeys.Add(NoteEntry.KeyOf(seg[k].Text)); break;
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
        // #322：嚴格讀檔——讀失敗時不判為「目標夾已不存在」（不寫錯誤之落點）
        var ok = _store.TryLoadStrict(out var dEnd, out _);
        var missing = ok && NotesStore.FindFolder(dEnd, folderId) is null;
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
