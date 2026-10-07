using System.Windows;

namespace LingoIsland.Present;

/// <summary>
/// [modHmi匯入結果視窗]（spec#14，#322）：背景匯入結束（完成／取消／中斷）或全部自備中譯寫入後之結果表——非模態（<c>Show</c>）、
/// 不搶焦點（<c>ShowActivated=False</c>，使用者正在打字或按住錄音不被打斷）、可捲動與選取複製（失敗清單可達 500 行，<c>MessageBox</c> 不可捲）。
/// </summary>
public partial class NotesImportResultWindow : Window
{
    public NotesImportResultWindow(NotesImportEnding ending, string body)
    {
        InitializeComponent();
        Title = NotesImport.ResultWindowTitle(ending);
        HeaderText.Text = NotesImport.ResultHeader(ending);
        BodyText.Text = body;
        OkBtn.Click += (_, _) => Close();
    }
}
