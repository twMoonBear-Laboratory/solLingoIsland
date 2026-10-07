using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LingoIsland.Present;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modPresent模組] 筆記清單匯入契約「整批撤銷」（spec#14 擴充，#324）——單元（撤銷規則 <see cref="NoteImportUndo"/>、合併、文案、撤銷狀態四態）、
/// 整合（真 notes.json 臨時檔：fake 執行器之寫入日誌＝實際落地者→<see cref="NotesStore.UndoImportAndSave"/>→回到匯入前；取消中途、存檔失敗、全部跳過不寫、鎖檔、損毀、筆記頁快照蓋回之對照）、
/// 結構斷言（App 接線、結果視窗與筆記頁鈕）。線上查詢一律 fake——零 OpenAI 額度。
/// </summary>
public class NotesImportUndoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 1, 2, 3, TimeSpan.FromHours(8));
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-324-{Guid.NewGuid():N}.json");

    private static NoteEntry E(string word, string tr = "", string ph = "", string color = "", int score = -1)
        => new(Guid.NewGuid().ToString("N"), T0, word, ph, tr, color, score);

    /// <summary>兩夾：A（目標）與 B。</summary>
    private static (NotesData D, NoteFolder A, NoteFolder B) Data()
    {
        var d = new NotesData();
        var a = new NoteFolder { Name = "A" };
        var b = new NoteFolder { Name = "B" };
        d.Folders.Add(a); d.Folders.Add(b);
        return (d, a, b);
    }

    private static NoteWriteRecord Added(NoteEntry e, NoteFolder f) => new(null, e, f.Id);
    private static NoteWriteRecord Updated(NoteEntry before, NoteEntry after, NoteFolder f) => new(before, after, f.Id);

    // ---- 撤銷規則（純函式）----

    [Fact]
    public void Added_Unchanged_IsRemoved_OthersUntouched()
    {
        var (d, a, _) = Data();
        var keep = E("keep", "留著"); var x = E("orbit", "軌道");
        a.Entries.AddRange(new[] { x, keep });
        var plan = NoteImportUndo.Apply(d, new[] { Added(x, a) });
        Assert.Equal(1, plan.Removed);
        Assert.Equal(new[] { keep }, a.Entries);
    }

    [Fact]
    public void Added_Deleted_IsSkippedAsGone()
    {
        var (d, a, _) = Data();
        var x = E("orbit");
        var plan = NoteImportUndo.Apply(d, new[] { Added(x, a) });
        var s = Assert.Single(plan.Skips);
        Assert.Equal(NoteImportUndo.ReasonGone, s.Reason);
        Assert.Equal("orbit", s.Word);
    }

    [Theory]
    [InlineData("translation")]
    [InlineData("phonetic")]
    [InlineData("original")]
    [InlineData("color")]
    [InlineData("score")]
    public void Added_ModifiedAfterImport_IsKept(string what)
    {
        var (d, a, _) = Data();
        var x = E("orbit", "軌道", "/ˈɔːrbɪt/");
        var cur = what switch
        {
            "translation" => x with { Translation = "使用者改的" },
            "phonetic" => x with { Phonetic = "" },
            "original" => x with { Original = "orbital" },
            "color" => x with { Color = "#FFD1DC" },
            _ => x with { PracticeScore = 72 },
        };
        a.Entries.Add(cur);
        var plan = NoteImportUndo.Apply(d, new[] { Added(x, a) });
        Assert.Equal(NoteImportUndo.ReasonModified, Assert.Single(plan.Skips).Reason);
        Assert.Equal(new[] { cur }, a.Entries); // 一字不動
    }

    [Fact]
    public void Added_MovedToOtherFolder_IsKept_ButReorderedWithinSameFolder_IsRemoved()
    {
        var (d, a, b) = Data();
        var moved = E("moved"); var reordered = E("reordered"); var other = E("other");
        b.Entries.Add(moved);
        a.Entries.AddRange(new[] { other, reordered }); // 原本插在頂端，被拖到下面
        var plan = NoteImportUndo.Apply(d, new[] { Added(moved, a), Added(reordered, a) });
        Assert.Equal(NoteImportUndo.ReasonMoved, Assert.Single(plan.Skips).Reason);
        Assert.Equal(1, plan.Removed);
        Assert.Equal(new[] { other }, a.Entries);
        Assert.Equal(new[] { moved }, b.Entries);
    }

    [Fact]
    public void Updated_Unchanged_RestoresPhoneticAndTranslation_KeepsLaterColorScoreAndFolder()
    {
        var (d, a, b) = Data();
        var before = E("banana", "香蕉", "/bəˈnænə/");
        var after = before with { Phonetic = "/bəˈnɑːnə/", Translation = "〔新〕香蕉" };
        b.Entries.Add(after with { Color = "#C8E6C9", PracticeScore = 90 }); // 之後改了底色、練習過、移到 B
        var plan = NoteImportUndo.Apply(d, new[] { Updated(before, after, a) });
        Assert.Equal(1, plan.Restored);
        var now = Assert.Single(b.Entries);
        Assert.Equal(("/bəˈnænə/", "香蕉", "#C8E6C9", 90), (now.Phonetic, now.Translation, now.Color, now.PracticeScore));
        Assert.Equal(before.Id, now.Id);
    }

    [Fact]
    public void Updated_ContentChangedOrDeleted_IsSkipped()
    {
        var (d, a, _) = Data();
        var b1 = E("w1", "一"); var a1 = b1 with { Translation = "新一" };
        var b2 = E("w2", "二"); var a2 = b2 with { Translation = "新二" };
        a.Entries.Add(a1 with { Translation = "使用者再改" });
        var plan = NoteImportUndo.Apply(d, new[] { Updated(b1, a1, a), Updated(b2, a2, a) });
        Assert.Equal(new[] { NoteImportUndo.ReasonGoneUpdated, NoteImportUndo.ReasonModified }, plan.Skips.Select(s => s.Reason)); // 逆序
        Assert.Equal("使用者再改", a.Entries[0].Translation);
    }

    [Fact]
    public void SameIdTwice_AddedThenRefreshed_MergedAsOneAddedEntry()
    {
        // 背景匯入期間本批新增之 X 被「編輯重譯」改成本批後面之字 Y，執行器輪到 Y 時刷新同一筆
        var (d, a, _) = Data();
        var x = E("lantern", "燈籠");
        var edited = x with { Original = "orbit", Translation = "軌道（手改）", PracticeScore = -1 };
        var refreshed = edited with { Translation = "〔新〕軌道" };
        a.Entries.Add(refreshed);
        var journal = new[] { Added(x, a), Updated(edited, refreshed, a) };
        var merged = Assert.Single(NoteImportUndo.Merge(journal));
        Assert.True(merged.IsAdded);
        var plan = NoteImportUndo.Apply(d, journal);
        Assert.Single(plan.Items);
        Assert.Equal(1, plan.Removed);
        Assert.Empty(a.Entries);
    }

    [Fact]
    public void Plan_DoesNotMutate_EmptyJournal_EmptyPlan()
    {
        var (d, a, _) = Data();
        var x = E("orbit"); a.Entries.Add(x);
        var plan = NoteImportUndo.Plan(d, new[] { Added(x, a) });
        Assert.Equal(1, plan.Removed);
        Assert.Single(a.Entries);
        Assert.Empty(NoteImportUndo.Plan(d, Array.Empty<NoteWriteRecord>()).Items);
        Assert.False(NoteImportUndo.Plan(d, Array.Empty<NoteWriteRecord>()).HasChange);
    }

    // ---- 文案 ----

    private static NoteUndoPlan PlanOf(int removed, int restored, params string[] skipped)
        => new(Enumerable.Range(0, removed).Select(i => new NoteUndoItem("r" + i, true, NoteUndoAction.Remove, ""))
            .Concat(Enumerable.Range(0, restored).Select(i => new NoteUndoItem("u" + i, false, NoteUndoAction.Restore, "")))
            .Concat(skipped.Select(w => new NoteUndoItem(w, true, NoteUndoAction.Skip, NoteImportUndo.ReasonModified))).ToList());

    [Fact]
    public void ConfirmText_OmitsZeroLines_ListsSkippedWords_AtMostTen()
    {
        var t1 = NotesImportUndoText.ConfirmText(PlanOf(2, 0));
        Assert.Contains("・移除這次新增的 2 字", t1);
        Assert.DoesNotContain("還原", t1);
        Assert.DoesNotContain("保持現狀", t1);
        Assert.EndsWith("撤銷後無法再復原；已花用的 AI 查詢費用不會退回。", t1);
        var many = Enumerable.Range(1, 12).Select(i => "s" + i).ToArray();
        var t2 = NotesImportUndoText.ConfirmText(PlanOf(0, 1, many));
        Assert.Contains("・把這次更新的 1 字還原為匯入前的音標與中譯", t2);
        Assert.Contains("・12 字在匯入後已修改、移動或刪除，保持現狀不動：s1、s2、s3、s4、s5、s6、s7、s8、s9、s10…等 12 字", t2);
        Assert.DoesNotContain("s11", t2);
    }

    [Fact]
    public void Summary_Toast_NothingText()
    {
        var p = PlanOf(2, 1, "orbit");
        Assert.Equal("已撤銷本次匯入：移除 2 字、還原 1 字、跳過 1 字。\n跳過（保持現狀）：\n· orbit——匯入後已修改", NotesImportUndoText.Summary(p));
        Assert.Equal("已撤銷本次匯入：移除 2 字、還原 1 字（跳過 1 字）", NotesImportUndoText.Toast(p));
        Assert.Equal("已撤銷本次匯入：移除 2 字、還原 0 字", NotesImportUndoText.Toast(PlanOf(2, 0)));
        Assert.Equal("這次匯入的 3 字在匯入後都已修改、移動或刪除，沒有可撤銷的字。", NotesImportUndoText.NothingText(3));
    }

    // ---- 撤銷狀態四態 ----

    [Fact]
    public void LastResult_States_TerminalAndTexts()
    {
        var (_, a, _) = Data();
        var journal = new[] { Added(E("w"), a) };
        Assert.Equal(NotesImportUndoState.NotOffered, new NotesImportLastResult(NotesImportEnding.Completed, "body", Array.Empty<NoteWriteRecord>()).State);

        var m = new NotesImportLastResult(NotesImportEnding.Cancelled, "匯入已取消：…", journal);
        Assert.True(m.CanUndo);
        Assert.Equal(("匯入清單：已取消", "匯入已取消"), (m.Title, m.Header));
        Assert.Equal(NotesImportUndoText.HintAvailable, NotesImportUndoText.Hint(m.State));
        var changes = 0; m.Changed += () => changes++;
        m.MarkUndone(PlanOf(1, 0));
        Assert.Equal(NotesImportUndoState.Undone, m.State);
        Assert.Equal(("匯入清單：已撤銷", "已撤銷本次匯入"), (m.Title, m.Header));
        Assert.StartsWith("已撤銷本次匯入：移除 1 字、還原 0 字、跳過 0 字。\n\n——以下為原本的匯入結果——\n匯入已取消：", m.Body);
        Assert.Equal("", NotesImportUndoText.Hint(m.State));
        m.Expire(); m.MarkNothingToUndo(); // 終態不再轉移
        Assert.Equal(NotesImportUndoState.Undone, m.State);
        Assert.Equal(1, changes);

        var e = new NotesImportLastResult(NotesImportEnding.Completed, "b", journal);
        e.Expire();
        Assert.Equal((NotesImportUndoState.Expired, false), (e.State, e.CanUndo));
        Assert.Equal(NotesImportUndoText.HintExpired, NotesImportUndoText.Hint(e.State));
        e.MarkUndone(PlanOf(1, 0));
        Assert.Equal("b", e.Body);

        var n = new NotesImportLastResult(NotesImportEnding.Completed, "b", journal);
        n.MarkNothingToUndo();
        Assert.Equal(NotesImportUndoState.NothingToUndo, n.State);
        Assert.Equal(NotesImportUndoText.HintNothing, NotesImportUndoText.Hint(n.State));
    }

    // ---- 整合（真 notes.json 臨時檔）----

    private static (NotesStore Store, string FolderId) Seed(string path, params (string Word, string Tr)[] existing)
    {
        var store = new NotesStore(path);
        var d = store.LoadEnsured();
        foreach (var (w, tr) in existing) { NotesStore.AddToTopFolder(d, d.Folders[0], NoteEntry.From(new QueryResult(w, "/old/", tr), T0)); }
        store.Save(d);
        return (store, d.Folders[0].Id);
    }

    private static string Snap(NotesStore s) => JsonSerializer.Serialize(s.LoadStrict());

    [Fact]
    public async Task Integration_OnlineAddAndRefresh_PlusOwnAddAndUpdate_UndoRestoresExactly()
    {
        var path = TempPath();
        try
        {
            var (store, folder) = Seed(path, ("banana", "香蕉"), ("grape", "葡萄"));
            var before = Snap(store);
            var runner = new NotesImportRunner(store, NotesImportRunner.MakeFakeLookup(1));
            var items = new[]
            {
                new NotesImportItem("orbit"), new NotesImportItem("banana"),          // 線上新增＋線上刷新（勾選已在筆記）
                new NotesImportItem("kiwi", "奇異果"), new NotesImportItem("grape", "自備葡萄"), // 自備新增＋自備更新
            };
            var o = await runner.RunAsync(items, folder, null, null, CancellationToken.None);
            Assert.Equal((2, 2), (o.Added, o.Updated));
            Assert.Equal(4, o.Journal.Count);
            Assert.Equal(2, o.Journal.Count(r => r.IsAdded));
            var plan = store.UndoImportAndSave(o.Journal);
            Assert.Equal((2, 2, 0), (plan.Removed, plan.Restored, plan.Skipped));
            Assert.Equal(before, Snap(store)); // 回到匯入前（含順序、音標、中譯）
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Integration_CancelledMidway_JournalIsWrittenOnly_UndoRemovesThem()
    {
        var path = TempPath();
        try
        {
            var (store, folder) = Seed(path, ("keep", "留"));
            var before = Snap(store);
            using var cts = new CancellationTokenSource();
            var n = 0;
            var runner = new NotesImportRunner(store, async (w, ct) =>
            {
                if (++n == 3) { cts.Cancel(); }
                await Task.Delay(1, ct);
                return new QueryResult(w, "", "〔假〕" + w);
            });
            var o = await runner.RunAsync(new[] { "aa", "bb", "cc", "dd" }, folder, null, null, cts.Token);
            Assert.True(o.Cancelled);
            Assert.Equal(o.Added, o.Journal.Count);
            Assert.Equal(2, o.Journal.Count);
            Assert.Equal(2, store.UndoImportAndSave(o.Journal).Removed);
            Assert.Equal(before, Snap(store));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Integration_SnapshotCarriesJournal()
    {
        var path = TempPath();
        try
        {
            var (store, folder) = Seed(path);
            NotesImportRunner? runner = null;
            NotesImportOutcome? mid = null;
            runner = new NotesImportRunner(store, (w, _) =>
            {
                if (w == "bb") { mid = runner!.Snapshot(); }
                return Task.FromResult(new QueryResult(w, "", "t"));
            });
            await runner.RunAsync(new[] { "aa", "bb" }, folder, null, null, CancellationToken.None);
            Assert.Equal("aa", Assert.Single(mid!.Journal).After.Original);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Integration_WriteFailure_NotJournaled()
    {
        var path = TempPath();
        try
        {
            var (store, folder) = Seed(path);
            NotesImportOutcome o;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                o = await new NotesImportRunner(store, NotesImportRunner.MakeFakeLookup(1)).RunAsync(new[] { "aa" }, folder, null, null, CancellationToken.None);
            }
            Assert.Single(o.Failed);
            Assert.Empty(o.Journal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Integration_AllSkipped_NoWrite_LockedThrows_FileUnchanged_CorruptThrowsCorrupt()
    {
        var path = TempPath();
        try
        {
            var (store, folder) = Seed(path);
            var o = await new NotesImportRunner(store, NotesImportRunner.MakeFakeLookup(1)).RunAsync(new[] { "aa" }, folder, null, null, CancellationToken.None);
            // 使用者匯入後改了底色＝全部跳過：不寫檔
            var d = store.LoadStrict();
            NotesStore.SetEntryColor(d, o.Journal[0].After.Id, "#FFD1DC");
            store.Save(d);
            var saves = store.SaveCount;
            var plan = store.UndoImportAndSave(o.Journal);
            Assert.False(plan.HasChange);
            Assert.Equal(saves, store.SaveCount);

            // 被鎖：擲 IOException、檔不變
            var bytes = File.ReadAllBytes(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => store.UndoImportAndSave(o.Journal));
                Assert.ThrowsAny<IOException>(() => store.PlanUndo(o.Journal));
            }
            Assert.Equal(bytes, File.ReadAllBytes(path));

            File.WriteAllText(path, "{ broken");
            Assert.Throws<NotesFileCorruptException>(() => store.UndoImportAndSave(o.Journal));
            Assert.Equal("{ broken", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Integration_NotesPageStaleSnapshot_WouldWriteBack_SyncedDoesNot()
    {
        var path = TempPath();
        try
        {
            var (store, folder) = Seed(path, ("keep", "留"));
            var o = await new NotesImportRunner(store, NotesImportRunner.MakeFakeLookup(1)).RunAsync(new[] { "aa", "bb" }, folder, null, null, CancellationToken.None);
            var pageData = store.LoadStrict(); // 筆記頁之記憶體資料（匯入後同步過）
            store.UndoImportAndSave(o.Journal);

            // 對照組：未同步之舊快照整份存檔→撤銷被蓋回（證明風險為真）
            var stale = JsonSerializer.Deserialize<NotesData>(JsonSerializer.Serialize(pageData))!;
            NotesStore.SetEntryColor(stale, stale.Folders[0].Entries.Single(e => e.Original == "keep").Id, "#C8E6C9");
            store.Save(stale);
            Assert.Equal(3, store.LoadStrict().Folders[0].Entries.Count);
            store.UndoImportAndSave(o.Journal); // 還原對照組之影響（aa、bb 被寫回後仍與寫入時相同→移除）

            // 契約 ⑥：撤銷後同步（以磁碟現況換掉記憶體資料）再整份存檔→撤銷不被蓋回
            var synced = store.LoadStrict();
            NotesStore.SetEntryColor(synced, synced.Folders[0].Entries.Single(e => e.Original == "keep").Id, "#FFD1DC");
            store.Save(synced);
            Assert.Equal(new[] { "keep" }, store.LoadStrict().Folders[0].Entries.Select(e => e.Original));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Integration_TargetFolderDeleted_FallbackFolderRecorded_UndoFindsIt()
    {
        var path = TempPath();
        try
        {
            var (store, _) = Seed(path);
            var o = await new NotesImportRunner(store, NotesImportRunner.MakeFakeLookup(1)).RunAsync(new[] { "aa" }, "no-such-folder", null, null, CancellationToken.None);
            Assert.Equal(store.LoadStrict().Folders[0].Id, Assert.Single(o.Journal).FolderId);
            Assert.Equal(1, store.UndoImportAndSave(o.Journal).Removed);
        }
        finally { File.Delete(path); }
    }

    // ---- 結構斷言 ----

    [Fact]
    public void Structure_App_ExpiresOnNewBatch_SetsLastImportBeforeResultBranches()
    {
        var cs = Code("sysLingoIsland", "App.xaml.cs");
        var bg = Body(cs, "RunBackgroundImportAsync", "Task");
        Assert.True(Idx(bg, "BeginNewImportForUndo()") < Idx(bg, "runner.RunAsync("), "背景：執行器開始前使上一批失效");
        Assert.True(Idx(bg, "SetLastImport(outcome, folderName)") < Idx(bg, "FinishBackgroundImport(outcome"), "背景：上次匯入先於結果呈現");
        var own = Body(cs, "RunOwnOnlyNotesImport");
        Assert.True(Idx(own, "BeginNewImportForUndo()") < Idx(own, "runner.RunOwnOnly("));
        Assert.True(Idx(own, "SetLastImport(outcome, folderName)") < Idx(own, "ShowPendingImportResult"));
        Assert.DoesNotContain("BeginNewImportForUndo", Body(cs, "RunNotesImport")); // 守備或缺金鑰而未執行者不失效
        Assert.Contains("_undoPrompting", Body(cs, "ShowPendingImportResult"));
    }

    [Fact]
    public void Structure_App_UndoFlow_RecheckAfterConfirm_SyncPage_NoFailureRecord()
    {
        var cs = Code("sysLingoIsland", "App.xaml.cs");
        var u = Body(cs, "UndoLastImport");
        var confirm = Idx(u, "NotesImportUndoText.ConfirmText(trial)");
        var recheck = u.IndexOf("ReferenceEquals(_lastImport, m)", confirm, StringComparison.Ordinal);
        Assert.True(Idx(u, "_notesStore.PlanUndo(") < confirm);
        Assert.True(recheck > confirm && recheck < Idx(u, "_notesStore.UndoImportAndSave("), "「是」之後先重查才寫入");
        Assert.True(Idx(u, "_notesStore.UndoImportAndSave(") < Idx(u, "_notesPage?.SyncAfterUndoWrite()"));
        Assert.True(Idx(u, "_notesPage?.SyncAfterUndoWrite()") < Idx(u, "m.MarkUndone(plan)"));
        Assert.Contains("RestoreRunning", u);
        Assert.Contains("ImportRunning", u);
        Assert.DoesNotContain("ImportFailure", u); // ⑨：不動失敗紀錄
        var edit = Body(cs, "EditNoteEntryAsync", "Task");
        Assert.True(Idx(edit, "EntryGoneEditToast") < Idx(edit, "QueryTextAsync("), "編輯重譯於查詢前確認條目仍在");
        var page = Code("sysLingoIsland", "modPresent", "NotesPage.xaml.cs");
        Assert.Contains("NotesImportUndoText.EntryGoneScoreToast", page);
    }

    [Fact]
    public void Structure_Xaml_ResultWindowAndNotesPageButtons()
    {
        var rw = ReadRepoFile("sysLingoIsland", "modPresent", "NotesImportResultWindow.xaml");
        var undo = Regex.Match(rw, "<Button x:Name=\"UndoBtn\".*?</Button>", RegexOptions.Singleline).Value;
        Assert.Contains("AutomationProperties.AutomationId=\"NotesImportResultUndo\"", undo);
        Assert.Contains("ToolTipService.ShowOnDisabled=\"True\"", undo);
        Assert.Matches(new Regex("<Trigger Property=\"IsEnabled\" Value=\"False\">\\s*<Setter Property=\"Opacity\""), undo);
        Assert.Contains("AutomationProperties.AutomationId=\"NotesImportResultUndoHint\"", rw);
        Assert.True(rw.IndexOf("NotesImportResultUndo\"", StringComparison.Ordinal) < rw.IndexOf("NotesImportResultOk", StringComparison.Ordinal));
        var np = ReadRepoFile("sysLingoIsland", "modPresent", "NotesPage.xaml");
        var last = Regex.Match(np, "<Button x:Name=\"LastResultBtn\".*?</Button>", RegexOptions.Singleline).Value;
        Assert.Contains("AutomationProperties.AutomationId=\"NotesImportLastResultBtn\"", last);
        Assert.Contains("ToolTipService.ShowOnDisabled=\"True\"", last);
        Assert.Contains("Visibility=\"Collapsed\"", last);
    }

    private static int Idx(string s, string token)
    {
        var i = s.IndexOf(token, StringComparison.Ordinal);
        Assert.True(i >= 0, "找不到：" + token);
        return i;
    }

    private static string Code(params string[] parts)
        => Regex.Replace(Regex.Replace(ReadRepoFile(parts), "/\\*.*?\\*/", "", RegexOptions.Singleline), "//[^\\n]*", "");

    private static string Body(string code, string method, string ret = "void|bool")
    {
        var m = Regex.Match(code, @"\b(" + ret + @")\s+" + method + @"\s*\(");
        Assert.True(m.Success, $"找不到方法 {method}");
        var start = code.IndexOf('{', m.Index);
        var depth = 0;
        for (var i = start; i < code.Length; i++)
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
