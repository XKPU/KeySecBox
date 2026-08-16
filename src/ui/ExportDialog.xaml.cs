using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class ExportDialog : ContentDialog
{
    public bool Verified { get; private set; }

    public ExportDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
    }

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        DialogAnim.Play(this);
        _ = PasswordBox.Focus(FocusState.Programmatic);
    }

    internal void Init(int count)
    {
        CountText.Text = count.ToString();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = !string.IsNullOrEmpty(PasswordBox.Password);
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 拦截默认关闭，由异步校验流程决定
        args.Cancel = true;
        IsPrimaryButtonEnabled = false;
        Busy.Visibility = Visibility.Visible;

        // 用临时 Store 打开同一保险库校验密码：正确则继续，错误则提示（不影响当前会话）
        using var probe = new NativeMethods.Store();
        var pwd = PasswordBox.Password;
        PasswordBox.Password = ""; // 用完立即擦除
        int rc = await System.Threading.Tasks.Task.Run(
            () => probe.Open(AppPaths.VaultBase, pwd));
        pwd = "";

        Busy.Visibility = Visibility.Collapsed;
        if (rc == NativeMethods.KSBOX_OK)
        {
            Verified = true;
            Hide();
            return;
        }

        ErrorText.Text = rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
            ? "密码错误，无法导出。"
            : $"校验失败（错误码 {rc}）。";
        ErrorText.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = true;
    }
}
