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

/// <summary>
/// 彙總確認表之一列：原文、狀態、預設勾選。<paramref name="Source"/>＝來源檔顯示名（#320 來源欄）；
/// <paramref name="FirstSource"/>＝重複列之首見來源（同檔時與 Source 相同）。
/// </summary>
public sealed record NotesImportEntry(string Text, NotesImportStatus Status, string ExistingFolder = "", string Source = "", string FirstSource = "")
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

/// <summary>未納入之檔的原因類別（#320）：分類被拒（非清單檔、資料夾）或讀檔後不可用（讀不到、亂碼、沒有字）。</summary>
public enum NotesImportExcludeKind { NotListFile, Folder, Unreadable, Misdecoded, Empty }

/// <summary>未納入確認表之一檔（#320）：顯示名、原因類別、原因文字。</summary>
public sealed record NotesImportExcluded(string FileName, NotesImportExcludeKind Kind, string Reason);

/// <summary>可用來源之一檔（#320）：完整路徑、顯示名（同名檔附上層夾）、解析後之候選原文。</summary>
public sealed record NotesImportSource(string Path, string DisplayName, IReadOnlyList<string> Lines);

/// <summary>多檔載入結果（#320）：可用來源（依檔名自然排序）、讀後不可用之檔，或整批拒收之原因（合計逾 2 MB）。</summary>
public sealed record NotesImportLoad(IReadOnlyList<NotesImportSource> Sources, IReadOnlyList<NotesImportExcluded> Excluded, string? Error)
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

    /// <summary>選檔對話框標題（#320：可多選、可拖放）。</summary>
    public const string DialogTitle = "選擇英文清單（可多選；也可把檔案拖進筆記頁）";

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
                if (line.TrimStart().StartsWith("#")) { continue; }   // Anki 匯出檔頭 `#separator:tab`／`#html:true` 等註解行
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
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^\(?\d{1,3}(?:[.)]\s+|[、．]\s*)(?=\S)"); // `.`／`)` 後須有空白（`1.5 million` 不剝）；中文頓號可不留空
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
        => ScanSources(new[] { new NotesImportSource("", "", lines.ToList()) }, folderOfExisting);

    /// <summary>
    /// 合併預掃描（#320，純函式）：依 <paramref name="sources"/> 之順序（呼叫端已依檔名自然排序）與檔內行序串接後<b>一次</b>判定——
    /// 同鍵只留首見（不論同檔或跨檔），非首見者標 <see cref="NotesImportStatus.DuplicateInFile"/> 並記首見來源；
    /// <see cref="MaxLines"/>／<see cref="MaxWords"/> 以合併後計、逾限整批拒收。可用來源 ≥2 時用多檔文案，否則與單檔 <see cref="Scan(IEnumerable{string}, Func{string, string?})"/> 同。
    /// </summary>
    public static NotesImportScan ScanSources(IReadOnlyList<NotesImportSource> sources, Func<string, string?> folderOfExisting)
    {
        var multi = sources.Count >= 2;
        var firstSourceOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = new List<NotesImportEntry>();
        var lineCount = 0;
        foreach (var src in sources)
        {
            foreach (var raw in src.Lines)
            {
                var text = (raw ?? "").Trim();
                if (text.Length == 0) { continue; }
                if (++lineCount > MaxLines)
                {
                    return new NotesImportScan(entries, multi
                        ? $"這 {sources.Count} 個檔合計超過 {MaxLines} 行（含重複），不像單字清單——請確認是否選錯檔、或分幾批匯入。"
                        : $"這份清單超過 {MaxLines} 行（含重複），不像單字清單——請確認是否選錯檔、或拆成幾份再匯入。");
                }
                var key = NoteEntry.KeyOf(text);
                if (firstSourceOf.TryGetValue(key, out var first))
                {
                    entries.Add(new NotesImportEntry(text, NotesImportStatus.DuplicateInFile, "", src.DisplayName, first));
                    continue;
                }
                firstSourceOf[key] = src.DisplayName;
                var folder = folderOfExisting(key);
                entries.Add(folder is null
                    ? new NotesImportEntry(text, NotesImportStatus.New, "", src.DisplayName)
                    : new NotesImportEntry(text, NotesImportStatus.AlreadyInNotes, folder, src.DisplayName));
            }
        }
        if (entries.Count == 0) { return new NotesImportScan(entries, EmptyFileMessage); }
        var unique = firstSourceOf.Count;
        if (unique > MaxWords)
        {
            return new NotesImportScan(entries, multi
                ? $"這 {sources.Count} 個檔合計有 {unique} 個不重複的字，超過單次上限 {MaxWords} 字。請分幾批匯入（不會只匯入前 {MaxWords} 個）。"
                : $"這份清單有 {unique} 個不重複的字，超過單次上限 {MaxWords} 字。請拆成幾份再匯入（不會只匯入前 {MaxWords} 個）。");
        }
        return new NotesImportScan(entries, null);
    }

    /// <summary>單檔沒有任何可匯入之字（v4.16.0 文案）。</summary>
    public const string EmptyFileMessage = "檔案裡沒有任何可匯入的字——每行一個英文單字或片語（csv 只取第一欄），空行會被忽略。";

    /// <summary>單檔疑似非 UTF-8（v4.16.0 文案）。</summary>
    public const string MisdecodedFileMessage = "這個檔案疑似不是 UTF-8 編碼（讀出了亂碼字元）。請在記事本「另存新檔」時把編碼改為 UTF-8，再匯入一次——不會拿亂碼去查詢。";

    /// <summary>拖入全不可收時之提示（#320）。</summary>
    public const string DropRejectedHint = "只能拖入 .txt／.csv 清單檔——這次拖進來的都不是，不會匯入。";

    /// <summary>是否為可匯入之清單檔副檔名（`.txt`／`.csv`，不分大小寫；#320）。</summary>
    public static bool IsListFile(string path)
    {
        var ext = Path.GetExtension(path ?? "");
        return string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 副檔名分類（#320，純函式；兩入口共用）：資料夾→「是資料夾」、非 `.txt`／`.csv`→「不是 .txt／.csv」，其餘接受（保留原順序、不遞迴展開資料夾）。
    /// 被拒者依檔名自然排序，供「未納入」顯示。
    /// </summary>
    public static (IReadOnlyList<string> Accepted, IReadOnlyList<NotesImportExcluded> Rejected) SplitByExtension(IEnumerable<string>? paths, Func<string, bool> isDirectory)
    {
        var accepted = new List<string>();
        var rejected = new List<NotesImportExcluded>();
        foreach (var p in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(p)) { continue; }
            var name = Path.GetFileName(p.TrimEnd('\\', '/'));
            if (isDirectory(p)) { rejected.Add(new NotesImportExcluded(name, NotesImportExcludeKind.Folder, "是資料夾（請拖入裡面的 .txt／.csv）")); }
            else if (string.Equals(Path.GetExtension(p), ".lnk", StringComparison.OrdinalIgnoreCase)) { rejected.Add(new NotesImportExcluded(name, NotesImportExcludeKind.NotListFile, "是捷徑（請拖入原檔）")); }
            else if (!IsListFile(p)) { rejected.Add(new NotesImportExcluded(name, NotesImportExcludeKind.NotListFile, "不是 .txt／.csv")); }
            else { accepted.Add(p); }
        }
        rejected.Sort((a, b) => NotesStore.NaturalCompare(a.FileName, b.FileName));
        return (accepted, rejected);
    }

    /// <summary>
    /// 多檔載入（#320）：只收已通過 <see cref="SplitByExtension"/> 之清單檔；讀檔與長度以委派注入（單元測試不碰磁碟）。
    /// 路徑 <c>GetFullPath</c> 後不分大小寫去重；依檔名自然排序（同名以完整路徑定先後；同名者顯示名附上層夾）；
    /// 合計逾 <see cref="MaxFileBytes"/> 整批拒收（不讀）；逐檔讀不到／亂碼／沒有字→不納入、不連坐其他檔。
    /// </summary>
    public static NotesImportLoad LoadSources(IEnumerable<string> listFiles, Func<string, long> length, Func<string, string> read)
    {
        var paths = listFiles
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => { try { return Path.GetFullPath(p); } catch { return p; } })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p), Comparer<string>.Create(NotesStore.NaturalCompare))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displays = UniqueDisplayNames(paths);
        string Display(string p) => displays[p];

        var excluded = new List<NotesImportExcluded>();
        var sized = new List<string>();
        long total = 0;
        foreach (var p in paths)
        {
            try { total += length(p); sized.Add(p); }
            catch (Exception ex) { excluded.Add(new NotesImportExcluded(Display(p), NotesImportExcludeKind.Unreadable, "讀不到：" + ReasonOf(ex))); }
        }
        if (total > MaxFileBytes)
        {
            var mb = total / 1024 / 1024.0;
            return new NotesImportLoad(Array.Empty<NotesImportSource>(), excluded, paths.Count == 1
                ? $"這個檔案有 {mb:0.#} MB，超過清單檔上限 {MaxFileBytes / 1024 / 1024} MB——它可能不是單字清單。請確認後再選。"
                : $"所選 {paths.Count} 個檔合計 {mb:0.#} MB，超過上限 {MaxFileBytes / 1024 / 1024} MB——裡面可能有不是單字清單的檔。請確認後再選，或分幾批匯入。");
        }

        var sources = new List<NotesImportSource>();
        foreach (var p in sized)
        {
            string content;
            try { content = read(p); }
            catch (Exception ex) { excluded.Add(new NotesImportExcluded(Display(p), NotesImportExcludeKind.Unreadable, "讀不到：" + ReasonOf(ex))); continue; }
            if (LooksMisdecoded(content)) { excluded.Add(new NotesImportExcluded(Display(p), NotesImportExcludeKind.Misdecoded, "疑似不是 UTF-8 編碼")); continue; }
            var lines = ParseLines(content, IsCsv(p));
            if (lines.Count == 0) { excluded.Add(new NotesImportExcluded(Display(p), NotesImportExcludeKind.Empty, "沒有可匯入的字")); continue; }
            sources.Add(new NotesImportSource(p, Display(p), lines));
        }
        return new NotesImportLoad(sources, excluded, null);
    }

    /// <summary>
    /// 全數不可用時之中止訊息（#320，純函式）：只有 1 個清單檔且無被拒檔→沿用 v4.16.0 之單檔文案；否則逐檔列「檔名（原因）」（被拒者在前）。
    /// </summary>
    public static string AllUnusableText(IReadOnlyList<NotesImportExcluded> excluded, int listFileCount)
    {
        if (listFileCount == 1 && excluded.Count == 1)
        {
            var only = excluded[0];
            return only.Kind switch
            {
                NotesImportExcludeKind.Misdecoded => MisdecodedFileMessage,
                NotesImportExcludeKind.Empty => EmptyFileMessage,
                NotesImportExcludeKind.Unreadable => "讀不到這個檔案：" + only.Reason.Replace("讀不到：", ""),
                _ => $"{only.FileName}（{only.Reason}）",
            };
        }
        const int show = 10; // 數百檔時不讓對話框超出螢幕
        return "沒有可以匯入的檔——只接受 .txt（每行一字）與 .csv（取第一欄）：\n"
               + string.Join("\n", excluded.Take(show).Select(x => $"· {x.FileName}（{x.Reason}）"))
               + (excluded.Count > show ? $"\n…等 {excluded.Count} 個" : "");
    }

    /// <summary>「未納入」行與中止訊息最多列幾檔（其餘以「…等 N 個」收尾，避免數百檔把確認表擠沒）。</summary>
    public const int MaxExcludedShown = 5;

    /// <summary>確認頁「未納入」一行（#320，純函式）：無則空字串（不顯示）；逾 <see cref="MaxExcludedShown"/> 檔截斷（完整清單見 <see cref="ExcludedFullText"/>）。</summary>
    public static string ExcludedText(IReadOnlyList<NotesImportExcluded> excluded)
        => excluded.Count == 0 ? "" : "未納入：" + string.Join("；", excluded.Take(MaxExcludedShown).Select(x => $"{x.FileName}（{x.Reason}）"))
           + (excluded.Count > MaxExcludedShown ? $"…等 {excluded.Count} 個（滑鼠停著看全部）" : "");

    /// <summary>「未納入」完整清單（ToolTip 用）。</summary>
    public static string ExcludedFullText(IReadOnlyList<NotesImportExcluded> excluded)
        => string.Join("\n", excluded.Select(x => $"{x.FileName}（{x.Reason}）"));

    /// <summary>讀檔例外之白話原因（#320）：常見者對應短中文，其餘沿用例外訊息。</summary>
    public static string ReasonOf(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException => "找不到這個檔（可能已被移動、刪除或磁碟已拔除）",
        UnauthorizedAccessException => "沒有讀取權限",
        IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 => "被其他程式開著或鎖住（例如 Excel），請關閉後再試", // 共用／鎖定違規
        PathTooLongException => "路徑太長",
        IOException => "讀取失敗（" + ex.Message + "）",
        _ => ex.Message,
    };

    /// <summary>
    /// 來源顯示名（#320，純函式）：預設檔名；同名者附上層夾（`上層\檔名`），仍撞名（上層夾也同名或在磁碟根）則用完整路徑——保證兩兩不同。
    /// </summary>
    public static Dictionary<string, string> UniqueDisplayNames(IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in paths.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            if (g.Count() == 1) { result[g.First()] = g.Key; continue; }
            var withParent = g.ToDictionary(p => p, p =>
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(p) ?? "");
                return parent.Length > 0 ? parent + "\\" + Path.GetFileName(p) : p;
            }, StringComparer.OrdinalIgnoreCase);
            var clash = withParent.Values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1);
            foreach (var p in g) { result[p] = clash ? p : withParent[p]; }
        }
        return result;
    }

    /// <summary>確認頁首行之來源摘要（#320，純函式）：1 檔＝檔名；多檔＝「N 個檔（a、b…）」，逾 5 檔列前 5 個加「…等 N 個」。</summary>
    public static string SourcesText(IReadOnlyList<string> names)
    {
        if (names.Count <= 1) { return names.Count == 1 ? names[0] : ""; }
        const int show = 5;
        var list = string.Join("、", names.Take(show)) + (names.Count > show ? $"…等 {names.Count} 個" : "");
        return $"{names.Count} 個檔（{list}）";
    }

    /// <summary>單列狀態文案（純函式）——與設計 [modHmi筆記匯入確認頁] 狀態欄一一對應，文字即原因。</summary>
    public static string StatusText(NotesImportEntry e) => e.Status switch
    {
        NotesImportStatus.New => "新字",
        NotesImportStatus.AlreadyInNotes => e.ExistingFolder.Length > 0
            ? $"已在筆記「{e.ExistingFolder}」（預設略過；勾選＝重新查詢更新原筆）"
            : "已在筆記（預設略過；勾選＝重新查詢更新原筆）",
        NotesImportStatus.DuplicateInFile => e.FirstSource.Length > 0 && !string.Equals(e.FirstSource, e.Source, StringComparison.Ordinal)
            ? $"與「{e.FirstSource}」重複（只留第一筆）"
            : "檔內重複（只留第一筆）",
        _ => "",
    };

    /// <summary>表上方之計數摘要（純函式）：`共 5 列：新字 3、已在筆記 1、檔內重複 1`；多檔（可用來源 ≥2）末項為「重複」（含跨檔，#320）。</summary>
    public static string SummaryText(IReadOnlyList<NotesImportEntry> entries, bool multiSource = false)
    {
        var n = entries.Count(e => e.Status == NotesImportStatus.New);
        var a = entries.Count(e => e.Status == NotesImportStatus.AlreadyInNotes);
        var d = entries.Count(e => e.Status == NotesImportStatus.DuplicateInFile);
        return $"共 {entries.Count} 列：新字 {n}、已在筆記 {a}、{(multiSource ? "重複" : "檔內重複")} {d}";
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
        if (addedWords is { Count: > 0 })
        {
            const int show = 20; // 500 字時不讓首段成一大段、把失敗清單推到最下
            sb.Append($"（{string.Join("、", addedWords.Take(show))}" + (addedWords.Count > show ? $" …等 {addedWords.Count} 字）" : "）"));
        }
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
