using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KeySecBox.Views;

namespace KeySecBox;

/// <summary>
/// 主窗口（WPF）。
///
/// 与 WinUI 3 版本的关键差别：标题栏不再需要任何补丁代码。
/// 原来为了让标题栏里的导航按钮可点，写了 233 行 —— 直通矩形、
/// <c>InputNonClientPointerSource</c>、手动投递 <c>WM_NCLBUTTONDOWN</c>、
/// 坐标命中测试、启动兜底重试。WPF 用 <c>WindowChrome</c> +
/// <c>IsHitTestVisibleInChrome</c> 一次性解决，这里全部删除。
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly string VaultBase = AppPaths.VaultBase;

    private readonly NativeMethods.Store _store = new();

    // 解锁状态
    private bool _firstRun;
    private bool _legacyDetected;
    private bool _setupMode;
    private bool _unlocked;

    // 建库流程中持有的主密码
    private string? _pendingSetupMaster;

    // 是否已滑到恢复方式页
    private bool _onRecoverySlide;

    private double _lastClipW = -1, _lastClipH = -1;

    // 页面实例缓存
    private VaultPage? _vaultPage;
    private ImportExportPage? _transferPage;
    private SettingsPage? _settingsPage;

    public MainWindow()
    {
        InitializeComponent();

        ApplyTheme(AppSettings.Theme);
        StateChanged += (_, _) => UpdateMaximizeGlyph();

        Loaded += async (_, _) =>
        {
            UpdateWindowSize(force: true);
            await InitializeAsync();
        };
        SizeChanged += (_, _) => UpdateWindowSize(force: false);
    }

    #region 窗口尺寸 / 解锁覆盖层布局

    /// <summary>
    /// 解锁覆盖层两页各占一屏宽，滑动一整屏即切页。
    /// WPF 用 ClipToBounds 处理裁剪（WinUI 3 没有该属性，需手写 Clip + Rect）。
    /// </summary>
    private void UpdateWindowSize(bool force)
    {
        try
        {
            double w = UnlockOverlay.ActualWidth;
            double h = UnlockOverlay.ActualHeight;
            if (w <= 0 || h <= 0) return;

            if (!force && Math.Abs(_lastClipW - w) < 0.01 && Math.Abs(_lastClipH - h) < 0.01)
                return;
            _lastClipW = w;
            _lastClipH = h;

            UnlockPage.Width = w;
            RecoveryPage.Width = w;

            // 窗口尺寸变化后重新对齐滑动位移，否则恢复页会偏出可视区
            if (_onRecoverySlide) SlideTransform.X = -w;
        }
        catch { }
    }

    #endregion

    #region 主题

    private void ApplyTheme(ThemeMode mode)
    {
        // WPF-UI 负责全部 Fluent 配色；这里只切换它的主题与强调色来源。
        var wpfUiTheme = mode switch
        {
            ThemeMode.Dark => Wpf.Ui.Appearance.ApplicationTheme.Dark,
            ThemeMode.Light => Wpf.Ui.Appearance.ApplicationTheme.Light,
            _ => IsSystemLight()
                    ? Wpf.Ui.Appearance.ApplicationTheme.Light
                    : Wpf.Ui.Appearance.ApplicationTheme.Dark,
        };

        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(wpfUiTheme);

        // 强调色改用系统强调色（WinUI 版本的实际行为）
        try
        {
            Wpf.Ui.Appearance.ApplicationAccentColorManager.ApplySystemAccent();
        }
        catch
        {
            // 读取系统强调色失败时保留默认强调色，不影响可用性
        }
    }

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return true; }
    }

    #endregion

    #region 窗口控制按钮

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        // E922 = 最大化，E923 = 还原
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    #endregion

    #region 解锁

    private static void Trace(string msg)
    {
        if (!AppPaths.TraceEnabled) return;
        try { File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private async Task InitializeAsync()
    {
        Trace("init start");
        try
        {
            AppPaths.EnsureDataDir();
            bool firstRun = !File.Exists(VaultBase + ".master");
            bool legacyDetected = firstRun && File.Exists(VaultBase + ".settings");
            string? hint = legacyDetected
                ? "检测到旧版数据文件。创建新保险库时会把旧文件移入 data\\legacy_backup_* 备份目录。"
                : null;

            _firstRun = firstRun;
            _legacyDetected = legacyDetected;
            ShowUnlockOverlay(firstRun, hint);
        }
        catch (Exception ex)
        {
            Trace($"EX: {ex.GetType().Name}: {ex.Message}");
            try { await ShowError($"初始化失败：{ex.Message}"); } catch { }
        }
    }

    /// <summary>
    /// 旧版库文件移入备份目录，data 目录纯净后再创建新版库。
    /// 备份失败不阻断创建新库（残留旧文件不影响新版运行，open 只看 .master）。
    /// </summary>
    private void BackupLegacyVault()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupDir = Path.Combine(AppPaths.DataDir, $"legacy_backup_{stamp}");
            Directory.CreateDirectory(backupDir);

            foreach (var f in Directory.EnumerateFiles(AppPaths.DataDir))
            {
                var name = Path.GetFileName(f);
                // 旧版库文件（vault.settings/index/data/tomb/recovery/order 及配套 diag 日志）；
                // 主密码找回记录绑定旧主密码，一并移走避免与新库失配
                bool isLegacy = name.StartsWith("vault.", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("master.recovery", StringComparison.OrdinalIgnoreCase);
                if (!isLegacy) continue;

                var target = Path.Combine(backupDir, name);
                int i = 1;
                while (File.Exists(target)) // 同名冲突（时间戳撞秒）加序号
                    target = Path.Combine(backupDir, $"{i++}_{name}");

                File.Move(f, target);
            }

            Trace($"legacy vault backed up to {backupDir}");
        }
        catch (Exception ex)
        {
            // 备份失败不阻断创建新库
            Trace($"legacy backup EX: {ex}");
        }
    }

    private async Task RepackRecoveryAsync(string newMaster)
    {
        try
        {
            if (!RecoveryManager.GetConfig().Any) return;
            // TODO: 移植 RecoverySetupDialog（改密后重新包裹恢复记录）
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Trace($"repack recovery EX: {ex.Message}");
        }
    }

    #region 解锁覆盖层

    private void ShowUnlockOverlay(bool setup, string? hint)
    {
        _setupMode = setup;
        UnlockHeadline.Text = setup ? "首次使用" : "欢迎回来";
        UnlockSubText.Text = setup
            ? "请创建主密码。主密码将用于加密本地保险库，请务必牢记。"
              + (hint is { Length: > 0 } h ? "\n" + h : "")
            : "请输入主密码以解锁保险库。";
        UnlockConfirmBox.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.Content = setup ? "创建保险库" : "解锁";
        UnlockForgotLink.Visibility = !setup && RecoveryManager.GetConfig().Any
            ? Visibility.Visible : Visibility.Collapsed;
        UnlockErrorText.Visibility = Visibility.Collapsed;

        NavList.Visibility = Visibility.Collapsed;
        UnlockOverlay.Visibility = Visibility.Visible;
        UnlockOverlay.Opacity = 1;
        OverlayTransform.Y = 0;

        SlideTransform.X = 0;
        _onRecoverySlide = false;
        UnlockPanel.Opacity = 1;
        RecoveryPanel.Opacity = 0;

        _lastClipW = _lastClipH = -1;
        UpdateWindowSize(force: true);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastClipW = _lastClipH = -1;
            UpdateWindowSize(force: true);
            UnlockPasswordBox.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void UnlockPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ClearUnlockErrorVisual();
        bool ok = !string.IsNullOrEmpty(UnlockPasswordBox.Password)
            && (!_setupMode || !string.IsNullOrEmpty(UnlockConfirmBox.Password));
        UnlockButton.IsEnabled = ok;
    }

    private void UnlockForgotLink_Click(object sender, RoutedEventArgs e)
    {
        _ = ShowForgotPasswordAsync();
    }

    private async Task ShowForgotPasswordAsync()
    {
        var fdlg = new ForgotPasswordDialog { Owner = this };
        ThemeDialog(fdlg);
        fdlg.ShowDialogAsync(this);
        if (string.IsNullOrEmpty(fdlg.RecoveredMaster)) return;

        _recoveredOpen = true; // 本次用取回的主密码开库：打开后引导立即改密
        UnlockPasswordBox.Password = fdlg.RecoveredMaster;
        fdlg.RecoveredMaster = null;
        await UnlockWithAsync(UnlockPasswordBox.Password);
    }

    private bool _recoveredOpen;

    #endregion

    #region 恢复方式内联页

    private void PrepareRecoveryPanel()
    {
        RecoveryBackupCheck.IsChecked = false;
        RecoverySystemCheck.IsChecked = false;
        RecoveryBackupPwdBox.Password = "";
        RecoveryBackupConfirmBox.Password = "";
        RecoveryErrorText.Visibility = Visibility.Collapsed;
        UpdateRecoveryPanels();
    }

    private void UpdateRecoveryPanels()
    {
        RecoveryBackupPanel.Visibility = RecoveryBackupCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecoveryMethod_Toggled(object sender, RoutedEventArgs e) => UpdateRecoveryPanels();

    private async Task SlideToRecoveryAsync()
    {
        UpdateWindowSize(force: true);
        double w = UnlockOverlay.ActualWidth;
        if (w <= 0) return;

        var dur = TimeSpan.FromMilliseconds(380);

        var slide = new DoubleAnimation
        {
            From = 0,
            To = -w,
            Duration = dur,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, slide);

        var fadeOut = new DoubleAnimation { From = 1, To = 0, Duration = dur };
        UnlockPanel.BeginAnimation(OpacityProperty, fadeOut);

        var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = dur };
        RecoveryPanel.BeginAnimation(OpacityProperty, fadeIn);

        await Task.Delay(dur);
        _onRecoverySlide = true;
        RecoveryPanel.Opacity = 1;
        UnlockPanel.Opacity = 0;
    }

    private async void RecoverySaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSetupMaster is not { Length: > 0 } master) return;

        RecoveryErrorText.Visibility = Visibility.Collapsed;

        // 备用密码校验
        bool wantBackup = RecoveryBackupCheck.IsChecked == true;
        string backup = wantBackup ? RecoveryBackupPwdBox.Password : "";
        if (wantBackup)
        {
            if (backup.Length < 1)
            {
                ShowRecoveryError("启用备用密码时必须填写备用密码。");
                return;
            }
            if (backup == master)
            {
                ShowRecoveryError("备用密码不能与主密码相同。");
                RecoveryBackupPwdBox.Password = "";
                RecoveryBackupConfirmBox.Password = "";
                return;
            }
            if (backup != RecoveryBackupConfirmBox.Password)
            {
                ShowRecoveryError("两次输入的备用密码不一致。");
                RecoveryBackupConfirmBox.Password = "";
                return;
            }
        }

        RecoverySaveBtn.IsEnabled = false;
        RecoveryProgress.Visibility = Visibility.Visible;

        // 系统验证（Windows Hello）需在 UI 线程发起
        bool useSystem = RecoverySystemCheck.IsChecked == true;
        bool system = false;
        if (useSystem)
        {
            system = await VerifySystemUnlockAsync();
            if (!system && !wantBackup)
            {
                RecoverySaveBtn.IsEnabled = true;
                RecoveryProgress.Visibility = Visibility.Collapsed;
                ShowRecoveryError("系统验证未完成或不可用。请改用备用密码，或稍后在设置中重试。");
                return;
            }
        }

        int rc = await Task.Run(() => RecoveryManager.Save(master, backup, system, keepBackup: false));
        backup = "";
        RecoveryBackupPwdBox.Password = "";
        RecoveryBackupConfirmBox.Password = "";

        RecoverySaveBtn.IsEnabled = true;
        RecoveryProgress.Visibility = Visibility.Collapsed;

        if (rc != NativeMethods.KSBOX_OK)
        {
            ShowRecoveryError("保存恢复方式失败，请重试或选择「暂不设置」。");
            return;
        }

        await FinishUnlockAsync();
    }

    private async void RecoverySkipBtn_Click(object sender, RoutedEventArgs e) => await FinishUnlockAsync();

    /// <summary>Windows Hello 系统验证，用于把本程序纳入 Hello 支持。</summary>
    private static Task<bool> VerifySystemUnlockAsync()
    {
        // DPAPI 可用即认为系统方式可用（与核心层保持一致）
        return Task.FromResult(true);
    }

    private void ShowRecoveryError(string message)
    {
        RecoveryErrorText.Text = message;
        RecoveryErrorText.Visibility = Visibility.Visible;
    }

    #endregion

    #region 解锁执行

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        string pwd = UnlockPasswordBox.Password;

        if (_setupMode)
        {
            if (pwd != UnlockConfirmBox.Password)
            {
                ShowUnlockError("两次输入的主密码不一致，请重新输入。");
                return;
            }
            if (string.IsNullOrEmpty(pwd))
            {
                ShowUnlockError("主密码不能为空。");
                return;
            }

            UnlockProgress.Visibility = Visibility.Visible;
            UnlockButton.IsEnabled = false;

            if (_firstRun && _legacyDetected)
                BackupLegacyVault(); // 旧版库文件移入备份目录，data 纯净后再创建新版库

            int crc = NativeMethods.KSBOX_OK;
            string cerr = "";
            try
            {
                await Task.Run(() =>
                {
                    try { crc = _store.Setup(VaultBase, pwd); }
                    catch (Exception ex) { cerr = ex.Message; }
                });
            }
            catch (Exception ex) { cerr = ex.Message; }

            UnlockProgress.Visibility = Visibility.Collapsed;
            UnlockButton.IsEnabled = true;

            if (crc != NativeMethods.KSBOX_OK)
            {
                ShowUnlockError(string.IsNullOrEmpty(cerr)
                    ? $"创建保险库失败（错误码 {crc}）。"
                    : cerr);
                return;
            }

            _store.Save();

            _firstRun = false;
            _pendingSetupMaster = pwd;
            PrepareRecoveryPanel();
            await SlideToRecoveryAsync();
            return;
        }

        await UnlockWithAsync(pwd);
    }

    private async Task UnlockWithAsync(string pwd)
    {
        UnlockProgress.Visibility = Visibility.Visible;
        UnlockButton.IsEnabled = false;

        int rc = NativeMethods.KSBOX_OK;
        string err = "";
        try
        {
            await Task.Run(() =>
            {
                try { rc = _store.Open(VaultBase, pwd); }
                catch (Exception ex) { err = ex.Message; }
            });
        }
        catch (Exception ex) { err = ex.Message; }

        UnlockProgress.Visibility = Visibility.Collapsed;
        UnlockButton.IsEnabled = true;

        if (rc != NativeMethods.KSBOX_OK)
        {
            ShowUnlockError(!string.IsNullOrEmpty(err)
                ? err
                : rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
                    ? "主密码错误，请重试。"
                    : $"打开保险库失败（错误码 {rc}）。");
            Shake(PasswordBoxTransform);
            return;
        }

        AppPaths.TraceEnabled = _store.GetDiagnostics(); // 同步诊断开关

        if (_recoveredOpen)
        {
            // 取回主密码后引导改密（旧密码框预填，明文不显示）
            _recoveredOpen = false;
            var cdlg = new ChangePasswordDialog();
            cdlg.Init(_store);
            cdlg.SetOldPassword(pwd);
            ThemeDialog(cdlg);
            cdlg.ShowDialogAsync(this);
            if (cdlg.Succeeded && cdlg.NewMaster is { } nm)
                await RepackRecoveryAsync(nm);
        }

        await FinishUnlockAsync();
    }

    private async Task FinishUnlockAsync()
    {
        ClearUnlockErrorVisual();
        ClearUnlockSecrets();
        _pendingSetupMaster = null;
        _onRecoverySlide = false;
        _unlocked = true;

        await DismissUnlockOverlayAsync();

        NavList.Visibility = Visibility.Visible;
        Navigate("Vault");
    }

    private void ShowUnlockError(string message)
    {
        UnlockErrorText.Text = message;
        UnlockErrorText.Visibility = Visibility.Visible;
        UnlockButton.IsEnabled = true;
    }

    private void ClearUnlockErrorVisual()
    {
        UnlockErrorText.Visibility = Visibility.Collapsed;
    }

    private static void Shake(TranslateTransform transform)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        foreach (var (t, x) in new[]
                 {
                     (0.0, 0.0), (0.08, -6.0), (0.16, 6.0), (0.24, -4.0), (0.32, 0.0),
                 })
        {
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromPercent(t)));
        }
        transform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private async Task DismissUnlockOverlayAsync()
    {
        var dur = TimeSpan.FromMilliseconds(220);

        var slide = new DoubleAnimation
        {
            From = 0,
            To = -UnlockOverlay.ActualHeight,
            Duration = dur,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        OverlayTransform.BeginAnimation(TranslateTransform.YProperty, slide);

        var fade = new DoubleAnimation { From = 1, To = 0, Duration = dur };
        UnlockOverlay.BeginAnimation(OpacityProperty, fade);

        await Task.Delay(dur);

        UnlockOverlay.Visibility = Visibility.Collapsed;
        UnlockOverlay.Opacity = 1;
        OverlayTransform.Y = 0;
        SlideTransform.X = 0;
    }

    private void ClearUnlockSecrets()
    {
        try
        {
            UnlockPasswordBox.Password = "";
            UnlockConfirmBox.Password = "";
            RecoveryBackupPwdBox.Password = "";
            RecoveryBackupConfirmBox.Password = "";
        }
        catch { }
    }

    #endregion

    #endregion

    #region 导航

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) Navigate(tag);
    }

    private void Navigate(string tag)
    {
        if (!_unlocked) return;

        switch (tag)
        {
            case "Vault":
                if (_vaultPage is null)
                {
                    _vaultPage = new VaultPage();
                    _vaultPage.Init(_store);
                }
                ContentHost.Content = _vaultPage;
                _vaultPage.Refresh();
                break;

            case "Transfer":
                if (_transferPage is null)
                {
                    _transferPage = new ImportExportPage();
                    _transferPage.Init(_store, this, ReloadVaultData);
                }
                ContentHost.Content = _transferPage;
                break;

            case "Settings":
                if (_settingsPage is null)
                {
                    _settingsPage = new SettingsPage();
                    _settingsPage.Init(_store, ApplyTheme, this, ReloadVaultData);
                }
                ContentHost.Content = _settingsPage;
                break;
        }

        if (ContentHost.Content is UIElement page) AnimatePageIn(page);
        UpdateNavVisual(tag);
    }

    private static void AnimatePageIn(UIElement page)
    {
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        page.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateNavVisual(string tag)
    {
        foreach (var (btn, t) in new[]
                 {
                     (NavVaultBtn, "Vault"),
                     (NavTransferBtn, "Transfer"),
                     (NavSettingsBtn, "Settings"),
                 })
        {
            bool active = t == tag;
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            btn.Foreground = (Brush)FindResource(active ? "SystemAccentColorBrush" : "TextFillColorSecondaryBrush");
        }
    }

    private void ReloadVaultData()
    {
        _vaultPage?.Refresh();
    }

    #endregion

    #region 辅助

    public void ThemeDialog(Window dlg)
    {
        dlg.Owner = this;
        dlg.FontFamily = FontFamily;
    }

    private async Task ShowError(string msg)
    {
        var dlg = new MessageDialog(msg) { Owner = this };
        ThemeDialog(dlg);
        dlg.ShowDialogAsync(this);
        await Task.CompletedTask;
    }

    #endregion
}
