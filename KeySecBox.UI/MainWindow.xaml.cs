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

                // 幂等：值没变就不要再赋值。写入 TitleBar.Height 会触发一次布局，
                // 在快速切换页面（布局频繁）时形成高频往返，表现为界面卡住。
                //
                // 注意 NaN：XAML 里 TitleBar 只设了 MinHeight、没有 Height，
                // 所以初始 TitleBar.Height 是 NaN（Auto）。而 NaN 参与比较恒为 false，
                // 若直接用 Math.Abs(cur - h) > eps 判断，会永远不成立 →
                // 高度永远不赋值 → 标题栏保持默认 32px（表现为"标题栏变小了"）。
                double cur = TitleBar.Height;
                if (double.IsNaN(cur) || Math.Abs(cur - h) > 0.01)
                {
                    TitleBar.Height = h;
                    ScaleTitleBarContent(h); // 图标与内容随栏高同步缩放
                }

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
        //
        // 只缩放**外层容器**（TitleBarContent = 品牌 + 导航），不分别缩放
        // BrandPanel/NavList：它们各自以自身中心为原点缩放时会互相侵入对方区域，
        // 表现为「标题和按钮重叠」。统一缩放容器则整体放大，内部相对间距不变。
        private void ScaleTitleBarContent(double height)
        {
            double s = Math.Clamp(1.0 + (height / 32.0 - 1.0) * 0.4, 1.0, 1.25);
            ApplyContentScale(TitleBarContent, s);
        }

        private static void ApplyContentScale(FrameworkElement? element, double s)
        {
            if (element == null) return;

            // 复用同一个 ScaleTransform 并只在数值变化时写入：
            // 每次新建对象都会让布局失效，进而反复触发 LayoutUpdated。
            if (element.RenderTransform is not ScaleTransform st)
            {
                st = new ScaleTransform { ScaleX = s, ScaleY = s };
                element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                element.RenderTransform = st;
                return;
            }

            if (Math.Abs(st.ScaleX - s) > 0.001 || Math.Abs(st.ScaleY - s) > 0.001)
            {
                st.ScaleX = s;
                st.ScaleY = s;
            }
        }

        // 裁剪解锁覆盖层到内容区，避免上滑/侧滑时盖住标题栏。
        // 滑动容器本身不裁剪：外层 Clip 就是视口，已足够把屏幕外的页挡住。
        private double _lastClipW = -1, _lastClipH = -1;

        private void UpdateUnlockClip()
        {
            try
            {
                double w = UnlockOverlay.ActualWidth;
                double h = UnlockOverlay.ActualHeight;

                // 未完成布局时宽高为 0，此时不能把 Clip 设成空矩形——
                // 那会把整个覆盖层裁没（表现为整页发黑）。保留 XAML 里的占位 Rect。
                if (w <= 0 || h <= 0) return;

                // 尺寸没变就不重复写（写 Width/Clip 会让布局失效）
                if (Math.Abs(_lastClipW - w) < 0.01 && Math.Abs(_lastClipH - h) < 0.01) return;
                _lastClipW = w;
                _lastClipH = h;

                UnlockOverlayClip.Rect = new Windows.Foundation.Rect(0, 0, w, h);

                // 两页各占一屏宽。用代码设置而非 Binding：
                // 布局阶段 ActualWidth 可能为 0，Binding 会把整页压成 0 宽而显示为空白。
                UnlockPage.Width = w;
                RecoveryPage.Width = w;

                // 窗口尺寸变化后，位移量（一屏宽）随之改变，需重新对齐，
                // 否则恢复方式页会偏出可视区（滑动动画用的是动画开始时的屏宽）。
                if (_onRecoverySlide)
                    SlideTransform.X = -w;
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

        #region 标题栏

        // 窗口控制按钮（最小化/最大化/关闭）一律由系统绘制，
        // 因此本类不再包含对应的自绘按钮处理函数。

        /// <summary>
        /// 标题栏：扩展内容到标题栏 + 用 SetTitleBar 登记拖动区，
        /// 再用 InputNonClientPointerSource 把「交互控件」声明为直通区。
        ///
        /// 这是微软官方文档要求的做法。原文（Title bar customization）：
        ///   "If you place interactive content in your title bar, you need to
        ///    specify the regions that are interactive ... you need to use the
        ///    InputNonClientPointerSource class to specify areas where input is
        ///    passed through to the interactive control, rather than handled by
        ///    the title bar."
        ///
        /// 教训：第五轮我曾以为"上层子元素会自动优先拿到命中"，于是删掉直通区、
        /// 只留 SetTitleBar —— 结果**所有按钮都点不动**（标题栏整片被系统当作
        /// 非工作区吞掉输入）。该假设是错的，官方明确要求显式声明交互区。
        /// </summary>
        private void SetupTitleBar()
        {
            try
            {
                ExtendsContentIntoTitleBar = true;

                // 拖动区只交给空白背景层；交互控件通过直通区声明
                SetTitleBar(DragRegion);

                // 文档要求：PreferredHeightOption 必须在 ExtendsContentIntoTitleBar
                // 为 true 之后设置（在 SyncTitleBarHeight 里设置）。
                SizeChanged += (_, _) =>
                {
                    SyncTitleBarHeight();
                    UpdateUnlockClip();
                };

                // 文档要求：初始直通区必须在元素完成布局后再计算，
                // 否则拿到的 ActualWidth/坐标是错的。
                TitleBar.Loaded += (_, _) => UpdateInteractiveRegions("TitleBar.Loaded");
                TitleBar.SizeChanged += (_, _) => UpdateInteractiveRegions("TitleBar.SizeChanged");

                SyncTitleBarHeight();
                RootGrid.Loaded += (_, _) =>
                {
                    SyncTitleBarHeight();
                    UpdateInteractiveRegions("RootGrid.Loaded");
                };
                Activated += (_, _) =>
                {
                    SyncTitleBarHeight();
                    UpdateInteractiveRegions("Activated");
                };
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncTitleBarHeight();
                    UpdateInteractiveRegions("TryEnqueue");
                });

                // 兜底重试：实测 trace.log 里首次成功下发可能比启动晚 10 秒以上，
                // 这段时间直通区是空的 → 用户看到"刚启动时有概率穿透"。
                // 用短定时器持续重试，直到成功下发过一次为止（成功后即停）。
                StartInteractiveRegionRetry();
            }
            catch { }
        }

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _regionRetryTimer;
        private int _regionRetryCount;

        /// <summary>
        /// 启动后用短定时器反复尝试下发直通区，直到成功一次。
        /// 只解决"启动初期的空窗期"：一旦成功下发即停止，不做常驻轮询。
        /// </summary>
        private void StartInteractiveRegionRetry()
        {
            if (_regionRetryTimer != null) return;

            var timer = DispatcherQueue.CreateTimer();
            _regionRetryTimer = timer;
            timer.Interval = TimeSpan.FromMilliseconds(150);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                _regionRetryCount++;

                // 已成功下发过 → 停
                if (_interactiveRects.Count > 0)
                {
                    timer.Stop();
                    Diag("Interactive", "RetryTimer",
                        $"停止重试（已成功下发），共重试 {_regionRetryCount} 次");
                    return;
                }

                UpdateInteractiveRegions($"RetryTimer#{_regionRetryCount}");

                // 上限约 6 秒（40 次 × 150ms），之后由 SizeChanged / Activated 兜住
                if (_regionRetryCount >= 40) timer.Stop();
            };
            timer.Start();
        }

        // 上次下发的直通矩形，用于去重（避免每次布局都走 COM）
        private List<Windows.Graphics.RectInt32> _interactiveRects = new();

        /// <summary>
        /// 计算并下发「交互区」矩形：这些区域把输入直通给 XAML 控件，
        /// 而不是被系统当作标题栏拖动区吞掉。
        /// 官方示例用 TransformToVisual(null) + TransformBounds，
        /// 这里同样用包围盒，能正确包含 RenderTransform 的缩放效果。
        /// </summary>
        private void UpdateInteractiveRegions()
        {
            UpdateInteractiveRegions("?");
        }

        /// <param name="origin">触发来源，仅用于日志（定位"启动初期为何没登记"）</param>
        private void UpdateInteractiveRegions(string origin)
        {
            try
            {
                if (!ExtendsContentIntoTitleBar)
                {
                    Diag("Interactive", origin, "跳过：ExtendsContentIntoTitleBar=false");
                    return;
                }

                var root = TitleBar.XamlRoot;
                if (root == null)
                {
                    Diag("Interactive", origin, "跳过：XamlRoot 为 null");
                    return;
                }
                double scale = root.RasterizationScale;
                if (scale <= 0)
                {
                    Diag("Interactive", origin, $"跳过：scale={scale}");
                    return;
                }

                var rects = new List<Windows.Graphics.RectInt32>();
                var parts = new List<string>();
                foreach (var el in new FrameworkElement?[] { BrandPanel, NavList })
                {
                    string name = el?.Name ?? "null";
                    if (el == null) continue;
                    if (el.Visibility != Visibility.Visible)
                    {
                        parts.Add($"{name}:不可见");
                        continue;
                    }
                    if (el.ActualWidth <= 0 || el.ActualHeight <= 0)
                    {
                        // 关键：布局未完成时尺寸为 0，此时**绝不能**下发空矩形，
                        // 否则会把已有直通区清掉，形成"启动初期穿透"的窗口期。
                        parts.Add($"{name}:尺寸为0(w={el.ActualWidth:0.#},h={el.ActualHeight:0.#})");
                        continue;
                    }

                    // 官方写法：TransformToVisual(null) 得到相对窗口的包围盒，
                    // 天然包含 RenderTransform（本类会给内容加 ScaleTransform）。
                    var bounds = el.TransformToVisual(null).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, el.ActualWidth, el.ActualHeight));

                    // 竖直方向放宽到整条标题栏：按钮可点高度有限（Padding 12,3），
                    // 若只按元素高度声明，紧贴上下边缘按下仍会被当作拖动区。
                    double top = 0;
                    double height = TitleBar.ActualHeight > 0
                        ? TitleBar.ActualHeight
                        : bounds.Height;

                    // 水平各留 2 DIP 余量，避免边缘差 1px 就漏回拖动区
                    const double pad = 2.0;
                    rects.Add(new Windows.Graphics.RectInt32
                    {
                        X = (int)Math.Floor((bounds.X - pad) * scale),
                        Y = (int)Math.Floor(top * scale),
                        Width = (int)Math.Ceiling((bounds.Width + pad * 2) * scale),
                        Height = (int)Math.Ceiling(height * scale)
                    });
                    parts.Add($"{name}:x={bounds.X:0.#},w={bounds.Width:0.#}");
                }

                // 算不出完整矩形时**直接返回**，不做任何 ClearRegionRects：
                // 清空会让系统把整条标题栏当拖动区，启动初期表现为"点按钮穿透"。
                // 保留上一次的直通区（可能为空，那就等下一次布局完成再设）。
                if (rects.Count == 0)
                {
                    Diag("Interactive", origin, "未下发（无有效矩形，保留上次）。" +
                        string.Join(", ", parts));
                    return;
                }

                if (SameRects(rects, _interactiveRects))
                {
                    Diag("Interactive", origin, "未下发（与上次相同）。" + string.Join(", ", parts));
                    return;
                }

                var src = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                        WinRT.Interop.WindowNative.GetWindowHandle(this)));
                if (src == null)
                {
                    Diag("Interactive", origin, "跳过：InputNonClientPointerSource 为 null");
                    return;
                }

                src.ClearRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough);
                src.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough,
                    rects.ToArray());

                _interactiveRects = rects;

                string desc = string.Join(" | ", rects.Select(r =>
                    $"({r.X},{r.Y},{r.Width}x{r.Height})"));
                Diag("Interactive", origin, $"已下发 n={rects.Count} {desc}");
            }
            catch (Exception ex)
            {
                Diag("Interactive", origin, $"异常：{ex.GetType().Name} {ex.Message}");
            }
        }

        // 诊断日志（仅诊断模式）：origin 标明是哪条触发路径
        private static void Diag(string tag, string origin, string msg)
        {
            if (!AppPaths.TraceEnabled) return;
            try
            {
                File.AppendAllText(AppPaths.TraceLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] ({origin}) {msg}\n");
            }
            catch { }
        }

        private static bool SameRects(List<Windows.Graphics.RectInt32> a,
                                      List<Windows.Graphics.RectInt32> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].X != b[i].X || a[i].Y != b[i].Y ||
                    a[i].Width != b[i].Width || a[i].Height != b[i].Height) return false;
            }
            return true;
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

        // 改密/取回复原后同步取回库（仍使用对话框：此时已在主界面内，无覆盖层可内联）
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
            // 复位滑动位置与两页显隐，回到主密码页（恢复页在右列待滑入，故透明度归零）
            SlideTransform.X = 0;
            _onRecoverySlide = false;
            UnlockPanel.Opacity = 1;
            RecoveryPanel.Opacity = 0;
            // 覆盖层刚变为可见时 ActualWidth 可能尚未就绪，先同步一次；
            // 布局完成后再补一次，确保两页宽度不为 0（否则整页空白）。
            // 因为 UpdateUnlockClip 会按尺寸去重，这里先清掉缓存强制重算。
            _lastClipW = _lastClipH = -1;
            UpdateUnlockClip();
            DispatcherQueue.TryEnqueue(() =>
            {
                _lastClipW = _lastClipH = -1; // 覆盖层此前是 Collapsed，尺寸需要重新读取
                UpdateUnlockClip();
                _ = UnlockPasswordBox.Focus(FocusState.Programmatic);
            });
        }

        #region 恢复方式内联页

        // 建库流程中持有的主密码，用于内联恢复页保存恢复记录
        private string? _pendingSetupMaster;

        // 是否已滑到恢复方式页（用于窗口尺寸变化时保持位移正确）
        private bool _onRecoverySlide;

        /// <summary>准备好内联恢复页的内容（建库后调用，主密码由 _pendingSetupMaster 提供）。</summary>
        private void PrepareRecoveryPanel()
        {
            RecoveryErrorText.Visibility = Visibility.Collapsed;
            RecoveryProgress.Visibility = Visibility.Collapsed;
            RecoverySaveBtn.IsEnabled = true;

            // 新建库时通常尚无恢复记录；沿用统一逻辑以便复用既有配置
            var cfg = RecoveryManager.GetConfig();
            RecoveryBackupCheck.IsChecked = cfg.HasBackup;
            RecoverySystemCheck.IsChecked = cfg.HasSystem;
            RecoveryBackupPwdBox.Password = "";
            RecoveryBackupConfirmBox.Password = "";
            UpdateRecoveryPanels();
        }

        private void UpdateRecoveryPanels()
        {
            RecoveryBackupPanel.Visibility = RecoveryBackupCheck.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RecoveryMethod_Toggled(object sender, RoutedEventArgs e) => UpdateRecoveryPanels();

        /// <summary>主密码页左滑出去、恢复方式页随之从右侧滑入（整体左移一屏宽）。</summary>
        private async Task SlideToRecoveryAsync()
        {
            // 位移量 = 单页宽度；两页宽度由 UpdateUnlockClip 设为覆盖层宽度
            UpdateUnlockClip(); // 确保滑动前两页宽度已就绪
            double slide = UnlockOverlay.ActualWidth;
            if (slide <= 0) slide = Content.XamlRoot?.Size.Width ?? 900;

            var sb = new Storyboard();
            var move = new DoubleAnimation
            {
                To = -slide,
                Duration = new Duration(TimeSpan.FromMilliseconds(380)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(move, SlideTransform);
            Storyboard.SetTargetProperty(move, "X");
            sb.Children.Add(move);

            // 页 0 淡出、页 1 淡入，强化「翻页」观感
            var fadeOut = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240))
            };
            Storyboard.SetTarget(fadeOut, UnlockPanel);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            sb.Children.Add(fadeOut);

            var fadeIn = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(320)),
                BeginTime = TimeSpan.FromMilliseconds(140)
            };
            Storyboard.SetTarget(fadeIn, RecoveryPanel);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            sb.Children.Add(fadeIn);

            sb.Begin();
            await Task.Delay(400);

            UnlockPanel.Opacity = 0;
            RecoveryPanel.Opacity = 1;
            _onRecoverySlide = true;
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
                if (backup.Length < 1) { ShowRecoveryError("启用备用密码时必须填写备用密码。"); return; }
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

        private async void RecoverySkipBtn_Click(object sender, RoutedEventArgs e)
            => await FinishUnlockAsync();

        /// <summary>Windows Hello 系统验证，用于把本程序纳入 Hello 支持。</summary>
        private static async Task<bool> VerifySystemUnlockAsync()
        {
            try
            {
                var avail = await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync();
                if (avail != Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available)
                    return false;
                var res = await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync(
                    "KeySecBox 需要使用系统解锁验证将本程序纳入 Windows Hello 支持，以便忘记密码时取回保险库。");
                return res == Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified;
            }
            catch
            {
                return false;
            }
        }

        private void ShowRecoveryError(string message)
        {
            RecoveryErrorText.Text = message;
            RecoveryErrorText.Visibility = Visibility.Visible;
        }

        #endregion

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
                    // 建库成功后不解锁进入，而是把解锁页左滑出去、露出内联的恢复方式设置页；
                    // 由该页的「保存」或「暂不设置」继续完成解锁流程。
                    _pendingSetupMaster = pwd;
                    UnlockProgress.Visibility = Visibility.Collapsed;
                    UnlockButton.IsEnabled = true;
                    PrepareRecoveryPanel();
                    await SlideToRecoveryAsync();
                    return;
                }
                if (_recoveredOpen)
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

                await FinishUnlockAsync();
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

        /// <summary>解锁收尾：清错误态、隐藏覆盖层、恢复导航并进入库页面。</summary>
        private async Task FinishUnlockAsync()
        {
            ClearUnlockErrorVisual(); // 清掉上一次失败遗留的红色描边
            ClearUnlockSecrets();
            _pendingSetupMaster = null;
            _onRecoverySlide = false;
            _unlocked = true;

            await DismissUnlockOverlayAsync(); // 正确：覆盖层向上滑出
            NavList.Visibility = Visibility.Visible; // 解锁后恢复页面切换按钮
            Navigate("Vault");
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

            // 先清空密码再抖动：抖动作用在容器的 TranslateTransform 上，
            // 与文本内容无关，但先清空可确保抖动期间不留明文
            ClearUnlockSecrets();

            Shake(PasswordBoxTransform);
            if (_setupMode) Shake(ConfirmBoxTransform);
        }

        // 清除错误描边：用 ClearValue 移除本地值，让控件模板的默认描边重新生效
        // （赋透明画刷只是把红色换成透明，仍会压过模板默认值）
        private void ClearUnlockErrorVisual()
        {
            UnlockPasswordBox.ClearValue(Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty);
            UnlockPasswordBox.ClearValue(Microsoft.UI.Xaml.Controls.Control.BorderThicknessProperty);
            UnlockConfirmBox.ClearValue(Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty);
            UnlockConfirmBox.ClearValue(Microsoft.UI.Xaml.Controls.Control.BorderThicknessProperty);
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
