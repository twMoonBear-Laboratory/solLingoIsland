namespace LingoIsland.Query;

/// <summary>
/// 匯入之一筆寫入紀錄（spec#14／#324 整批撤銷 ①）：<see cref="Before"/> 為 null＝新增（<see cref="After"/> 為寫入之條目全貌、<see cref="FolderId"/>＝落點夾）；
/// 非 null＝更新（覆寫前與覆寫後之全貌，同一 Id）。只在存檔成功之後產生。
/// </summary>
public sealed record NoteWriteRecord(NoteEntry? Before, NoteEntry After, string FolderId)
{
    public bool IsAdded => Before is null;
}

/// <summary>撤銷之逐筆處置。</summary>
public enum NoteUndoAction { Remove, Restore, Skip }

/// <summary>撤銷計畫之一筆：字（寫入後之原文）、原為新增與否、處置與跳過原因（非跳過為空）。</summary>
public sealed record NoteUndoItem(string Word, bool WasAdded, NoteUndoAction Action, string Reason);

/// <summary>撤銷計畫（依處理順序）＋三類計數。</summary>
public sealed record NoteUndoPlan(IReadOnlyList<NoteUndoItem> Items)
{
    public int Removed => Items.Count(i => i.Action == NoteUndoAction.Remove);
    public int Restored => Items.Count(i => i.Action == NoteUndoAction.Restore);
    public int Skipped => Items.Count(i => i.Action == NoteUndoAction.Skip);
    public IReadOnlyList<NoteUndoItem> Skips => Items.Where(i => i.Action == NoteUndoAction.Skip).ToList();
    public bool HasChange => Removed + Restored > 0;
}

/// <summary>
/// 整批撤銷之純函式（[modPresent模組] 筆記清單匯入契約「整批撤銷」④；#324）：依寫入日誌、以條目 Id 於現行資料判定逐筆處置——
/// 新增之字與寫入時全貌相同且仍在落點夾才移除；更新之字原文、音標、中譯與寫入後相同才把音標與中譯還原為覆寫前（其餘欄位維持現況）；
/// 其餘跳過並附原因。同一 Id 多筆先合併。不依賴 UI、可單元測試。
/// </summary>
public static class NoteImportUndo
{
    public const string ReasonGone = "已不在筆記";
    public const string ReasonGoneUpdated = "已不在筆記，無法還原";
    public const string ReasonModified = "匯入後已修改";
    public const string ReasonMoved = "匯入後已移到其他資料夾";

    /// <summary>
    /// 依 Id 合併（④）：覆寫前＝最早一筆之覆寫前（最早一筆為新增即仍為新增）、寫入後＝最後一筆之寫入後、落點夾＝最早一筆之落點夾；
    /// 次序依各 Id 最後一次寫入之先後。
    /// </summary>
    public static IReadOnlyList<NoteWriteRecord> Merge(IReadOnlyList<NoteWriteRecord> journal)
    {
        var first = new Dictionary<string, NoteWriteRecord>(StringComparer.Ordinal);
        var last = new Dictionary<string, (NoteWriteRecord Rec, int Index)>(StringComparer.Ordinal);
        for (var i = 0; i < journal.Count; i++)
        {
            var r = journal[i];
            first.TryAdd(r.After.Id, r);
            last[r.After.Id] = (r, i);
        }
        return last.OrderBy(kv => kv.Value.Index)
            .Select(kv => first[kv.Key] with { After = kv.Value.Rec.After })
            .ToList();
    }

    /// <summary>試算（不改動 <paramref name="d"/>）。</summary>
    public static NoteUndoPlan Plan(NotesData d, IReadOnlyList<NoteWriteRecord> journal) => Run(d, journal, apply: false);

    /// <summary>依計畫改動 <paramref name="d"/>（移除／還原），回計畫。</summary>
    public static NoteUndoPlan Apply(NotesData d, IReadOnlyList<NoteWriteRecord> journal) => Run(d, journal, apply: true);

    private static NoteUndoPlan Run(NotesData d, IReadOnlyList<NoteWriteRecord> journal, bool apply)
    {
        var merged = Merge(journal);
        var items = new List<NoteUndoItem>();
        for (var k = merged.Count - 1; k >= 0; k--) // 逆序
        {
            var r = merged[k];
            var word = r.After.Original;
            var (folder, idx) = Locate(d, r.After.Id);
            if (r.IsAdded)
            {
                if (folder is null) { items.Add(new(word, true, NoteUndoAction.Skip, ReasonGone)); continue; }
                var cur = folder.Entries[idx];
                if (cur != r.After) { items.Add(new(word, true, NoteUndoAction.Skip, ReasonModified)); continue; }
                if (folder.Id != r.FolderId) { items.Add(new(word, true, NoteUndoAction.Skip, ReasonMoved)); continue; }
                if (apply) { folder.Entries.RemoveAt(idx); }
                items.Add(new(word, true, NoteUndoAction.Remove, ""));
            }
            else
            {
                if (folder is null) { items.Add(new(word, false, NoteUndoAction.Skip, ReasonGoneUpdated)); continue; }
                var cur = folder.Entries[idx];
                if (cur.Original != r.After.Original || cur.Phonetic != r.After.Phonetic || cur.Translation != r.After.Translation)
                {
                    items.Add(new(word, false, NoteUndoAction.Skip, ReasonModified));
                    continue;
                }
                if (apply) { folder.Entries[idx] = cur with { Phonetic = r.Before!.Phonetic, Translation = r.Before.Translation }; }
                items.Add(new(word, false, NoteUndoAction.Restore, ""));
            }
        }
        return new NoteUndoPlan(items);
    }

    private static (NoteFolder? Folder, int Index) Locate(NotesData d, string id)
    {
        foreach (var f in NotesStore.AllFolders(d))
        {
            var i = f.Entries.FindIndex(e => e.Id == id);
            if (i >= 0) { return (f, i); }
        }
        return (null, -1);
    }
}
