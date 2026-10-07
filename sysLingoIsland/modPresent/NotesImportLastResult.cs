using System.Text;
using LingoIsland.Query;

namespace LingoIsland.Present;

/// <summary>撤銷狀態（#324 整批撤銷 ③）：除 <see cref="Available"/> 外皆為終態；<see cref="NotOffered"/>＝一字未寫入、不顯示鈕。</summary>
public enum NotesImportUndoState { NotOffered, Available, Undone, NothingToUndo, Expired }

/// <summary>
/// 「上次匯入」（[modPresent模組] 筆記清單匯入契約「整批撤銷」③，#324）：結局、結果全文、寫入日誌與撤銷狀態——<c>App</c> 持有、結果視窗只呈現；
/// 撤銷完成後之標頭、Title、內文寫回本物件（自筆記頁「上次匯入結果」重開即見）。狀態轉移為純邏輯、可單元測試。
/// </summary>
public sealed class NotesImportLastResult
{
    public NotesImportLastResult(NotesImportEnding ending, string body, IReadOnlyList<NoteWriteRecord> journal)
    {
        Ending = ending;
        Body = body;
        Journal = journal;
        State = journal.Count > 0 ? NotesImportUndoState.Available : NotesImportUndoState.NotOffered;
    }

    public NotesImportEnding Ending { get; }
    public IReadOnlyList<NoteWriteRecord> Journal { get; }
    public string Body { get; private set; }
    public NotesImportUndoState State { get; private set; }

    /// <summary>狀態或內文變更（結果視窗據以重繪）。</summary>
    public event Action? Changed;

    public string Title => State == NotesImportUndoState.Undone ? "匯入清單：已撤銷" : NotesImport.ResultWindowTitle(Ending);
    public string Header => State == NotesImportUndoState.Undone ? "已撤銷本次匯入" : NotesImport.ResultHeader(Ending);

    /// <summary>撤銷鈕是否可按。</summary>
    public bool CanUndo => State == NotesImportUndoState.Available;

    /// <summary>合併後之條目數（＝新增＋更新；⑦ 之 n）。</summary>
    public int EntryCount => NoteImportUndo.Merge(Journal).Count;

    /// <summary>下一批開始（③）：可撤→已失效；其餘不變。</summary>
    public void Expire() => Move(NotesImportUndoState.Expired);

    /// <summary>試算或實算全部跳過（⑦）：可撤→無可撤。</summary>
    public void MarkNothingToUndo() => Move(NotesImportUndoState.NothingToUndo);

    /// <summary>撤銷完成（⑧）：可撤→已撤銷，內文首段插入撤銷摘要。</summary>
    public void MarkUndone(NoteUndoPlan plan)
    {
        if (State != NotesImportUndoState.Available) { return; }
        Body = NotesImportUndoText.Summary(plan) + "\n\n" + NotesImportUndoText.OriginalResultLead + "\n" + Body;
        Move(NotesImportUndoState.Undone);
    }

    private void Move(NotesImportUndoState to)
    {
        if (State != NotesImportUndoState.Available) { return; } // 終態不再轉移
        State = to;
        Changed?.Invoke();
    }
}

/// <summary>整批撤銷之文案（#324 ③⑦⑧，純函式）。</summary>
public static class NotesImportUndoText
{
    public const string DialogTitle = "撤銷本次匯入";
    public const string ButtonText = "撤銷本次匯入";
    public const string ButtonUndoneText = "已撤銷";
    public const string ButtonToolTip = "移除這次新增的字、把這次更新的字還原為匯入前；匯入後改過的字不動";
    public const string HintAvailable = "下一次匯入開始後，就不能撤銷這次了；關掉此視窗後，可從筆記頁「上次匯入結果」再打開";
    public const string HintExpired = "已開始新的匯入——只能撤銷最近一次匯入";
    public const string HintNothing = "這次匯入的字在匯入後都已修改、移動或刪除，沒有可撤銷的";
    public const string OriginalResultLead = "——以下為原本的匯入結果——";
    public const string RestoreBusyText = "正在匯入備份資料，完成後程式會關閉，這次匯入無法再撤銷。";
    public const string ImportBusyText = "有一批匯入正在進行，上一次匯入已不能撤銷。";
    public const string RecheckFailedText = "確認期間已開始新的匯入或正在匯入備份資料，這次沒有撤銷任何字。";
    public const string ReadLockedText = "暫時讀不到筆記檔（可能被其他程式鎖住），這次沒有撤銷任何字，請稍後再按一次。";
    public const string EntryGoneEditToast = "這則筆記已不在（可能已被撤銷或刪除），變更未儲存";
    public const string EntryGoneScoreToast = "這則筆記已不在（可能已被撤銷或刪除），這次分數未記下";
    public const string LastResultToolTip = "再打開最近一次匯入的結果視窗（可在那裡撤銷）";
    public const string LastResultBusyToolTip = "匯入進行中——結束後可看這一批的結果";

    /// <summary>撤銷說明列（③）；已撤銷與不提供時為空（隱藏）。</summary>
    public static string Hint(NotesImportUndoState s) => s switch
    {
        NotesImportUndoState.Available => HintAvailable,
        NotesImportUndoState.Expired => HintExpired,
        NotesImportUndoState.NothingToUndo => HintNothing,
        _ => "",
    };

    /// <summary>全部跳過（⑦）。</summary>
    public static string NothingText(int n) => $"這次匯入的 {n} 字在匯入後都已修改、移動或刪除，沒有可撤銷的字。";

    /// <summary>存檔失敗（⑤）。</summary>
    public static string SaveFailedText(string reason) => $"筆記存檔失敗（{reason}），這次沒有撤銷任何字。";

    /// <summary>確認框內文（⑦）：0 筆之行不列；跳過字列前 10 個。</summary>
    public static string ConfirmText(NoteUndoPlan plan)
    {
        var sb = new StringBuilder("要撤銷這次匯入嗎？\n");
        if (plan.Removed > 0) { sb.Append($"\n・移除這次新增的 {plan.Removed} 字"); }
        if (plan.Restored > 0) { sb.Append($"\n・把這次更新的 {plan.Restored} 字還原為匯入前的音標與中譯"); }
        if (plan.Skipped > 0)
        {
            var words = plan.Skips.Select(i => i.Word).ToList();
            var shown = string.Join("、", words.Take(10)) + (words.Count > 10 ? $"…等 {words.Count} 字" : "");
            sb.Append($"\n・{plan.Skipped} 字在匯入後已修改、移動或刪除，保持現狀不動：{shown}");
        }
        sb.Append("\n\n撤銷後無法再復原；已花用的 AI 查詢費用不會退回。");
        return sb.ToString();
    }

    /// <summary>完成摘要（⑧）：首句＋（有跳過時）逐字跳過清單。</summary>
    public static string Summary(NoteUndoPlan plan)
    {
        var sb = new StringBuilder($"已撤銷本次匯入：移除 {plan.Removed} 字、還原 {plan.Restored} 字、跳過 {plan.Skipped} 字。");
        if (plan.Skipped > 0)
        {
            sb.Append("\n跳過（保持現狀）：");
            foreach (var i in plan.Skips) { sb.Append($"\n· {i.Word}——{i.Reason}"); }
        }
        return sb.ToString();
    }

    /// <summary>完成 toast（⑧）。</summary>
    public static string Toast(NoteUndoPlan plan)
        => $"已撤銷本次匯入：移除 {plan.Removed} 字、還原 {plan.Restored} 字" + (plan.Skipped > 0 ? $"（跳過 {plan.Skipped} 字）" : "");
}
