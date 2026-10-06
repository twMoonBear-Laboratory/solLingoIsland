using System;
using System.Collections.Generic;
using System.Linq;
using LingoIsland.Present;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modPresent模組] 筆記清單匯入契約（spec#14，#309）之預掃描純函式：txt 逐行／csv 第一欄（引號、轉義）／BOM／空行空白／
/// 檔內重複／已在筆記標記／空檔與逾限拒收／各段文案。單元層（unit）：不碰檔案、不碰網路。
/// </summary>
public class NotesImportTests
{
    private static NotesImportScan ScanOf(string content, bool csv, params string[] existing)
    {
        var keys = existing.Select(NoteEntry.KeyOf).ToHashSet(StringComparer.Ordinal);
        return NotesImport.Scan(NotesImport.ParseLines(content, csv), k => keys.Contains(k));
    }

    // ---- ParseLines：txt ----

    [Fact]
    public void ParseLines_Txt_TrimsSkipsBlank_AllNewlineStyles()
    {
        var lines = NotesImport.ParseLines("apple\r\n  banana  \n\n\rcherry\r\n   \n", csv: false);
        Assert.Equal(new[] { "apple", "banana", "cherry" }, lines);
    }

    [Fact]
    public void ParseLines_StripsUtf8Bom()
    {
        var lines = NotesImport.ParseLines("\uFEFFapple\nbanana", csv: false);
        Assert.Equal("apple", lines[0]); // BOM 不得黏在第一字前
    }

    [Fact]
    public void ParseLines_Txt_KeepsPhraseWithSpacesAndCommas()
    {
        // txt 模式不切逗號：整行即一則片語
        var lines = NotesImport.ParseLines("take it easy\nso far, so good", csv: false);
        Assert.Equal(new[] { "take it easy", "so far, so good" }, lines);
    }

    [Fact]
    public void ParseLines_Empty_ReturnsEmpty()
    {
        Assert.Empty(NotesImport.ParseLines("", csv: false));
        Assert.Empty(NotesImport.ParseLines(null, csv: true));
        Assert.Empty(NotesImport.ParseLines("\n\n  \n", csv: false));
    }

    // ---- ParseLines：csv 第一欄 ----

    [Fact]
    public void ParseLines_Csv_TakesFirstColumnOnly_SkipsCommonHeaderRow()
    {
        var lines = NotesImport.ParseLines("Word,translation,note\napple,蘋果,fruit\nbanana,香蕉\n", csv: true);
        Assert.Equal(new[] { "apple", "banana" }, lines); // 首列 `Word` 為常見表頭→略過，不當成字查
    }

    [Fact]
    public void ParseLines_Csv_FirstRowNotHeader_IsKept()
    {
        var lines = NotesImport.ParseLines("apple,蘋果\nword,字\n", csv: true);
        Assert.Equal(new[] { "apple", "word" }, lines); // 只看第一列；第二列的 word 是真字
    }

    [Fact]
    public void ParseLines_Txt_DoesNotSkipHeaderWords()
    {
        Assert.Equal(new[] { "word", "apple" }, NotesImport.ParseLines("word\napple", csv: false)); // txt 無表頭概念
    }

    [Theory]
    [InlineData("apple\n�banana", true)]
    [InlineData("apple\nbanana", false)]
    [InlineData("", false)]
    public void LooksMisdecoded_DetectsReplacementChar(string content, bool expected)
        => Assert.Equal(expected, NotesImport.LooksMisdecoded(content));

    [Theory]
    [InlineData("\"take it, easy\",x", "take it, easy")]   // 引號欄位內含逗號
    [InlineData("\"say \"\"hi\"\"\",x", "say \"hi\"")]      // "" 轉義
    [InlineData("\"quoted\"", "quoted")]                      // 僅一欄且帶引號
    [InlineData("plain", "plain")]                            // 無逗號無引號
    [InlineData(",second", "")]                               // 首欄空
    public void FirstCsvField_HandlesQuotesAndEscapes(string line, string expected)
        => Assert.Equal(expected, NotesImport.FirstCsvField(line));

    [Fact]
    public void ParseLines_Csv_SkipsRowsWithEmptyFirstColumn()
    {
        var lines = NotesImport.ParseLines(",only-second\napple,x\n\"\",y", csv: true);
        Assert.Equal(new[] { "apple" }, lines);
    }

    [Fact]
    public void IsCsv_ByExtensionCaseInsensitive()
    {
        Assert.True(NotesImport.IsCsv(@"C:\x\words.CSV"));
        Assert.False(NotesImport.IsCsv(@"C:\x\words.txt"));
        Assert.False(NotesImport.IsCsv(@"C:\x\words"));
    }

    // ---- Scan：狀態標記 ----

    [Fact]
    public void Scan_MarksNew_Existing_AndInFileDuplicates()
    {
        var scan = ScanOf("apple\nbanana\ncherry\nCherry\n  APPLE \n", csv: false, "banana");
        Assert.True(scan.IsOk);
        var e = scan.Entries;
        Assert.Equal(5, e.Count);
        Assert.Equal(NotesImportStatus.New, e[0].Status);              // apple
        Assert.Equal(NotesImportStatus.AlreadyInNotes, e[1].Status);   // banana 已在筆記
        Assert.Equal(NotesImportStatus.New, e[2].Status);              // cherry 首見
        Assert.Equal(NotesImportStatus.DuplicateInFile, e[3].Status);  // Cherry：大小寫折疊後同鍵→檔內重複
        Assert.Equal(NotesImportStatus.DuplicateInFile, e[4].Status);  // APPLE：去空白＋折疊後同鍵
        Assert.Equal("Cherry", e[3].Text);                             // 原文保留（顯示用）
    }

    [Fact]
    public void Scan_DefaultSelection_OnlyNewIsSelected_DuplicateNotSelectable()
    {
        var scan = ScanOf("apple\nbanana\napple", csv: false, "banana");
        Assert.True(scan.Entries[0].DefaultSelected);
        Assert.False(scan.Entries[1].DefaultSelected);
        Assert.True(scan.Entries[1].IsSelectable);     // 已在筆記：預設不勾、可改選
        Assert.False(scan.Entries[2].DefaultSelected);
        Assert.False(scan.Entries[2].IsSelectable);    // 檔內重複：不可勾
    }

    [Fact]
    public void Scan_EmptyFile_RejectedWithReason()
    {
        var scan = ScanOf("\n\n", csv: false);
        Assert.False(scan.IsOk);
        Assert.Contains("沒有任何可匯入的字", scan.Error);
        Assert.Empty(scan.Entries);
    }

    [Fact]
    public void Scan_OverLimit_RejectedNotTruncated()
    {
        var many = string.Join("\n", Enumerable.Range(0, NotesImport.MaxWords + 1).Select(i => $"w{i}"));
        var scan = ScanOf(many, csv: false);
        Assert.False(scan.IsOk);
        Assert.Contains($"{NotesImport.MaxWords}", scan.Error);
        Assert.Equal(NotesImport.MaxWords + 1, scan.Entries.Count); // 不默默截斷：全列回傳供顯示，但 Error 擋住往下走
    }

    [Fact]
    public void Scan_AtLimit_Ok_DuplicatesDoNotCountTowardLimit()
    {
        var exact = string.Join("\n", Enumerable.Range(0, NotesImport.MaxWords).Select(i => $"w{i}"));
        Assert.True(ScanOf(exact, csv: false).IsOk);
        var withDups = exact + "\nw0\nW1"; // 重複不計入上限
        Assert.True(ScanOf(withDups, csv: false).IsOk);
    }

    // ---- 文案 ----

    [Fact]
    public void Texts_StatusSummaryButtonCost()
    {
        var scan = ScanOf("apple\nbanana\napple", csv: false, "banana");
        Assert.Equal("新字", NotesImport.StatusText(scan.Entries[0]));
        Assert.Contains("已在筆記", NotesImport.StatusText(scan.Entries[1]));
        Assert.Contains("檔內重複", NotesImport.StatusText(scan.Entries[2]));
        Assert.Equal("共 3 列：新字 1、已在筆記 1、檔內重複 1", NotesImport.SummaryText(scan.Entries));
        Assert.Equal("查詢並加入 3 字", NotesImport.ConfirmButtonText(3));
        Assert.Contains("共 3 次 AI 查詢", NotesImport.CostText(3));
        Assert.Contains("尚未勾選", NotesImport.CostText(0));
    }

    [Fact]
    public void ResultText_ListsSkippedAndFailedWithReasons()
    {
        var text = NotesImport.ResultText(2, 1, new[] { "banana" }, new List<(string, string)> { ("durian", "逾時") }, "Unit 3");
        Assert.Contains("已加入 2 字到「Unit 3」", text);
        Assert.Contains("更新 1 字", text);
        Assert.Contains("略過 1 字", text);
        Assert.Contains("失敗 1 字", text);
        Assert.Contains("· banana", text);
        Assert.Contains("· durian——逾時", text);
    }

    [Fact]
    public void ResultText_AllSucceeded_NoSkippedOrFailedSections()
    {
        var text = NotesImport.ResultText(3, 0, Array.Empty<string>(), Array.Empty<(string, string)>(), "My Notes");
        Assert.Equal("匯入完成：已加入 3 字到「My Notes」。", text);
    }

    [Theory]
    [InlineData("apple", true)]
    [InlineData("take it easy", false)]
    [InlineData("", false)]
    public void IsSingleWord_SplitsQueryPath(string text, bool single)
        => Assert.Equal(single, NotesImport.IsSingleWord(text));
}
