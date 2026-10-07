using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LingoIsland.Present;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modPresent模組] 筆記清單匯入契約「背景執行」（spec#14 擴充，#322）——單元（剩餘時間估算、文案、執行器之結構化進度、取消、插入位置、中斷）、
/// 整合（真 notes.json 臨時檔：匯入途中模擬筆記頁「同步後以 Id 寫入並整份存檔」不遺失匯入之字、取消後磁碟＝已完成者、讀檔失敗不洗掉筆記）、
/// 結構斷言（App 不再以 AI 動作頁跑匯入、不 Task.Run、同步進度回呼、結束確認先於守衛等）。線上查詢一律 fake——零 OpenAI 額度。
/// </summary>
public class NotesImportBackgroundTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-bg-{Guid.NewGuid():N}.json");

    private static (NotesStore Store, string FolderId) Seed(string path, params string[] existing)
    {
        var store = new NotesStore(path);
        var d = store.LoadEnsured();
        foreach (var w in existing) { NotesStore.AddToTopFolder(d, d.Folders[0], NoteEntry.From(new QueryResult(w, "", ""), DateTimeOffset.Now)); }
        store.Save(d);
        return (store, d.Folders[0].Id);
    }

    private static Func<string, CancellationToken, Task<QueryResult>> Ok(Action<string>? onCall = null)
        => (w, _) => { onCall?.Invoke(w); return Task.FromResult(new QueryResult(w, $"[{w}]", $"譯:{w}")); };

    /// <summary>假時鐘：每次查詢推進固定秒數（測試剩餘時間）。</summary>
    private sealed class FakeClock
    {
        public TimeSpan Now;
        public TimeSpan Get() => Now;
    }

    // ---- 剩餘時間估算（純函式）----

    [Fact]
    public void Eta_NeedsTwoSamples_ThenAverageTimesRemaining()
    {
        var eta = new NotesImportEta();
        Assert.Null(eta.Estimate(10));
        eta.Record(TimeSpan.FromSeconds(2));
        Assert.Null(eta.Estimate(10));                                   // 未滿 2 筆不估
        eta.Record(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(30), eta.Estimate(10));        // 平均 3 秒 × 10
        Assert.Null(eta.Estimate(0));                                    // 無剩餘線上字不估
    }

    [Fact]
    public void Eta_SlidingWindowOfTen_ReflectsRecentSpeed()
    {
        var eta = new NotesImportEta();
        for (var i = 0; i < 10; i++) { eta.Record(TimeSpan.FromSeconds(1)); }
        for (var i = 0; i < 10; i++) { eta.Record(TimeSpan.FromSeconds(5)); } // 網路變慢：舊樣本滑出
        Assert.Equal(NotesImportEta.Window, eta.SampleCount);
        Assert.Equal(TimeSpan.FromSeconds(10), eta.Estimate(2));
    }

    [Theory]
    [InlineData(0, 5, "剩餘時間估算中…", true)]
    [InlineData(59, 5, "約剩不到 1 分鐘", false)]
    [InlineData(61, 5, "約剩 2 分鐘", false)]
    [InlineData(3570, 5, "約剩 1 小時", false)]         // 59 分 30 秒 → 進位 60 → 1 小時
    [InlineData(3660, 5, "約剩 1 小時 1 分鐘", false)]
    [InlineData(7200, 5, "約剩 2 小時", false)]
    [InlineData(100, 0, "", false)]                     // 無剩餘線上字：不顯示
    public void RemainingText_States(int seconds, int onlineRemaining, string expected, bool nullEta)
        => Assert.Equal(expected, NotesImport.RemainingText(nullEta ? null : TimeSpan.FromSeconds(seconds), onlineRemaining));

    [Fact]
    public void ProgressText_WithCurrentAndEta_CancellingAndOwnOnlyRemaining()
    {
        var p = new NotesImportProgress(37, 120, "photosynthesis", 83, TimeSpan.FromSeconds(150), Wrote: false);
        Assert.Equal("正在匯入到「英文課 › Unit 4」：已完成 37／120（查詢中：photosynthesis）· 約剩 3 分鐘", NotesImport.ProgressText("英文課 › Unit 4", p, cancelling: false));
        Assert.Equal("正在取消匯入到「英文課 › Unit 4」…（已完成 37／120；已加入的字會保留）", NotesImport.ProgressText("英文課 › Unit 4", p, cancelling: true));
        var ownOnly = new NotesImportProgress(110, 120, "", 0, null, Wrote: true);
        Assert.Equal("正在匯入到「A」：已完成 110／120", NotesImport.ProgressText("A", ownOnly, false)); // 只剩自備字：無括號、無剩餘時間
    }

    [Fact]
    public void Texts_BusyExitRestoreAndResultHeads()
    {
        Assert.StartsWith("已有一批匯入正在進行（已完成 3／8）", NotesImport.BusyHint(3, 8));
        Assert.Contains("「取消」", NotesImport.BusyHint(3, 8));
        Assert.StartsWith("匯入清單還在進行（已完成 3／8）。", NotesImport.ExitConfirmText(3, 8));
        Assert.Contains("已加入的字會保留，其餘不會加入", NotesImport.ExitConfirmText(3, 8));
        Assert.Contains("再匯入資料", NotesImport.RestoreBlockedText(1, 2));
        Assert.Equal("匯入清單：已取消", NotesImport.ResultWindowTitle(NotesImportEnding.Cancelled));
        Assert.Equal("匯入中斷", NotesImport.ResultHeader(NotesImportEnding.Interrupted));
        Assert.NotEqual("匯入清單", NotesImport.ResultWindowTitle(NotesImportEnding.Completed)); // 與確認頁不同名
        var cancelled = new NotesImportOutcome(2, 0, Array.Empty<string>(), Array.Empty<(string, string)>()) { Cancelled = true, AddedWords = new[] { "a", "b" } };
        var body = NotesImport.ResultBody(cancelled, "夾");
        Assert.StartsWith("匯入已取消：已加入 2 字到「夾」", body);
        Assert.EndsWith("（已取消——已加入的字保留，其餘未加入。）", body);
        var broken = new NotesImportOutcome(1, 0, Array.Empty<string>(), Array.Empty<(string, string)>()) { Error = "boom" };
        Assert.Equal(NotesImportEnding.Interrupted, broken.Ending);
        Assert.Contains("（匯入中斷：boom——已加入的字保留，其餘未加入。）", NotesImport.ResultBody(broken, "夾"));
        Assert.StartsWith("匯入完成：", NotesImport.ResultBody(new NotesImportOutcome(0, 0, Array.Empty<string>(), new[] { ("x", "未查詢") }), "夾")); // 早停歸完成
    }

    [Theory]
    [InlineData("1200", 1200)]
    [InlineData(" 1 ", 1)]
    [InlineData("60000", 60000)]
    [InlineData("0", null)]
    [InlineData("60001", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FakeLookupSeam_OnlyValidIntegerEnables(string? env, int? expected)
        => Assert.Equal(expected, NotesImportRunner.FakeLookupDelayMs(env));

    [Fact]
    public async Task FakeLookup_ReturnsTaggedTranslation_AndHonoursCancellation()
    {
        var r = await NotesImportRunner.MakeFakeLookup(1)("orbit", CancellationToken.None);
        Assert.Equal(NotesImportRunner.FakeTranslationPrefix + "orbit", r.Translation);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NotesImportRunner.MakeFakeLookup(60000)("x", cts.Token));
    }

    // ---- 執行器之結構化進度 ----

    [Fact]
    public async Task Progress_BeforeAndAfterEachOnlineWord_OwnSegmentOnce_EtaAfterTwoSamples()
    {
        var path = TempPath();
        try
        {
            var (store, folderId) = Seed(path);
            var clock = new FakeClock();
            var events = new List<NotesImportProgress>();
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, _) => { clock.Now += TimeSpan.FromSeconds(3); return Task.FromResult(new QueryResult(w, "/p/", "譯")); };
            var runner = new NotesImportRunner(store, lookup, clock: clock.Get);
            var items = new[] { new NotesImportItem("own1", "自1"), new NotesImportItem("w1"), new NotesImportItem("w2"), new NotesImportItem("w3"), new NotesImportItem("own2", "自2") };
            var outcome = await runner.RunAsync(items, folderId, null, null, CancellationToken.None, events.Add);

            Assert.Equal(5, outcome.Added);
            // 自備段一則（寫入後）→ 每個線上字送出前、處理完各一則 → 末段自備一則
            Assert.Equal(new[] { "", "w1", "", "w2", "", "w3", "", "" }, events.Select(e => e.Current));
            Assert.Equal(new[] { 1, 1, 2, 2, 3, 3, 4, 5 }, events.Select(e => e.Done));
            Assert.All(events, e => Assert.Equal(5, e.Total));
            Assert.Equal(new[] { 3, 3, 2, 2, 1, 1, 0, 0 }, events.Select(e => e.OnlineRemaining)); // 不含自備字、含進行中之字
            Assert.Equal(new[] { true, false, true, false, true, false, true, true }, events.Select(e => e.Wrote));
            Assert.Null(events[3].Remaining);                                       // 1 筆樣本：估算中
            Assert.Equal(TimeSpan.FromSeconds(3), events[5].Remaining);             // 2 筆平均 3 秒 × 剩 1 字
            Assert.Null(events[6].Remaining);                                       // 剩餘線上字 0：不估
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Progress_EarlyStop_RemainingOnlineZero_FailuresCountAsDone()
    {
        var path = TempPath();
        try
        {
            var (store, folderId) = Seed(path);
            var events = new List<NotesImportProgress>();
            Func<string, CancellationToken, Task<QueryResult>> fail = (_, _) => throw new QueryException("401");
            var runner = new NotesImportRunner(store, fail);
            var outcome = await runner.RunAsync(Enumerable.Range(1, 6).Select(i => new NotesImportItem("w" + i)).ToList(), folderId, null, null, CancellationToken.None, events.Add);
            Assert.Equal(6, outcome.Failed.Count);
            Assert.Equal(6, events.Last().Done);
            Assert.Equal(0, events.Last().OnlineRemaining);                         // 早停後不估
            Assert.Null(events.Last().Remaining);
            Assert.Equal(NotesImportEnding.Completed, outcome.Ending);              // 早停歸完成
        }
        finally { File.Delete(path); }
    }

    // ---- 取消 ----

    [Fact]
    public async Task Cancel_InFlightWordNotWritten_EarlierKept()
    {
        var path = TempPath();
        try
        {
            var (store, folderId) = Seed(path);
            using var cts = new CancellationTokenSource();
            Func<string, CancellationToken, Task<QueryResult>> lookup = async (w, ct) =>
            {
                if (w == "w3") { cts.Cancel(); }
                await Task.Delay(w == "w3" ? 60000 : 0, ct);
                return new QueryResult(w, "", "譯");
            };
            var runner = new NotesImportRunner(store, lookup);
            var outcome = await runner.RunAsync(new[] { "w1", "w2", "w3", "w4" }.Select(w => new NotesImportItem(w)).ToList(), folderId, null, null, cts.Token);
            Assert.True(outcome.Cancelled);
            Assert.Equal(new[] { "w1", "w2" }, outcome.AddedWords);
            var d = new NotesStore(path).Load();
            Assert.Equal(new[] { "w1", "w2" }, d.Folders[0].Entries.Select(e => e.Original)); // 磁碟＝已完成者，無半筆
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancel_ResponseArrivedButCancelledFirst_NotWritten()
    {
        var path = TempPath();
        try
        {
            var (store, folderId) = Seed(path);
            using var cts = new CancellationTokenSource();
            // 回應已到（不拋 OperationCanceledException）而權杖已取消——寫入前再檢查，不寫入
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, _) => { if (w == "w2") { cts.Cancel(); } return Task.FromResult(new QueryResult(w, "", "譯")); };
            var outcome = await new NotesImportRunner(store, lookup).RunAsync(new[] { "w1", "w2", "w3" }.Select(w => new NotesImportItem(w)).ToList(), folderId, null, null, cts.Token);
            Assert.True(outcome.Cancelled);
            Assert.Equal(new[] { "w1" }, new NotesStore(path).Load().Folders[0].Entries.Select(e => e.Original));
        }
        finally { File.Delete(path); }
    }

    // ---- 插入位置：本批相連 ----

    [Fact]
    public async Task BatchInsert_UserAddsAndDeletesMeanwhile_BatchStaysContiguousInListOrder()
    {
        var path = TempPath();
        try
        {
            var (store, folderId) = Seed(path, "old1", "old2");
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, _) =>
            {
                // 匯入期間：使用者從別處加一字到同夾頂端（如字典「加入筆記」）、並刪掉本批第二字
                if (w == "c")
                {
                    var d = store.LoadStrict();
                    d.Folders[0].Entries.Insert(0, NoteEntry.From(new QueryResult("dict", "", ""), DateTimeOffset.Now));
                    NotesStore.RemoveEntry(d, d.Folders[0].Entries.First(e => e.Original == "b").Id);
                    store.Save(d);
                }
                return Task.FromResult(new QueryResult(w, "", "譯"));
            };
            await new NotesImportRunner(store, lookup).RunAsync(new[] { "a", "b", "c", "d" }.Select(w => new NotesImportItem(w)).ToList(), folderId, null, null, CancellationToken.None);
            var order = new NotesStore(path).Load().Folders[0].Entries.Select(e => e.Original).ToList();
            Assert.Equal(new[] { "dict", "a", "c", "d", "old2", "old1" }.Where(x => x.StartsWith("old") is false).ToList(), order.Where(x => !x.StartsWith("old")).ToList());
            Assert.Equal(order.IndexOf("a") + 1, order.IndexOf("c"));                // b 被刪後 c 接在寫入最晚且仍在者（a）之後
            Assert.Equal(order.IndexOf("c") + 1, order.IndexOf("d"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BatchInsertIndex_AllMovedAway_GoesTop()
    {
        var f = new NoteFolder { Name = "x" };
        f.Entries.Add(NoteEntry.From(new QueryResult("z", "", ""), DateTimeOffset.Now));
        Assert.Equal(0, NotesStore.BatchInsertIndex(f, new[] { NoteEntry.KeyOf("gone") }));
        Assert.Equal(1, NotesStore.BatchInsertIndex(f, new[] { NoteEntry.KeyOf("gone"), NoteEntry.KeyOf("z") }));
    }

    // ---- 中斷：未預期例外不往外擲 ----

    [Fact]
    public async Task Interrupted_UnexpectedErrorOutsidePerWord_ReturnsOutcomeWithError_KeepsDone()
    {
        var path = TempPath();
        try
        {
            var (store, folderId) = Seed(path);
            var runner = new NotesImportRunner(store, Ok());
            var outcome = await runner.RunAsync(new[] { "w1", "w2" }.Select(w => new NotesImportItem(w)).ToList(), folderId, null, null, CancellationToken.None,
                p => { if (p.Done == 1) { throw new InvalidOperationException("進度回呼壞了"); } });
            Assert.Equal("進度回呼壞了", outcome.Error);
            Assert.Equal(NotesImportEnding.Interrupted, outcome.Ending);
            Assert.Equal(new[] { "w1" }, outcome.AddedWords);                        // 至該點之逐字保留
        }
        finally { File.Delete(path); }
    }

    // ---- 整合：筆記頁與背景寫入之一致（同步協定）----

    [Fact]
    public async Task PageProtocol_SyncThenWriteById_DoesNotLoseImportedWords_StaleSnapshotWouldLose()
    {
        var path = TempPath();
        try
        {
            var (store, folderId) = Seed(path, "keep");
            var page = store.LoadStrict();          // 筆記頁之記憶體快照（_data）
            var staleControl = store.LoadStrict();  // 對照組：不同步之舊快照
            var keepId = page.Folders[0].Entries[0].Id;
            var runner = new NotesImportRunner(store, Ok());
            await runner.RunAsync(new[] { "w1", "w2" }.Select(w => new NotesImportItem(w)).ToList(), folderId, null, null, CancellationToken.None,
                p =>
                {
                    if (!p.Wrote) { return; }
                    Assert.True(store.TryLoadStrict(out page, out _));                  // App 於同步回呼內呼叫筆記頁同步：換新資料
                    NotesStore.SetEntryColor(page, keepId, "#FFE4EC");                  // 使用者緊接著整理（以 Id 對現行資料）
                    Assert.True(store.TrySave(page, out _));                             // 筆記頁整份寫回
                });
            var d = new NotesStore(path).Load();
            Assert.Equal(new[] { "w1", "w2", "keep" }, d.Folders[0].Entries.Select(e => e.Original)); // 匯入之字不遺失
            Assert.Equal("#FFE4EC", d.Folders[0].Entries.Single(e => e.Original == "keep").Color);   // 使用者之整理也在

            NotesStore.SetEntryColor(staleControl, keepId, "#000000");
            store.Save(staleControl);                                                    // 對照：舊快照整份寫回
            Assert.Equal(new[] { "keep" }, new NotesStore(path).Load().Folders[0].Entries.Select(e => e.Original)); // 風險為真：匯入之字被覆蓋
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LockedNotesFile_RunnerCountsFailure_DoesNotWipeNotes_TryLoadStrictFails()
    {
        var path = TempPath();
        try
        {
            var (store, folderId) = Seed(path, "keep1", "keep2");
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, _) => Task.FromResult(new QueryResult(w, "", "譯"));
            NotesImportOutcome outcome;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))   // 模擬防毒／雲端同步鎖檔
            {
                Assert.False(store.TryLoadStrict(out _, out var err));
                Assert.StartsWith("讀取筆記檔失敗", err);
                Assert.Throws<IOException>(() => store.AddToNamedFolderAndSave(new QueryResult("x", "", ""), "My Notes", null, DateTimeOffset.Now));
                outcome = await new NotesImportRunner(store, lookup).RunAsync(new[] { "w1" }.Select(w => new NotesImportItem(w)).ToList(), folderId, null, null, CancellationToken.None);
            }
            Assert.Single(outcome.Failed);
            Assert.StartsWith("讀取筆記檔失敗", outcome.Failed[0].Reason);
            Assert.False(outcome.TargetFolderMissing);                                   // 讀失敗不誤判目標夾不存在
            Assert.Equal(new[] { "keep2", "keep1" }.OrderBy(x => x), new NotesStore(path).Load().Folders[0].Entries.Select(e => e.Original).OrderBy(x => x)); // 未被空結構洗掉
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadStrict_MissingFile_IsFreshEnsured()
    {
        var store = new NotesStore(TempPath());
        Assert.True(store.TryLoadStrict(out var d, out _));
        Assert.Single(d.Folders);
    }

    // ---- 結構斷言（讀原始碼純文字）----

    [Fact]
    public void Structure_AppRunsImportInBackground_NoModalAiWindow_NoTaskRun_SyncCallback()
    {
        var app = ReadRepoFile("sysLingoIsland", "App.xaml.cs");
        var run = Body(app, "private void RunNotesImport(");
        Assert.DoesNotContain("AiActionWindow", run);
        Assert.DoesNotContain("Task.Run", run);
        Assert.Contains("RunBackgroundImportAsync", run);
        Assert.Contains("FakeLookupDelayMs", run);
        var bg = Body(app, "private async Task RunBackgroundImportAsync(");
        Assert.DoesNotContain("Task.Run", bg);
        Assert.DoesNotContain("IProgress", bg);
        Assert.Contains("progress: OnImportProgress", bg);
        var cb = Body(app, "private void OnImportProgress(");
        Assert.Contains("SyncAfterImportWrite", cb);
        Assert.True(cb.IndexOf("SyncAfterImportWrite", StringComparison.Ordinal) < cb.IndexOf("UpdateImportProgress", StringComparison.Ordinal)); // 先同步再更新畫面
        var own = Body(app, "private void RunOwnOnlyNotesImport(");
        Assert.DoesNotContain("MessageBox", own);
        Assert.Contains("ShowPendingImportResult", own);
    }

    [Fact]
    public void Structure_ExitConfirmBeforeUnsavedGuard_AndRestartConfirm()
    {
        var app = ReadRepoFile("sysLingoIsland", "App.xaml.cs");
        var exit = Body(app, "private void ExitApp(");
        Assert.True(exit.IndexOf("AskStopImport", StringComparison.Ordinal) < exit.IndexOf("ConfirmLeaveCurrentPage", StringComparison.Ordinal));
        Assert.True(exit.IndexOf("ConfirmLeaveCurrentPage", StringComparison.Ordinal) < exit.IndexOf("_importCts?.Cancel()", StringComparison.Ordinal)); // 守衛通過才取消
        Assert.Contains("ConfirmRestart = ConfirmRestartDuringImport", app);
        var about = ReadRepoFile("sysLingoIsland", "modPresent", "AboutPage.xaml.cs");
        Assert.Contains("ConfirmRestart?.Invoke() ?? true", about);
        var options = ReadRepoFile("sysLingoIsland", "modPresent", "OptionsPage.xaml.cs");
        Assert.Contains("RestoreBlockedReason?.Invoke()", Body(options, "private async void OnImportData("));
    }

    [Fact]
    public void Structure_NotesPage_SelectedById_SyncSwapsData_ImportBlocked_ResultWindowNoActivate()
    {
        var page = ReadRepoFile("sysLingoIsland", "modPresent", "NotesPage.xaml.cs");
        Assert.Contains("NotesStore.FindFolder(_data, t.Id)", page.Substring(page.IndexOf("private NoteFolder? Selected", StringComparison.Ordinal), 220));
        var sync = Body(page, "public void SyncAfterImportWrite(");
        Assert.Contains("TryLoadStrict", sync);
        Assert.Contains("_data = fresh", sync);
        Assert.Contains("ImportBlockedReason", Body(page, "private void BeginImportList("));
        Assert.Contains("ImportBlockedReason", Body(page, "private void OnFileDrop("));
        Assert.DoesNotContain("_store.Save(_data)", page);                             // 筆記頁存檔一律經可偵知失敗之出口
        var reload = Body(page, "public void Reload(");
        Assert.Contains("TryLoadStrict", reload);
        var xaml = ReadRepoFile("sysLingoIsland", "modPresent", "NotesImportResultWindow.xaml");
        Assert.Contains("ShowActivated=\"False\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"NotesImportResultWindow\"", xaml);
        var main = ReadRepoFile("sysLingoIsland", "modPresent", "MainWindow.xaml");
        foreach (var id in new[] { "NotesImportProgress", "NotesImportProgressText", "NotesImportProgressBar", "NotesImportProgressCancel" })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", main);
        }
    }

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 " + signature);
        var brace = code.IndexOf('{', start);
        var depth = 0;
        for (var i = brace; i < code.Length; i++)
        {
            if (code[i] == '{') { depth++; }
            else if (code[i] == '}' && --depth == 0) { return code[start..(i + 1)]; }
        }
        return code[start..];
    }

    private static string ReadRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "sysLingoIsland")))
            {
                return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
            }
        }
        throw new InvalidOperationException("找不到 repo 根");
    }
}
