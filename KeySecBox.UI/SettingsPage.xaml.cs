using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

// 设置页：改动即时生效并落盘
public sealed partial class SettingsPage : Page
{
    private NativeMethods.Store? _store;
    private Action<ThemeMode>? _applyTheme;
    private IntPtr _ownerHwnd;
    private Action? _onDataChanged;

    #region 初始化

    public SettingsPage()
    {
        InitializeComponent();
    }

    internal void Init(NativeMethods.Store store, Action<ThemeMode> applyTheme, IntPtr ownerHwnd, Action? onDataChanged = null)
    {
        _store = store;
        _applyTheme = applyTheme;
        _ownerHwnd = ownerHwnd;
        _onDataChanged = onDataChanged;

        // 初始化期间给控件赋值会触发各自的 Changed 事件，
        // 若不拦截就会「仅打开设置页」便产生一次设置落盘与诊断写入副作用。
        _loading = true;
        try
        {
            ThemePicker.SelectedIndex = AppSettings.Theme switch
            {
                ThemeMode.Light => 1,
                ThemeMode.Dark => 2,
                _ => 0
            };

            // 帧率滑块：min 1, max 显示器刷新率
            int maxRate = AppSettings.MonitorRefreshRate;
            FrameRateSlider.Minimum = 1;
            FrameRateSlider.Maximum = maxRate;
            FrameRateSlider.Value = Math.Min(AppSettings.FrameRate, maxRate);
            UpdateFrameRateHint();

            DiagToggle.IsOn = store.GetDiagnostics();

            LoadAppearance();

            VersionText.Text = $"KeySecBox v{AppVersion}";
        }
        finally
        {
            _loading = false;
        }
    }

    // 初始化赋值期间为 true，用于屏蔽各控件 Changed 事件的副作用
    private bool _loading;

    private bool _loadingAppearance;

    private void LoadAppearance()
    {
        _loadingAppearance = true;

        DialogCornerSlider.Value = AppSettings.DialogCornerRadius;
        UpdateDialogCornerHint();

        _loadingAppearance = false;
    }

    private void UpdateDialogCornerHint()
        => DialogCornerHint.Text = $"当前：{(int)Math.Round(DialogCornerSlider.Value)} px（0 为直角）";

    private void OnDialogCornerChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading || _loadingAppearance) return;
        UpdateDialogCornerHint();
        AppSettings.DialogCornerRadius = (int)Math.Round(DialogCornerSlider.Value); // 即时生效
    }

    // 从程序集文件属性读取版本（由构建时 version.txt 注入）
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

    private void OnFrameRateChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        UpdateFrameRateHint();
        if (_loading) return; // 初始化赋值不写回配置

        // 拖动过程中每一步都落盘会造成大量同步磁盘写入（UI 卡顿），
        // 这里做防抖：连续变更只在停止约 400ms 后写回一次。
        ScheduleFrameRateCommit();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _frameRateCommitTimer;

    // 合并连续的帧率变更，只在最后一次变更后写回配置
    private void ScheduleFrameRateCommit()
    {
        _frameRateCommitTimer ??= DispatcherQueue.CreateTimer();
        _frameRateCommitTimer.Tick -= OnFrameRateCommitTick;
        _frameRateCommitTimer.Tick += OnFrameRateCommitTick;
        _frameRateCommitTimer.Interval = TimeSpan.FromMilliseconds(400);
        _frameRateCommitTimer.IsRepeating = false;
        _frameRateCommitTimer.Start(); // 重新计时，避免拖动中反复写盘
    }

    private void OnFrameRateCommitTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        => CommitFrameRate();

    // 立即提交当前帧率并停止待执行的防抖计时器
    private void CommitFrameRate()
    {
        _frameRateCommitTimer?.Stop();
        AppSettings.FrameRate = (int)Math.Round(FrameRateSlider.Value);
    }

    #endregion

    #region 改密

    private async void ChangePwdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;
        var root = XamlRoot;   // 关闭设置前先捕获，供后续对话框使用
        var dlg = new ChangePasswordDialog();
        dlg.Init(store);
        dlg.XamlRoot = root;
        ThemeDialog(dlg);
        await dlg.ShowAsync();
        if (!dlg.Succeeded) return;

        // 改密成功后，用新密码重包恢复记录（不修改恢复方式本身）
        if (dlg.NewMaster is { } newMaster && RecoveryManager.GetConfig().Any)
            await RePackRecoveryAsync(root, newMaster);

        var info = new ContentDialog
        {
            XamlRoot = root,
            Title = "KeySecBox",
            Content = "密码已修改，所有条目已用新密码重新加密。",
            CloseButtonText = "确定"
        };
        ThemeDialog(info);
        await info.ShowAsync();
    }

    #endregion

    #region 恢复方式

    // 设置中重新配置恢复方式：需先验证当前主密码
    private async void RecoveryCfgBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;
        var rdlg = new RecoverySetupDialog();
        rdlg.Init(null, store); // 传入 store，对话框内验证当前主密码
        if (await ShowChildAsync(rdlg) != ContentDialogResult.Primary) return;
    }

    // 忘记密码：立即恢复，取回的主密码填入改密对话框旧密码框，引导改一个新密码
    private async void ForgotPwdBtn_Click(object sender, RoutedEventArgs e)
    {
        var root = XamlRoot;
        var fdlg = new ForgotPasswordDialog();
        await ShowChildAsync(fdlg);
        if (string.IsNullOrEmpty(fdlg.RecoveredMaster))
        {
            await ShowMessage("未能取回主密码。");
            return;
        }
        string recovered = fdlg.RecoveredMaster;
        fdlg.RecoveredMaster = null;

        var cdlg = new ChangePasswordDialog();
        cdlg.Init(_store!);
        cdlg.SetOldPassword(recovered);
        cdlg.XamlRoot = root;
        ThemeDialog(cdlg);
        await cdlg.ShowAsync();
        if (cdlg.Succeeded && cdlg.NewMaster is { } nm && RecoveryManager.GetConfig().Any)
            await RePackRecoveryAsync(root, nm);
    }

    private async Task RePackRecoveryAsync(XamlRoot root, string newMaster)
    {
        try
        {
            // 改密后取回库必须同步更新（旧记录对应旧密码），仅「更新」可完成
            var rdlg = new RecoverySetupDialog();
            rdlg.Init(newMaster, null, updateMode: true);
            rdlg.XamlRoot = root;
            ThemeDialog(rdlg);
            await rdlg.ShowAsync();
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
        var theme = ThemePicker.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        AppSettings.Theme = theme;
        _applyTheme?.Invoke(theme);

        CommitFrameRate(); // 取消防抖计时器，避免随后的重复写盘

        if (_store is { } store)
        {
            bool diag = DiagToggle.IsOn; // UI 线程取值，后台线程严禁触碰 UI 元素
            int drc = await Task.Run(() => store.SetDiagnostics(diag));
            if (drc != NativeMethods.KSBOX_OK)
            {
                SetStatus($"保存诊断设置失败（错误码 {drc}）。", isError: true);
                return;
            }
            AppPaths.TraceEnabled = store.GetDiagnostics(); // 同步运行期追踪开关
        }

        if (showStatus) SetStatus("设置已保存。", isError: false);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
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
        StatusText.Foreground = isError
            ? LookupBrush("SystemControlErrorTextForegroundBrush", Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C))
            : LookupBrush("AccentTextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 0x67, 0x50, 0xA4));
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    #endregion

    #region 辅助

    internal static void Trace(string msg)
    {
        if (!AppPaths.TraceEnabled) return; // 仅诊断模式下记录
        try { System.IO.File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    // 页面内弹出子对话框（无 Hide 语义，需先收起自身）
    private async Task<ContentDialogResult> ShowChildAsync(ContentDialog child)
    {
        child.XamlRoot = XamlRoot;
        ThemeDialog(child);
        return await child.ShowAsync();
    }

    // ContentDialog 不继承父对话框主题，显式套用
    private void ThemeDialog(ContentDialog dlg)
    {
        dlg.RequestedTheme = ActualTheme;
        dlg.CornerRadius = new CornerRadius(AppSettings.DialogCornerRadius);
    }

    private async Task ShowMessage(string text)
    {
        await ShowChildAsync(new ContentDialog
        {
            Title = "KeySecBox",
            Content = text,
            CloseButtonText = "确定"
        });
    }

    private static Microsoft.UI.Xaml.Media.Brush LookupBrush(string key,
        Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value)
                && value is Microsoft.UI.Xaml.Media.Brush brush)
                return brush;
        }
        catch
        {
        }

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(fallback);
    }

    #endregion
}
