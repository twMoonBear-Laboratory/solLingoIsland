using System.IO;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>條目排序模式（#126）：Manual＝使用者拖曳之手動序（＝「自訂順序」SSOT）；Alpha＝依原文；Time＝依登記時間。</summary>
public enum NoteSortMode { Manual, Alpha, Time }

/// <summary>
/// 單一資料夾之條目排序狀態（#126；隨夾存 notes.json、per-folder）：作用中模式＋<b>每模式各自方向</b>。
/// 排序為**非破壞式檢視投影**（<see cref="NotesStore.ProjectView"/>）之狀態，不改 <see cref="NoteFolder.Entries"/> 手動序。
/// </summary>
public sealed class FolderSort
{
    public NoteSortMode Mode { get; set; } = NoteSortMode.Manual;
    public bool AlphaAsc { get; set; } = true;   // 字母：預設 A→Z（順向）
    public bool TimeAsc { get; set; } = false;   // 日期：預設 New→Old（新在上，順「新在前」慣例）
    public bool ManualAsc { get; set; } = true;  // 自訂：預設正向（＝Entries 現行序）

    /// <summary>目前作用中模式對應之方向（升／降）。</summary>
    public bool CurrentAscending => Mode switch
    {
        NoteSortMode.Alpha => AlphaAsc,
        NoteSortMode.Time => TimeAsc,
        _ => ManualAsc,
    };
}

/// <summary>一個自訂類別資料夾：Id＋名稱＋<b>子資料夾</b>（多層）＋有序條目清單（新在前）。</summary>
public sealed class NoteFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>子資料夾（多層樹；Issue #34）。舊平面 notes.json 無此鍵 → 反序列化為空、即單層。</summary>
    public List<NoteFolder> Folders { get; set; } = new();
    public List<NoteEntry> Entries { get; set; } = new();
    /// <summary>條目排序狀態（#126；非破壞式投影之記憶）。舊 notes.json 無此鍵 → null → 視為預設（Manual/依各模式預設方向）。</summary>
    public FolderSort? Sort { get; set; }
}

/// <summary>我的筆記根結構：頂層資料夾清單（每個可再含子資料夾）。</summary>
public sealed class NotesData
{
    public List<NoteFolder> Folders { get; set; } = new();
}

/// <summary>加入我的筆記之結果（供 toast 回饋）。</summary>
public enum NoteAddResult { Added, AlreadyExists, Empty }

/// <summary>自備中譯批次寫入之逐字結果（spec#14／#321）：加入新筆、以自備中譯更新既有筆（只換中譯）、或原文為空。</summary>
public enum OwnTranslationWriteResult { Added, Updated, Empty }

/// <summary>筆記檔內容損毀（無法解析，#322）：與「暫時讀不到（被鎖）」區分——重試不會好，須明訊並指引自備份還原。</summary>
public sealed class NotesFileCorruptException : IOException
{
    public NotesFileCorruptException(Exception inner)
        : base("筆記檔內容已損毀、無法讀取——請到「選項」分頁「資料備份與搬遷」以「匯入資料…」還原備份", inner) { }
}

/// <summary>
/// 我的筆記本機儲存（[modQuery模組] 我的筆記儲存契約，spec#7；Issue #34 樹化）。存
/// <c>%APPDATA%\LingoIsland\notes.json</c>。資料夾為**多層樹**（向後相容舊平面）；加入以英文原文正規化
/// 跨全樹去重；資料夾 CRUD（含子夾）、條目排序、節點移動（防移入自身/子孫成環）皆為不依賴 UI 之純函式、可單元測試。
/// 讀取失敗退空結構、寫入失敗靜默降級——皆不致命；金鑰不入筆記；不受歷史清除影響。
/// </summary>
public sealed class NotesStore
{
    public const string DefaultFolderName = "My Notes";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;

    public NotesStore(string? path = null) => _path = path ?? DefaultPath;

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland");

    public static string DefaultPath => Path.Combine(DefaultDir, "notes.json");

    public NotesData Load()
    {
        try
        {
            return JsonSerializer.Deserialize<NotesData>(File.ReadAllText(_path)) ?? new NotesData();
        }
        catch
        {
            return new NotesData();
        }
    }

    public NotesData LoadEnsured()
    {
        var d = Load();
        Ensure(d);
        return d;
    }

    /// <summary>
    /// 嚴格讀檔（#322）：檔不存在＝新結構（首次使用）；檔存在而讀取或解析失敗（例如被防毒、雲端同步軟體暫時鎖住）即擲 <see cref="IOException"/>——
    /// 供一切「載入→改→存」寫入路徑與筆記頁同步使用，不得以空結構寫回而洗掉整份筆記（<see cref="Load"/> 之退空降級只留給程式啟動之首次載入）。
    /// </summary>
    public NotesData LoadStrict()
    {
        if (!File.Exists(_path)) { var empty = new NotesData(); Ensure(empty); return empty; }
        string text;
        try { text = File.ReadAllText(_path); }
        catch (Exception ex) { throw new IOException("讀取筆記檔失敗：" + ex.Message, ex); } // 多為被防毒／雲端同步暫時鎖住：稍後重試可恢復
        NotesData? d;
        try { d = JsonSerializer.Deserialize<NotesData>(text); }
        catch (Exception ex) { throw new NotesFileCorruptException(ex); } // 內容損毀：重試不會好，須明訊並指引還原
        d ??= new NotesData();
        Ensure(d);
        return d;
    }

    /// <summary><see cref="LoadStrict"/> 之不擲例外版：失敗回 false 與白話原因（#322 筆記頁同步用）。</summary>
    public bool TryLoadStrict(out NotesData data, out string error) => TryLoadStrict(out data, out error, out _);

    /// <summary>同上，另回 <paramref name="corrupt"/>：true＝內容損毀（重試不會好），false＝暫時讀不到（多為被鎖，稍後可恢復）。</summary>
    public bool TryLoadStrict(out NotesData data, out string error, out bool corrupt)
    {
        try { data = LoadStrict(); error = ""; corrupt = false; return true; }
        catch (IOException ex) { data = new NotesData(); error = ex.Message; corrupt = ex is NotesFileCorruptException; return false; }
    }

    public static void Ensure(NotesData d)
    {
        if (d.Folders.Count == 0)
        {
            d.Folders.Add(new NoteFolder { Name = DefaultFolderName });
        }
    }

    /// <summary>本實體成功落地之存檔次數（#321：供測試斷言批次寫入只寫一次；不影響行為）。</summary>
    public int SaveCount { get; private set; }

    public void Save(NotesData d)
    {
        try
        {
            WriteAtomic(JsonSerializer.Serialize(d, Opts));
            SaveCount++;
        }
        catch { /* 寫入失敗不影響主流程 */ }
    }

    /// <summary>原子寫入（#322）：先寫同目錄暫存檔再以覆蓋式搬移取代——中途斷電或被中止不留半份 JSON（背景匯入每字一存，寫入次數大增）。</summary>
    private void WriteAtomic(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    public NoteAddResult AddAndSave(QueryResult r, DateTimeOffset now)
    {
        var d = LoadStrict(); // #322：讀失敗擲出、不以空結構寫回
        var res = AddTo(d, NoteEntry.From(r, now));
        if (res == NoteAddResult.Added)
        {
            Save(d);
        }
        return res;
    }

    /// <summary>
    /// 加入至**指定名稱之頂層資料夾**並套底色（Issue #55）：<paramref name="folderName"/> 空則退回預設夾行為
    /// （第一個頂層夾）；找不到同名頂層夾則建立。跨全樹去重（同 <see cref="AddAndSave"/> 語意），
    /// 加入時設 <paramref name="colorHex"/>（空＝無底色）。回 <see cref="NoteAddResult"/>。
    /// </summary>
    public NoteAddResult AddToNamedFolderAndSave(QueryResult r, string? folderName, string? colorHex, DateTimeOffset now)
    {
        var d = LoadStrict(); // #322：讀失敗擲出、不以空結構寫回
        var entry = NoteEntry.From(r, now) with { Color = colorHex ?? "" };
        var res = string.IsNullOrWhiteSpace(folderName)
            ? AddTo(d, entry) // 空名＝預設夾（第一個頂層）
            : AddToTopFolder(d, EnsureTopFolderByName(d, folderName!), entry);
        if (res == NoteAddResult.Added)
        {
            Save(d);
        }
        return res;
    }

    /// <summary>
    /// 加入至**指定 Id 之資料夾（含子夾）**並套底色（spec#14／#309 匯入清單：目標＝筆記頁目前選取之夾，可為任意層）：
    /// 跨全樹去重（同 <see cref="AddAndSave"/> 語意）、加入即存檔（一字一存，批次中途取消已加者保留）。
    /// <paramref name="folderId"/> 找不到（例如匯入途中該夾被刪）則退回第一個頂層夾，不丟失該字。
    /// <paramref name="insertAt"/>：插入索引（null＝頂端）；<paramref name="batchKeys"/> 非 null 時（#322 匯入）改插在本批寫入最晚且仍在夾內者之後（見 <see cref="BatchInsertIndex"/>），<paramref name="insertAt"/> 不用。
    /// 存檔失敗**擲出**（不同於 <see cref="Save"/> 之靜默降級）——匯入結果表不得把未落地之字計已加入。回 <see cref="NoteAddResult"/>。
    /// </summary>
    public NoteAddResult AddToFolderAndSave(QueryResult r, string folderId, string? colorHex, DateTimeOffset now, int? insertAt = null, IReadOnlyList<string>? batchKeys = null)
    {
        var d = LoadStrict(); // #322：讀失敗擲出、不以空結構寫回
        var entry = NoteEntry.From(r, now) with { Color = colorHex ?? "" };
        var folder = FindFolder(d, folderId) ?? d.Folders[0];
        if (string.IsNullOrEmpty(entry.Key)) { return NoteAddResult.Empty; }
        if (Contains(d, entry.Key)) { return NoteAddResult.AlreadyExists; }
        var idx = batchKeys is null ? Math.Clamp(insertAt ?? 0, 0, folder.Entries.Count) : BatchInsertIndex(folder, batchKeys);
        folder.Entries.Insert(idx, entry);
        if (!TrySave(d, out var err)) { throw new IOException("筆記存檔失敗：" + err); }
        return NoteAddResult.Added;
    }

    /// <summary>
    /// 批次相連之插入位置（#322）：本批已加入之字（<paramref name="batchKeys"/> 依寫入順序）中、仍在 <paramref name="folder"/> 內且寫入順序最晚者之後；
    /// 全不在（本批第一字，或已全被刪除或移走）則置頂。匯入期間使用者在同一夾加字或拖動其他卡，本批後續之字仍接續在本批之後。
    /// </summary>
    public static int BatchInsertIndex(NoteFolder folder, IReadOnlyList<string> batchKeys)
    {
        for (var k = batchKeys.Count - 1; k >= 0; k--)
        {
            var i = folder.Entries.FindIndex(e => e.Key == batchKeys[k]);
            if (i >= 0) { return i + 1; }
        }
        return 0;
    }

    /// <summary>存檔並回報成敗（供須偵知寫入失敗之路徑，spec#14）；<see cref="Save"/> 之靜默降級版本仍供既有路徑使用。</summary>
    public bool TrySave(NotesData d, out string error)
    {
        try
        {
            WriteAtomic(JsonSerializer.Serialize(d, Opts));
            SaveCount++;
            error = "";
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>資料夾之顯示路徑「父夾 › 子夾」（同名夾可辨，spec#14 確認頁首行）；找不到回空字串。</summary>
    public static string FolderPath(NotesData d, string folderId)
    {
        var chain = new List<string>();
        bool Walk(IEnumerable<NoteFolder> folders)
        {
            foreach (var f in folders)
            {
                chain.Add(f.Name);
                if (f.Id == folderId || Walk(f.Folders)) { return true; }
                chain.RemoveAt(chain.Count - 1);
            }
            return false;
        }
        return Walk(d.Folders) ? string.Join(" › ", chain) : "";
    }

    /// <summary>
    /// 以**同去重鍵之既有筆記**為對象更新其音標／翻譯並存檔（spec#14／#309：匯入清單勾選「已在筆記」列＝重新查詢刷新原筆）：
    /// 留在原夾、不重複建立、**保留 Id／AddedAt／Color／練習分數**（字沒變、成績不歸零——與 <see cref="UpdateEntryContent"/> 之「原文已變」語意不同）；
    /// 原文採既有筆記之寫法（不以 AI 回之大小寫整形覆蓋）。找不到該鍵（他處同時刪除）回 false、不寫入。
    /// </summary>
    public bool RefreshEntryByKeyAndSave(QueryResult r)
    {
        var key = NoteEntry.KeyOf(r.Original);
        if (string.IsNullOrEmpty(key)) { return false; }
        var d = LoadStrict(); // #322
        foreach (var f in AllFolders(d))
        {
            var i = f.Entries.FindIndex(e => e.Key == key);
            if (i >= 0)
            {
                f.Entries[i] = f.Entries[i] with { Phonetic = r.Phonetic, Translation = r.Translation };
                if (!TrySave(d, out var err)) { throw new IOException("筆記存檔失敗：" + err); }
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 自備中譯之批次寫入（spec#14／#321）：**一次載入、一次存檔**——語意逐字同 <see cref="AddToFolderAndSave"/>（跨全樹去重、底色、
    /// 目標夾不在退回第一個頂層夾、插入位置同 <see cref="AddToFolderAndSave"/>（有 batchKeys 時接在本批寫入最晚者之後，否則第 k 個加入者插在 insertAt＋k））＋已在筆記者以自備中譯更新
    /// （只換中譯、保留音標／Id／底色／練習分數——與 <see cref="RefreshEntryByKeyAndSave"/> 之全欄刷新不同）。存檔失敗擲出 <see cref="IOException"/>、筆記檔不變（整批未落地）。
    /// </summary>
    public IReadOnlyList<OwnTranslationWriteResult> AddOrRefreshOwnTranslationsAndSave(IReadOnlyList<QueryResult> results, string folderId, string? colorHex, DateTimeOffset now, int insertAt, IReadOnlyList<string>? batchKeys = null)
    {
        var d = LoadStrict(); // #322：讀失敗擲出、不以空結構寫回
        var folder = FindFolder(d, folderId) ?? d.Folders[0];
        var idx = batchKeys is null ? Math.Clamp(insertAt, 0, folder.Entries.Count) : BatchInsertIndex(folder, batchKeys);
        var outcome = new List<OwnTranslationWriteResult>();
        foreach (var r in results)
        {
            var entry = NoteEntry.From(r, now) with { Color = colorHex ?? "" };
            if (string.IsNullOrEmpty(entry.Key)) { outcome.Add(OwnTranslationWriteResult.Empty); continue; }
            var existing = AllFolders(d).Select(f => (f, i: f.Entries.FindIndex(e => e.Key == entry.Key))).FirstOrDefault(x => x.i >= 0);
            if (existing.f is not null)
            {
                existing.f.Entries[existing.i] = existing.f.Entries[existing.i] with { Translation = r.Translation };
                outcome.Add(OwnTranslationWriteResult.Updated);
                continue;
            }
            folder.Entries.Insert(Math.Min(idx, folder.Entries.Count), entry);
            idx++;
            outcome.Add(OwnTranslationWriteResult.Added);
        }
        if (outcome.Any(o => o != OwnTranslationWriteResult.Empty) && !TrySave(d, out var err)) { throw new IOException("筆記存檔失敗：" + err); }
        return outcome;
    }

    /// <summary>
    /// 批次把多筆**原文**加入指定頂層資料夾（epic #178 增量6′-D：某說話人台詞一鍵收藏、免 AI 翻譯）：
    /// 逐筆建僅含原文之 <see cref="NoteEntry"/>（音標／翻譯空）、跨全樹去重、**依傳入順序附加**（保 cue 時間序）、一次存檔。
    /// <paramref name="folderName"/> 空則退回預設夾；找不到同名頂層夾則建立。回 (已加, 略過＝空白或已存在)。
    /// </summary>
    public (int Added, int Skipped) AddManyRawToNamedFolderAndSave(IEnumerable<string> originals, string? folderName, string? colorHex, DateTimeOffset now)
    {
        var d = LoadStrict(); // #322：讀失敗擲出、不以空結構寫回
        var folder = EnsureTopFolderByName(d, string.IsNullOrWhiteSpace(folderName) ? DefaultFolderName : folderName!);
        int added = 0, skipped = 0;
        foreach (var raw in originals)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0 || Contains(d, NoteEntry.KeyOf(text))) { skipped++; continue; } // 空白或跨全樹已存在→略過
            folder.Entries.Add(NoteEntry.From(new QueryResult(text, "", ""), now) with { Color = colorHex ?? "" }); // 附加（依 cue 序，非插頂）
            added++;
        }
        if (added > 0) { Save(d); }
        return (added, skipped);
    }

    /// <summary>濾出**尚未收錄**（跨全樹去重、原文正規化）之原文清單（epic #178 增量6′-D：批次翻譯前先去重，免對已在筆記者重複付費）：保原順序、去批次內重複、去空白。</summary>
    public List<string> NewOriginals(IEnumerable<string> originals)
    {
        var d = LoadEnsured();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in originals)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) { continue; }
            var key = NoteEntry.KeyOf(text);
            if (seen.Add(key) && !Contains(d, key)) { result.Add(text); } // 批次內未見過 且 樹中未有
        }
        return result;
    }

    // ---- 純函式（樹感知，可單元測試） ----

    /// <summary>樹的前序走訪（含所有子資料夾）。</summary>
    public static IEnumerable<NoteFolder> AllFolders(NotesData d) => Walk(d.Folders);

    private static IEnumerable<NoteFolder> Walk(IEnumerable<NoteFolder> folders)
    {
        foreach (var f in folders)
        {
            yield return f;
            foreach (var s in Walk(f.Folders))
            {
                yield return s;
            }
        }
    }

    public static NoteFolder? FindFolder(NotesData d, string id) =>
        AllFolders(d).FirstOrDefault(f => f.Id == id);

    /// <summary>某去重鍵所在之資料夾（spec#14 確認頁顯示「已在筆記「夾名」」）；不在回 null。</summary>
    public static NoteFolder? FolderOfKey(NotesData d, string key) =>
        string.IsNullOrEmpty(key) ? null : AllFolders(d).FirstOrDefault(f => f.Entries.Any(e => e.Key == key));

    /// <summary>某去重鍵是否已存在於樹中任一資料夾。</summary>
    public static bool Contains(NotesData d, string key) =>
        !string.IsNullOrEmpty(key) && AllFolders(d).Any(f => f.Entries.Any(e => e.Key == key));

    /// <summary>加入至第一個頂層資料夾頂端；空原文回 Empty、已存在（跨全樹去重）回 AlreadyExists。</summary>
    public static NoteAddResult AddTo(NotesData d, NoteEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Key))
        {
            return NoteAddResult.Empty;
        }
        if (Contains(d, entry.Key))
        {
            return NoteAddResult.AlreadyExists;
        }
        Ensure(d);
        d.Folders[0].Entries.Insert(0, entry);
        return NoteAddResult.Added;
    }

    /// <summary>取得指定名稱之頂層資料夾（Issue #55）；不存在則於頂層新建並回傳。名稱空白退回預設夾名。</summary>
    public static NoteFolder EnsureTopFolderByName(NotesData d, string name)
    {
        var target = string.IsNullOrWhiteSpace(name) ? DefaultFolderName : name.Trim();
        var existing = d.Folders.FirstOrDefault(f => string.Equals(f.Name, target, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }
        var created = new NoteFolder { Name = target };
        d.Folders.Add(created);
        return created;
    }

    /// <summary>加入至指定頂層資料夾頂端（Issue #55）；空原文回 Empty、跨全樹去重已存在回 AlreadyExists。</summary>
    public static NoteAddResult AddToTopFolder(NotesData d, NoteFolder folder, NoteEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Key))
        {
            return NoteAddResult.Empty;
        }
        if (Contains(d, entry.Key))
        {
            return NoteAddResult.AlreadyExists;
        }
        folder.Entries.Insert(0, entry);
        return NoteAddResult.Added;
    }

    /// <summary>
    /// 建立資料夾之預設名（檔案總管慣例，Issue #38）：`新資料夾`，已占用則 `新資料夾 (2)`、`(3)`…
    /// 全樹唯一（建立後隨即進入原地更名，故只需避免同名混淆）。
    /// </summary>
    public static string NextNewFolderName(NotesData d)
    {
        var names = AllFolders(d).Select(f => f.Name).ToHashSet();
        const string baseName = "New Folder";
        if (!names.Contains(baseName))
        {
            return baseName;
        }
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 檔案總管式**自然排序**比較（Issue #42，等效 `StrCmpLogicalW` 核心行為）：
    /// 連續數字段依**數值**比較（「新資料夾 (2)」＜「新資料夾 (10)」），其餘字元依目前文化、不分大小寫。
    /// </summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) { i++; }
                while (j < b.Length && char.IsAsciiDigit(b[j])) { j++; }
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length)
                {
                    return na.Length - nb.Length; // 位數多者數值大
                }
                var c = string.CompareOrdinal(na, nb);
                if (c != 0)
                {
                    return c;
                }
            }
            else
            {
                var c = string.Compare(a[i].ToString(), b[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
                if (c != 0)
                {
                    return c;
                }
                i++;
                j++;
            }
        }
        return (a.Length - i) - (b.Length - j); // 前綴相同者短在前
    }

    /// <summary>全樹**同層依名稱自然排序**（檔案總管慣例，Issue #42）：拖曳只改歸屬、順序一律由名稱決定。</summary>
    public static void SortFolders(NotesData d) => SortSiblings(d.Folders);

    private static void SortSiblings(List<NoteFolder> list)
    {
        list.Sort((x, y) => NaturalCompare(x.Name, y.Name));
        foreach (var f in list)
        {
            SortSiblings(f.Folders);
        }
    }

    /// <summary>清空指定資料夾之全部條目（不含子資料夾；右欄[清除全部]，Issue #42）。找不到夾即無為。</summary>
    public static void ClearEntries(NotesData d, string folderId) => FindFolder(d, folderId)?.Entries.Clear();

    /// <summary>
    /// 設定條目底色（Issue #44）：跨全樹依 Id 尋得後以 record `with` 換置；<paramref name="color"/>
    /// 為 hex 字串、空＝預設白。找不到條目回 false。
    /// </summary>
    public static bool SetEntryColor(NotesData d, string entryId, string color)
    {
        foreach (var f in AllFolders(d))
        {
            var i = f.Entries.FindIndex(e => e.Id == entryId);
            if (i >= 0)
            {
                f.Entries[i] = f.Entries[i] with { Color = color ?? "" };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 記錄一次發音練習分數並保留**最佳分**（spec#10）：跨全樹依 Id 尋得後，將 <see cref="NoteEntry.PracticeScore"/>
    /// 更新為既有值與本次 <paramref name="score"/> 之較大者（取 Max；既有 -1＝未練，故首次即採本次分）。找不到條目回 false。
    /// 成績框顯示此最佳分、是否達標由呈現層以「最佳分 ≥ 及格門檻」判定；曾達標即恆綠，直到 <see cref="ResetFolderPractice"/> 歸零。
    /// </summary>
    public static bool SetPracticeScore(NotesData d, string entryId, int score)
    {
        foreach (var f in AllFolders(d))
        {
            var i = f.Entries.FindIndex(e => e.Id == entryId);
            if (i >= 0)
            {
                var best = Math.Max(f.Entries[i].PracticeScore, score);
                f.Entries[i] = f.Entries[i] with { PracticeScore = best };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 更新條目之三欄內容（複查回饋：編輯原文後重譯）：以新結果覆蓋 Original/Phonetic/Translation，
    /// **保留 Id/AddedAt/Color**；因原文已變、發音練習最佳分歸 -1（成績框回未練）。找不到 id 回 false。純函式、可測。
    /// </summary>
    public static bool UpdateEntryContent(NotesData d, string entryId, QueryResult r)
    {
        foreach (var f in AllFolders(d))
        {
            var i = f.Entries.FindIndex(e => e.Id == entryId);
            if (i >= 0)
            {
                f.Entries[i] = f.Entries[i] with
                {
                    Original = r.Original,
                    Phonetic = r.Phonetic,
                    Translation = r.Translation,
                    PracticeScore = -1, // 原文變更 → 練習分數失效、回未練
                };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 清空指定資料夾之發音練習紀錄（spec#10）：該夾所有條目 <see cref="NoteEntry.PracticeScore"/> 歸 -1
    /// （成績框全回未練灰「—」），子夾與他夾不動。取代原「清除全部（刪筆記）」之 UI 入口——本法**不刪筆記、只重置練習**。
    /// 找不到夾即無為。純函式、可單元測試。
    /// </summary>
    public static void ResetFolderPractice(NotesData d, string folderId)
    {
        var f = FindFolder(d, folderId);
        if (f is null)
        {
            return;
        }
        for (var i = 0; i < f.Entries.Count; i++)
        {
            f.Entries[i] = f.Entries[i] with { PracticeScore = -1 };
        }
    }

    /// <summary>新增頂層資料夾。</summary>
    public static NoteFolder AddFolder(NotesData d, string name)
    {
        var f = new NoteFolder { Name = Clean(name) };
        d.Folders.Add(f);
        return f;
    }

    /// <summary>於指定父夾下新增子資料夾（找不到父夾回 null）。</summary>
    public static NoteFolder? AddSubFolder(NotesData d, string parentId, string name)
    {
        var p = FindFolder(d, parentId);
        if (p is null)
        {
            return null;
        }
        var f = new NoteFolder { Name = Clean(name) };
        p.Folders.Add(f);
        return f;
    }

    public static void RenameFolder(NotesData d, string id, string name)
    {
        var f = FindFolder(d, id);
        if (f is not null && !string.IsNullOrWhiteSpace(name))
        {
            f.Name = name.Trim();
        }
    }

    /// <summary>刪除資料夾（連其子孫與條目）；刪後確保仍有預設資料夾。</summary>
    public static void RemoveFolder(NotesData d, string id)
    {
        RemoveFolderById(d.Folders, id);
        Ensure(d);
    }

    private static bool RemoveFolderById(List<NoteFolder> list, string id)
    {
        var i = list.FindIndex(f => f.Id == id);
        if (i >= 0)
        {
            list.RemoveAt(i);
            return true;
        }
        foreach (var f in list)
        {
            if (RemoveFolderById(f.Folders, id))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>自樹中任一資料夾刪除指定條目。</summary>
    public static void RemoveEntry(NotesData d, string entryId)
    {
        foreach (var f in AllFolders(d))
        {
            f.Entries.RemoveAll(e => e.Id == entryId);
        }
    }

    /// <summary>將條目移動到目標資料夾頂端（跨夾歸類）。</summary>
    public static void MoveEntry(NotesData d, string entryId, string toFolderId)
    {
        var to = FindFolder(d, toFolderId);
        if (to is null)
        {
            return;
        }
        foreach (var f in AllFolders(d))
        {
            var idx = f.Entries.FindIndex(e => e.Id == entryId);
            if (idx >= 0)
            {
                if (ReferenceEquals(f, to))
                {
                    return;
                }
                var e = f.Entries[idx];
                f.Entries.RemoveAt(idx);
                to.Entries.Insert(0, e);
                return;
            }
        }
    }

    /// <summary>
    /// 移動資料夾節點到新父夾（<paramref name="toParentId"/> 為 null＝移到頂層）。
    /// **防成環**：不得移入自身或其子孫；找不到節點/父夾回 false。
    /// </summary>
    public static bool MoveFolder(NotesData d, string folderId, string? toParentId)
    {
        var node = FindFolder(d, folderId);
        if (node is null)
        {
            return false;
        }
        List<NoteFolder> target;
        if (toParentId is null)
        {
            target = d.Folders;
        }
        else
        {
            var p = FindFolder(d, toParentId);
            if (p is null || p.Id == folderId || IsDescendant(node, p.Id))
            {
                return false; // 移入自身或子孫 → 拒絕（防環）
            }
            target = p.Folders;
        }
        if (!DetachFolder(d.Folders, node))
        {
            return false;
        }
        target.Add(node);
        return true;
    }

    private static bool IsDescendant(NoteFolder node, string maybeDescendantId) =>
        Walk(node.Folders).Any(f => f.Id == maybeDescendantId);

    private static bool DetachFolder(List<NoteFolder> list, NoteFolder node)
    {
        if (list.Remove(node))
        {
            return true;
        }
        foreach (var f in list)
        {
            if (DetachFolder(f.Folders, node))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 依英文原文自然排序目前資料夾之條目（Issue #52）：<paramref name="ascending"/>＝順向（A→Z）、否則反向（Z→A）。
    /// 沿用 <see cref="NaturalCompare"/>（大小寫不敏感、數字段依數值），與資料夾排序一致；重排後由呼叫端 Save 持久化。
    /// 空夾即無為。純函式、可單元測試。
    /// <para><b>#126 起已由 <see cref="ProjectView"/> 非破壞式投影取代、生產不再呼叫</b>；僅存單元測試佐證自然排序語義。
    /// <b>切勿</b>用於筆記檢視——就地變異 <c>f.Entries</c> 會覆寫使用者手動序（#126 刻意移除之破壞式行為）。</para>
    /// </summary>
    public static void SortEntries(NoteFolder f, bool ascending)
    {
        f.Entries.Sort((x, y) =>
        {
            var c = NaturalCompare(x.Original ?? "", y.Original ?? "");
            return ascending ? c : -c;
        });
    }

    /// <summary>
    /// **非破壞式**檢視投影（#126）：依 <paramref name="mode"/>＋<paramref name="ascending"/> 產出**顯示序之新清單**，
    /// **不改動** <paramref name="entries"/>（手動序＝「自訂順序」SSOT，恆為傳入序）。Alpha 依原文 <see cref="NaturalCompare"/>；
    /// Time 以**穩定排序**依 <see cref="NoteEntry.AddedAt"/>（無值 <c>default</c> 視為最舊）；Manual 直接用手動序（asc）或其反序（desc）。
    /// 純函式、可單元測試——排序改為此投影後，字母／日期不再覆寫使用者拖曳之手動序。
    /// </summary>
    public static List<NoteEntry> ProjectView(IEnumerable<NoteEntry> entries, NoteSortMode mode, bool ascending)
    {
        var list = entries.ToList();
        switch (mode)
        {
            case NoteSortMode.Alpha:
                list.Sort((x, y) =>
                {
                    var c = NaturalCompare(x.Original ?? "", y.Original ?? "");
                    return ascending ? c : -c;
                });
                return list;
            case NoteSortMode.Time:
                return (ascending
                    ? list.OrderBy(e => e.AddedAt)
                    : list.OrderByDescending(e => e.AddedAt)).ToList();
            default: // Manual：手動序（正向）或其反序
                if (!ascending)
                {
                    list.Reverse();
                }
                return list;
        }
    }

    /// <summary>
    /// 依登記時間排序目前資料夾之條目（Issue #104）：<paramref name="ascending"/>＝舊→新（Old→New）、否則新→舊。
    /// 以 OrderBy **穩定排序**（同時刻多筆維持相對順序；穩定性條款限本時間排序）；
    /// `AddedAt == default`（極舊資料無值）視為最舊——Old→New 排最前、New→Old 排最後。
    /// 空夾即無為；重排後由呼叫端 Save 持久化。純函式、可單元測試。
    /// <para><b>#126 起已由 <see cref="ProjectView"/> 非破壞式投影取代、生產不再呼叫</b>；僅存單元測試佐證穩定時間排序語義。
    /// <b>切勿</b>用於筆記檢視——就地變異 <c>f.Entries</c> 會覆寫使用者手動序（#126 刻意移除之破壞式行為）。</para>
    /// </summary>
    public static void SortEntriesByTime(NoteFolder f, bool ascending)
    {
        var sorted = (ascending
            ? f.Entries.OrderBy(e => e.AddedAt)
            : f.Entries.OrderByDescending(e => e.AddedAt)).ToList();
        f.Entries.Clear();
        f.Entries.AddRange(sorted);
    }

    /// <summary>同一資料夾內把條目自 from 位置移到 to 位置（拖曳排序）。</summary>
    public static void Reorder(NoteFolder f, int from, int to)
    {
        if (from < 0 || from >= f.Entries.Count)
        {
            return;
        }
        to = Math.Max(0, Math.Min(to, f.Entries.Count - 1));
        if (from == to)
        {
            return;
        }
        var e = f.Entries[from];
        f.Entries.RemoveAt(from);
        f.Entries.Insert(to, e);
    }

    private static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? "New Folder" : name.Trim();
}
