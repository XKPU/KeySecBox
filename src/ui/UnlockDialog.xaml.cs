using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class UnlockDialog : ContentDialog
{
    public string Password => PasswordBox.Password;
    public bool IsSetupMode { get; private set; }

    public UnlockDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
    }

    public UnlockDialog(Window owner, bool setup) : this()
    {
        XamlRoot = owner.Content.XamlRoot;
        IsSetupMode = setup;
        if (setup) Title = "🔐 设置解锁密码";
    }

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        // 统一淡入动画（与其他对话框保持一致）
        DialogAnim.Play(this);
        _ = PasswordBox.Focus(FocusState.Programmatic);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = !string.IsNullOrEmpty(PasswordBox.Password);
    }

    public void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
        PasswordBox.Password = "";
        IsPrimaryButtonEnabled = false;
    }
}
