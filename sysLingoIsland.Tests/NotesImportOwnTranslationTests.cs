using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LingoIsland.Present;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 筆記清單匯入之 CSV 第二欄自備中譯（spec#14 擴充，#321）：
/// 單元＝第二欄解析（引號、前置空白、引號欄跨行續接與退回）、同字多筆之中譯取捨（遞補、衝突）、確認頁文案三態、ToItems；
/// 整合＝執行器以<b>計數 fake</b>查詢（零 OpenAI 額度）寫真 notes.json——自備中譯之字查詢呼叫數＝0、混合時＝線上數、
/// 已在筆記以自備中譯更新只換中譯、早停只計線上、自備段批次寫入次數；真暫存 csv 全鏈；
/// 結構斷言＝App 全自備分支不建查詢服務、不開 AI 動作頁。
/// </summary>
public class NotesImportOwnTranslationTests
{
    // ---- 解析：第二欄 ----

    [Theory]
    [InlineData("apple,蘋果", "蘋果")]
    [InlineData("apple, \"蘋果, 紅\"", "蘋果, 紅")]      // 逗號後空白再引號（手打常見）
    [InlineData("\"a \"\"b\"\"\",c", "c")]
    [InlineData("apple", "")]
    [InlineData("apple,蘋果,fruit", "蘋果")]             // 第三欄起忽略
    [InlineData("\"apple\" ,蘋果", "蘋果")]               // 第一欄閉合引號後至逗號之字元忽略
    [InlineData("apple,   ", "")]
    [InlineData("apple,\"放輕鬆, 別緊張\"", "放輕鬆, 別緊張")]
    [InlineData("12\" ruler,尺", "尺")]                  // 欄中段引號為字面
    public void SecondCsvField_QuotesSpacesAndExtraColumns(string line, string expected)
        => Assert.Equal(expected, NotesImport.SecondCsvField(line));

    [Theory]
    [InlineData("apple,蘋果")]
    [InlineData("\"a, b\",c")]
    [InlineData("\"a \"\"b\"\"\",c")]
    [InlineData("\"unclosed, x")]
    [InlineData("12\" ruler,尺")]
    [InlineData("  spaced  ,x")]
    [InlineData("\"a\"b,c")]
    [InlineData("\" a \",x")]
    [InlineData("plain")]
    public void FirstColumn_SameAsLegacyFirstCsvField(string line)
        {
        Assert.Equal(NotesImport.LegacyFirstCsvField(line).Trim(), NotesImport.ParseEntries(line, csv: true).Single().Text); // 新解析器之第一欄＝v4.18.0 行為
        Assert.Equal(NotesImport.LegacyFirstCsvField(line), NotesImport.FirstCsvField(line));
    }

    [Fact]
    public void ParseEntries_Csv_SecondColumnTrimmed_EmptyIsNone_HeaderRowSkippedWhole()
    {
        var rows = NotesImport.ParseEntries("word,中文\napple, 蘋果 \nkiwi,\n\"take it easy\",\"放輕鬆, 別緊張\"\n", csv: true);
        Assert.Equal(new[] { ("apple", "蘋果"), ("kiwi", ""), ("take it easy", "放輕鬆, 別緊張") }, rows.Select(r => (r.Text, r.Translation)));
    }

    [Fact]
    public void ParseEntries_Txt_NeverHasTranslation_EvenWithTab()
    {
        var rows = NotesImport.ParseEntries("apple\t蘋果\nbanana,香蕉\n", csv: false);
        Assert.Equal(new[] { ("apple", ""), ("banana,香蕉", "") }, rows.Select(r => (r.Text, r.Translation)));
    }

    [Fact]
    public void ParseEntries_QuotedCellSpanningLines_JoinedWithSemicolon_BlankPartsDropped()
    {
        var rows = NotesImport.ParseEntries("apple,\"蘋果\n\n紅色的果實\n\"\nbanana,香蕉", csv: true);
        Assert.Equal(new[] { ("apple", "蘋果；紅色的果實"), ("banana", "香蕉") }, rows.Select(r => (r.Text, r.Translation)));
    }

    [Fact]
    public void ParseEntries_QuotedFirstColumnSpanningLines_JoinedWithSpace()
    {
        var rows = NotesImport.ParseEntries("\"take it\neasy\",放輕鬆", csv: true);
        Assert.Equal(("take it easy", "放輕鬆"), (rows.Single().Text, rows.Single().Translation));
    }

    [Fact]
    public void ParseEntries_UnclosedToEndOfFile_FallsBackLineByLine_NoSwallow()
    {
        var rows = NotesImport.ParseEntries("apple,\"蘋果\nbanana,香蕉\ncherry", csv: true);
        Assert.Equal(new[] { ("apple", "蘋果"), ("banana", "香蕉"), ("cherry", "") }, rows.Select(r => (r.Text, r.Translation)));
    }

    [Fact]
    public void ParseEntries_StrayQuoteInContinuation_FallsBack()
    {
        var rows = NotesImport.ParseEntries("apple,\"蘋果\nsay \"hi\" now,x", csv: true);
        Assert.Equal(new[] { "apple", "say \"hi\" now" }, rows.Select(r => r.Text));
        Assert.Equal("蘋果", rows[0].Translation);
    }

    [Fact]
    public void ParseEntries_ContinuationBeyondLimit_FallsBack()
    {
        var middle = string.Join("\n", Enumerable.Range(1, NotesImport.MaxContinuationLines + 2).Select(i => $"w{i}"));
        var rows = NotesImport.ParseEntries($"apple,\"x\n{middle}\ny\",z", csv: true);
        Assert.Equal(NotesImport.MaxContinuationLines + 4, rows.Count);   // apple、w1..w22、y" 各自成列
        Assert.Equal("apple", rows[0].Text);
    }

    [Fact]
    public void ParseLines_IsTextProjectionOfParseEntries_LegacyCsvUnchanged()
    {
        var content = "Word,translation,note\napple,蘋果,fruit\nbanana,香蕉\n\"a, b\",c\n";
        Assert.Equal(new[] { "apple", "banana", "a, b" }, NotesImport.ParseLines(content, csv: true));
    }

    [Fact]
    public void ParseEntries_ThirdColumnQuotedCellSpanningLines_NotSplitIntoWords()
    {
        var rows = NotesImport.ParseEntries("apple,蘋果,\"例句一\n例句二\"\nbanana,香蕉", csv: true);
        Assert.Equal(new[] { ("apple", "蘋果"), ("banana", "香蕉") }, rows.Select(r => (r.Text, r.Translation)));
    }

    // ---- 載入與合併掃描 ----

    private static NotesImportSource Src(string name, params (string Text, string Tr)[] rows)
        => new(name, name, rows.Select(r => r.Text).ToList(), rows.Select(r => r.Tr).ToList());

    [Fact]
    public void LoadSources_CsvCarriesTranslations_TxtNull()
    {
        var files = new Dictionary<string, string> { [@"C:\x\a.csv"] = "apple,蘋果\nkiwi,\n", [@"C:\x\b.txt"] = "cherry\n" };
        var load = NotesImport.LoadSources(files.Keys, p => files[p].Length, p => files[p]);
        Assert.Equal(new[] { "蘋果", "" }, load.Sources.Single(s => s.DisplayName == "a.csv").Translations);
        Assert.Null(load.Sources.Single(s => s.DisplayName == "b.txt").Translations);
    }

    [Fact]
    public void ScanSources_FirstSeenWithTranslation_FillForward_Conflict_SameNotFlagged()
    {
        var scan = NotesImport.ScanSources(new[]
        {
            Src("a.csv", ("apple", "蘋果"), ("kiwi", ""), ("bank", "銀行")),
            Src("b.csv", ("Apple", "蘋果"), ("KIWI", "奇異果"), ("bank", "河岸")),
        }, k => k == "bank" ? "Unit 1" : null);
        Assert.True(scan.IsOk);
        var e = scan.Entries;
        Assert.Equal(("蘋果", false), (e[0].OwnTranslation, e[0].TranslationFromDuplicate));
        Assert.Equal(("奇異果", true), (e[1].OwnTranslation, e[1].TranslationFromDuplicate));   // 首見空白→遞補
        Assert.Equal("銀行", e[2].OwnTranslation);
        Assert.False(e[3].TranslationRejected);                                               // 同譯不註
        Assert.False(e[4].TranslationRejected);                                               // 被採用者本身
        Assert.True(e[5].TranslationRejected);                                                // 河岸≠銀行
        Assert.Equal("與「a.csv」重複（只留第一筆）；中譯「河岸」未採用", NotesImport.StatusText(e[5]));
        Assert.Equal("與「a.csv」重複（只留第一筆）", NotesImport.StatusText(e[5], forceOnline: true));
        Assert.Equal("自備中譯：奇異果（取自重複列）", NotesImport.TranslationSourceText(e[1], false));
        Assert.Equal("", NotesImport.TranslationSourceText(e[3], false));
        Assert.Equal("勾選＝以自備中譯更新；已在筆記「Unit 1」", NotesImport.StatusText(e[2]));
        Assert.StartsWith("已在筆記「Unit 1」（預設略過；勾選＝重新查詢更新原筆）", NotesImport.StatusText(e[2], forceOnline: true));
    }

    [Fact]
    public void ScanSources_NoTranslations_EntriesSameAsBefore()
    {
        var scan = NotesImport.ScanSources(new[] { new NotesImportSource("a.txt", "a.txt", new[] { "apple", "Apple" }) }, _ => null);
        Assert.False(NotesImport.AnyOwnTranslation(scan.Entries));
        Assert.Equal("共 2 列：新字 1、已在筆記 0、檔內重複 1", NotesImport.SummaryText(scan.Entries));
        Assert.Equal("新字", NotesImport.StatusText(scan.Entries[0]));
    }

    // ---- 文案三態與交出清單 ----

    [Fact]
    public void ButtonCostSummary_ThreeStates()
    {
        Assert.Equal("查詢並加入 3 字", NotesImport.ConfirmButtonText(3, 3));
        Assert.Equal("加入 3 字（查詢 1 字）", NotesImport.ConfirmButtonText(3, 1));
        Assert.Equal("加入 2 字（不查詢）", NotesImport.ConfirmButtonText(2, 0));
        Assert.Equal("查詢並加入 0 字", NotesImport.ConfirmButtonText(0, 0));
        Assert.Equal(NotesImport.CostText(3), NotesImport.CostText(3, 3));
        Assert.Contains("共 1 次 AI 查詢", NotesImport.CostText(3, 1));
        Assert.Contains("另 2 字採用自備中譯", NotesImport.CostText(3, 1));
        Assert.Contains("不會呼叫 AI、不花費用", NotesImport.CostText(2, 0));
        Assert.Contains("尚未勾選", NotesImport.CostText(0, 0));

        var e = NotesImport.ScanSources(new[] { Src("a.csv", ("apple", "蘋果"), ("kiwi", ""), ("Apple", "")) }, _ => null).Entries;
        Assert.Equal("共 3 列：新字 2、已在筆記 0、檔內重複 1、有自備中譯 1", NotesImport.SummaryText(e));
        Assert.Equal("共 3 列：新字 2、已在筆記 0、檔內重複 1、有自備中譯 1（已改查線上）", NotesImport.SummaryText(e, forceOnline: true));
        Assert.Equal("線上查詢", NotesImport.TranslationSourceText(e[0], forceOnline: true));
    }

    [Fact]
    public void ToItems_SelectedOnly_ForceOnlineClearsTranslations()
    {
        var e = NotesImport.ScanSources(new[] { Src("a.csv", ("apple", "蘋果"), ("kiwi", ""), ("mango", "芒果")) }, _ => null).Entries;
        var items = NotesImport.ToItems(e, i => i != 1, forceOnline: false);
        Assert.Equal(new[] { new NotesImportItem("apple", "蘋果"), new NotesImportItem("mango", "芒果") }, items);
        Assert.All(NotesImport.ToItems(e, _ => true, forceOnline: true), x => Assert.False(x.IsOwn));
    }

    [Fact]
    public void ResultText_MentionsOwnTranslationCount()
        => Assert.Contains("其中 2 字採用自備中譯、未查詢", NotesImport.ResultText(2, 0, Array.Empty<string>(), Array.Empty<(string, string)>(), "Unit 3", null, ownTranslationUsed: 2));

    // ---- 整合：執行器（計數 fake，零 OpenAI 額度） ----

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-own-{Guid.NewGuid():N}.json");

    private static (NotesStore Store, string TopId) Seed(string path, params NoteEntry[] existing)
    {
        var store = new NotesStore(path);
        var d = store.LoadEnsured();
        foreach (var x in existing) { NotesStore.AddToTopFolder(d, d.Folders[0], x); }
        store.Save(d);
        return (new NotesStore(path), d.Folders[0].Id);
    }

    private static Func<string, CancellationToken, Task<QueryResult>> Counting(List<string> calls, Func<string, bool>? fail = null)
        => (w, _) =>
        {
            calls.Add(w);
            if (fail?.Invoke(w) == true) { throw new QueryException("逾時"); }
            return Task.FromResult(new QueryResult(w, $"[{w}]", $"譯:{w}"));
        };

    [Fact]
    public void RunOwnOnly_GuardLookupNeverCalled_SingleSave_PhoneticEmpty()
    {
        var path = TempPath();
        try
        {
            var (store, topId) = Seed(path);
            var runner = new NotesImportRunner(store, NotesImportRunner.NoLookup);
            var outcome = runner.RunOwnOnly(new[] { new NotesImportItem("grape", "葡萄"), new NotesImportItem("mango", "芒果, 熱帶水果") }, topId, "#FFE4EC");
            Assert.Equal((2, 0, 2), (outcome.Added, outcome.Updated, outcome.OwnTranslationUsed));
            Assert.Empty(outcome.Failed);
            Assert.Equal(1, store.SaveCount);
            var entries = new NotesStore(path).Load().Folders[0].Entries;
            Assert.Equal(new[] { ("grape", "", "葡萄"), ("mango", "", "芒果, 熱帶水果") }, entries.Take(2).Select(x => (x.Original, x.Phonetic, x.Translation)));
            Assert.Throws<ArgumentException>(() => runner.RunOwnOnly(new[] { new NotesImportItem("kiwi") }, topId, ""));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_AllOwn_ZeroLookupCalls()
    {
        var path = TempPath();
        try
        {
            var (store, topId) = Seed(path);
            var calls = new List<string>();
            var outcome = await new NotesImportRunner(store, Counting(calls)).RunAsync(
                new[] { new NotesImportItem("grape", "葡萄"), new NotesImportItem("lemon", "檸檬") }, topId, "", null, CancellationToken.None);
            Assert.Empty(calls);
            Assert.Equal(2, outcome.Added);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_Mixed_CallsEqualOnlineCount_OrderKept_ExistingRefreshKeepsPhoneticAndScore()
    {
        var path = TempPath();
        try
        {
            var old = NoteEntry.From(new QueryResult("apple", "[ˈæpəl]", "舊譯"), DateTimeOffset.Now) with { PracticeScore = 88, Color = "#ABCDEF" };
            var (store, topId) = Seed(path, old);
            var calls = new List<string>();
            var reports = new List<string>();
            var outcome = await new NotesImportRunner(store, Counting(calls)).RunAsync(new[]
            {
                new NotesImportItem("grape", "葡萄"), new NotesImportItem("kiwi"), new NotesImportItem("apple", "蘋果"), new NotesImportItem("mango", "芒果"),
            }, topId, "", reports.Add, CancellationToken.None);
            Assert.Equal(new[] { "kiwi" }, calls);
            Assert.Equal((3, 1, 3), (outcome.Added, outcome.Updated, outcome.OwnTranslationUsed));
            var entries = new NotesStore(path).Load().Folders[0].Entries;
            Assert.Equal(new[] { "grape", "kiwi", "mango", "apple" }, entries.Select(x => x.Original));
            var apple = entries.Single(x => x.Original == "apple");
            Assert.Equal(("[ˈæpəl]", "蘋果", 88, "#ABCDEF", old.Id), (apple.Phonetic, apple.Translation, apple.PracticeScore, apple.Color, apple.Id));
            Assert.Equal("譯:kiwi", entries.Single(x => x.Original == "kiwi").Translation);
            Assert.Contains(reports, r => r.StartsWith("加入中 1–1/4：自備中譯 1 字"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_EarlyStopCountsOnlyOnline_OwnBetweenNeitherResetsNorBlocks()
    {
        var path = TempPath();
        try
        {
            var (store, topId) = Seed(path);
            var calls = new List<string>();
            var outcome = await new NotesImportRunner(store, Counting(calls, _ => true)).RunAsync(new[]
            {
                new NotesImportItem("own1", "一"), new NotesImportItem("x1"), new NotesImportItem("own2", "二"), new NotesImportItem("x2"),
                new NotesImportItem("x3"), new NotesImportItem("own3", "三"), new NotesImportItem("x4"),
            }, topId, "", null, CancellationToken.None);
            Assert.Equal(new[] { "x1", "x2", "x3" }, calls);                                   // 第 3 次線上失敗即早停，x4 不查
            Assert.Equal(new[] { "own1", "own2", "own3" }, outcome.AddedWords);                // 自備字不連坐
            Assert.StartsWith(NotesImportRunner.NotQueriedReason, outcome.Failed.Single(f => f.Word == "x4").Reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_LongOwnRun_WrittenInSegments()
    {
        var path = TempPath();
        try
        {
            var (store, topId) = Seed(path);
            var before = store.SaveCount;
            var items = Enumerable.Range(1, 120).Select(i => new NotesImportItem($"w{i:000}", $"譯{i}")).Append(new NotesImportItem("online")).ToList();
            var calls = new List<string>();
            var outcome = await new NotesImportRunner(store, Counting(calls)).RunAsync(items, topId, "", null, CancellationToken.None);
            Assert.Equal(121, outcome.Added);
            Assert.Equal(new[] { "online" }, calls);
            Assert.Equal(3 + 1, store.SaveCount - before);                                     // 50＋50＋20 三段＋線上一字
            Assert.Equal(items.Select(x => x.Text), new NotesStore(path).Load().Folders[0].Entries.Select(x => x.Original));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RunOwnOnly_SaveFails_AllFailed_NothingWritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lingoisland-own-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new NotesStore(dir);                                                  // 路徑是資料夾：讀退空、寫必失敗
            var outcome = new NotesImportRunner(store, NotesImportRunner.NoLookup).RunOwnOnly(new[] { new NotesImportItem("grape", "葡萄"), new NotesImportItem("kiwi", "奇異果") }, "x", "");
            Assert.Equal((0, 2, 0), (outcome.Added, outcome.Failed.Count, outcome.OwnTranslationUsed));
            Assert.Contains("筆記存檔失敗", outcome.Failed[0].Reason);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_Mixed_SegmentSaveFails_ThatSegmentFailed_OthersContinue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lingoisland-own-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var calls = new List<string>();
            var outcome = await new NotesImportRunner(new NotesStore(dir), Counting(calls)).RunAsync(new[]   // 路徑是資料夾：每次存檔皆失敗
            {
                new NotesImportItem("own1", "一"), new NotesImportItem("x1"), new NotesImportItem("own2", "二"),
            }, "x", "", null, CancellationToken.None);
            Assert.Equal(new[] { "x1" }, calls);                                                // 段失敗不中斷其後線上字
            Assert.Equal(new[] { "own1", "x1", "own2" }, outcome.Failed.Select(f => f.Word));   // 其後之段照常嘗試
            Assert.Equal(0, outcome.OwnTranslationUsed);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_CancelBetweenOwnSegments_WrittenSegmentKept()
    {
        var path = TempPath();
        try
        {
            var (store, topId) = Seed(path);
            using var cts = new CancellationTokenSource();
            var calls = new List<string>();
            var items = Enumerable.Range(1, 60).Select(i => new NotesImportItem($"w{i:00}", $"譯{i}")).Append(new NotesImportItem("online")).ToList();
            var outcome = await new NotesImportRunner(store, Counting(calls)).RunAsync(items, topId, "",
                r => { if (r.StartsWith("加入中 1–50/")) { cts.Cancel(); } }, cts.Token);           // 第一段寫入前按下取消
            Assert.True(outcome.Cancelled);
            Assert.Equal(50, outcome.Added);                                                    // 已寫入之段保留
            Assert.Empty(calls);
            Assert.Equal(50, new NotesStore(path).Load().Folders[0].Entries.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Integration_RealCsvFiles_LoadScanToItemsRunner_ZeroCallsForOwn()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lingoisland-own-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var notesPath = Path.Combine(dir, "notes.json");
        try
        {
            File.WriteAllText(Path.Combine(dir, "unit5.csv"), "word,中文\ngrape,葡萄\nkiwi,\n\"mango\",\"芒果\n熱帶水果\"\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "unit6.txt"), "kiwi\napple\n", new UTF8Encoding(false));
            var (store, topId) = Seed(notesPath);
            var split = NotesImport.SplitByExtension(Directory.GetFiles(dir, "unit*"), Directory.Exists);
            var load = NotesImport.LoadSources(split.Accepted, p => new FileInfo(p).Length, NotesImport.ReadAllText);
            var scan = NotesImport.ScanSources(load.Sources, _ => null);
            Assert.True(scan.IsOk);
            var items = NotesImport.ToItems(scan.Entries, i => scan.Entries[i].DefaultSelected, forceOnline: false);
            Assert.Equal(new[] { ("grape", "葡萄"), ("kiwi", ""), ("mango", "芒果；熱帶水果"), ("apple", "") }, items.Select(x => (x.Text, x.OwnTranslation)));
            var calls = new List<string>();
            var outcome = await new NotesImportRunner(store, Counting(calls)).RunAsync(items, topId, "", null, CancellationToken.None);
            Assert.Equal(new[] { "kiwi", "apple" }, calls);                                    // 呼叫數＝線上數
            Assert.Equal(2, outcome.OwnTranslationUsed);
            var saved = new NotesStore(notesPath).Load().Folders[0].Entries;
            Assert.Equal("芒果；熱帶水果", saved.Single(x => x.Original == "mango").Translation);
            Assert.Equal("", saved.Single(x => x.Original == "grape").Phonetic);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- 結構斷言：App 全自備分支 ----

    [Fact]
    public void Structure_AppOwnOnlyBranch_NoQueryServiceNoAiActionWindow()
    {
        var cs = Regex.Replace(Regex.Replace(ReadRepoFile("sysLingoIsland", "App.xaml.cs"), "/\\*.*?\\*/", "", RegexOptions.Singleline), "//[^\\n]*", "");
        var run = Body(cs, "RunNotesImport");
        Assert.Matches(new Regex(@"if \(words\.All\(w => w\.IsOwn\)\) \{ RunOwnOnlyNotesImport\(folderId, folderName, words, sources\); return; \}"), run); // #323 起帶本批來源
        Assert.True(run.IndexOf("RunOwnOnlyNotesImport(", StringComparison.Ordinal) < run.IndexOf("QueryService", StringComparison.Ordinal)); // 分流在建查詢服務之前
        var own = Body(cs, "RunOwnOnlyNotesImport");
        Assert.DoesNotContain("QueryService", own);
        Assert.DoesNotContain("AiActionWindow", own);
        Assert.Contains("NotesImportRunner.NoLookup", own);
        Assert.Contains("RunOwnOnly(", own);
    }

    private static string Body(string code, string method)
    {
        var m = Regex.Match(code, @"\b(void|bool)\s+" + method + @"\s*\(");
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
