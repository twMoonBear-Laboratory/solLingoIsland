using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LingoIsland.Present;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modPresent模組] 筆記清單匯入契約「只勾上次失敗字」（spec#14 擴充，#323）——單元（更新規則、按鈕 N、狀態前置、儲存之損毀退路、測試縫）、
/// 整合（真 notes.json 臨時檔＋真紀錄檔：fake 注入失敗→紀錄→重匯只勾失敗→成功→清除；取消、早停、自備段寫入失敗、Snapshot）、
/// 結構斷言（App 各收尾與結束入口之接線、確認頁鈕）。線上查詢一律 fake——零 OpenAI 額度。
/// </summary>
public class NotesImportRetryFailedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
    private static string TempPath(string ext = ".json") => Path.Combine(Path.GetTempPath(), $"lingoisland-323-{Guid.NewGuid():N}{ext}");

    private static ImportFailureSource Src(string path, params string[] words) => new(path, words);
    private static ImportFailureRecord Rec(string path, DateTimeOffset at, params string[] words) => new(path, words, at);

    private static (List<ImportFailureRecord> R, bool Changed) Apply(IReadOnlyList<ImportFailureRecord> old, IReadOnlyList<ImportFailureSource> batch,
        string[] ok, string[] bad, DateTimeOffset? now = null, int max = ImportFailureStore.MaxFiles)
        => ImportFailureLog.Apply(old, batch, ok, bad, now ?? T0.AddHours(1), max);

    // ---- 更新規則（純函式）----

    [Fact]
    public void Apply_FailedRecordedInFileOrder_AndToEveryFileContainingTheWord()
    {
        var (r, changed) = Apply(Array.Empty<ImportFailureRecord>(),
            new[] { Src(@"C:\l\a.txt", "zeta", "Alpha", "beta"), Src(@"C:\l\b.txt", "alpha", "gamma") },
            ok: new[] { "beta", "gamma" }, bad: new[] { "zeta", "alpha" });
        Assert.True(changed);
        Assert.Equal(new[] { "zeta", "Alpha" }, r.Single(x => x.Path == @"C:\l\a.txt").Words); // 依該檔清單序、存該檔首見寫法
        Assert.Equal(new[] { "alpha" }, r.Single(x => x.Path == @"C:\l\b.txt").Words);          // 跨檔重複：b 也記
    }

    [Fact]
    public void Apply_SuccessRemovesFromAllFiles_IncludingFilesNotInBatch()
    {
        var old = new[] { Rec(@"C:\l\a.txt", T0, "w1", "w2"), Rec(@"C:\l\b.txt", T0, "w1") };
        var (r, _) = Apply(old, new[] { Src(@"C:\l\a.txt", "w1", "w2") }, ok: new[] { "W1" }, bad: Array.Empty<string>());
        Assert.Equal(new[] { "w2" }, r.Single(x => x.Path == @"C:\l\a.txt").Words);
        Assert.DoesNotContain(r, x => x.Path == @"C:\l\b.txt"); // 成功是字的屬性：b 之 w1 亦剔除、空即刪筆
    }

    [Fact]
    public void Apply_UnprocessedUnselectedOrSkippedWordsKeepTheirState()
    {
        // 舊紀錄 w1、w2；本次只處理了 w3（成功）——w1（未勾）、w2（取消後未處理或略過）維持
        var old = new[] { Rec(@"C:\l\a.txt", T0, "w1", "w2") };
        var (r, _) = Apply(old, new[] { Src(@"C:\l\a.txt", "w1", "w2", "w3") }, ok: new[] { "w3" }, bad: Array.Empty<string>());
        Assert.Equal(new[] { "w1", "w2" }, r.Single().Words);
    }

    [Fact]
    public void Apply_WordsNoLongerInFileAreDropped_AndAllSucceededRemovesEntry()
    {
        var old = new[] { Rec(@"C:\l\a.txt", T0, "gone", "w1") };
        var (r1, _) = Apply(old, new[] { Src(@"C:\l\a.txt", "w1", "new") }, ok: Array.Empty<string>(), bad: Array.Empty<string>());
        Assert.Equal(new[] { "w1" }, r1.Single().Words);
        var (r2, changed) = Apply(r1, new[] { Src(@"C:\l\a.txt", "w1", "new") }, ok: new[] { "w1" }, bad: Array.Empty<string>());
        Assert.True(changed);
        Assert.Empty(r2); // 全數成功＝清除
    }

    [Fact]
    public void Apply_IsIdempotent_AndRefreshesUpdatedAtOnlyForBatchFiles()
    {
        var old = new[] { Rec(@"C:\l\a.txt", T0, "w1"), Rec(@"C:\l\other.txt", T0, "x") };
        var batch = new[] { Src(@"C:\l\a.txt", "w1", "w2") };
        var now = T0.AddDays(1);
        var (r1, _) = Apply(old, batch, ok: Array.Empty<string>(), bad: new[] { "w2" }, now);
        var (r2, changed2) = Apply(r1, batch, ok: Array.Empty<string>(), bad: new[] { "w2" }, now);
        Assert.False(changed2); // 同一結果重複套用不變（結束入口 Snapshot 後收尾又跑到亦無妨）
        Assert.Equal(r1.Select(x => (x.Path, string.Join(",", x.Words))), r2.Select(x => (x.Path, string.Join(",", x.Words))));
        Assert.Equal(now, r1.Single(x => x.Path == @"C:\l\a.txt").UpdatedAt);
        Assert.Equal(T0, r1.Single(x => x.Path == @"C:\l\other.txt").UpdatedAt); // 不在本批不刷新
    }

    [Fact]
    public void Apply_NothingToRecord_NoChange()
    {
        var (r, changed) = Apply(Array.Empty<ImportFailureRecord>(), new[] { Src(@"C:\l\a.txt", "w1") }, ok: new[] { "w1" }, bad: Array.Empty<string>());
        Assert.Empty(r);
        Assert.False(changed);
    }

    [Fact]
    public void Apply_PathComparedCaseInsensitively_NewSpellingKept()
    {
        var old = new[] { Rec(@"C:\L\A.TXT", T0, "w1") };
        var (r, changed) = Apply(old, new[] { Src(@"C:\l\a.txt", "w1", "w2") }, ok: Array.Empty<string>(), bad: new[] { "w2" });
        Assert.True(changed);
        var only = Assert.Single(r);
        Assert.Equal(@"C:\l\a.txt", only.Path);
        Assert.Equal(new[] { "w1", "w2" }, only.Words);
    }

    [Fact]
    public void Apply_OverCap_EvictsOldest_TiesEvictNonBatchFirst()
    {
        var now = T0.AddDays(1);
        var old = new[] { Rec(@"C:\l\old.txt", T0, "x"), Rec(@"C:\l\tie.txt", now, "y") };
        var (r, _) = Apply(old, new[] { Src(@"C:\l\a.txt", "w") }, ok: Array.Empty<string>(), bad: new[] { "w" }, now, max: 2);
        Assert.Equal(new[] { @"C:\l\a.txt", @"C:\l\tie.txt" }, r.Select(x => x.Path).OrderBy(p => p));
        var (r2, _) = Apply(new[] { Rec(@"C:\l\tie.txt", now, "y") }, new[] { Src(@"C:\l\a.txt", "w") }, ok: Array.Empty<string>(), bad: new[] { "w" }, now, max: 1);
        Assert.Equal(@"C:\l\a.txt", Assert.Single(r2).Path); // 同時間並列：先淘汰不在本批者
    }

    [Fact]
    public void FailedKeysFor_UnionOfBatchPaths_CaseInsensitive()
    {
        var recs = new[] { Rec(@"C:\l\a.txt", T0, "Apple"), Rec(@"C:\l\b.txt", T0, "kiwi"), Rec(@"C:\l\c.txt", T0, "fig") };
        var keys = ImportFailureLog.FailedKeysFor(recs, new[] { @"c:\L\A.txt", @"C:\l\b.txt" });
        Assert.Equal(new[] { "apple", "kiwi" }, keys.OrderBy(k => k));
    }

    // ---- 按鈕 N、狀態前置、文案 ----

    [Fact]
    public void Mark_CountsSelectableRowsOnly_IncludingAlreadyInNotes_NotDuplicates()
    {
        var entries = new List<NotesImportEntry>
        {
            new("apple", NotesImportStatus.New),
            new("banana", NotesImportStatus.AlreadyInNotes, "My Notes"),
            new("Apple", NotesImportStatus.DuplicateInFile, "", "b.txt", "a.txt"),
            new("cherry", NotesImportStatus.New),
        };
        var marked = NotesImport.MarkPreviouslyFailed(entries, new HashSet<string> { "apple", "banana" });
        Assert.Equal(2, NotesImport.PreviouslyFailedCount(marked));
        Assert.False(marked[2].PreviouslyFailed); // 重複列不標
        Assert.Equal("上次匯入失敗；新字", NotesImport.StatusText(marked[0]));
        Assert.StartsWith(NotesImport.PreviouslyFailedPrefix, NotesImport.StatusText(marked[1]));
        Assert.Equal("檔內重複（只留第一筆）".Replace("檔內重複", "與「a.txt」重複"), NotesImport.StatusText(marked[2]));
        Assert.Equal("新字", NotesImport.StatusText(marked[3]));
        Assert.Equal(0, NotesImport.PreviouslyFailedCount(NotesImport.MarkPreviouslyFailed(entries, new HashSet<string>())));
    }

    [Fact]
    public void StatusPrefix_AppliesToBothVariants_IncludingOwnTranslationAndForceOnline()
    {
        var e = new NotesImportEntry("grape", NotesImportStatus.AlreadyInNotes, "Unit 4", OwnTranslation: "葡萄", PreviouslyFailed: true);
        Assert.Equal("上次匯入失敗；勾選＝以自備中譯更新；已在筆記「Unit 4」", NotesImport.StatusText(e, forceOnline: false));
        Assert.StartsWith("上次匯入失敗；已在筆記「Unit 4」", NotesImport.StatusText(e, forceOnline: true));
    }

    [Fact]
    public void ButtonText_AndResultFailedTitle()
    {
        Assert.Equal("只勾上次失敗字（3）", NotesImport.SelectFailedButtonText(3));
        Assert.Equal("只勾上次失敗字", NotesImport.SelectFailedButtonText(0));
        var text = NotesImport.ResultText(1, 0, Array.Empty<string>(), new[] { ("kiwi", "逾時") }, "My Notes");
        Assert.Contains("再匯入同一份清單時，按確認表上的「只勾上次失敗字」即可只重試這幾個", text);
        Assert.DoesNotContain("可再匯入一次只勾這幾個", text);
    }

    [Fact]
    public void FailureSources_FromLoadedSources_PathAndWordsInOrder()
    {
        var s = NotesImport.FailureSources(new[] { new NotesImportSource(@"C:\l\a.csv", "a.csv", new[] { "x", "y" }), new NotesImportSource("", "", new[] { "z" }) });
        var only = Assert.Single(s);
        Assert.Equal(@"C:\l\a.csv", only.Path);
        Assert.Equal(new[] { "x", "y" }, only.Words);
    }

    // ---- 儲存（薄 I/O）：損毀退路、不影響匯入 ----

    [Fact]
    public void Store_RoundTrip_AtomicNoTmpLeft_StoresOnlyPathWordsTime()
    {
        var path = TempPath();
        try
        {
            var store = new ImportFailureStore(path);
            Assert.True(store.Update(new[] { Src(@"C:\l\a.txt", "w1", "w2") }, new[] { "w1" }, new[] { "w2" }, T0));
            Assert.False(File.Exists(path + ".tmp"));
            var (recs, state) = store.Read();
            Assert.Equal(ImportFailureReadState.Ok, state);
            Assert.Equal(new[] { "w2" }, Assert.Single(recs).Words);
            var json = File.ReadAllText(path);
            Assert.Contains("\"Version\": 1", json);
            Assert.DoesNotContain("Translation", json);
            Assert.DoesNotContain("Reason", json);
            Assert.Equal(new[] { "w2" }, store.FailedKeysFor(new[] { @"c:\l\A.txt" }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_MissingAndAllSucceeded_DoesNotCreateFile()
    {
        var path = TempPath();
        var store = new ImportFailureStore(path);
        Assert.False(store.Update(new[] { Src(@"C:\l\a.txt", "w1") }, new[] { "w1" }, Array.Empty<string>(), T0));
        Assert.False(File.Exists(path));
        Assert.Empty(store.FailedKeysFor(new[] { @"C:\l\a.txt" }));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("{\"Files\":[]}")]
    [InlineData("{\"Version\":0,\"Files\":[]}")]
    public void Store_Corrupt_ReadsAsEmpty_ThenOverwrittenOnNextWrite(string content)
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, content);
            var store = new ImportFailureStore(path);
            Assert.Equal(ImportFailureReadState.Corrupt, store.Read().State);
            Assert.Empty(store.FailedKeysFor(new[] { @"C:\l\a.txt" }));
            Assert.True(store.Update(new[] { Src(@"C:\l\a.txt", "w") }, Array.Empty<string>(), new[] { "w" }, T0));
            Assert.Equal(ImportFailureReadState.Ok, store.Read().State);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_InvalidEntriesSkippedIndividually()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"Version\":1,\"Files\":[{\"Path\":\"\",\"Words\":[\"a\"]},{\"Path\":\"C:\\\\x.txt\",\"Words\":[1]},{\"Path\":\"C:\\\\ok.txt\",\"Words\":[\"good\"],\"UpdatedAt\":\"2026-10-07T00:00:00Z\"}]}");
            var (recs, state) = new ImportFailureStore(path).Read();
            Assert.Equal(ImportFailureReadState.Ok, state);
            Assert.Equal(@"C:\ok.txt", Assert.Single(recs).Path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_NewerVersion_IsNotOverwritten()
    {
        var path = TempPath();
        try
        {
            const string newer = "{\"Version\":2,\"Files\":[],\"Extra\":true}";
            File.WriteAllText(path, newer);
            var store = new ImportFailureStore(path);
            Assert.Equal(ImportFailureReadState.Newer, store.Read().State);
            Assert.False(store.Update(new[] { Src(@"C:\l\a.txt", "w") }, Array.Empty<string>(), new[] { "w" }, T0));
            Assert.Equal(newer, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_LockedFile_NoWrite_NoThrow_OtherRecordsKept()
    {
        var path = TempPath();
        try
        {
            var store = new ImportFailureStore(path);
            store.Update(new[] { Src(@"C:\l\other.txt", "x") }, Array.Empty<string>(), new[] { "x" }, T0);
            var before = File.ReadAllText(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(ImportFailureReadState.Unreadable, store.Read().State);
                Assert.Empty(store.FailedKeysFor(new[] { @"C:\l\other.txt" })); // 讀不到＝按鈕停用
                Assert.False(store.Update(new[] { Src(@"C:\l\a.txt", "w") }, Array.Empty<string>(), new[] { "w" }, T0)); // 不擲出、不寫
            }
            Assert.Equal(before, File.ReadAllText(path)); // 他檔紀錄未被「以無紀錄算出之結果」蓋掉
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_UnwritableTarget_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lingoisland-323-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ImportFailureStore(dir); // 路徑是資料夾：讀為 Unreadable、寫入不可能——一律不擲出
            Assert.Empty(store.FailedKeysFor(new[] { @"C:\l\a.txt" }));
            Assert.False(store.Update(new[] { Src(@"C:\l\a.txt", "w") }, Array.Empty<string>(), new[] { "w" }, T0));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 測試縫 ----

    [Fact]
    public async Task FakeFailOnce_FailsListedWordsOnlyOnce_ThenSucceeds()
    {
        Assert.Equal(new[] { "kiwi", "fig" }, NotesImportRunner.ParseFailOnce(" Kiwi , fig,,kiwi "));
        Assert.Empty(NotesImportRunner.ParseFailOnce(null));
        var once = new HashSet<string>(StringComparer.Ordinal);
        var lookup = NotesImportRunner.MakeFakeLookup(1, "kiwi", once);
        var ex = await Assert.ThrowsAsync<QueryException>(() => lookup("KIWI", CancellationToken.None));
        Assert.Equal(NotesImportRunner.FakeFailReason, ex.Message);
        Assert.StartsWith(NotesImportRunner.FakeTranslationPrefix, (await lookup("kiwi", CancellationToken.None)).Translation);
        Assert.StartsWith(NotesImportRunner.FakeTranslationPrefix, (await NotesImportRunner.MakeFakeLookup(1)("kiwi", CancellationToken.None)).Translation);
        // 每批各建一個委派：共用集合才是「本行程第一次」（e2e 實撞：每批新委派致重匯又失敗）
        Assert.StartsWith(NotesImportRunner.FakeTranslationPrefix, (await NotesImportRunner.MakeFakeLookup(1, "kiwi", once)("kiwi", CancellationToken.None)).Translation);
    }

    [Fact]
    public void Structure_FailOnceSeam_OnlyWithDelayedFakeLookup()
    {
        var run = Body(Code("sysLingoIsland", "App.xaml.cs"), "RunNotesImport");
        var m = Regex.Match(run, @"fakeMs is int ms\s*\?\s*NotesImportRunner\.MakeFakeLookup\(ms, Environment\.GetEnvironmentVariable\(NotesImportRunner\.FakeFailOnceEnvVar\), _fakeFailedOnce\)\s*:\s*NotesImportRunner\.MakeLookup\(");
        Assert.True(m.Success, "注入失敗之測試縫須只在延遲假查詢分支生效");
    }

    // ---- 整合：真 notes.json＋真紀錄檔 ----

    private static (NotesStore Store, string FolderId) Seed(string path, params string[] existing)
    {
        var store = new NotesStore(path);
        var d = store.LoadEnsured();
        foreach (var w in existing) { NotesStore.AddToTopFolder(d, d.Folders[0], NoteEntry.From(new QueryResult(w, "", ""), DateTimeOffset.Now)); }
        store.Save(d);
        return (store, d.Folders[0].Id);
    }

    private static void Record(ImportFailureStore fs, IReadOnlyList<ImportFailureSource> src, NotesImportOutcome o)
        => fs.Update(src, o.AddedWords.Concat(o.UpdatedWords), o.Failed.Select(f => f.Word), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Integration_FailThenRetryOnlyFailed_ThenRecordCleared()
    {
        var notes = TempPath(); var rec = TempPath();
        try
        {
            var (store, folder) = Seed(notes, "banana");
            var fs = new ImportFailureStore(rec);
            var list = @"C:\lists\unit10.txt";
            var words = new[] { "lantern", "orbit", "quartz", "banana", "velvet" };
            var src = new[] { Src(list, words) };
            var runner = new NotesImportRunner(store, NotesImportRunner.MakeFakeLookup(1, "orbit,quartz,banana", new HashSet<string>()));
            var o1 = await runner.RunAsync(words.Select(w => new NotesImportItem(w)).ToList(), folder, null, null, CancellationToken.None);
            Assert.Equal(new[] { "orbit", "quartz", "banana" }, o1.Failed.Select(f => f.Word)); // banana＝已在筆記之更新失敗
            Record(fs, src, o1);
            Assert.Equal(new[] { "orbit", "quartz", "banana" }, Assert.Single(fs.Read().Records).Words);

            // 重匯：預掃描標記→按鈕 N＝3（含已在筆記之 banana）→只勾失敗字
            var data = store.LoadEnsured();
            var scan = NotesImport.Scan(words, k => NotesStore.FolderOfKey(data, k) is { } f ? f.Name : null);
            var marked = NotesImport.MarkPreviouslyFailed(scan.Entries, fs.FailedKeysFor(new[] { list.ToUpperInvariant() }));
            Assert.Equal(3, NotesImport.PreviouslyFailedCount(marked));
            var items = NotesImport.ToItems(marked, i => marked[i].PreviouslyFailed, forceOnline: false);
            Assert.Equal(new[] { "orbit", "quartz", "banana" }, items.Select(x => x.Text));
            var o2 = await runner.RunAsync(items, folder, null, null, CancellationToken.None);
            Assert.Empty(o2.Failed);
            Assert.Equal(new[] { "banana" }, o2.UpdatedWords);
            Record(fs, src, o2);
            Assert.Empty(fs.Read().Records); // 全數成功＝清除
            Assert.Empty(fs.FailedKeysFor(new[] { list })); // 再開確認頁：按鈕停用
        }
        finally { File.Delete(notes); File.Delete(rec); }
    }

    [Fact]
    public async Task Integration_CancelMidway_UnprocessedNotRecorded_SnapshotEqualsProcessed()
    {
        var notes = TempPath(); var rec = TempPath();
        try
        {
            var (store, folder) = Seed(notes);
            var fs = new ImportFailureStore(rec);
            var words = new[] { "aa", "bb", "cc", "dd", "ee" };
            using var cts = new CancellationTokenSource();
            NotesImportRunner? runner = null;
            NotesImportOutcome? snap = null;
            var calls = 0;
            Func<string, CancellationToken, Task<QueryResult>> lookup = (w, ct) =>
            {
                calls++;
                if (w == "bb") { throw new QueryException("逾時"); }
                return Task.FromResult(new QueryResult(w, "", "譯:" + w));
            };
            runner = new NotesImportRunner(store, lookup);
            var o = await runner.RunAsync(words.Select(w => new NotesImportItem(w)).ToList(), folder, null, null, cts.Token,
                progress: p => { if (p.Done == 3 && p.Current.Length == 0 && snap is null) { snap = runner.Snapshot(); cts.Cancel(); } });
            Assert.True(o.Cancelled);
            Assert.NotNull(snap);
            Assert.Equal(new[] { "aa", "cc" }, snap!.AddedWords);
            Assert.Equal(new[] { "bb" }, snap.Failed.Select(f => f.Word));
            Record(fs, new[] { Src(@"C:\l\c.txt", words) }, snap);
            Record(fs, new[] { Src(@"C:\l\c.txt", words) }, o); // 收尾又跑到：冪等
            Assert.Equal(new[] { "bb" }, Assert.Single(fs.Read().Records).Words); // dd、ee 未處理＝不算失敗
            Assert.Equal(3, calls);
        }
        finally { File.Delete(notes); File.Delete(rec); }
    }

    [Fact]
    public async Task Integration_EarlyStopWordsAreRecordedAsFailed()
    {
        var notes = TempPath(); var rec = TempPath();
        try
        {
            var (store, folder) = Seed(notes);
            var fs = new ImportFailureStore(rec);
            var words = Enumerable.Range(0, 6).Select(i => "w" + i).ToArray();
            var runner = new NotesImportRunner(store, (_, _) => throw new QueryException("離線"));
            var o = await runner.RunAsync(words.Select(w => new NotesImportItem(w)).ToList(), folder, null, null, CancellationToken.None);
            Assert.Contains(o.Failed, f => f.Reason.StartsWith(NotesImportRunner.NotQueriedReason));
            Record(fs, new[] { Src(@"C:\l\e.txt", words) }, o);
            Assert.Equal(words, Assert.Single(fs.Read().Records).Words);
        }
        finally { File.Delete(notes); File.Delete(rec); }
    }

    [Fact]
    public void Integration_OwnTranslationWriteFailure_IsRecorded_OwnSuccessIsNot()
    {
        var notes = TempPath(); var rec = TempPath();
        try
        {
            var (store, folder) = Seed(notes);
            var fs = new ImportFailureStore(rec);
            var runner = new NotesImportRunner(store, NotesImportRunner.NoLookup);
            var items = new[] { new NotesImportItem("grape", "葡萄"), new NotesImportItem("mango", "芒果") };
            var src = new[] { Src(@"C:\l\own.csv", "grape", "mango") };
            NotesImportOutcome locked;
            using (new FileStream(notes, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { locked = runner.RunOwnOnly(items, folder, null); }
            Assert.Equal(2, locked.Failed.Count);
            Record(fs, src, locked);
            Assert.Equal(new[] { "grape", "mango" }, Assert.Single(fs.Read().Records).Words);
            var ok = runner.RunOwnOnly(items, folder, null);
            Assert.Empty(ok.Failed);
            Record(fs, src, ok);
            Assert.Empty(fs.Read().Records);
            Assert.DoesNotContain("葡萄", File.Exists(rec) ? File.ReadAllText(rec) : ""); // 不存中譯
        }
        finally { File.Delete(notes); File.Delete(rec); }
    }

    [Fact]
    public async Task Integration_RecordFileLocked_ImportOutcomeUnchanged()
    {
        var notes = TempPath(); var rec = TempPath();
        try
        {
            var (store, folder) = Seed(notes);
            File.WriteAllText(rec, "{\"Version\":1,\"Files\":[]}");
            var fs = new ImportFailureStore(rec);
            using var lockRec = new FileStream(rec, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var runner = new NotesImportRunner(store, NotesImportRunner.MakeFakeLookup(1, "bb", new HashSet<string>()));
            var o = await runner.RunAsync(new[] { new NotesImportItem("aa"), new NotesImportItem("bb") }, folder, null, null, CancellationToken.None);
            Record(fs, new[] { Src(@"C:\l\x.txt", "aa", "bb") }, o); // 不擲出
            Assert.Equal(1, o.Added);
            Assert.Single(o.Failed);
            Assert.Equal(1, store.LoadEnsured().Folders[0].Entries.Count(e => e.Original == "aa"));
        }
        finally { File.Delete(notes); File.Delete(rec); }
    }

    // ---- 結構斷言 ----

    [Fact]
    public void Structure_AppRecordsFailures_BeforeResultBranches_AndBeforeCancelAtExits()
    {
        var cs = Code("sysLingoIsland", "App.xaml.cs");
        var bg = Body(cs, "RunBackgroundImportAsync", "Task");
        Assert.True(bg.IndexOf("RecordImportFailures(sources, outcome)", StringComparison.Ordinal) is var i && i > 0
                    && i < bg.IndexOf("FinishBackgroundImport(outcome", StringComparison.Ordinal), "背景收尾須先記紀錄再進結果呈現");
        var own = Body(cs, "RunOwnOnlyNotesImport");
        Assert.True(own.IndexOf("RecordImportFailures(sources, outcome)", StringComparison.Ordinal) is var j && j > 0
                    && j < own.IndexOf("ShowPendingImportResult", StringComparison.Ordinal));
        foreach (var exit in new[] { "ExitApp", "OnSessionEnding", "ConfirmRestartDuringImport" })
        {
            var b = Body(cs, exit);
            var rec = b.IndexOf("RecordImportFailuresBeforeExit()", StringComparison.Ordinal);
            Assert.True(rec > 0 && rec < b.IndexOf("_importCts?.Cancel()", StringComparison.Ordinal), exit + "：須於取消權杖之前更新紀錄");
        }
        var rif = Body(cs, "RecordImportFailures");
        Assert.Contains("catch (Exception)", rif);
        Assert.Contains("outcome.UpdatedWords", rif);
        Assert.DoesNotContain("RecordImportFailures", Body(cs, "RunNotesImport")); // 守備提前返回不更新
    }

    [Fact]
    public void Structure_NotesPagePassesSources_ConfirmPageHasButton()
    {
        var page = Code("sysLingoIsland", "modPresent", "NotesPage.xaml.cs");
        Assert.Contains("ImportConfirmed?.Invoke(folder.Id, folderPath, win.SelectedItems, NotesImport.FailureSources(load.Sources))", page);
        Assert.Contains("NotesImport.MarkPreviouslyFailed(scan.Entries, FailureStore.FailedKeysFor(", page);
        var xaml = ReadRepoFile("sysLingoIsland", "modPresent", "NotesImportWindow.xaml");
        var btn = Regex.Match(xaml, "<Button x:Name=\"SelectFailedBtn\"[^>]*/>", RegexOptions.Singleline).Value;
        Assert.Contains("AutomationProperties.AutomationId=\"NotesImportSelectFailed\"", btn);
        Assert.Contains("ToolTipService.ShowOnDisabled=\"True\"", btn);
        Assert.True(xaml.IndexOf("NotesImportSelectNone", StringComparison.Ordinal) < xaml.IndexOf("NotesImportSelectFailed", StringComparison.Ordinal));
        var win = Code("sysLingoIsland", "modPresent", "NotesImportWindow.xaml.cs");
        Assert.Contains("r.Box.IsChecked = r.Entry.PreviouslyFailed && r.Entry.IsSelectable", win); // 恰勾 N 列、其餘不勾
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
