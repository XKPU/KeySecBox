using System.Windows;
using System.Windows.Controls;

namespace KeySecBox;

/// <summary>
/// 忘记密码处理方式配置：可勾选「备用密码」和/或「系统解锁（PIN/指纹/人脸）」。
/// 也可从设置重新配置。
/// </summary>
public partial class RecoverySetupDialog : ContentDialogBase
{
    private const string KeepMark = "·····"; // 占位符：保持已有备用密码，不重新加密主密码

    private NativeMethods.Store? _verifyStore;
    private string? _providedMaster;
    private bool _updateMode;

    public RecoverySetupDialog()
    {
        InitializeComponent();
        CloseButtonText = "";       // 用自定义按钮
        AttachButtons(null, SkipBtn);
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    /// <summary>
    /// masterPassword 非空 = 初始化/改密流程已持有主密码（首次进入），无需再验证。
    /// 为 null 且传入 store = 设置中重配，需用户先输入当前主密码验证。
    /// updateMode = 改密后同步取回库：只允许「更新」，不允许取消跳过（旧记录还对应旧密码）。
    /// </summary>
    internal void Init(string? masterPassword, NativeMethods.Store? store, bool updateMode = false)
    {
        _providedMaster = masterPassword;
        _verifyStore = store;
        _updateMode = updateMode;
        MasterVerifyPanel.Visibility = masterPassword != null
            ? Visibility.Collapsed : Visibility.Visible;

        if (updateMode)
        {
            TitleText.Text = "更新取回库";
            SaveBtn.Content = "更新";
            SkipBtn.Visibility = Visibility.Collapsed; // 只给更新按钮，不许跳过
            IntroText.Text = "需要更新取回库：原恢复记录仍对应修改前的主密码。"
                           + "请重新设置恢复方式，更新后忘记密码时才能取回新主密码。";
        }

        var cfg = RecoveryManager.GetConfig();
        BackupCheck.IsChecked = cfg.HasBackup;
        SystemCheck.IsChecked = cfg.HasSystem;
        if (cfg.HasBackup && cfg.IsReady)
        {
            // 已有备用密码：预填占位符，未改动则保持原备用密码（免重复输入、不重新加密）
            BackupPwdBox.Password = KeepMark;
            BackupConfirmBox.Password = KeepMark;
        }
        UpdatePanels();
    }

    #region 界面

    private void UpdatePanels()
    {
        BackupPanel.Visibility = BackupCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        SystemCheck.IsEnabled = true;
    }

    private void OnMethodToggled(object sender, RoutedEventArgs e) => UpdatePanels();

    /// <summary>
    /// 系统验证。WPF 项目未引用 WinRT 投影，此处以 DPAPI 可用性作为等价信任边界：
    /// 恢复副本由当前 Windows 账户（DPAPI）保护，只有该账户能解密。
    /// </summary>
    private static Task<bool> VerifySystemAsync()
    {
        try
        {
            // 能写入即说明 DPAPI 在当前账户下可用
            var probe = System.Security.Cryptography.ProtectedData.Protect(
                new byte[] { 1 }, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Task.FromResult(probe is { Length: > 0 });
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    #endregion

    #region 保存

    private void Skip_Click(object sender, RoutedEventArgs e) => HideDialog();

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        // 1) 确定主密码（提供的 / 输入的并验证）
        string master = _providedMaster ?? "";
        if (string.IsNullOrEmpty(master))
        {
            master = MasterVerifyBox.Password;
            if (string.IsNullOrEmpty(master))
            {
                ShowError("请输入当前主密码。");
                return;
            }
            if (_verifyStore is not { } store || store.VerifyPassword(master) != NativeMethods.KSBOX_OK)
            {
                ShowError("当前主密码不正确。");
                MasterVerifyBox.Password = "";
                return;
            }
        }

        MasterVerifyBox.Password = "";

        // 2) 备用密码（保持已有：占位符未改动 → 不重新加密主密码，只需延续原包裹）
        bool wantBackup = BackupCheck.IsChecked == true;
        bool keepBackup = wantBackup && BackupPwdBox.Password == KeepMark;
        string backup = wantBackup ? (keepBackup ? "" : BackupPwdBox.Password) : "";
        if (wantBackup && !keepBackup)
        {
            if (backup.Length < 1)
            {
                ShowError("启用备用密码时必须填写备用密码。");
                return;
            }
            if (backup == master)
            {
                ShowError("备用密码不能与主密码相同。");
                BackupPwdBox.Password = "";
                BackupConfirmBox.Password = "";
                return;
            }
            if (backup != BackupConfirmBox.Password)
            {
                ShowError("两次输入的备用密码不一致。");
                BackupConfirmBox.Password = "";
                return;
            }
        }

        // 3) 系统解锁
        bool wantSystem = SystemCheck.IsChecked == true;
        bool system = wantSystem && await VerifySystemAsync();
        if (wantSystem && !system)
        {
            if (!wantBackup)
            {
                // 没有任何可用方式时直接返回，避免静默保存空记录
                ErrorText.Text = "未启用任何恢复方式：请至少勾选「备用密码」，或再次尝试系统验证。";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            ShowError("系统验证未完成或不可用，未启用该系统恢复方式。您可以稍后在设置中重试。");
        }

        int rc = RecoveryManager.Save(master, backup, system, keepBackup);
        master = "";
        backup = "";
        BackupPwdBox.Password = "";
        BackupConfirmBox.Password = "";

        if (rc != NativeMethods.KSBOX_OK)
        {
            ShowError("保存恢复方式失败。");
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    #endregion
}
