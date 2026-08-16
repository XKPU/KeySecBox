using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Security.Credentials.UI;
using Windows.System;
using Windows.UI.Core;

namespace KeySecBox;

/// <summary>
/// 忘记密码恢复入口。取回的主密码置于 RecoveredMaster，
/// 由调用方自动填入解锁流程并最终擦除，不显示在界面上。
/// </summary>
public sealed partial class ForgotPasswordDialog : ContentDialog
{
    public string? RecoveredMaster { get; set; }

    public ForgotPasswordDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    #region 界面

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        var cfg = RecoveryManager.GetConfig();
        BackupPanel.Visibility = cfg.HasBackup ? Visibility.Visible : Visibility.Collapsed;
        SystemPanel.Visibility = cfg.HasSystem ? Visibility.Visible : Visibility.Collapsed;
    }

    // 不允许复制带出备用密码：Ctrl+C/X/A/U 一律拦截；Enter 直接取回
    private void BackupPwdBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HasCtrlDown() && e.Key is VirtualKey.C or VirtualKey.X or VirtualKey.A or VirtualKey.U or VirtualKey.Insert)
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _ = TryBackupAsync();
            e.Handled = true;
        }
    }

    private void BackupContextRequested(object sender, ContextRequestedEventArgs e) => e.Handled = true;

    private static bool HasCtrlDown()
    {
        try
        {
            var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            return state.HasFlag(CoreVirtualKeyStates.Down);
        }
        catch { return false; }
    }

    #endregion

    #region 取回

    private async void BackupRecover_Click(object sender, RoutedEventArgs e) => await TryBackupAsync();

    private async Task<bool> TryBackupAsync()
    {
        // UI 线程先取出明文，后台线程严禁触碰 UI 元素（跨线程读 PasswordBox 会抛 RPC_E_WRONG_THREAD）
        string backupPwd = BackupPwdBox.Password;
        BackupPwdBox.Password = "";
        var master = await Task.Run(() => RecoveryManager.RecoverByBackup(backupPwd));
        if (string.IsNullOrEmpty(master))
        {
            ErrorText.Text = "备用密码不正确，无法取回。";
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
        RecoveredMaster = master;
        Hide();
        return true;
    }

    private async void SystemRecover_Click(object sender, RoutedEventArgs e)
    {
        var res = await UserConsentVerifier.RequestVerificationAsync(
            "KeySecBox 需要验证您的身份以取回保险库主密码。");
        if (res != UserConsentVerificationResult.Verified)
        {
            ErrorText.Text = "系统验证未完成，已取消。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        var master = await Task.Run(() => RecoveryManager.RecoverBySystem());
        if (string.IsNullOrEmpty(master))
        {
            ErrorText.Text = "取回失败（当前 Windows 账户无法解密恢复记录）。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        RecoveredMaster = master;
        Hide();
    }

    #endregion
}
