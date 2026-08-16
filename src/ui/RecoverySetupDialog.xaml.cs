using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Security.Credentials.UI;

namespace KeySecBox;

/// <summary>
/// 忘记密码处理方式配置：可勾选「备用密码」和/或「系统解锁（PIN/指纹/人脸）」。
/// 首次创建密码后自动弹出；也可从设置重新配置。
/// </summary>
public sealed partial class RecoverySetupDialog : ContentDialog
{
    private const string KeepMark = "·····"; // 占位符：保持已有备用密码，不重新加密主密码
    private NativeMethods.Store? _verifyStore;
    private string? _providedMaster;

    public RecoverySetupDialog()
    {
        InitializeComponent();
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
        MasterVerifyPanel.Visibility = masterPassword != null ? Visibility.Collapsed : Visibility.Visible;

        if (updateMode)
        {
            Title = "更新取回库";
            PrimaryButtonText = "更新";
            CloseButtonText = ""; // 只给更新按钮，不许跳过
            IntroText.Text = "需要更新取回库：原恢复记录仍对应修改前的主密码。请重新设置恢复方式，更新后忘记密码时才能取回新主密码。";
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

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e) => UpdatePanels();

    private void UpdatePanels()
    {
        BackupPanel.Visibility = BackupCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SystemCheck.IsEnabled = true;
    }

    private void OnMethodToggled(object sender, RoutedEventArgs e) => UpdatePanels();

    private static async Task<bool> VerifySystemAsync()
    {
        try
        {
            var avail = await UserConsentVerifier.CheckAvailabilityAsync();
            if (avail != UserConsentVerifierAvailability.Available) return false;
            var res = await UserConsentVerifier.RequestVerificationAsync(
                "KeySecBox 需要使用系统解锁验证将本程序纳入 Windows Hello 支持，以便忘记密码时取回保险库。");
            return res == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 保存

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await SaveAsync();
    }

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
        bool keepBackup = BackupCheck.IsChecked == true && BackupPwdBox.Password == KeepMark;
        string backup = BackupCheck.IsChecked == true ? (keepBackup ? "" : BackupPwdBox.Password) : "";
        if (BackupCheck.IsChecked == true && !keepBackup)
        {
            if (backup.Length < 1)
            {
                ShowError("启用备用密码时必须填写备用密码。");
                return;
            }
            if (backup == master)
            {
                ShowError("备用密码不能与主密码相同。");
                BackupPwdBox.Password = ""; BackupConfirmBox.Password = "";
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
        bool system = SystemCheck.IsChecked == true && await VerifySystemAsync();
        if (SystemCheck.IsChecked == true && !system)
        {
            ShowError("系统验证未完成或不可用，未启用该系统恢复方式。您可以稍后在设置中重试。");
            if (BackupCheck.IsChecked != true)
            {
                // 没有任何可用方式时直接返回，避免静默保存空记录
                ErrorText.Text = "未启用任何恢复方式：请至少勾选「备用密码」，或再次尝试系统验证。";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        int rc = RecoveryManager.Save(master, backup, system, keepBackup);
        master = ""; backup = "";
        BackupPwdBox.Password = ""; BackupConfirmBox.Password = "";
        if (rc != NativeMethods.KSBOX_OK)
        {
            ShowError("保存恢复方式失败。");
            return;
        }
        Hide();
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    #endregion
}
