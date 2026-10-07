using LingoIsland.Query;

namespace LingoIsland.Present;

/// <summary>
/// 筆記頁與背景匯入同步之純函式（#322，契約「背景執行」⑥）：不依賴 WPF、可單元測試。
/// </summary>
public static class NotesPageSync
{
    /// <summary>
    /// 資料夾結構簽章：(上層 Id, Id, 名稱) 之集合，<b>與兄弟順序無關</b>——筆記頁 <c>BuildTree</c> 會就地依名稱排序記憶體資料，
    /// 而磁碟上之順序未必已排序（例如他處以名稱新建頂層夾附加在末尾），以順序比對會把「沒變」誤判為「結構有變」而逐字重建整棵樹。
    /// </summary>
    public static string StructureSignature(NotesData d)
    {
        var items = new List<string>();
        void Walk(IEnumerable<NoteFolder> fs, string parent)
        {
            foreach (var f in fs) { items.Add(parent + ">" + f.Id + ":" + f.Name); Walk(f.Folders, f.Id); }
        }
        Walk(d.Folders, "");
        items.Sort(StringComparer.Ordinal);
        return string.Join("|", items);
    }

    /// <summary>目前夾之顯示簽章：顯示序之 Id、音標、中譯（有變才重繪條目區）。</summary>
    public static string DisplaySignature(IEnumerable<NoteEntry>? displayed)
        => displayed is null ? "" : string.Join("|", displayed.Select(e => e.Id + "\u001f" + e.Phonetic + "\u001f" + e.Translation));

    /// <summary>
    /// 拖曳排序之落點（#322 ⑥(d)）：以畫面槽位所對應之鄰卡 Id 換算——插在 <paramref name="shownIds"/>[slot] 之前；槽位在末端則插在最後一張顯示卡之後。
    /// <paramref name="idsAfterRemoval"/>＝已移除被拖之卡後之資料序。鄰卡即被拖之卡本身或已不在（被刪）→ 留在原位 <paramref name="from"/>。
    /// 畫面待更新（背景匯入剛插入新字而尚未重繪）時亦落在使用者瞄準之兩卡之間。
    /// </summary>
    public static int DropIndex(IReadOnlyList<string?> shownIds, int slot, IReadOnlyList<string> idsAfterRemoval, string movingId, int from)
    {
        int to;
        if (slot < shownIds.Count)
        {
            var before = shownIds[slot];
            to = before is null || before == movingId ? -1 : IndexOf(idsAfterRemoval, before);
        }
        else
        {
            var after = shownIds.Count > 0 ? shownIds[^1] : null;
            to = after is null || after == movingId ? -1 : IndexOf(idsAfterRemoval, after) is var i and >= 0 ? i + 1 : -1;
        }
        return to < 0 ? Math.Clamp(from, 0, idsAfterRemoval.Count) : to;
    }

    /// <summary>
    /// 條目區背景重繪之閘（#322 ⑥(c)，狀態機、不依賴 WPF）：<see cref="Request"/> 於延後條件成立或本頁不可見時只標記待更新；
    /// 節流期間（上一次重繪後 2 秒內）合併為一次排隊；<see cref="Tick"/>（節流計時到）補做排隊者；回 true＝呼叫端此刻重繪並啟動節流計時。
    /// </summary>
    public sealed class BackgroundRenderGate
    {
        private bool _throttling, _queued;

        /// <summary>畫面待更新（延後或節流中）。</summary>
        public bool Stale { get; private set; }

        public bool Request(bool hold, bool visible)
        {
            if (hold || !visible) { Stale = true; return false; }
            if (_throttling) { _queued = true; Stale = true; return false; }
            _throttling = true;
            Stale = false;
            return true;
        }

        public bool Tick(bool hold, bool visible)
        {
            _throttling = false;
            if (!_queued) { return false; }
            _queued = false;
            return Request(hold, visible);
        }

        /// <summary>任何原因之整區重繪（切夾、整頁重載）皆使畫面與資料一致。</summary>
        public void MarkRendered() => Stale = false;
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var i = 0; i < ids.Count; i++) { if (ids[i] == id) { return i; } }
        return -1;
    }
}
