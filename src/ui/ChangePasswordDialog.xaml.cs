using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace KeySecBox;

public sealed partial class ChangePasswordDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    private bool _busy;
    public bool Succeeded { get; private set; }

    /// <summary>改密成功后新主密码（供上层重包恢复记录）。</summary>
    public string? NewMaster { get; private set; }

    #region 初始化

    public ChangePasswordDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
        PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; // 由异步流程决定关闭
            await RunChangeAsync();
        };
        Loaded += (_, _) => DialogAnim.Play(this);
        // 关闭即清空输入框，不留明文
        Closed += (_, _) =>
        {
            OldBox.Password = "";
            NewBox.Password = "";
            ConfirmBox.Password = "";
        };
    }

    internal void Init(NativeMethods.Store store)
    {
        _store = store;
        ForgotPwdLink.Visibility = RecoveryManager.GetConfig().Any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>预填旧密码框（忘记密码取回后引导直接修改时使用）。</summary>
    internal void SetOldPassword(string pwd)
    {
        OldBox.Password = pwd;
        OnChanged(this, new RoutedEventArgs());
    }

    #endregion

    #region 事件

    private async void ForgotPwdLink_Click(object sender, RoutedEventArgs e)
    {
        var fdlg = new ForgotPasswordDialog();
        fdlg.XamlRoot = XamlRoot;
        Hide(); // 子对话框与父 ContentDialog 不能并存
        await fdlg.ShowAsync();
        if (!string.IsNullOrEmpty(fdlg.RecoveredMaster))
        {
            OldBox.Password = fdlg.RecoveredMaster ?? ""; // 填入旧密码框，便于直接确认修改
            fdlg.RecoveredMaster = null;
        }
        await ShowAsync(); // 重新显示本对话框
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = OldBox.Password.Length >= 1
            && NewBox.Password.Length >= 1
            && ConfirmBox.Password.Length >= 1;
    }

    // 禁止把密码复制带出界面：禁止明文开关、Ctrl+C/X/A/U 与右键菜单；粘贴仍允许
    private void PasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!HasCtrlDown()) return;
        if (e.Key is VirtualKey.C or VirtualKey.X or VirtualKey.A or VirtualKey.U or VirtualKey.Insert)
            e.Handled = true;
    }

    private void PasswordContextRequested(object sender, ContextRequestedEventArgs e)
        => e.Handled = true;

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

    #region 改密流程

    public async Task<bool> ChangeAsync()
    {
        if (_store is not { } store) return false;
        _busy = true;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = "";
        Busy.IsActive = true;

        // 明文密码在 UI 线程先行取出，后台线程严禁触碰 UI 元素
        string oldPwd = OldBox.Password;
        string newPwd = NewBox.Password;

        var rc = await Task.Run(() =>
        {
            int v = store.VerifyPassword(oldPwd);
            if (v != NativeMethods.KSBOX_OK) return v;
            return store.ChangePassword(newPwd);
        });

        _busy = false;
        Busy.IsActive = false;
        CloseButtonText = "关闭";
        IsPrimaryButtonEnabled = true;

        if (rc == NativeMethods.KSBOX_OK)
        {
            Succeeded = true;
            NewMaster = newPwd;
            Hide();
            return true;
        }
        ErrorText.Text = rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
            ? "旧密码不正确，无法修改。"
            : $"修改密码失败（错误码 {rc}）。";
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    private bool Validate()
    {
        if (OldBox.Password.Length == 0) { ErrorText.Text = "请输入旧密码。"; return false; }
        if (NewBox.Password.Length == 0) { ErrorText.Text = "新密码不能为空。"; return false; }
        // 允许新密码与旧密码相同：取回主密码后用户可选择保持原主密码
        if (NewBox.Password != ConfirmBox.Password)
        {
            ErrorText.Text = "两次输入的密码不一致。";
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
        return true;
    }

    private async Task RunChangeAsync()
    {
        if (_busy) return;
        ErrorText.Visibility = Visibility.Collapsed;
        if (!Validate()) return;
        await ChangeAsync();
    }

    #endregion
}
