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
/// [modPresent模組] 筆記清單匯入契約（spec#14，#309）之批次執行器——整合層（intTest）：以 fake 查詢委派（<b>零 OpenAI 呼叫</b>）
/// 對**真實 notes.json 臨時檔**驗逐字查詢→寫入目前選取夾（含子夾）→一字一存；失敗不中斷其餘；取消時已加者保留；
/// 已在筆記者勾選＝刷新原筆、不重複建立；夾被刪時退回預設夾不丟字。
/// </summary>
public class NotesImportRunnerTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-import-{Guid.NewGuid():N}.json");

    private static Func<string, CancellationToken, Task<QueryResult>> Ok(Action<string>? onCall = null)
        => (w, _) => { onCall?.Invoke(w); return Task.FromResult(new QueryResult(w, $"[{w}]", $"譯:{w}")); };

    private static (NotesStore Store, string SubId, string TopId) Seed(string path)
    {
        var store = new NotesStore(path);
        var d = store.LoadEnsured();                              // 預設夾 My Notes
        var sub = NotesStore.AddSubFolder(d, d.Folders[0].Id, "Unit 3")!;
        NotesStore.AddToTopFolder(d, d.Folders[0], NoteEntry.From(new QueryResult("banana", "", ""), DateTimeOffset.Now));
        store.Save(d);
        return (store, sub.Id, d.Folders[0].Id);
    }

    [Fact]
    public async Task Run_AddsToSelectedSubFolder_PersistsPerWord_WithPhoneticAndTranslation()
    {
        var path = TempPath();
        try
        {
            var (store, subId, _) = Seed(path);
            var calls = new List<string>();
            var reports = new List<string>();
            var runner = new NotesImportRunner(store, Ok(calls.Add), () => new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
            var outcome = await runner.RunAsync(new[] { "apple", "cherry" }, subId, "#FFE4EC", reports.Add, CancellationToken.None);

            Assert.Equal(2, outcome.Added);
            Assert.Empty(outcome.Skipped);
            Assert.Empty(outcome.Failed);
            Assert.False(outcome.Cancelled);
            Assert.Equal(new[] { "apple", "cherry" }, calls);          // 勾選數＝查詢次數
            Assert.Equal(new[] { "查詢中 1/2：apple", "查詢中 2/2：cherry" }, reports);
            Assert.Equal(new[] { "apple", "cherry" }, outcome.AddedWords);
            Assert.False(outcome.TargetFolderMissing);

            var d = new NotesStore(path).Load();                        // 真檔讀回
            var sub = NotesStore.FindFolder(d, subId)!;
            Assert.Equal(new[] { "apple", "cherry" }, sub.Entries.Select(e => e.Original)); // 批次內保清單序、整批置頂
            Assert.All(sub.Entries, e => { Assert.StartsWith("[", e.Phonetic); Assert.StartsWith("譯:", e.Translation); Assert.Equal("#FFE4EC", e.Color); });
            Assert.Single(d.Folders[0].Entries);                        // 頂層夾只有原本的 banana，未被寫入
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Run_OneFailureDoesNotStopOthers_FailedListedWithReason()
    {
        var path = TempPath();
        try
        {
            var (store, subId, _) = Seed(path);
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, _) =>
                w == "durian" ? throw new QueryException("查詢逾時") : Task.FromResult(new QueryResult(w, "p", "t"));
            var runner = new NotesImportRunner(store, lookup);
            var outcome = await runner.RunAsync(new[] { "apple", "durian", "cherry" }, subId, "", null, CancellationToken.None);

            Assert.Equal(2, outcome.Added);
            Assert.Single(outcome.Failed);
            Assert.Equal(("durian", "查詢逾時"), outcome.Failed[0]);
            var sub = NotesStore.FindFolder(new NotesStore(path).Load(), subId)!;
            Assert.Equal(new[] { "apple", "cherry" }, sub.Entries.Select(e => e.Original)); // 失敗字不佔位，仍保序
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Run_AlreadyInNotes_Checked_RefreshesExistingEntryInPlace_NoDuplicate()
    {
        var path = TempPath();
        try
        {
            var (store, subId, _) = Seed(path);
            // 原筆 banana：空音標、練習分 90、底色 #AAA，位於頂層夾
            var d0 = store.LoadEnsured();
            var idx = d0.Folders[0].Entries.FindIndex(e => e.Key == "banana");
            d0.Folders[0].Entries[idx] = d0.Folders[0].Entries[idx] with { PracticeScore = 90, Color = "#AAA" };
            store.Save(d0);

            var runner = new NotesImportRunner(store, Ok());
            var outcome = await runner.RunAsync(new[] { "Banana", "apple" }, subId, "", null, CancellationToken.None); // 大小寫不同仍同鍵

            Assert.Equal(1, outcome.Added);
            Assert.Equal(1, outcome.Updated);
            Assert.Empty(outcome.Skipped);
            var d = new NotesStore(path).Load();
            var all = NotesStore.AllFolders(d).SelectMany(f => f.Entries).Where(e => e.Key == "banana").ToList();
            Assert.Single(all);                              // 不重複建立
            var banana = all[0];
            Assert.Equal("banana", banana.Original);         // 原文寫法保留（不被 AI 大小寫整形）
            Assert.Equal("[Banana]", banana.Phonetic);       // 音標／翻譯已刷新
            Assert.Equal("譯:Banana", banana.Translation);
            Assert.Equal(90, banana.PracticeScore);          // 練習分保留
            Assert.Equal("#AAA", banana.Color);              // 底色保留
            Assert.Contains(d.Folders[0].Entries, e => e.Key == "banana"); // 留在原夾（頂層），未搬到子夾
            Assert.DoesNotContain(NotesStore.FindFolder(d, subId)!.Entries, e => e.Key == "banana");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RefreshEntryByKey_UnknownKey_ReturnsFalse_NoWrite()
    {
        var path = TempPath();
        try
        {
            var (store, _, _) = Seed(path);
            var before = File.GetLastWriteTimeUtc(path);
            Assert.False(store.RefreshEntryByKeyAndSave(new QueryResult("no-such-word", "p", "t"))); // 原筆已不在→呼叫端計略過
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Run_Cancel_StopsEarly_KeepsAlreadyAdded()
    {
        var path = TempPath();
        try
        {
            var (store, subId, _) = Seed(path);
            using var cts = new CancellationTokenSource();
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, ct) =>
            {
                if (w == "cherry") { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
                return Task.FromResult(new QueryResult(w, "p", "t"));
            };
            var runner = new NotesImportRunner(store, lookup);
            var outcome = await runner.RunAsync(new[] { "apple", "cherry", "durian" }, subId, "", null, cts.Token);

            Assert.True(outcome.Cancelled);
            Assert.Equal(1, outcome.Added);
            Assert.Empty(outcome.Failed);                                // 取消不算失敗
            var sub = NotesStore.FindFolder(new NotesStore(path).Load(), subId)!;
            Assert.Equal(new[] { "apple" }, sub.Entries.Select(e => e.Original)); // 已加者保留、未加者不建
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Run_FolderDeletedMidway_FallsBackToDefaultFolder_NoWordLost()
    {
        var path = TempPath();
        try
        {
            var (store, subId, topId) = Seed(path);
            var runner = new NotesImportRunner(store, Ok());
            var outcome = await runner.RunAsync(new[] { "apple" }, "no-such-folder-id", "", null, CancellationToken.None);
            Assert.True(outcome.TargetFolderMissing);   // 結果表據此如實改口、不寫原夾名
            var d = new NotesStore(path).Load();
            Assert.Contains(d.Folders[0].Entries, e => e.Original == "apple");
            Assert.Equal(topId, d.Folders[0].Id);
            Assert.Empty(NotesStore.FindFolder(d, subId)!.Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Run_EmptyQueryResult_CountsAsFailed()
    {
        var path = TempPath();
        try
        {
            var (store, subId, _) = Seed(path);
            var runner = new NotesImportRunner(store, (w, _) => Task.FromResult(new QueryResult("", "", "")));
            var outcome = await runner.RunAsync(new[] { "apple" }, subId, "", null, CancellationToken.None);
            Assert.Equal(0, outcome.Added);
            Assert.Single(outcome.Failed);
            Assert.Contains("沒有回傳內容", outcome.Failed[0].Reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MakeLookup_RoutesSingleWordVsPhrase_WithoutCallingNetwork()
    {
        // 只驗路徑選擇規則本身（IsSingleWord），不建 QueryService 打網路——正式委派之接線由 App 端承擔、端端另驗。
        Assert.True(NotesImport.IsSingleWord("apple"));
        Assert.False(NotesImport.IsSingleWord("take it easy"));
    }

    [Fact]
    public async Task Run_BatchOrder_PlacedOnTopAboveExisting_InListOrder()
    {
        var path = TempPath();
        try
        {
            var (store, _, topId) = Seed(path); // 頂層夾已有 banana
            var runner = new NotesImportRunner(store, Ok());
            await runner.RunAsync(new[] { "one", "two", "three" }, topId, "", null, CancellationToken.None);
            var top = new NotesStore(path).Load().Folders[0];
            Assert.Equal(new[] { "one", "two", "three", "banana" }, top.Entries.Select(e => e.Original)); // 整批置頂、批內保序
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Run_SystemicFailure_StopsAfterThreeConsecutive_RestMarkedNotQueried()
    {
        var path = TempPath();
        try
        {
            var (store, subId, _) = Seed(path);
            var calls = 0;
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, _) => { calls++; throw new QueryException("OPENAI_API_KEY 未設定"); };
            var runner = new NotesImportRunner(store, lookup);
            var words = Enumerable.Range(1, 10).Select(i => $"w{i}").ToArray();
            var outcome = await runner.RunAsync(words, subId, "", null, CancellationToken.None);

            Assert.Equal(NotesImportRunner.SystemicFailureStreak, calls);          // 只真的查了 3 次
            Assert.Equal(10, outcome.Failed.Count);                                 // 其餘如實計失敗
            Assert.Equal(3, outcome.Failed.Count(f => !f.Reason.StartsWith(NotesImportRunner.NotQueriedReason))); // 真的查過的 3 字帶原始錯誤
            Assert.Equal(7, outcome.Failed.Count(f => f.Reason.StartsWith(NotesImportRunner.NotQueriedReason) && f.Reason.Contains("未設定"))); // 帶首字實際錯誤
            Assert.Equal(0, outcome.Added);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Run_SystemicFailure_NotTriggered_WhenAnySuccessBefore()
    {
        var path = TempPath();
        try
        {
            var (store, subId, _) = Seed(path);
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, _) =>
                w == "ok" ? Task.FromResult(new QueryResult(w, "p", "t")) : throw new QueryException("逾時");
            var runner = new NotesImportRunner(store, lookup);
            var outcome = await runner.RunAsync(new[] { "ok", "a", "b", "c", "d", "e" }, subId, "", null, CancellationToken.None);
            Assert.Equal(1, outcome.Added);
            Assert.Equal(5, outcome.Failed.Count);
            Assert.DoesNotContain(outcome.Failed, f => f.Reason.StartsWith(NotesImportRunner.NotQueriedReason)); // 有過成功→每字都真的查
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Run_AiRespellsOriginal_KeyStillFollowsUserWord()
    {
        var path = TempPath();
        try
        {
            var (store, subId, _) = Seed(path); // 頂層已有 banana
            // AI 把 "bananna"（使用者誤拼、確認頁判為新字）校正回 "banana"——若照 AI 字取鍵會去更新別筆；須以使用者原字為準
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, _) => Task.FromResult(new QueryResult("banana", "p", "t"));
            var runner = new NotesImportRunner(store, lookup);
            var outcome = await runner.RunAsync(new[] { "bananna" }, subId, "", null, CancellationToken.None);
            Assert.Equal(1, outcome.Added);
            Assert.Equal(0, outcome.Updated);
            var d = new NotesStore(path).Load();
            Assert.Contains(NotesStore.FindFolder(d, subId)!.Entries, e => e.Original == "bananna"); // 以使用者原字入筆
            Assert.Equal("", d.Folders[0].Entries.Single(e => e.Key == "banana").Phonetic);          // 原 banana 未被動到
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FolderPath_ShowsParentChain_ForSubFolder()
    {
        var path = TempPath();
        try
        {
            var (store, subId, topId) = Seed(path);
            var d = store.LoadEnsured();
            Assert.Equal("My Notes › Unit 3", NotesStore.FolderPath(d, subId));
            Assert.Equal("My Notes", NotesStore.FolderPath(d, topId));
            Assert.Equal("", NotesStore.FolderPath(d, "nope"));
        }
        finally { File.Delete(path); }
    }
}
