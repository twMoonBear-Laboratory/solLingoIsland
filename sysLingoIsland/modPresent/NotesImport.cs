using System.IO;
using System.Text;
using LingoIsland.Query;

namespace LingoIsland.Present;

/// <summary>預掃描後每字一列之狀態（spec#14，#309）：三態皆須在確認頁以文字顯示，不只靠顏色。</summary>
public enum NotesImportStatus
{
    /// <summary>新字——預設勾選、會查詢並加入。</summary>
    New,
    /// <summary>已在筆記（跨全樹、依 <see cref="NoteEntry.KeyOf"/> 去重）——預設不勾、可逐列改選；勾選＝重新查詢並更新原筆之音標／翻譯（留原夾、不重複建立）。</summary>
    AlreadyInNotes,
    /// <summary>檔內重複（同鍵之非首見者）——預設不勾且不可改選，只留首見那列。</summary>
    DuplicateInFile,
}

/// <summary>彙總確認表之一列：原文、狀態、預設勾選。</summary>
public sealed record NotesImportEntry(string Text, NotesImportStatus Status, string ExistingFolder = "")
{
    /// <summary>預設勾選＝新字。</summary>
    public bool DefaultSelected => Status == NotesImportStatus.New;

    /// <summary>可由使用者改選者＝新字與已在筆記；檔內重複不可勾（勾了也只會被去重擋下，徒增一次查詢費用）。</summary>
    public bool IsSelectable => Status != NotesImportStatus.DuplicateInFile;
}

/// <summary>預掃描結果：列清單，或整檔拒收之原因（逾上限、空檔）。</summary>
public sealed record NotesImportScan(IReadOnlyList<NotesImportEntry> Entries, string? Error)
{
    public bool IsOk => Error is null;
}

/// <summary>
/// 【匯入清單】之純函式輔助（[modPresent模組] 筆記清單匯入契約，spec#14／#309）：解析 txt／csv 第一欄、
/// 去空白空行、檔內去重、對照既有筆記標狀態、各段文案。<b>純函式、不碰 UI 與網路</b>——比照 <see cref="AcquireBatch"/>
/// 把批次流程之判斷集中於此以便單元測試；讀檔由 <see cref="ReadAllText"/> 薄接線負責、查詢與寫入由 <see cref="NotesImportRunner"/> 負責。
/// </summary>
public static class NotesImport
{
    /// <summary>單次匯入字數上限——逾限整檔拒收並明訊，不默默截斷（與 <see cref="AcquireBatch.Merge"/> 同紀律）。</summary>
    public const int MaxWords = 500;

    /// <summary>原始有效行數上限（含重複）：逾此整檔拒收——重複字不計入 <see cref="MaxWords"/>，若不另設此閘，數十萬行重複字會通過預掃描並在確認頁逐列建元件而凍結。</summary>
    public const int MaxLines = 5000;

    /// <summary>清單檔大小上限（先擋、不讀入記憶體）：單字清單不該這麼大，逾此多半是選錯檔。</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>選檔對話框篩選。</summary>
    public const string DialogFilter = "英文清單 (*.txt;*.csv)|*.txt;*.csv|文字檔 (*.txt)|*.txt|CSV 檔 (*.csv)|*.csv";

    /// <summary>csv 首列第一欄為下列常見表頭字（不分大小寫）即略過該列——否則表頭會被當成一個字查詢並寫入筆記。</summary>
    public static readonly string[] CsvHeaderWords = { "word", "words", "english", "vocabulary", "term", "phrase" };

    /// <summary>讀出之內容含 U+FFFD（UTF-8 解碼失敗之替代字元）即判疑似編碼不符（例如舊式 ANSI／Big5 txt）——不照查照付。</summary>
    public static bool LooksMisdecoded(string? content) => !string.IsNullOrEmpty(content) && content.IndexOf('�') >= 0;

    /// <summary>副檔名是否以 CSV 規則解析（取第一欄）；其餘一律當每行一字之純文字。</summary>
    public static bool IsCsv(string path)
        => string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);

    /// <summary>讀檔薄接線：UTF-8（含 BOM 自動剝除）；原檔唯讀不改寫。</summary>
    public static string ReadAllText(string path) => File.ReadAllText(path, new UTF8Encoding(false));

    /// <summary>
    /// 自檔案內容抽出候選原文（純函式）：逐行（`\r\n`／`\n`／`\r` 皆可）、剝 BOM、
    /// <paramref name="csv"/>＝true 時取第一欄（支援雙引號欄位、欄內逗號與 `""` 轉義；分隔符只認逗號）；
    /// txt 行若含 **tab** 只取 tab 前（Anki 匯出之 `apple⇥蘋果` 常規）、並剝除行首 **編號**（`1.`／`1)`／`(1)`／`1、`）；
    /// 去前後空白、去空行。<b>不去重</b>——重複之判定與標記歸 <see cref="Scan"/>，使確認頁能把「檔內重複」顯示出來。
    /// </summary>
    public static List<string> ParseLines(string? content, bool csv)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(content)) { return result; }
        var text = content.TrimStart('﻿');
        var firstRow = true;
        foreach (var raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = raw;
            if (csv) { line = FirstCsvField(raw); }
            else
            {
                var tab = line.IndexOf('\t');
                if (tab >= 0) { line = line[..tab]; }          // Anki／試算表 TSV：只取第一欄
                line = StripLeadingNumber(line);
            }
            var t = line.Trim();
            if (t.Length == 0) { continue; }
            if (csv && firstRow && CsvHeaderWords.Contains(t, StringComparer.OrdinalIgnoreCase)) { firstRow = false; continue; } // 表頭列略過
            firstRow = false;
            result.Add(t);
        }
        return result;
    }

    /// <summary>剝除行首編號（純函式）：`1. apple`／`12) apple`／`(3) apple`／`4、apple` → `apple`；純數字行不剝（那是整行內容）。</summary>
    public static string StripLeadingNumber(string line)
    {
        var s = line.TrimStart();
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^\(?\d{1,3}[.)、．]\s*(?=\S)");
        return m.Success ? s[m.Length..] : line;
    }

    /// <summary>取 CSV 一列之第一欄（純函式）：`"a, b",c` → `a, b`；`""` 轉義為 `"`；無引號者取至首個逗號。</summary>
    public static string FirstCsvField(string line)
    {
        if (string.IsNullOrEmpty(line)) { return ""; }
        if (line[0] != '"')
        {
            var comma = line.IndexOf(',');
            return comma < 0 ? line : line[..comma];
        }
        var sb = new StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; continue; } // "" → "
                break;                                                                            // 收尾引號
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 預掃描（純函式）：對 <see cref="ParseLines"/> 之結果逐字標狀態——檔內同鍵（<see cref="NoteEntry.KeyOf"/>）只留首見、其餘
    /// <see cref="NotesImportStatus.DuplicateInFile"/>；首見者再以 <paramref name="existsInNotes"/>（鍵→是否已在筆記）標
    /// <see cref="NotesImportStatus.AlreadyInNotes"/> 或 <see cref="NotesImportStatus.New"/>。空清單或逾 <see cref="MaxWords"/>（以去重後之不重複字數計）
    /// 回 <see cref="NotesImportScan.Error"/>，整檔拒收。
    /// </summary>
    public static NotesImportScan Scan(IEnumerable<string> lines, Func<string, bool> existsInNotes)
        => Scan(lines, key => existsInNotes(key) ? "" : null);

    /// <summary>同上，<paramref name="folderOfExisting"/> 回該字所在資料夾路徑（null＝不在筆記；空字串＝在但不知夾），供確認頁顯示「已在筆記（在「夾名」）」。</summary>
    public static NotesImportScan Scan(IEnumerable<string> lines, Func<string, string?> folderOfExisting)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<NotesImportEntry>();
        var lineCount = 0;
        foreach (var raw in lines)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) { continue; }
            if (++lineCount > MaxLines) { return new NotesImportScan(entries, $"這份清單超過 {MaxLines} 行（含重複），不像單字清單——請確認是否選錯檔、或拆成幾份再匯入。"); }
            var key = NoteEntry.KeyOf(text);
            if (!seen.Add(key)) { entries.Add(new NotesImportEntry(text, NotesImportStatus.DuplicateInFile)); continue; }
            var folder = folderOfExisting(key);
            entries.Add(folder is null
                ? new NotesImportEntry(text, NotesImportStatus.New)
                : new NotesImportEntry(text, NotesImportStatus.AlreadyInNotes, folder));
        }
        if (entries.Count == 0) { return new NotesImportScan(entries, "檔案裡沒有任何可匯入的字——每行一個英文單字或片語（csv 只取第一欄），空行會被忽略。"); }
        if (seen.Count > MaxWords) { return new NotesImportScan(entries, $"這份清單有 {seen.Count} 個不重複的字，超過單次上限 {MaxWords} 字。請拆成幾份再匯入（不會只匯入前 {MaxWords} 個）。"); }
        return new NotesImportScan(entries, null);
    }

    /// <summary>單列狀態文案（純函式）——與設計 [modHmi筆記匯入確認頁] 狀態欄一一對應，文字即原因。</summary>
    public static string StatusText(NotesImportEntry e) => e.Status switch
    {
        NotesImportStatus.New => "新字",
        NotesImportStatus.AlreadyInNotes => e.ExistingFolder.Length > 0
            ? $"已在筆記「{e.ExistingFolder}」（預設略過；勾選＝重新查詢更新原筆）"
            : "已在筆記（預設略過；勾選＝重新查詢更新原筆）",
        NotesImportStatus.DuplicateInFile => "檔內重複（只留第一筆）",
        _ => "",
    };

    /// <summary>表上方之計數摘要（純函式）：`共 5 列：新字 3、已在筆記 1、檔內重複 1`。</summary>
    public static string SummaryText(IReadOnlyList<NotesImportEntry> entries)
    {
        var n = entries.Count(e => e.Status == NotesImportStatus.New);
        var a = entries.Count(e => e.Status == NotesImportStatus.AlreadyInNotes);
        var d = entries.Count(e => e.Status == NotesImportStatus.DuplicateInFile);
        return $"共 {entries.Count} 列：新字 {n}、已在筆記 {a}、檔內重複 {d}";
    }

    /// <summary>主鈕文案（純函式）：N＝勾選數、隨勾選即時更新；0 由呼叫端停用主鈕。</summary>
    public static string ConfirmButtonText(int selected) => $"查詢並加入 {selected} 字";

    /// <summary>費用揭露一行（純函式）：按下前即知會花幾次 AI 查詢；0 時改為提示。</summary>
    public static string CostText(int selected) => selected <= 0
        ? "尚未勾選任何字——勾選後才會查詢；已在筆記之字不會重複建立。"
        : $"共 {selected} 次 AI 查詢，將使用你的 OpenAI 金鑰；已在筆記之字不會重複建立。";

    /// <summary>
    /// 批次完成後之結果表（純函式）：已加入／更新／略過／失敗計數＋逐字失敗原因。更新＝勾選之「已在筆記」字已重新查詢並刷新原筆；
    /// 略過＝加入當下已在筆記、而原筆已不存在可更新（他處同時刪除）之邊角。
    /// </summary>
    public static string ResultText(int added, int updated, IReadOnlyList<string> skipped, IReadOnlyList<(string Word, string Reason)> failed, string folderName, IReadOnlyList<string>? addedWords = null)
    {
        var sb = new StringBuilder();
        sb.Append($"匯入完成：已加入 {added} 字到「{folderName}」");
        if (addedWords is { Count: > 0 }) { sb.Append($"（{string.Join("、", addedWords)}）"); }
        if (updated > 0) { sb.Append($"、更新 {updated} 字（原筆留在原夾）"); }
        if (skipped.Count > 0) { sb.Append($"、略過 {skipped.Count} 字"); }
        if (failed.Count > 0) { sb.Append($"、失敗 {failed.Count} 字"); }
        sb.Append('。');
        if (skipped.Count > 0)
        {
            sb.Append("\n\n略過（加入當下已在筆記、原筆已不在）：\n");
            sb.Append(string.Join("\n", skipped.Select(w => $"· {w}")));
        }
        if (failed.Count > 0)
        {
            sb.Append("\n\n失敗（其餘字不受影響；可再匯入一次只勾這幾個）：\n");
            sb.Append(string.Join("\n", failed.Select(f => $"· {f.Word}——{f.Reason}")));
        }
        return sb.ToString();
    }

    /// <summary>單字或片語之查詢路徑（純函式，與字典頁手動查詢同規則）：單一 token（無空白）→查字義；含空白→整句翻譯。</summary>
    public static bool IsSingleWord(string text) => text.Length > 0 && !text.Any(char.IsWhiteSpace);
}
