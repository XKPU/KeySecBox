using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class ForgotPasswordDialog : ContentDialog
{
    private readonly IMasterRecoveryService _recovery;
    public string? RecoveredPassword { get; private set; }

    public ForgotPasswordDialog() { InitializeComponent(); _recovery = null!; }

    public ForgotPasswordDialog(Window owner, IMasterRecoveryService recovery) : this()
    {
        XamlRoot = owner.Content.XamlRoot;
        _recovery = recovery;
    }

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e) => DialogAnim.Play(this);

    private void SystemUnlockBtn_Click(object sender, RoutedEventArgs e)
    {
        var pwd = _recovery.RecoverBySystem();
        if (pwd != null)
        {
            RecoveredPassword = pwd;
            ResultText.Text = "恢复成功！";
            Hide();
        }
        else
        {
            ResultText.Text = "系统解锁失败。请尝试备用密码。";
        }
    }

    private void RecoverBtn_Click(object sender, RoutedEventArgs e)
    {
        var pwd = _recovery.RecoverByBackup(BackupPasswordBox.Password);
        if (pwd != null)
        {
            RecoveredPassword = pwd;
            ResultText.Text = "恢复成功！";
            Hide();
        }
        else
        {
            ResultText.Text = "备用密码错误。";
        }
    }
}