using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace KeySecBox;

/// <summary>选择导出方式并用保险库密码二次确认；加密导出可沿用保险库密码或自定义密码。</summary>
public partial class ExportMethodDialog : ContentDialogBase
{
    private ExportMethod _method = ExportMethod.Csv;
    private ExportRequest? _request;

    public ExportMethodDialog()
    {
        InitializeComponent();
        CloseButtonText = "";          // 用自定义「取消」按钮
        AttachButtons(OkBtn, CancelBtn);
        IsPrimaryButtonEnabled = false;

        PrimaryButtonClick += OnPrimaryButtonClick;

        Loaded += ContentDialog_Loaded;
    }

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        DialogAnim.Play(this);
        PasswordBox.Focus();
    }

    internal void Init(int entryCount)
    {
        bool hasEntries = entryCount > 0;
        CountText.Text = hasEntries
            ? $"保险库当前有 {entryCount} 条条目，请选择导出方式："
            : "保险库中没有条目，仅可导出完整数据目录。";

        // 无条目时禁用依赖条目数据的方式（CSV / 加密 CSV）
        foreach (var item in new[] { CsvRadio, EncCsvRadio, DirRadio, EncDirRadio })
        {
            if (item.Tag is string tag
                && Enum.TryParse<ExportMethod>(tag, out var m)
                && m is not (ExportMethod.DataDirectory or ExportMethod.EncryptedDataDirectory))
            {
                item.IsEnabled = hasEntries;
            }
        }

        // 2 = 完整数据目录（明文）
        (hasEntries ? CsvRadio : DirRadio).IsChecked = true;
    }

    /// <summary>取出本次导出请求（取走即置空，避免重复导出）。</summary>
    internal ExportRequest? TakeRequest()
    {
        var req = _request;
        _request = null;
        return req;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => HideDialog();

    /// <summary>
    /// 等价 WinUI 的 RadioButtons.SelectionChanged：仅在「刚被选中」的项上执行一次。
    /// </summary>
    private void OnMethodChecked(object sender, RoutedEventArgs e)
    {
        // 组内单选：Checked 会先于旧项 Unchecked 冒泡，只在 sender 为选中项时处理
        if (sender is not RadioButton { IsChecked: true } rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ExportMethod>(tag, out var m)) return;

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

    private async void OnPrimaryButtonClick(object? sender, DialogButtonClickEventArgs args)
    {
        // 拦截默认关闭，由异步校验流程决定（等价原 args.Cancel = true）
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

        if (rc != NativeMethods.KSBOX_OK)
        {
            IsPrimaryButtonEnabled = true;
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

        // 校验通过：关闭对话框并取「主按钮」结果（等价原 Hide()）。
        // 调用方主要通过 TakeRequest() 判定，这里返回 Primary 以保持语义一致。
        HideDialogWithPrimary();
    }

    /// <summary>Hide() 但同时把结果标记为 Primary，供调用方判定。</summary>
    private void HideDialogWithPrimary()
    {
        // DialogResult 仅在 ShowDialog 打开时才能赋值；非模态下退化为 Close()
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
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
