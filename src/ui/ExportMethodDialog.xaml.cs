using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

/// <summary>选择导出方式并用保险库密码二次确认；加密导出可沿用保险库密码或自定义密码。</summary>
public sealed partial class ExportMethodDialog : ContentDialog
{
    private ExportMethod _method = ExportMethod.Csv;
    private ExportRequest? _request;

    public ExportMethodDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
    }

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        DialogAnim.Play(this);
        _ = PasswordBox.Focus(FocusState.Programmatic);
    }

    internal void Init(int entryCount)
    {
        bool hasEntries = entryCount > 0;
        CountText.Text = hasEntries
            ? $"保险库当前有 {entryCount} 条条目，请选择导出方式："
            : "保险库中没有条目，仅可导出完整数据目录。";

        // 无条目时禁用依赖条目数据的方式（CSV / 加密 CSV）
        foreach (var item in MethodList.Items)
        {
            if (item is RadioButton rb
                && rb.Tag is string tag
                && Enum.TryParse<ExportMethod>(tag, out var m)
                && m is not (ExportMethod.DataDirectory or ExportMethod.EncryptedDataDirectory))
            {
                rb.IsEnabled = hasEntries;
            }
        }

        MethodList.SelectedIndex = hasEntries ? 0 : 2; // 2 = 完整数据目录（明文）
    }

    /// <summary>取出本次导出请求（取走即置空，避免重复导出）。</summary>
    internal ExportRequest? TakeRequest()
    {
        var req = _request;
        _request = null;
        return req;
    }

    private void OnMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MethodList.SelectedItem is RadioButton { Tag: string tag } && Enum.TryParse<ExportMethod>(tag, out var m))
            _method = m;

        bool encrypted = _method is ExportMethod.EncryptedCsv
            or ExportMethod.EncryptedDataDirectory;

        EncryptPanel.Visibility = encrypted ? Visibility.Visible : Visibility.Collapsed;
        PlainWarn.Visibility = encrypted ? Visibility.Collapsed : Visibility.Visible;
        if (!encrypted) ClearCustomPassword();
    }

    private void OnUseCustomPwdChanged(object sender, RoutedEventArgs e)
    {
        bool custom = UseCustomPwd.IsChecked == true;
        CustomPwd.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        CustomPwd2.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (!custom) ClearCustomPassword();
        ErrorText.Visibility = Visibility.Collapsed;
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

        string vaultPassword = PasswordBox.Password;
        if (string.IsNullOrEmpty(vaultPassword)) { ShowError("请输入保险库密码。"); return; }

        bool encrypted = _method is ExportMethod.EncryptedCsv
            or ExportMethod.EncryptedDataDirectory;

        string? encryptPassword = null;
        if (encrypted && UseCustomPwd.IsChecked == true)
        {
            if (string.IsNullOrEmpty(CustomPwd.Password)) { ShowError("请输入自定义加密密码。"); return; }
            if (CustomPwd.Password != CustomPwd2.Password) { ShowError("两次输入的加密密码不一致。"); return; }
            encryptPassword = CustomPwd.Password;
        }

        IsPrimaryButtonEnabled = false;
        Busy.Visibility = Visibility.Visible;

        // 用临时 Store 打开同一保险库校验密码：正确则继续，错误则提示（不影响当前会话）
        int rc = await Task.Run(() =>
        {
            using var probe = new NativeMethods.Store();
            return probe.Open(AppPaths.VaultBase, vaultPassword);
        });

        Busy.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = true;

        if (rc != NativeMethods.KSBOX_OK)
        {
            ShowError(rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
                ? "密码错误，无法导出。"
                : $"校验失败（错误码 {rc}）。");
            return;
        }

        // 加密导出：自定义密码优先，否则默认沿用保险库密码；明文导出不持有密码
        _request = new ExportRequest(_method, encrypted ? encryptPassword ?? vaultPassword : null);

        // 明文立即出栈
        PasswordBox.Password = "";
        ClearCustomPassword();
        vaultPassword = "";

        Hide();
    }

    private void ClearCustomPassword()
    {
        CustomPwd.Password = "";
        CustomPwd2.Password = "";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
