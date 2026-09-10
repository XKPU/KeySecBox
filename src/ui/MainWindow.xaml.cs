using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;

namespace KeySecBox
{
    public sealed partial class MainWindow : Window
    {
        // 保险库 basename
        private static readonly string VaultBase = AppPaths.VaultBase;

        private readonly NativeMethods.Store _store = new();
        // 解锁状态：首次建库 / 旧版数据检测 / 取回主密码后引导改密
        private bool _firstRun;
        private bool _legacyDetected;
        private bool _setupMode;
        private bool _recoveredOpen;
        private bool _initStarted;
        private bool _unlocked;

        // 当前页面（用于导航高亮与新建条目跳转）
        private string _currentPage = "";

        #region 初始化

        public MainWindow()
        {
            InitializeComponent();
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            SetupTitleBar();
            // 系统明暗切换时标题栏跟随实际生效明暗
            if (Content is FrameworkElement themeRoot)
                themeRoot.ActualThemeChanged += (_, _) =>
                {
                    try { UpdateTitleBarColors(ResolveEffectiveTheme(AppSettings.Theme)); }
                    catch { }
                };
            // XamlRoot 在根元素 Loaded 后才就绪，解锁覆盖层与对话框都依赖它
            RootGrid.Loaded += (_, _) =>
            {
                ApplyTheme(AppSettings.Theme); // 元素载入后 ActualTheme 才可靠
                if (_initStarted) return;
                _initStarted = true;
                _ = InitializeAsync();
            };
        }

        /// <summary>初始化标题栏：扩展内容、同步高度、启用拖动与双击最大化。</summary>
        private void SetupTitleBar()
        {
            try
            {
                ExtendsContentIntoTitleBar = true;
                // 不用 SetTitleBar（会使整片区域成为系统 caption，吞掉按钮点击）
                UpdateMaximizeIcon();
                SizeChanged += (_, _) =>
                {
                    UpdateMaximizeIcon();
                    SyncTitleBarHeight();
                    UpdateUnlockClip();
                };
                EnableDoubleClickMaximize(); // 双击标题栏最大化/还原

                CaptionButtons.Visibility = Visibility.Collapsed; // 使用系统控制按钮

                // 高度需多时机同步（构造阶段读取常为 0）
                SyncTitleBarHeight();
                RootGrid.Loaded += (_, _) => SyncTitleBarHeight();
                Activated += (_, _) => SyncTitleBarHeight();
                DispatcherQueue.TryEnqueue(SyncTitleBarHeight);
            }
            catch { }
        }


        // 诊断日志限次，避免 SizeChanged 刷屏
        private int _titleBarDiagLogged;
        private const int TitleBarDiagMax = 12;

        private void SyncTitleBarHeight()
        {
            try
            {
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

                // 系统返回物理像素，XAML 用有效像素，需按 DPI 换算
                double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
                if (scale <= 0) scale = 1.0;

                double sysRaw = AppWindow.TitleBar.Height;
                double h = sysRaw > 0
                    ? sysRaw / scale                    // 物理像素 → 有效像素
                    : 48;                               // 读不到时回退到 Tall 的 48 有效像素

                TitleBar.Height = h;
                ScaleTitleBarContent(h); // 图标与内容随栏高同步缩放

                if (_titleBarDiagLogged < TitleBarDiagMax)
                {
                    _titleBarDiagLogged++;
                    WriteTitleBarDiag(h, null);
                }
            }
            catch (Exception ex)
            {
                if (_titleBarDiagLogged < TitleBarDiagMax)
                {
                    _titleBarDiagLogged++;
                    WriteTitleBarDiag(-1, ex.Message);
                }
            }
        }

        // 内容随栏高温和缩放（基准 32）
        private void ScaleTitleBarContent(double height)
        {
            double s = Math.Clamp(1.0 + (height / 32.0 - 1.0) * 0.4, 1.0, 1.25);
            ApplyContentScale(BrandPanel, s);
            ApplyContentScale(NavList, s);
        }

        private static void ApplyContentScale(FrameworkElement? element, double s)
        {
            if (element == null) return;
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            element.RenderTransform =
                new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = s, ScaleY = s };
        }

        // 裁剪覆盖层到内容区，避免上滑时盖住标题栏
        private void UpdateUnlockClip()
        {
            try
            {
                UnlockOverlayClip.Rect = new Windows.Foundation.Rect(
                    0, 0, UnlockOverlay.ActualWidth, UnlockOverlay.ActualHeight);
            }
            catch { }
        }

        // 标题栏诊断日志（仅诊断模式）
        private void WriteTitleBarDiag(double requested, string? error)
        {
            if (!AppPaths.TraceEnabled) return; // 未开启诊断模式则不写日志

            try
            {
                var tb = AppWindow.TitleBar;
                // 注意：Window 本身没有 XamlRoot（那是 UIElement 的属性），取根元素上的
                double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;

                // sysHeightRaw 为物理像素，sysHeightDip 为换算后的有效像素（应与 myActual 相等）
                string msg =
                    $"[TitleBar] sysHeightRaw={tb.Height:0.##} sysHeightDip={tb.Height / scale:0.##} " +
                    $"sysRightInset={tb.RightInset:0.##} sysLeftInset={tb.LeftInset:0.##} " +
                    $"option={tb.PreferredHeightOption} dpiScale={scale:0.##} requested={requested:0.##} " +
                    $"myHeight={TitleBar.Height:0.##} myActual={TitleBar.ActualHeight:0.##} " +
                    $"err={error ?? "-"}";

                File.AppendAllText(
                    AppPaths.TraceLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        #region 自绘窗口控制按钮

        // 控制按钮沿用系统绘制，配色统一；自绘按钮组默认隐藏
        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
        }

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        // 系统拖动（等效按下原生标题栏）
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint SendMessage(nint hWnd, int Msg, nint wParam, nint lParam);

        // 异步投递，避免同步重入
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(nint hWnd, int Msg, nint wParam, nint lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const nint HTCAPTION = 2;

        private bool _dragStarting; // 递归防护

        private void DragRegion_PointerPressed(object sender,
            Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_dragStarting) return;

            // 交互元素上不启动拖动（事件会冒泡到本层）
            if (e.OriginalSource is DependencyObject src && IsInteractive(src)) return;

            _dragStarting = true;
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ReleaseCapture();
                PostMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
            catch { }
            finally
            {
                _dragStarting = false;
            }
        }

        private void EnableDoubleClickMaximize()
        {
            TitleBar.DoubleTapped += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject src && IsInteractive(src)) return;
                ToggleMaximize();
            };
        }

        // 标题栏内的可交互元素
        private static bool IsInteractive(DependencyObject node)
        {
            var current = node;
            while (current != null)
            {
                if (current is Button) return true;
                if (current is FrameworkElement
                    {
                        Name: "NavList" or "BrandPanel" or "CaptionButtons"
                    }) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void ToggleMaximize()
        {
            if (AppWindow.Presenter is not OverlappedPresenter p) return;
            if (p.State == OverlappedPresenterState.Maximized) p.Restore();
            else p.Maximize();
            UpdateMaximizeIcon();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // 最大化 / 还原 切换图标（E922 最大化，E923 还原）
        private void UpdateMaximizeIcon()
        {
            bool max = AppWindow.Presenter is OverlappedPresenter p
                && p.State == OverlappedPresenterState.Maximized;
            MaximizeIcon.Glyph = max ? "\uE923" : "\uE922";
        }

        #endregion

        private void ApplyTheme(ThemeMode mode)
        {
            if (Content is FrameworkElement root)
                root.RequestedTheme = mode switch
                {
                    ThemeMode.Light => ElementTheme.Light,
                    ThemeMode.Dark => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            // 标题栏不随 RequestedTheme 变色，按实际生效明暗手动着色
            UpdateTitleBarColors(ResolveEffectiveTheme(mode));
        }

        // 对话框按生效明暗套主题
        private ContentDialog ThemeDialog(ContentDialog dlg)
        {
            dlg.RequestedTheme = ResolveEffectiveTheme(AppSettings.Theme);
            dlg.CornerRadius = new CornerRadius(AppSettings.DialogCornerRadius);
            return dlg;
        }

        // 旧版库文件移入 legacy_backup_<时间戳>，使 data 纯净
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
                // 备份失败不阻断创建新库（残留旧文件不影响新版运行，open 只看 .master）
                Trace($"legacy backup EX: {ex}");
            }
        }

        // 实际生效明暗：System 模式用内容 ActualTheme，未载入时回落系统注册表
        private ElementTheme ResolveEffectiveTheme(ThemeMode mode)
        {
            if (mode == ThemeMode.Dark) return ElementTheme.Dark;
            if (mode == ThemeMode.Light) return ElementTheme.Light;
            var actual = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
            if (actual != ElementTheme.Default) return actual;
            return IsSystemLight() ? ElementTheme.Light : ElementTheme.Dark;
        }

        private static bool IsSystemLight()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
            }
            catch { }
            return true;
        }

        private static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

        // 自定义标题栏：背景与导航由 XAML 自绘；未能隐藏系统按钮时，用主题配色让它融入风格
        private void UpdateTitleBarColors(ElementTheme theme)
        {
            try
            {
                var tb = AppWindow.TitleBar;
                bool dark = theme == ElementTheme.Dark;
                var fg = dark ? C(0xFF, 0xFF, 0xFF) : C(0x10, 0x10, 0x10);

                // 两边同用不透明色以消除色差
                var bar = dark ? C(0x1C, 0x1C, 0x1C) : C(0xF9, 0xF9, 0xF9);
                TitleBar.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(bar);

                tb.BackgroundColor = bar;
                tb.InactiveBackgroundColor = bar;

                // 悬停 / 按下做轻微区分
                tb.ButtonBackgroundColor = bar;
                tb.ButtonInactiveBackgroundColor = bar;
                tb.ButtonForegroundColor = fg;
                tb.ButtonInactiveForegroundColor = fg;
                tb.ButtonHoverBackgroundColor = dark ? C(0x2E, 0x2E, 0x2E) : C(0xE8, 0xE8, 0xE8);
                tb.ButtonHoverForegroundColor = fg;
                tb.ButtonPressedBackgroundColor = dark ? C(0x3A, 0x3A, 0x3A) : C(0xD0, 0xD0, 0xD0);
                tb.ButtonPressedForegroundColor = fg;

                tb.ForegroundColor = fg;
                tb.InactiveForegroundColor = fg;
            }
            catch
            {
                // 标题栏 API 不可用时忽略
            }
        }

        #endregion

        #region 解锁

        private static void Trace(string msg)
        {
            if (!AppPaths.TraceEnabled) return; // 仅诊断模式下记录
            try { File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
        }

        private async Task InitializeAsync()
        {
            Trace("init start");
            try
            {
                AppPaths.EnsureDataDir(); // 数据目录必须存在，否则首次 Setup/Save 时 fopen 失败
                bool firstRun = !File.Exists(VaultBase + ".master");
                bool legacyDetected = firstRun && File.Exists(VaultBase + ".settings");
                string? hint = legacyDetected
                    ? "检测到旧版数据文件。创建新保险库时会把旧文件移入 data\\legacy_backup_* 备份目录，\n创建后可在 设置→数据→导入旧版库 中选择该备份目录合并旧数据。"
                    : null;
                _firstRun = firstRun;
                _legacyDetected = legacyDetected;
                // 全页覆盖式解锁：正确时覆盖层向上滑出，错误时输入框边缘变红并抖动
                ShowUnlockOverlay(firstRun, hint);
            }
            catch (Exception ex)
            {
                Trace($"EX: {ex.GetType().Name}: {ex.Message}");
                try { await ShowError($"初始化失败：{ex.Message}"); } catch { }
            }
        }

        // 忘记密码恢复方式配置
        private async Task PromptRecoverySetupAsync(string? providedMaster = null)
        {
            try
            {
                var rdlg = new RecoverySetupDialog { XamlRoot = Content.XamlRoot };
                rdlg.Init(providedMaster, _store);
                ThemeDialog(rdlg);
                await rdlg.ShowAsync();
            }
            catch (Exception ex)
            {
                Trace($"recovery setup EX: {ex.Message}");
            }
        }

        // 改密/取回复原后同步取回库
        private async Task RepackRecoveryAsync(string newMaster)
        {
            try
            {
                if (!RecoveryManager.GetConfig().Any) return;
                var rdlg = new RecoverySetupDialog { XamlRoot = Content.XamlRoot };
                rdlg.Init(newMaster, null, updateMode: true);
                ThemeDialog(rdlg);
                await rdlg.ShowAsync();
            }
            catch (Exception ex)
            {
                Trace($"repack recovery EX: {ex.Message}");
            }
        }

        #region 解锁覆盖层

        /// <summary>显示全页解锁覆盖层（首次建库与后续解锁共用同一覆盖层）。</summary>
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
            // 未解锁时标题栏保留品牌与窗口控制按钮，但不展示页面切换
            NavList.Visibility = Visibility.Collapsed;
            UnlockOverlay.Visibility = Visibility.Visible;
            UnlockOverlay.Opacity = 1;
            OverlayTransform.Y = 0;
            _ = UnlockPasswordBox.Focus(FocusState.Programmatic);
        }

        private void UnlockPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            UnlockErrorText.Visibility = Visibility.Collapsed;
            ClearUnlockErrorVisual();
            UnlockButton.IsEnabled = UnlockPasswordBox.Password.Length > 0
                && (!_setupMode || UnlockConfirmBox.Password.Length > 0);
        }

        private async void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            string pwd = UnlockPasswordBox.Password;
            if (pwd.Length == 0) return;

            if (_setupMode && pwd != UnlockConfirmBox.Password)
            {
                ShowUnlockError("两次输入的主密码不一致，请重新输入。");
                return;
            }

            UnlockButton.IsEnabled = false;
            UnlockProgress.Visibility = Visibility.Visible;
            UnlockErrorText.Visibility = Visibility.Collapsed;

            try
            {
                if (_firstRun && _legacyDetected)
                    BackupLegacyVault(); // 旧版库文件移入备份目录，data 纯净后再创建新版库

                int rc = await Task.Run(() => _firstRun
                    ? _store.Setup(VaultBase, pwd)
                    : _store.Open(VaultBase, pwd));

                if (rc != NativeMethods.KSBOX_OK)
                {
                    ShowUnlockError(rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
                        ? "密码错误，请重试。"
                        : $"打开保险库失败（错误码 {rc}）。");
                    UnlockButton.IsEnabled = true;
                    return;
                }

                AppPaths.TraceEnabled = _store.GetDiagnostics(); // 同步诊断开关
#if DEBUG
                if (_store.SetDiagnostics(true) == NativeMethods.KSBOX_OK)
                {
                    AppPaths.TraceEnabled = true;
                    Trace("debug build: diagnostics auto-enabled");
                }
#endif
                if (_firstRun)
                {
                    _store.Save();
                    await PromptRecoverySetupAsync(pwd); // 建库后引导配置恢复方式
                }
                else if (_recoveredOpen)
                {
                    // 取回主密码后引导改密（旧密码框预填，明文不显示）
                    var cdlg = new ChangePasswordDialog { XamlRoot = Content.XamlRoot };
                    cdlg.Init(_store);
                    cdlg.SetOldPassword(pwd);
                    ThemeDialog(cdlg);
                    await cdlg.ShowAsync();
                    if (cdlg.Succeeded && cdlg.NewMaster is { } nm)
                        await RepackRecoveryAsync(nm);
                }

                ClearUnlockSecrets();
                _unlocked = true;

                await DismissUnlockOverlayAsync(); // 正确：覆盖层向上滑出
                NavList.Visibility = Visibility.Visible; // 解锁后恢复页面切换按钮
                Navigate("Vault");
            }
            catch (Exception ex)
            {
                Trace($"unlock EX: {ex.Message}");
                ShowUnlockError($"解锁失败：{ex.Message}");
                UnlockButton.IsEnabled = true;
            }
            finally
            {
                UnlockProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async void UnlockForgotLink_Click(object sender, RoutedEventArgs e)
        {
            var fdlg = new ForgotPasswordDialog { XamlRoot = Content.XamlRoot };
            ThemeDialog(fdlg);
            await fdlg.ShowAsync();
            if (string.IsNullOrEmpty(fdlg.RecoveredMaster)) return;

            _recoveredOpen = true; // 本次用取回的主密码开库：打开后引导立即改密
            UnlockPasswordBox.Password = fdlg.RecoveredMaster;
            fdlg.RecoveredMaster = null;
            UnlockButton_Click(sender, e);
        }

        /// <summary>错误态：输入框边缘变红并横向抖动。</summary>
        private void ShowUnlockError(string message)
        {
            UnlockErrorText.Text = message;
            UnlockErrorText.Visibility = Visibility.Visible;

            var red = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
            UnlockPasswordBox.BorderBrush = red;
            UnlockPasswordBox.BorderThickness = new Thickness(1.5);
            if (_setupMode)
            {
                UnlockConfirmBox.BorderBrush = red;
                UnlockConfirmBox.BorderThickness = new Thickness(1.5);
            }

            Shake(PasswordBoxTransform);
            if (_setupMode) Shake(ConfirmBoxTransform);

            ClearUnlockSecrets();
        }

        // 还原为模板默认描边（透明描边即回落默认视觉）
        private void ClearUnlockErrorVisual()
        {
            var none = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            UnlockPasswordBox.BorderBrush = none;
            UnlockPasswordBox.BorderThickness = new Thickness(1);
            UnlockConfirmBox.BorderBrush = none;
            UnlockConfirmBox.BorderThickness = new Thickness(1);
        }

        private static void Shake(TranslateTransform transform)
        {
            var sb = new Storyboard();
            var anim = new DoubleAnimationUsingKeyFrames();
            double[] frames = { 0, -10, 9, -7, 6, -4, 3, 0 };
            for (int i = 0; i < frames.Length; i++)
                anim.KeyFrames.Add(new LinearDoubleKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 45)),
                    Value = frames[i]
                });
            Storyboard.SetTarget(anim, transform);
            Storyboard.SetTargetProperty(anim, "X");
            sb.Children.Add(anim);
            sb.Begin();
        }

        /// <summary>解锁正确：覆盖层整体上滑出画面并淡出。</summary>
        private async Task DismissUnlockOverlayAsync()
        {
            double height = UnlockOverlay.ActualHeight;
            if (height <= 0) height = 900; // 尚未完成布局时给一个足够大的位移

            var sb = new Storyboard();

            var move = new DoubleAnimation
            {
                To = -height,
                Duration = new Duration(TimeSpan.FromMilliseconds(520)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(move, OverlayTransform);
            Storyboard.SetTargetProperty(move, "Y");
            sb.Children.Add(move);

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(420))
            };
            Storyboard.SetTarget(fade, UnlockOverlay);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            sb.Begin();
            await Task.Delay(560);
            UnlockOverlay.Visibility = Visibility.Collapsed;
        }

        private void ClearUnlockSecrets()
        {
            UnlockPasswordBox.Password = "";
            UnlockConfirmBox.Password = "";
        }

        #endregion

        #region 导航

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_unlocked) return;
            if (sender is Button { Tag: string tag }) Navigate(tag);
        }

        // 页面实例缓存：切回时复用，保证载入动画只在进入应用时触发一次
        private VaultPage? _vaultPage;
        private ImportExportPage? _transferPage;
        private SettingsPage? _settingsPage;

        /// <summary>页面切换：复用缓存实例并播放滑入动画。同一页面重复点击不重建。</summary>
        private void Navigate(string tag)
        {
            if (_currentPage == tag && ContentFrame.Content != null) return;
            _currentPage = tag;

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            switch (tag)
            {
                case "Transfer":
                    if (ContentFrame.Content is not ImportExportPage)
                    {
                        if (_transferPage == null)
                        {
                            _transferPage = new ImportExportPage();
                            _transferPage.Init(_store, hwnd, ReloadVaultData);
                        }
                        ContentFrame.Content = _transferPage;
                        AnimatePageIn(_transferPage);
                    }
                    break;

                case "Settings":
                    if (ContentFrame.Content is not SettingsPage)
                    {
                        if (_settingsPage == null)
                        {
                            _settingsPage = new SettingsPage();
                            _settingsPage.Init(_store, ApplyTheme, hwnd, ReloadVaultData);
                        }
                        ContentFrame.Content = _settingsPage;
                        AnimatePageIn(_settingsPage);
                    }
                    break;

                default:
                    if (ContentFrame.Content is not VaultPage)
                    {
                        if (_vaultPage == null)
                        {
                            _vaultPage = new VaultPage();
                            _vaultPage.Init(_store); // 仅首次：内部播放一次入场动画
                        }
                        ContentFrame.Content = _vaultPage;
                        AnimatePageIn(_vaultPage);
                    }
                    break;
            }

            UpdateNavVisual(tag);
        }

        /// <summary>页面切换动画：新页面自右轻微滑入并淡入。</summary>
        private static void AnimatePageIn(UIElement page)
        {
            var transform = page.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                page.RenderTransform = transform;
            }

            var sb = new Storyboard();

            var move = new DoubleAnimation
            {
                From = 28, To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(move, transform);
            Storyboard.SetTargetProperty(move, "X");
            sb.Children.Add(move);

            var fade = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(260))
            };
            Storyboard.SetTarget(fade, page);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            sb.Begin();
        }

        private void UpdateNavVisual(string tag)
        {
            foreach (var child in NavList.Children)
                if (child is Button btn)
                    btn.Opacity = (btn.Tag as string) == tag ? 1.0 : 0.7;
        }

        /// <summary>导入等外部改动后同步刷新库页面数据。</summary>
        private void ReloadVaultData()
        {
            if (ContentFrame.Content is VaultPage page) page.Refresh();
        }

        #endregion

        #endregion

        #region 辅助

        private async Task ShowError(string msg)
        {
            var dlg = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "提示",
                Content = msg,
                CloseButtonText = "知道了"
            };
            ThemeDialog(dlg);
            await dlg.ShowAsync();
        }

        // 把 target 原地同步为 source，
        private static void SyncInPlace<T>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, long> idOf)
        {
            if (ReferenceEquals(target, source)) return;

            for (int i = target.Count - 1; i >= 0; i--)
            {
                bool found = false;
                for (int s = 0; s < source.Count && !found; s++)
                    if (idOf(source[s]) == idOf(target[i])) found = true;
                if (!found) target.RemoveAt(i);
            }

            int t = 0;
            for (int s = 0; s < source.Count; s++)
            {
                var item = source[s];
                if (t < target.Count && idOf(target[t]) == idOf(item))
                {
                    if (!ReferenceEquals(target[t], item)) target[t] = item;
                    t++;
                    continue;
                }
                int existing = -1;
                for (int j = t; j < target.Count; j++)
                    if (idOf(target[j]) == idOf(item)) { existing = j; break; }
                if (existing >= 0)
                {
                    target.Move(existing, t);
                }
                else target.Insert(t, item);
                t++;
            }
            while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
        }

        private static int IndexOfId<T>(IReadOnlyList<T> list, long id, Func<T, long> idOf)
        {
            for (int i = 0; i < list.Count; i++)
                if (idOf(list[i]) == id) return i;
            return -1;
        }

        #endregion

    }
}
