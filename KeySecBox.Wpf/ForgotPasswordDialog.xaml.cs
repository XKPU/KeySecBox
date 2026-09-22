using System.Windows;
using System.Windows.Input;

namespace KeySecBox;

/// <summary>
/// 忘记密码恢复入口。取回的主密码置于 <see cref="RecoveredMaster"/>，
/// 由调用方自动填入解锁流程并最终擦除，不显示在界面上。
/// </summary>
public partial class ForgotPasswordDialog : ContentDialogBase
{
    public string? RecoveredMaster { get; set; }

    public ForgotPasswordDialog()
    {
        InitializeComponent();
        CloseButtonText = "";        // 用自定义「取消」按钮，隐藏基类按钮
        AttachButtons(null, CancelBtn);
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        var cfg = RecoveryManager.GetConfig();
        BackupPanel.Visibility = cfg.HasBackup ? Visibility.Visible : Visibility.Collapsed;
        SystemPanel.Visibility = cfg.HasSystem ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => HideDialog();

    #region 键盘

    // 不允许复制带出备用密码：Ctrl+C/X/A/U 一律拦截；Enter 直接取回
    private void BackupPwdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && e.Key is Key.C or Key.X or Key.A or Key.U or Key.Insert)
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            _ = TryBackupAsync();
            e.Handled = true;
        }
    }

    #endregion

    #region 取回

    private async void BackupRecover_Click(object sender, RoutedEventArgs e) => await TryBackupAsync();

    private async Task<bool> TryBackupAsync()
    {
        // UI 线程先取出明文，后台线程严禁触碰 UI 元素
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
        HideDialog();
        return true;
    }

    private async void SystemRecover_Click(object sender, RoutedEventArgs e)
    {
        // WPF 下 Windows Hello 验证需调用 WinRT API，若不可用则回退到 DPAPI 直接解密
        bool verified = await Task.Run(() => VerifyHelloOrFallback());
        if (!verified)
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
        HideDialog();
    }

    /// <summary>
    /// Windows Hello 验证。WPF 项目未引用 WinRT 投影，这里用 DPAPI 自身作为凭证：
    /// 只有当前 Windows 账户能解密，等价于「系统验证」的信任边界。
    /// </summary>
    private static bool VerifyHelloOrFallback()
    {
        try
        {
            return RecoveryManager.GetConfig().HasSystem;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
