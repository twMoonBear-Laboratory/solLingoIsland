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
/// 筆記清單匯入之多檔與拖放（spec#14 擴充，#320）：
/// 單元＝副檔名分類、多檔載入（讀檔委派注入、不碰磁碟）、合併預掃描、文案；
/// 整合＝真暫存檔兩個走 LoadSources→ScanSources→執行器 fake 查詢寫入真 notes.json（零 OpenAI 額度）；
/// 結構斷言＝讀 NotesPage.xaml(.cs) 純文字鎖拖放接線（STA 不可得，比照 UserManualTests）。
/// </summary>
public class NotesImportMultiTests
{
    // ---- SplitByExtension ----

    [Fact]
    public void Split_AcceptsTxtCsvAnyCase_RejectsOthersAndFolders_RejectedSortedNatural()
    {
        var dirs = new HashSet<string> { @"C:\x\Unit 2" };
        var (acc, rej) = NotesImport.SplitByExtension(
            new[] { @"C:\x\b.TXT", @"C:\x\notes.docx", @"C:\x\a.Csv", @"C:\x\Unit 2", @"C:\x\img10.png", @"C:\x\img2.png" }, dirs.Contains);
        Assert.Equal(new[] { @"C:\x\b.TXT", @"C:\x\a.Csv" }, acc);                 // 接受者保留原順序（排序歸 LoadSources）
        Assert.Equal(new[] { "img2.png", "img10.png", "notes.docx", "Unit 2" }, rej.Select(r => r.FileName)); // 自然排序
        Assert.Equal(NotesImportExcludeKind.Folder, rej.Single(r => r.FileName == "Unit 2").Kind);
        Assert.All(rej.Where(r => r.Kind == NotesImportExcludeKind.NotListFile), r => Assert.Equal("不是 .txt／.csv", r.Reason));
        Assert.Contains("資料夾", rej.Single(r => r.Kind == NotesImportExcludeKind.Folder).Reason);
    }

    [Fact]
    public void Split_NullOrBlank_Empty()
    {
        var (acc, rej) = NotesImport.SplitByExtension(null, _ => false);
        Assert.Empty(acc); Assert.Empty(rej);
        (acc, rej) = NotesImport.SplitByExtension(new[] { "", "  " }, _ => false);
        Assert.Empty(acc); Assert.Empty(rej);
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("a.CSV", true)]
    [InlineData("a.txt.docx", false)]
    [InlineData("a", false)]
    [InlineData("a.tsv", false)]
    public void IsListFile(string path, bool expected) => Assert.Equal(expected, NotesImport.IsListFile(path));

    // ---- LoadSources ----

    private static NotesImportLoad Load(Dictionary<string, string> files, params string[] paths)
        => NotesImport.LoadSources(paths.Length > 0 ? paths : files.Keys.ToArray(),
            p => files.TryGetValue(p, out var c) ? Encoding.UTF8.GetByteCount(c) : throw new FileNotFoundException("找不到檔案"),
            p => files.TryGetValue(p, out var c) ? c : throw new IOException("被鎖住"));

    [Fact]
    public void Load_SortsByFileNameNatural_NotByPickOrder()
    {
        var f = new Dictionary<string, string> { [@"C:\w\Unit 10.txt"] = "z", [@"C:\w\Unit 2.txt"] = "b", [@"C:\w\Unit 1.csv"] = "a" };
        var r = Load(f, @"C:\w\Unit 10.txt", @"C:\w\Unit 2.txt", @"C:\w\Unit 1.csv");
        Assert.True(r.IsOk);
        Assert.Equal(new[] { "Unit 1.csv", "Unit 2.txt", "Unit 10.txt" }, r.Sources.Select(s => s.DisplayName));
    }

    [Fact]
    public void Load_SamePathDifferentCase_TakenOnce()
    {
        var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"C:\w\a.txt"] = "apple" };
        var r = Load(f, @"C:\w\a.txt", @"c:\W\A.TXT");
        Assert.Single(r.Sources);
    }

    [Fact]
    public void Load_SameNameInDifferentFolders_DisplayNameHasParent_OrderedByFullPath()
    {
        var f = new Dictionary<string, string> { [@"C:\b\words.txt"] = "x", [@"C:\a\words.txt"] = "y" };
        var r = Load(f);
        Assert.Equal(new[] { @"a\words.txt", @"b\words.txt" }, r.Sources.Select(s => s.DisplayName));
    }

    [Fact]
    public void Load_TotalOver2MB_WholeBatchRejected_NotRead_MultiText()
    {
        var reads = 0;
        var r = NotesImport.LoadSources(new[] { @"C:\w\a.txt", @"C:\w\b.txt" },
            _ => NotesImport.MaxFileBytes / 2 + 1, _ => { reads++; return "x"; });
        Assert.False(r.IsOk);
        Assert.Equal(0, reads);                                   // 先擋、不讀
        Assert.Contains("所選 2 個檔合計", r.Error);
    }

    [Fact]
    public void Load_SingleFileOver2MB_KeepsV416Text()
    {
        var r = NotesImport.LoadSources(new[] { @"C:\w\a.txt" }, _ => NotesImport.MaxFileBytes + 1, _ => "x");
        Assert.StartsWith("這個檔案有", r.Error);
    }

    [Fact]
    public void Load_ExactlyAtLimit_Accepted()
    {
        var r = NotesImport.LoadSources(new[] { @"C:\w\a.txt" }, _ => NotesImport.MaxFileBytes, _ => "apple");
        Assert.True(r.IsOk);
    }

    [Fact]
    public void Load_BadFilesExcludedPerFile_OthersKept()
    {
        var f = new Dictionary<string, string>
        {
            [@"C:\w\good.txt"] = "apple\nbanana",
            [@"C:\w\big5.txt"] = "apple \uFFFD",
            [@"C:\w\empty.txt"] = "\r\n  \r\n",
        };
        var r = NotesImport.LoadSources(new[] { @"C:\w\good.txt", @"C:\w\big5.txt", @"C:\w\empty.txt", @"C:\w\gone.txt" },
            p => p.EndsWith("gone.txt") ? throw new FileNotFoundException("找不到檔案") : 10,
            p => f[p]);
        Assert.True(r.IsOk);
        Assert.Equal(new[] { "good.txt" }, r.Sources.Select(s => s.DisplayName));
        Assert.Equal(new[] { "apple", "banana" }, r.Sources[0].Lines);
        var byName = r.Excluded.ToDictionary(x => x.FileName);
        Assert.Equal(NotesImportExcludeKind.Misdecoded, byName["big5.txt"].Kind);
        Assert.Equal("疑似不是 UTF-8 編碼", byName["big5.txt"].Reason);
        Assert.Equal(NotesImportExcludeKind.Empty, byName["empty.txt"].Kind);
        Assert.Equal(NotesImportExcludeKind.Unreadable, byName["gone.txt"].Kind);
        Assert.StartsWith("讀不到：", byName["gone.txt"].Reason);
    }

    [Fact]
    public void Load_ReadThrows_ExcludedAsUnreadable()
    {
        var r = NotesImport.LoadSources(new[] { @"C:\w\locked.txt" }, _ => 5, _ => throw new IOException("The process cannot access the file because it is being used by another process."));
        Assert.Empty(r.Sources);
        Assert.Equal("讀不到：被其他程式開著或鎖住（例如 Excel），請關閉後再試", r.Excluded.Single().Reason); // 不露英文例外與完整路徑
    }

    [Fact]
    public void ReasonOf_MapsCommonExceptionsToShortChinese()
    {
        Assert.StartsWith("找不到", NotesImport.ReasonOf(new FileNotFoundException("x")));
        Assert.StartsWith("找不到", NotesImport.ReasonOf(new DirectoryNotFoundException("x")));
        Assert.Equal("沒有讀取權限", NotesImport.ReasonOf(new UnauthorizedAccessException("x")));
        Assert.Equal("其他", NotesImport.ReasonOf(new InvalidOperationException("其他")));
    }

    [Fact]
    public void UniqueDisplayNames_ParentClashOrDriveRoot_FallsBackToFullPath_AlwaysDistinct()
    {
        var paths = new[] { @"C:\A\x\list.txt", @"D:\B\x\list.txt", @"E:\list.txt", @"F:\list.txt", @"C:\w\solo.txt" };
        var d = NotesImport.UniqueDisplayNames(paths);
        Assert.Equal("solo.txt", d[@"C:\w\solo.txt"]);
        Assert.Equal(paths.Length, d.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // 兩兩不同
        Assert.Equal(@"C:\A\x\list.txt", d[@"C:\A\x\list.txt"]);                                    // 上層夾同名 x、磁碟根無上層 → 完整路徑
        var two = NotesImport.UniqueDisplayNames(new[] { @"C:\a\w.txt", @"C:\b\w.txt" });
        Assert.Equal(@"a\w.txt", two[@"C:\a\w.txt"]);
    }

    [Fact]
    public void Load_SameDisplayCollision_CrossFileDuplicateStillLabelledCrossFile()
    {
        var f = new Dictionary<string, string> { [@"C:\A\x\list.txt"] = "apple", [@"D:\B\x\list.txt"] = "Apple" };
        var r = Load(f);
        var scan = NotesImport.ScanSources(r.Sources, _ => null);
        Assert.StartsWith("與「", NotesImport.StatusText(scan.Entries[1]));                         // 不被誤標成「檔內重複」
    }

    [Fact]
    public void Split_Shortcut_GetsSpecificReason()
    {
        var (_, rej) = NotesImport.SplitByExtension(new[] { @"C:\w\words.TXT.lnk" }, _ => false);
        Assert.Equal("是捷徑（請拖入原檔）", rej.Single().Reason);
    }

    [Fact]
    public void ExcludedText_TruncatesAfterFive_FullTextHasAll()
    {
        var many = Enumerable.Range(1, 300).Select(i => new NotesImportExcluded($"img{i}.png", NotesImportExcludeKind.NotListFile, "不是 .txt／.csv")).ToList();
        var text = NotesImport.ExcludedText(many);
        Assert.Equal(NotesImport.MaxExcludedShown, Regex.Matches(text, "img").Count);
        Assert.EndsWith("…等 300 個（滑鼠停著看全部）", text);
        Assert.Equal(300, NotesImport.ExcludedFullText(many).Split('\n').Length);
        var msg = NotesImport.AllUnusableText(many, 0);
        Assert.Equal(10, Regex.Matches(msg, "· ").Count);
        Assert.EndsWith("…等 300 個", msg);
    }

    [Fact]
    public void Load_CsvParsedByExtension_FirstColumn()
    {
        var f = new Dictionary<string, string> { [@"C:\w\u.csv"] = "word,meaning\napple,蘋果" };
        var r = Load(f);
        Assert.Equal(new[] { "apple" }, r.Sources[0].Lines);
    }

    // ---- AllUnusableText ----

    [Fact]
    public void AllUnusable_SingleListFile_KeepsV416Texts()
    {
        Assert.Equal(NotesImport.MisdecodedFileMessage,
            NotesImport.AllUnusableText(new[] { new NotesImportExcluded("a.txt", NotesImportExcludeKind.Misdecoded, "疑似不是 UTF-8 編碼") }, 1));
        Assert.Equal(NotesImport.EmptyFileMessage,
            NotesImport.AllUnusableText(new[] { new NotesImportExcluded("a.txt", NotesImportExcludeKind.Empty, "沒有可匯入的字") }, 1));
        Assert.Equal("讀不到這個檔案：被鎖住",
            NotesImport.AllUnusableText(new[] { new NotesImportExcluded("a.txt", NotesImportExcludeKind.Unreadable, "讀不到：被鎖住") }, 1));
    }

    [Fact]
    public void AllUnusable_ZeroListFiles_ListsRejectedInOrder()
    {
        var text = NotesImport.AllUnusableText(new[]
        {
            new NotesImportExcluded("notes.docx", NotesImportExcludeKind.NotListFile, "不是 .txt／.csv"),
            new NotesImportExcluded("old.txt", NotesImportExcludeKind.Misdecoded, "疑似不是 UTF-8 編碼"),
        }, 1);
        Assert.Contains("· notes.docx（不是 .txt／.csv）\n· old.txt（疑似不是 UTF-8 編碼）", text);
    }

    // ---- ScanSources ----

    private static NotesImportSource Src(string name, params string[] lines) => new(@"C:\w\" + name, name, lines);

    [Fact]
    public void ScanSources_CrossFileDuplicate_FirstSeenKept_LabelsFirstSource()
    {
        var scan = NotesImport.ScanSources(new[] { Src("u3.txt", "apple", "cherry", "apple"), Src("u4.csv", "Cherry", "grape") }, _ => null);
        Assert.True(scan.IsOk);
        Assert.Equal(new[] { "apple", "cherry", "apple", "Cherry", "grape" }, scan.Entries.Select(e => e.Text));
        Assert.Equal(new[] { "u3.txt", "u3.txt", "u3.txt", "u4.csv", "u4.csv" }, scan.Entries.Select(e => e.Source));
        Assert.Equal("檔內重複（只留第一筆）", NotesImport.StatusText(scan.Entries[2]));            // 同檔
        Assert.Equal("與「u3.txt」重複（只留第一筆）", NotesImport.StatusText(scan.Entries[3]));    // 跨檔
        Assert.False(scan.Entries[3].IsSelectable);
        Assert.False(scan.Entries[3].DefaultSelected);
        Assert.True(scan.Entries[4].DefaultSelected);
    }

    [Fact]
    public void ScanSources_SingleSource_KeepsV416LinesAndEmptyTexts()
    {
        var tooMany = NotesImport.ScanSources(new[] { Src("a.txt", Enumerable.Repeat("apple", NotesImport.MaxLines + 1).ToArray()) }, _ => null);
        Assert.Equal($"這份清單超過 {NotesImport.MaxLines} 行（含重複），不像單字清單——請確認是否選錯檔、或拆成幾份再匯入。", tooMany.Error);
        var empty = NotesImport.ScanSources(new[] { Src("a.txt", "  ", "") }, _ => null);
        Assert.Equal("檔案裡沒有任何可匯入的字——每行一個英文單字或片語（csv 只取第一欄），空行會被忽略。", empty.Error);
        var dup = NotesImport.ScanSources(new[] { Src("a.txt", "apple", "Apple") }, _ => null);
        Assert.Equal("檔內重複（只留第一筆）", NotesImport.StatusText(dup.Entries[1]));
        Assert.Equal("共 2 列：新字 1、已在筆記 0、檔內重複 1", NotesImport.SummaryText(dup.Entries));
    }

    [Fact]
    public void ScanSources_MaxWords_CountedAfterMerge_MultiText()
    {
        var half = NotesImport.MaxWords / 2 + 1;
        var s1 = Src("a.txt", Enumerable.Range(0, half).Select(i => "w" + i).ToArray());
        var s2 = Src("b.txt", Enumerable.Range(half, half).Select(i => "w" + i).ToArray());
        Assert.True(NotesImport.ScanSources(new[] { s1 }, _ => null).IsOk);     // 各自未逾限
        var scan = NotesImport.ScanSources(new[] { s1, s2 }, _ => null);        // 合併後逾限
        Assert.False(scan.IsOk);
        Assert.Contains("這 2 個檔合計有", scan.Error);
    }

    [Fact]
    public void ScanSources_MaxLines_CountedAfterMerge_MultiText()
    {
        var n = NotesImport.MaxLines / 2 + 1;
        var s1 = Src("a.txt", Enumerable.Repeat("apple", n).ToArray());
        var s2 = Src("b.txt", Enumerable.Repeat("apple", n).ToArray());
        var scan = NotesImport.ScanSources(new[] { s1, s2 }, _ => null);
        Assert.False(scan.IsOk);
        Assert.Contains($"這 2 個檔合計超過 {NotesImport.MaxLines} 行", scan.Error);
    }

    [Fact]
    public void ScanSources_SingleSource_KeepsV416Texts()
    {
        var scan = NotesImport.ScanSources(new[] { Src("a.txt", Enumerable.Range(0, NotesImport.MaxWords + 1).Select(i => "w" + i).ToArray()) }, _ => null);
        Assert.StartsWith("這份清單有", scan.Error);
    }

    // ---- 文案 ----

    [Fact]
    public void SourcesText_SingleMultiAndTruncation()
    {
        Assert.Equal("a.txt", NotesImport.SourcesText(new[] { "a.txt" }));
        Assert.Equal("2 個檔（a.txt、b.csv）", NotesImport.SourcesText(new[] { "a.txt", "b.csv" }));
        var seven = Enumerable.Range(1, 7).Select(i => $"u{i}.txt").ToArray();
        Assert.Equal("7 個檔（u1.txt、u2.txt、u3.txt、u4.txt、u5.txt…等 7 個）", NotesImport.SourcesText(seven));
    }

    [Fact]
    public void SummaryText_MultiUsesRepeatLabel_SingleKeepsInFile()
    {
        var e = new[] { new NotesImportEntry("a", NotesImportStatus.New), new NotesImportEntry("A", NotesImportStatus.DuplicateInFile) };
        Assert.Equal("共 2 列：新字 1、已在筆記 0、檔內重複 1", NotesImport.SummaryText(e));
        Assert.Equal("共 2 列：新字 1、已在筆記 0、重複 1", NotesImport.SummaryText(e, multiSource: true));
    }

    [Fact]
    public void ExcludedText_EmptyWhenNone_JoinsOtherwise()
    {
        Assert.Equal("", NotesImport.ExcludedText(Array.Empty<NotesImportExcluded>()));
        Assert.Equal("未納入：x.docx（不是 .txt／.csv）；y.txt（沒有可匯入的字）", NotesImport.ExcludedText(new[]
        {
            new NotesImportExcluded("x.docx", NotesImportExcludeKind.NotListFile, "不是 .txt／.csv"),
            new NotesImportExcluded("y.txt", NotesImportExcludeKind.Empty, "沒有可匯入的字"),
        }));
    }

    // ---- 整合：真暫存檔 → 載入 → 合併掃描 → 執行器（fake 查詢）→ 真 notes.json ----

    [Fact]
    public async Task Integration_TwoRealFiles_MergedNewWordsAddedInMergedOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lingoisland-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var notesPath = Path.Combine(dir, "notes.json");
        try
        {
            File.WriteAllText(Path.Combine(dir, "unit4-words.csv"), "word\nCherry\ngrape\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "unit3-words.txt"), "apple\nbanana\ncherry\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "notes.docx"), "not a list");
            var store = new NotesStore(notesPath);
            var d = store.LoadEnsured();
            NotesStore.AddToTopFolder(d, d.Folders[0], NoteEntry.From(new QueryResult("banana", "", ""), DateTimeOffset.Now));
            store.Save(d);
            var folderId = d.Folders[0].Id;

            var (acc, rej) = NotesImport.SplitByExtension(Directory.GetFiles(dir).Where(p => !p.EndsWith("notes.json")), Directory.Exists);
            var load = NotesImport.LoadSources(acc, p => new FileInfo(p).Length, NotesImport.ReadAllText);
            Assert.Equal(new[] { "notes.docx" }, rej.Select(r => r.FileName));
            Assert.Equal(new[] { "unit3-words.txt", "unit4-words.csv" }, load.Sources.Select(s => s.DisplayName));

            var data = store.LoadEnsured();
            var scan = NotesImport.ScanSources(load.Sources, k => NotesStore.FolderOfKey(data, k) is { } f ? f.Name : null);
            Assert.Equal(new[] { "apple", "banana", "cherry", "Cherry", "grape" }, scan.Entries.Select(e => e.Text));
            Assert.Equal(NotesImportStatus.AlreadyInNotes, scan.Entries[1].Status);
            Assert.Equal("與「unit3-words.txt」重複（只留第一筆）", NotesImport.StatusText(scan.Entries[3]));

            var selected = scan.Entries.Where(e => e.DefaultSelected).Select(e => e.Text).ToList();
            var calls = new List<string>();
            var runner = new NotesImportRunner(store, (w, _) => { calls.Add(w); return Task.FromResult(new QueryResult(w, $"[{w}]", $"譯:{w}")); });
            var outcome = await runner.RunAsync(selected, folderId, "#FFE4EC", _ => { }, CancellationToken.None);

            Assert.Equal(new[] { "apple", "cherry", "grape" }, calls);    // 勾選數＝查詢次數（fake，零額度）
            Assert.Equal(3, outcome.Added);
            var top = new NotesStore(notesPath).Load().Folders[0];
            Assert.Equal(new[] { "apple", "cherry", "grape", "banana" }, top.Entries.Select(e => e.Original)); // 合併序整批置頂
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- 結構斷言：拖放接線（STA 不可得，讀純文字） ----

    private static string Code(params string[] parts) =>
        Regex.Replace(Regex.Replace(ReadRepoFile(parts), "/\\*.*?\\*/", "", RegexOptions.Singleline), "//[^\\n]*", "");

    [Fact]
    public void Structure_BothEntriesGoThroughBeginImportFiles_DialogMultiselect()
    {
        var cs = Code("sysLingoIsland", "modPresent", "NotesPage.xaml.cs");
        Assert.Contains("Multiselect = true", cs);
        Assert.Contains("BeginImportFiles(dlg.FileNames)", cs);
        Assert.Contains("BeginImportFiles(paths)", Body(cs, "RunDroppedImport"));
        Assert.Contains("SplitByExtension", Body(cs, "BeginImportFiles"));      // 分類在共用段
        Assert.Contains("PreviewDrop += OnFileDrop", cs);
        Assert.Contains("PreviewDragOver += OnFileDragOver", cs);
        Assert.Contains("PreviewDragEnter += OnFileDragEnter", cs);
        Assert.Contains("PreviewDragLeave += OnFileDragLeave", cs);
    }

    [Fact]
    public void Structure_DropCallbackDefersAndOpensNoModal()
    {
        var cs = Code("sysLingoIsland", "modPresent", "NotesPage.xaml.cs");
        var drop = Body(cs, "OnFileDrop");
        Assert.Contains("Dispatcher.BeginInvoke", drop);
        Assert.DoesNotContain("ShowDialog", drop);
        Assert.DoesNotContain("MessageBox", drop);
        Assert.DoesNotContain("BeginImportFiles(", drop);                      // 只能經延後之 RunDroppedImport
        var run = Body(cs, "RunDroppedImport");
        Assert.Contains("Activate()", run);
        Assert.Matches(new Regex(@"finally\s*\{\s*_importBusy\s*=\s*false"), run);
        Assert.Matches(new Regex(@"finally\s*\{\s*_importBusy\s*=\s*false"), Body(cs, "BeginImportList"));
    }

    [Fact]
    public void Structure_AllFileDragHandlersIgnoreNonFileDrop()
    {
        var cs = Code("sysLingoIsland", "modPresent", "NotesPage.xaml.cs");
        foreach (var h in new[] { "OnFileDragEnter", "OnFileDragOver", "OnFileDragLeave", "OnFileDrop" })
        {
            var body = Body(cs, h).TrimStart('{').TrimStart();
            Assert.StartsWith("if (!IsFileDrag(e)) { return; }", body); // 非 FileDrop 首行即返回、不設 Handled
        }
    }

    [Fact]
    public void Structure_PageHitTestableAndBanner()
    {
        var xaml = ReadRepoFile("sysLingoIsland", "modPresent", "NotesPage.xaml");
        Assert.Contains("AllowDrop=\"True\">", xaml);
        Assert.Contains("<Grid Background=\"Transparent\">", xaml);
        Assert.Matches(new Regex("x:Name=\"EntryScroll\"[^>]*Background=\"Transparent\""), xaml);
        Assert.Matches(new Regex("x:Name=\"DropBanner\"[^>]*IsHitTestVisible=\"False\""), xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"NotesDropBanner\"", xaml);
    }

    /// <summary>取方法本體（自宣告處起、至其第一個平衡大括號結束）。</summary>
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
