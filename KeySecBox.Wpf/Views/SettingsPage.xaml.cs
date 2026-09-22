using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KeySecBox;

namespace KeySecBox.Views;

/// <summary>设置页：改动即时生效并落盘。</summary>
public partial class SettingsPage : UserControl
{
    private NativeMethods.Store? _store;
    private Action<ThemeMode>? _applyTheme;
    private Window? _owner;
    private Action? _onDataChanged;

    // 初始化赋值期间为 true，用于屏蔽各控件 Changed 事件的副作用
    private bool _loading;

    private DispatcherTimer? _frameRateCommitTimer;

    public SettingsPage()
    {
        InitializeComponent();
    }

    internal void Init(NativeMethods.Store store, Action<ThemeMode> applyTheme,
                       Window owner, Action? onDataChanged = null)
    {
        _store = store;
        _applyTheme = applyTheme;
        _owner = owner;
        _onDataChanged = onDataChanged;

        // 初始化期间给控件赋值会触发各自的 Changed 事件，
        // 若不拦截就会「仅打开设置页」便产生一次设置落盘与诊断写入副作用。
        _loading = true;
        try
        {
            switch (AppSettings.Theme)
            {
                case ThemeMode.Light: ThemeLight.IsChecked = true; break;
                case ThemeMode.Dark: ThemeDark.IsChecked = true; break;
                default: ThemeSystem.IsChecked = true; break;
            }

            int maxRate = AppSettings.MonitorRefreshRate;
            FrameRateSlider.Minimum = 1;
            FrameRateSlider.Maximum = maxRate;
            FrameRateSlider.Value = Math.Min(AppSettings.FrameRate, maxRate);
            UpdateFrameRateHint();

            DiagToggle.IsChecked = store.GetDiagnostics();

            DialogCornerSlider.Value = AppSettings.DialogCornerRadius;
            UpdateDialogCornerHint();

            VersionText.Text = $"KeySecBox v{AppVersion}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateDialogCornerHint()
        => DialogCornerHint.Text = $"当前：{(int)Math.Round(DialogCornerSlider.Value)} px（0 为直角）";

    private void OnDialogCornerChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        UpdateDialogCornerHint();
        AppSettings.DialogCornerRadius = (int)Math.Round(DialogCornerSlider.Value); // 即时生效
    }

    private static string AppVersion
    {
        get
        {
            try
            {
                var loc = typeof(SettingsPage).Assembly.Location;
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(loc);
                if (!string.IsNullOrEmpty(info.FileVersion)) return info.FileVersion;
            }
            catch { }
            return "?";
        }
    }

    private void UpdateFrameRateHint()
    {
        int fps = (int)Math.Round(FrameRateSlider.Value);
        int maxRate = AppSettings.MonitorRefreshRate;
        FrameRateHint.Text = $"当前：{fps} fps（上限 {maxRate} Hz）";
    }

    private void OnFrameRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateFrameRateHint();
        if (_loading) return; // 初始化赋值不写回配置

        // 拖动过程中每一步都落盘会造成大量同步磁盘写入（UI 卡顿），
        // 这里做防抖：连续变更只在停止约 400ms 后写回一次。
        ScheduleFrameRateCommit();
    }

    private void ScheduleFrameRateCommit()
    {
        _frameRateCommitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _frameRateCommitTimer.Tick -= OnFrameRateCommitTick;
        _frameRateCommitTimer.Tick += OnFrameRateCommitTick;
        _frameRateCommitTimer.Stop();  // 重新计时，避免拖动中反复写盘
        _frameRateCommitTimer.Start();
    }

    private void OnFrameRateCommitTick(object? sender, EventArgs e) => CommitFrameRate();

    private void CommitFrameRate()
    {
        _frameRateCommitTimer?.Stop();
        AppSettings.FrameRate = (int)Math.Round(FrameRateSlider.Value);
    }

    #region 改密

    private void ChangePwdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;

        var dlg = new ChangePasswordDialog();
        dlg.Init(store);
        ThemeDialog(dlg);
        if (dlg.ShowDialogAsync(OwnerWindow()) != true || !dlg.Succeeded) return;

        // 改密成功后，用新密码重包恢复记录（不修改恢复方式本身）
        if (dlg.NewMaster is { } newMaster && RecoveryManager.GetConfig().Any)
            RepackRecovery(newMaster);

        ShowMessage("密码已修改，所有条目已用新密码重新加密。");
    }

    #endregion

    #region 恢复方式

    private void RecoveryCfgBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;
        var dlg = new RecoverySetupDialog();
        dlg.Init(null, store); // 传入 store，对话框内验证当前主密码
        ThemeDialog(dlg);
        dlg.ShowDialogAsync(OwnerWindow());
    }

    // 忘记密码：立即恢复，取回的主密码填入改密对话框旧密码框，引导改一个新密码
    private void ForgotPwdBtn_Click(object sender, RoutedEventArgs e)
    {
        var fdlg = new ForgotPasswordDialog();
        ThemeDialog(fdlg);
        fdlg.ShowDialogAsync(OwnerWindow());
        if (string.IsNullOrEmpty(fdlg.RecoveredMaster))
        {
            ShowMessage("未能取回主密码。");
            return;
        }

        string recovered = fdlg.RecoveredMaster;
        fdlg.RecoveredMaster = null;

        var cdlg = new ChangePasswordDialog();
        cdlg.Init(_store!);
        cdlg.SetOldPassword(recovered);
        ThemeDialog(cdlg);
        if (cdlg.ShowDialogAsync(OwnerWindow()) == true
            && cdlg.Succeeded && cdlg.NewMaster is { } nm
            && RecoveryManager.GetConfig().Any)
        {
            RepackRecovery(nm);
        }
    }

    private void RepackRecovery(string newMaster)
    {
        try
        {
            // 改密后取回库必须同步更新（旧记录对应旧密码），仅「更新」可完成
            var dlg = new RecoverySetupDialog();
            dlg.Init(newMaster, null, updateMode: true);
            ThemeDialog(dlg);
            dlg.ShowDialogAsync(OwnerWindow());
        }
        catch (Exception ex)
        {
            Trace($"repack recovery EX: {ex.Message}");
        }
    }

    #endregion

    #region 保存

    /// <summary>即时保存：主题、动画帧率、诊断开关。由各控件事件直接触发。</summary>
    private async Task SaveSettingsAsync(bool showStatus = true)
    {
        var theme = ThemeLight.IsChecked == true ? ThemeMode.Light
                  : ThemeDark.IsChecked == true ? ThemeMode.Dark
                  : ThemeMode.System;
        AppSettings.Theme = theme;
        _applyTheme?.Invoke(theme);

        CommitFrameRate(); // 取消防抖计时器，避免随后的重复写盘

        if (_store is { } store)
        {
            bool diag = DiagToggle.IsChecked == true; // UI 线程取值，后台线程严禁触碰 UI 元素
            int rc = await Task.Run(() => store.SetDiagnostics(diag));
            if (rc != NativeMethods.KSBOX_OK)
            {
                SetStatus($"保存诊断设置失败（错误码 {rc}）。", isError: true);
                return;
            }
            AppPaths.TraceEnabled = store.GetDiagnostics(); // 同步运行期追踪开关
        }

        if (showStatus) SetStatus("设置已保存。", isError: false);
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return; // 初始化赋值不触发保存
        _ = SaveSettingsAsync(showStatus: false);
    }

    private void OnDiagToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return; // 初始化赋值不触发保存
        _ = SaveSettingsAsync();
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "ErrorBrush" : "AccentBrush");
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    #endregion

    #region 辅助

    internal static void Trace(string msg)
    {
        if (!AppPaths.TraceEnabled) return; // 仅诊断模式下记录
        try { File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    private Window OwnerWindow() =>
        _owner ?? Window.GetWindow(this) ?? Application.Current.MainWindow;

    private void ThemeDialog(Window dlg) => dlg.FontFamily = FontFamily;

    private void ShowMessage(string text)
    {
        var dlg = new MessageDialog(text);
        ThemeDialog(dlg);
        dlg.ShowDialogAsync(OwnerWindow());
    }

    #endregion
}
