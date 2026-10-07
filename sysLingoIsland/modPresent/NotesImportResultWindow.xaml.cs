using System.Windows;

namespace LingoIsland.Present;

/// <summary>
/// [modHmi匯入結果視窗]（spec#14，#322）：背景匯入結束（完成／取消／中斷）或全部自備中譯寫入後之結果表——非模態（<c>Show</c>）、
/// 不搶焦點（<c>ShowActivated=False</c>，使用者正在打字或按住錄音不被打斷）、可捲動與選取複製（失敗清單可達 500 行，<c>MessageBox</c> 不可捲）。
/// #324：呈現 <see cref="NotesImportLastResult"/>（App 持有）——撤銷說明與「撤銷本次匯入」鈕依其撤銷狀態；按下交 <see cref="UndoRequested"/>（流程在 App），
/// 狀態或內文變更即重繪；關閉不失去撤銷（筆記頁「上次匯入結果」再開）。
/// </summary>
public partial class NotesImportResultWindow : Window
{
    public NotesImportResultWindow(NotesImportLastResult model)
    {
        InitializeComponent();
        Model = model;
        UndoBtn.Content = NotesImportUndoText.ButtonText;
        UndoBtn.ToolTip = NotesImportUndoText.ButtonToolTip;
        Render();
        model.Changed += Render;
        Closed += (_, _) => model.Changed -= Render;
        OkBtn.Click += (_, _) => Close();
        UndoBtn.Click += (_, _) => UndoRequested?.Invoke(this);
    }

    /// <summary>本視窗呈現之「上次匯入」。</summary>
    public NotesImportLastResult Model { get; }

    /// <summary>按下「撤銷本次匯入」（參數＝本視窗，作確認框之 owner）。</summary>
    public event Action<NotesImportResultWindow>? UndoRequested;

    private void Render()
    {
        Title = Model.Title;
        HeaderText.Text = Model.Header;
        BodyText.Text = Model.Body;
        var s = Model.State;
        UndoBtn.Visibility = s == NotesImportUndoState.NotOffered ? Visibility.Collapsed : Visibility.Visible;
        UndoBtn.IsEnabled = Model.CanUndo;
        UndoBtn.Content = s == NotesImportUndoState.Undone ? NotesImportUndoText.ButtonUndoneText : NotesImportUndoText.ButtonText;
        var hint = NotesImportUndoText.Hint(s);
        UndoHint.Text = hint;
        UndoHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UndoHint.ToolTip = hint.Length == 0 ? null : hint;
    }
}
