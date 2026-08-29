using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class UnlockDialog : ContentDialog
{
    public string Password => PasswordBox.Password;
    public bool IsSetupMode { get; private set; }
    public bool ForgotHandled { get; set; }

    public UnlockDialog() { InitializeComponent(); IsPrimaryButtonEnabled = false; }

    public UnlockDialog(Window owner, bool setup) : this()
    {
        XamlRoot = owner.Content.XamlRoot;
        IsSetupMode = setup;
        if (setup)
        {
            Title = "设置主密码"; PrimaryButtonText = "创建保险库";
            HeadlineText.Text = "首次使用，请创建主密码";
            SubText.Text = "输入两次以确认。主密码将用于加密本地保险库，请务必牢记。";
            ConfirmBox.Visibility = Visibility.Visible;
        }
        else
        {
            Title = "解锁保险库"; PrimaryButtonText = "解锁";
            HeadlineText.Text = "欢迎回来";
            SubText.Text = "请输入主密码以解密本地保险库。";
            ConfirmBox.Visibility = Visibility.Collapsed;
        }
    }

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        DialogAnim.Play(this);
        _ = PasswordBox.Focus(FocusState.Programmatic);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = !string.IsNullOrEmpty(PasswordBox.Password)
            && (IsSetupMode ? !string.IsNullOrEmpty(ConfirmBox.Password) : true);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (IsSetupMode && PasswordBox.Password != ConfirmBox.Password)
        {
            args.Cancel = true;
            ErrorText.Text = "两次输入的主密码不一致。";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ForgotPwdLink_Click(object sender, RoutedEventArgs e)
    {
        ForgotHandled = true;
        Hide();
    }

    public void ClearSecrets()
    {
        PasswordBox.Password = "";
        ConfirmBox.Password = "";
    }
}