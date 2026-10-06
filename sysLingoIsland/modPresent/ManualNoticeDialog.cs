using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace LingoIsland.Present;

/// <summary>
/// 使用手冊之提示框（#311）：關於分頁鈕與系統匣項共用，沿用 repo 既有之模態 <see cref="MessageBox"/>。
/// owner 依 <see cref="UserManual.ChooseOwner"/>：主視窗可見且非最小化即以其為 owner；否則以暫時之置頂隱形視窗為 owner
/// （一般 Win32 對話框、Ctrl+C 可複製全文；不用 <c>DefaultDesktopOnly</c>，其走另一條顯示路徑、外觀與行為不一）。
/// </summary>
public static class ManualNoticeDialog
{
    public static void Show(Window? main, string message)
    {
        var owner = UserManual.ChooseOwner(main is not null, main?.IsVisible == true,
            main?.WindowState == WindowState.Minimized);
        if (owner == NoticeOwner.MainWindow)
        {
            MessageBox.Show(main!, message, UserManual.DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var helper = new Window
        {
            Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            Topmost = true, ShowActivated = true, AllowsTransparency = true, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        try
        {
            helper.Show();
            helper.Activate();
            MessageBox.Show(helper, message, UserManual.DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            helper.Close();
        }
    }
}
