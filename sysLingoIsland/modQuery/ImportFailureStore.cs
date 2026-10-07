using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LingoIsland.Query;

/// <summary>匯入失敗紀錄之一筆（#323）：一個清單檔（完整路徑）之上次失敗原字（依該檔清單序）與最後匯入時間（UTC）。</summary>
public sealed record ImportFailureRecord(string Path, IReadOnlyList<string> Words, DateTimeOffset UpdatedAt);

/// <summary>本批一個來源檔（#323）：完整路徑與該檔之原字（依清單序；可含重複，比對以 <see cref="NoteEntry.KeyOf"/>）。</summary>
public sealed record ImportFailureSource(string Path, IReadOnlyList<string> Words);

/// <summary>讀紀錄之結果類別（#323）：Missing／Corrupt 視同無紀錄且可覆寫；Unreadable／Newer 視同無紀錄且<b>本次不寫入</b>。</summary>
public enum ImportFailureReadState { Ok, Missing, Corrupt, Unreadable, Newer }

/// <summary>
/// 只勾上次失敗字之純函式（[modPresent模組] 筆記清單匯入契約「只勾上次失敗字」③④，spec#14／#323）：
/// 更新規則與按鈕 N 之鍵集合——不碰磁碟，供單元測試。
/// </summary>
public static class ImportFailureLog
{
    /// <summary>
    /// 更新規則（③）：對本批每個來源檔 F：新(F)＝（舊(F) ∩ F 之字 − 成功）∪（失敗 ∩ F 之字）；成功之字另自<b>所有檔</b>剔除；
    /// 新紀錄為空即刪筆；本批含之檔仍有紀錄者 UpdatedAt 刷新為 <paramref name="nowUtc"/>、路徑寫法取本次；逾 <paramref name="maxFiles"/> 依 UpdatedAt 淘汰最舊（並列先淘汰非本批）。
    /// 回傳新紀錄與是否有任何變動（無變動即不必寫檔）。
    /// </summary>
    public static (List<ImportFailureRecord> Records, bool Changed) Apply(
        IReadOnlyList<ImportFailureRecord> old, IReadOnlyList<ImportFailureSource> batch,
        IEnumerable<string> succeeded, IEnumerable<string> failed, DateTimeOffset nowUtc, int maxFiles)
    {
        var ok = new HashSet<string>(succeeded.Select(NoteEntry.KeyOf).Where(k => k.Length > 0), StringComparer.Ordinal);
        var bad = new HashSet<string>(failed.Select(NoteEntry.KeyOf).Where(k => k.Length > 0), StringComparer.Ordinal);
        var batchByPath = new Dictionary<string, ImportFailureSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in batch) { if (!string.IsNullOrWhiteSpace(b.Path)) { batchByPath[b.Path] = b; } } // 同路徑重複時後者為準（呼叫端已去重）

        var result = new List<ImportFailureRecord>();
        var inBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oldByPath = new Dictionary<string, ImportFailureRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in old) { oldByPath.TryAdd(r.Path, r); }

        // 不在本批之檔：只剔除本次成功之字（成功是字的屬性）
        foreach (var r in old)
        {
            if (batchByPath.ContainsKey(r.Path) || !ReferenceEquals(oldByPath[r.Path], r)) { continue; }
            var kept = r.Words.Where(w => !ok.Contains(NoteEntry.KeyOf(w))).ToList();
            if (kept.Count > 0) { result.Add(kept.Count == r.Words.Count ? r : r with { Words = kept }); }
        }
        // 本批之檔：依該檔清單序重建
        foreach (var b in batchByPath.Values)
        {
            var oldKeys = oldByPath.TryGetValue(b.Path, out var o)
                ? new HashSet<string>(o.Words.Select(NoteEntry.KeyOf), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var words = new List<string>();
            foreach (var w in b.Words)
            {
                var t = (w ?? "").Trim();
                var k = NoteEntry.KeyOf(t);
                if (k.Length == 0 || !seen.Add(k)) { continue; }
                if (bad.Contains(k) || (oldKeys.Contains(k) && !ok.Contains(k))) { words.Add(t); }
            }
            if (words.Count > 0) { result.Add(new ImportFailureRecord(b.Path, words, nowUtc)); inBatch.Add(b.Path); }
        }
        // 上限：依 UpdatedAt 淘汰最舊，並列先淘汰非本批
        if (result.Count > maxFiles)
        {
            result = result.OrderByDescending(r => r.UpdatedAt).ThenBy(r => inBatch.Contains(r.Path) ? 0 : 1).Take(maxFiles).ToList();
        }
        return (result, !SameRecords(old, result));
    }

    /// <summary>按鈕 N 之鍵集合（④）：本批各來源檔（完整路徑、不分大小寫）之紀錄取聯集（去重鍵）。</summary>
    public static HashSet<string> FailedKeysFor(IReadOnlyList<ImportFailureRecord> records, IEnumerable<string> paths)
    {
        var want = new HashSet<string>(paths.Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (!want.Contains(r.Path)) { continue; }
            foreach (var w in r.Words) { var k = NoteEntry.KeyOf(w); if (k.Length > 0) { keys.Add(k); } }
        }
        return keys;
    }

    private static bool SameRecords(IReadOnlyList<ImportFailureRecord> a, IReadOnlyList<ImportFailureRecord> b)
    {
        if (a.Count != b.Count) { return false; }
        var byPath = new Dictionary<string, ImportFailureRecord>(StringComparer.Ordinal);
        foreach (var r in a) { byPath.TryAdd(r.Path, r); } // 路徑寫法（大小寫）不同即算變動
        foreach (var r in b)
        {
            if (!byPath.TryGetValue(r.Path, out var x) || x.UpdatedAt != r.UpdatedAt || !x.Words.SequenceEqual(r.Words, StringComparer.Ordinal)) { return false; }
        }
        return true;
    }
}

/// <summary>
/// 匯入失敗紀錄之儲存（[modQuery模組]，spec#14／#323）：<c>%APPDATA%\LingoIsland\import-failures.json</c>——只存清單檔路徑、失敗原字與最後匯入時間，
/// 不存中譯／音標／失敗原因。讀寫失敗與損毀一律不擲出（視同無紀錄、靜默略過），<b>絕不影響匯入本身</b>；原子寫入（同夾 <c>.tmp</c> 搬移取代）。
/// </summary>
public sealed class ImportFailureStore
{
    /// <summary>紀錄檔格式版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>至多保留之清單檔數（逾者依最後匯入時間淘汰最舊）。</summary>
    public const int MaxFiles = 200;

    public const string FileName = "import-failures.json";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland", FileName);

    private readonly string _path;

    public ImportFailureStore(string? path = null) => _path = path ?? DefaultPath;

    public string FilePath => _path;

    /// <summary>讀紀錄（不擲出）：檔不存在＝Missing；被鎖等 I/O 例外＝Unreadable；JSON 壞或 Version 缺／&lt;1＝Corrupt；Version 大於本版＝Newer；個別筆欄位不合只略過該筆。</summary>
    public (IReadOnlyList<ImportFailureRecord> Records, ImportFailureReadState State) Read()
    {
        string json;
        try
        {
            if (!File.Exists(_path)) { return (Array.Empty<ImportFailureRecord>(), ImportFailureReadState.Missing); }
            json = File.ReadAllText(_path);
        }
        catch (Exception) { return (Array.Empty<ImportFailureRecord>(), ImportFailureReadState.Unreadable); }
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            // 版本以數值判（日後寫成 2.0 或超出 int 亦認得是新版、不覆寫）；非數值或 <1＝損毀
            if (root is null || root["Version"] is not JsonValue vv || !vv.TryGetValue<double>(out var ver) || double.IsNaN(ver) || ver < 1)
            {
                return (Array.Empty<ImportFailureRecord>(), ImportFailureReadState.Corrupt);
            }
            if (ver > CurrentVersion) { return (Array.Empty<ImportFailureRecord>(), ImportFailureReadState.Newer); }
            if (root["Files"] is not JsonArray files) { return (Array.Empty<ImportFailureRecord>(), ImportFailureReadState.Corrupt); }
            var list = new List<ImportFailureRecord>();
            foreach (var f in files)
            {
                if (f is not JsonObject o) { continue; }
                if (o["Path"] is not JsonValue pv || !pv.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path)) { continue; }
                if (o["Words"] is not JsonArray wa) { continue; }
                var words = new List<string>();
                var bad = false;
                foreach (var w in wa)
                {
                    if (w is JsonValue wv && wv.TryGetValue<string>(out var s)) { if (s.Trim().Length > 0) { words.Add(s); } }
                    else { bad = true; break; }
                }
                if (bad || words.Count == 0) { continue; }
                var at = o["UpdatedAt"] is JsonValue tv && tv.TryGetValue<string>(out var ts) && DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                    ? t.ToUniversalTime() : DateTimeOffset.MinValue;
                if (list.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) { continue; }
                list.Add(new ImportFailureRecord(path, words, at));
            }
            return (list, ImportFailureReadState.Ok);
        }
        catch (Exception) { return (Array.Empty<ImportFailureRecord>(), ImportFailureReadState.Corrupt); }
    }

    /// <summary>預掃描用（④）：本批路徑之失敗鍵聯集；讀不到／損毀＝空集合（按鈕停用）。</summary>
    public HashSet<string> FailedKeysFor(IEnumerable<string> paths)
    {
        try { return ImportFailureLog.FailedKeysFor(Read().Records, paths); }
        catch (Exception) { return new HashSet<string>(StringComparer.Ordinal); }
    }

    /// <summary>
    /// 匯入結束之更新（③⑥）：讀→<see cref="ImportFailureLog.Apply"/>→有變才寫。讀取 I/O 失敗或檔為新版即本次不寫入；任何例外不擲出。
    /// 回傳是否實際寫入（測試用）。
    /// </summary>
    public bool Update(IReadOnlyList<ImportFailureSource> batch, IEnumerable<string> succeeded, IEnumerable<string> failed, DateTimeOffset now)
    {
        try
        {
            if (batch.Count == 0) { return false; }
            var (old, state) = Read();
            if (state is ImportFailureReadState.Unreadable or ImportFailureReadState.Newer) { return false; }
            var (records, changed) = ImportFailureLog.Apply(old, batch, succeeded, failed, now.ToUniversalTime(), MaxFiles);
            if (!changed && state != ImportFailureReadState.Corrupt) { return false; }
            if (records.Count == 0 && state == ImportFailureReadState.Missing) { return false; }
            Write(records);
            return true;
        }
        catch (Exception) { return false; } // 紀錄是輔助、不是帳：寫不進去下次再記
    }

    private void Write(IReadOnlyList<ImportFailureRecord> records)
    {
        var root = new JsonObject
        {
            ["Version"] = CurrentVersion,
            ["Files"] = new JsonArray(records.Select(r => (JsonNode)new JsonObject
            {
                ["Path"] = r.Path,
                ["Words"] = new JsonArray(r.Words.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
                ["UpdatedAt"] = r.UpdatedAt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            }).ToArray()),
        };
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Opts));
        File.Move(tmp, _path, overwrite: true);
    }
}
