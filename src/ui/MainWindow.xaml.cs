using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
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
        private readonly List<NativeMethods.Category> _categories = new();
        private NativeMethods.Category? _selectedCategory;
        private bool _allScope = true;
        private string _searchText = "";

        // 分类排序模式：仅改内存工作副本，点"保存"才写回 store
        private readonly List<NativeMethods.Category> _sortWorking = new();
        private bool _categorySortMode;

        // 列表数据源：常驻同一集合，刷新时按 Id 原地面补丁，避免整表重建产生"重新加载"感
        private readonly ObservableCollection<NativeMethods.Category> _categoryItems = new();
        private readonly ObservableCollection<NativeMethods.Entry> _entryItems = new();

        // 分类切换过渡：令牌 + 待切换标记，快速连续切换时取消上一段，避免动画失效/叠加
        private bool _dataReady;
        private int _scopeSeq;
        private bool _scopeSwapPending;  // 退场播完后待执行的数据切换 + 入场
        private int _scopeSwapSeq;
        private EntrySnap? _scopeSnap;     // 切换前的展示快照
        private List<NativeMethods.Entry> _scopeTarget = new(); // 切换后的目标实例列表
        // 排序移动动画完成回调
        private Action? _moveAnimCompleted;

        // 分类切换前的条目快照（索引 + 相对列表顶部的像素偏移）
        private sealed class EntrySnap
        {
            public Dictionary<long, int> Idx = new();
            public Dictionary<long, double> Tops = new();
        }

        // 容器动画：定时器逐帧更新容器的 平移 / 透明度 / 行内文本透明度。
        // 切换分类：离场条目向右淡出、新条目从左侧淡入、停留条目位置不变则不动、位置变了则滑过去；
        // 排序移动 = UIElement.Translation 平移。
        private sealed class ContainerAnim
        {
            public FrameworkElement Fe = null!;
            public long DurationMs;
            public bool EaseIn;
            public bool Move;                  // 平移：排序/停留条目垂直滑动、出入场水平滑动
            public double FromX, ToX;          // 水平平移（新条目左侧淡入、离场条目向右淡出）
            public double FromY, ToY;          // 垂直平移（排序滑动、停留条目位置滑动）
            public bool Fade;                  // 整框透明度
            public float FromOpacity, ToOpacity;
        }
        private readonly List<ContainerAnim> _containerAnims = new();
        private DispatcherQueueTimer? _containerAnimTimer;
        private long _containerAnimStart;

        #region 初始化

        public MainWindow()
        {
            InitializeComponent();
            CategoryList.ItemsSource = _categoryItems;
            EntryList.ItemsSource = _entryItems;
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            // 系统明暗切换或应用内切主题时，标题栏与「全部」高亮跟随实际生效明暗
            if (Content is FrameworkElement themeRoot)
                themeRoot.ActualThemeChanged += (_, _) =>
                {
                    try
                    {
                        UpdateTitleBarColors(ResolveEffectiveTheme(AppSettings.Theme));
                        RefreshScopeVisual();
                    }
                    catch { }
                };
            // 解锁对话框尽早弹出。WinUI3 Activated 时 XamlRoot 可能未就绪，ContentDialog 会因 XamlRoot=null 抛异常，在根元素 Loaded 后启动解锁。
            RootGrid.Loaded += (_, _) =>
            {
                ApplyTheme(AppSettings.Theme); // 元素载入后 ActualTheme 才可靠
                if (_initStarted) return;
                _initStarted = true;
                _ = InitializeAsync();
            };
        }

        private bool _initStarted;

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
            RefreshScopeVisual();
        }

        // ContentDialog 弹出不会继承窗口根元素的 RequestedTheme
        // 统一按生效明暗给对话框套主题，
        private ContentDialog ThemeDialog(ContentDialog dlg)
        {
            dlg.RequestedTheme = ResolveEffectiveTheme(AppSettings.Theme);
            dlg.CornerRadius = new CornerRadius(AppSettings.DialogCornerRadius);
            return dlg;
        }

        // 检测到旧版库：把旧版库文件整体移入 data\legacy_backup_<时间戳> 备份目录，
        // 使 data 目录纯净，随后由 Setup 创建全新库。
        // UI 运行文件及跟踪调试日志保留。
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

        // 标题栏需按生效明暗手动设置背景与按钮色
        private void UpdateTitleBarColors(ElementTheme theme)
        {
            try
            {
                var tb = AppWindow.TitleBar;
                bool dark = theme == ElementTheme.Dark;
                var bg = dark ? C(0x20, 0x20, 0x20) : C(0xF3, 0xF3, 0xF3);
                var bgInactive = dark ? C(0x1B, 0x1B, 0x1B) : C(0xF6, 0xF6, 0xF6);
                var fg = dark ? C(0xFF, 0xFF, 0xFF) : C(0x10, 0x10, 0x10);
                tb.BackgroundColor = bg;
                tb.InactiveBackgroundColor = bgInactive;
                tb.ForegroundColor = fg;
                tb.InactiveForegroundColor = fg;
                if (dark)
                {
                    tb.ButtonBackgroundColor = bg;
                    tb.ButtonInactiveBackgroundColor = bgInactive;
                    tb.ButtonForegroundColor = fg;
                    tb.ButtonHoverBackgroundColor = C(0x2E, 0x2E, 0x2E);
                    tb.ButtonHoverForegroundColor = fg;
                    tb.ButtonPressedBackgroundColor = C(0x3A, 0x3A, 0x3A);
                    tb.ButtonInactiveForegroundColor = fg;
                }
                else
                {
                    tb.ButtonBackgroundColor = bg;
                    tb.ButtonInactiveBackgroundColor = bgInactive;
                    tb.ButtonForegroundColor = fg;
                    tb.ButtonHoverBackgroundColor = C(0xE5, 0xE5, 0xE5);
                    tb.ButtonHoverForegroundColor = fg;
                    tb.ButtonPressedBackgroundColor = C(0xCC, 0xCC, 0xCC);
                    tb.ButtonInactiveForegroundColor = fg;
                }
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
                var dlg = new UnlockDialog(this, firstRun, hint);
                dlg.XamlRoot = Content.XamlRoot;
                ThemeDialog(dlg);
                string? recovered = null;
                bool recoveredOpen = false; // 本次用取回的主密码开库：打开后引导立即改密
                while (true)
                {
                    if (dlg.ForgotHandled)
                    {
                        dlg.ForgotHandled = false;
                        var fdlg = new ForgotPasswordDialog { XamlRoot = Content.XamlRoot };
                        ThemeDialog(fdlg);
                        await fdlg.ShowAsync();
                        if (!string.IsNullOrEmpty(fdlg.RecoveredMaster))
                            recovered = fdlg.RecoveredMaster;
                    }

                    string pwd;
                    if (recovered != null)
                    {
                        // 已取回主密码：直接尝试开库
                        pwd = recovered;
                        recovered = null;
                        recoveredOpen = true;
                    }
                    else
                    {
                        recoveredOpen = false;
                        var res = await dlg.ShowAsync();
                        if (dlg.ForgotHandled) continue;
                        if (res != ContentDialogResult.Primary)
                        {
                            dlg.ClearSecrets();
                            Close();
                            return;
                        }
                        pwd = dlg.Password;
                        dlg.ClearSecrets(); // 取值后立即擦除明文
                    }

                    if (firstRun && legacyDetected)
                        BackupLegacyVault(); // 旧版库文件移入备份目录，data 纯净后再创建新版库

                    int rc = firstRun
                        ? _store.Setup(VaultBase, pwd)
                        : _store.Open(VaultBase, pwd);

                    if (rc == NativeMethods.KSBOX_OK)
                    {
                        AppPaths.TraceEnabled = _store.GetDiagnostics(); // 同步诊断开关
#if DEBUG
                        if (_store.SetDiagnostics(true) == NativeMethods.KSBOX_OK)
                        {
                            AppPaths.TraceEnabled = true;
                            Trace("debug build: diagnostics auto-enabled");
                        }
                        else
                            Trace("debug build: diagnostics auto-enable failed");
#endif
                        if (firstRun)
                        {
                            _store.Save();
                            // 首次创建密码后引导配置恢复方式（携带刚创建的主密码）
                            await PromptRecoverySetupAsync(pwd);
                        }
                        else if (recoveredOpen)
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
                        pwd = "";
                        break;
                    }

                    pwd = "";
                    // 复用同一对话框重开，错误提示需在 ShowAsync 之前设置
                    dlg.ShowError(rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
                        ? "密码错误，请重试。"
                        : $"打开保险库失败（错误码 {rc}）。");
                }

                LoadCategories();
                RefreshEntryList();
                // 首次数据加载完成后才允许切换动画
                _dataReady = true;
                PlayUnlockIntro(); // 解锁后主界面入场：列表淡入上滑 0.45s
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

        #endregion

        #region 分类

        private void LoadCategories()
        {
            _categories.Clear();
            var list = _store.ListCategories();
            if (list != null) _categories.AddRange(list);
            RefreshCategoryList();
        }

        // 没有未分类条目时不展示「未分类」筛选（_categories 保持完整供映射与下拉使用）
        private void RefreshCategoryList()
        {
            if (_categorySortMode) return; // 排序模式下保持工作副本，不被覆盖
            var uncatCount = (_store.QueryCategory(NativeMethods.UncatId) ?? new()).Count;
            var shown = uncatCount == 0
                ? _categories.Where(c => c.Id != NativeMethods.UncatId).ToList()
                : _categories.ToList();
            var oldIds = new HashSet<long>(_categoryItems.Select(c => c.Id)); // 新建分类入场动画用
            for (int i = 0; i < shown.Count; i++)
            {
                var src = shown[i];
                int ex = IndexOfId(_categoryItems, src.Id, c => c.Id);
                if (ex >= 0)
                {
                    var live = _categoryItems[ex];
                    live.PatchFrom(src);
                    live.IsEditSort = false; // 退出排序模式后清掉行内按钮状态
                    live.CanMoveUp = false;
                    live.CanMoveDown = false;
                    shown[i] = live;
                }
            }
            SyncInPlace(_categoryItems, shown, c => c.Id);
            // 新建分类入场。
            var newIds = shown.Where(c => !oldIds.Contains(c.Id)).Select(c => c.Id).ToHashSet();
            if (newIds.Count > 0)
            {
                long durMs = AppSettings.AlignMsToFrames(AppSettings.ScopeEnterAnimMs);
                CategoryList.UpdateLayout(); // 保证新分类容器已实现
                for (int i = 0; i < CategoryList.Items.Count; i++)
                {
                    if (CategoryList.Items[i] is not NativeMethods.Category cat) continue;
                    if (!newIds.Contains(cat.Id)) continue;
                    if (CategoryList.ContainerFromIndex(i) is not FrameworkElement fe) continue;
                    PlayFadeIn(fe, durMs);
                }
            }
        }

        // 自包含淡入（Storyboard）：结束自动归位，不依赖全局容器动画系统
        private void PlayFadeIn(UIElement target, long ms)
        {
            target.Opacity = 0;
            var sb = new Storyboard();
            var fade = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);
            sb.Completed += (_, _) => target.Opacity = 1; // 兜底：确保永不滞留透明
            sb.Begin();
        }

        private void CategoryList_Loaded(object sender, RoutedEventArgs e)
        {
            SetScope(all: true); // 默认范围为「全部」
        }

        // 新增/重命名分类后仅刷新列表，保留当前范围
        private void ReloadCategoriesKeepScope()
        {
            bool keepAll = _allScope;
            long? keepId = _selectedCategory?.Id;
            LoadCategories();
            if (keepAll)
            {
                SetScope(all: true, scopeSwitch: keepId != null);
                return;
            }
            var match = keepId != null ? _categoryItems.FirstOrDefault(c => c.Id == keepId) : null;
            if (match != null)
            {
                _selectedCategory = match; // 直接用已展示实例，避免选中对象与列表绑定对象不一致
                _allScope = false;
                RefreshScopeVisual();
                RefreshEntryList(); // 重命名/新建分类后条目分类名同步刷新（非切换，不动画）
            }
            else
            {
                SetScope(all: true, scopeSwitch: true);
            }
        }

        private void SetScope(bool all, bool scopeSwitch = false)
        {
            _allScope = all;
            if (all)
            {
                _selectedCategory = null;
                CategoryList.SelectedItem = null;
            }
            RefreshScopeVisual();
            RefreshEntryList(scopeSwitch);
        }

        // 「全部」按钮状态配色：选中主色实底，未选中透明底+次级文字
        private void RefreshScopeVisual()
        {
            if (AllScopeBtn == null) return;
            bool dark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
            Brush? bg, fg;
            if (_allScope)
            {
                bg = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
                    ? Windows.UI.Color.FromArgb(255, 0xE4, 0xD7, 0xFF)
                    : Windows.UI.Color.FromArgb(255, 0x67, 0x50, 0xA4));
                fg = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
                    ? Windows.UI.Color.FromArgb(255, 0x21, 0x00, 0x5D)
                    : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                bg = null;
                fg = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
                    ? Windows.UI.Color.FromArgb(255, 0xC4, 0xC7, 0xC5)
                    : Windows.UI.Color.FromArgb(255, 0x5C, 0x5F, 0x66));
            }
            AllScopeBtn.Background = bg;
            AllScopeBtn.Foreground = fg;
            if (AllScopeIcon != null) AllScopeIcon.Foreground = fg;
            if (AllScopeText != null) AllScopeText.Foreground = fg;
        }

        private void AllScopeBtn_Click(object sender, RoutedEventArgs e)
        {
            SetScope(all: true, scopeSwitch: true);
        }

        #region 分类排序模式

        // 当前界面展示的分类顺序（与 RefreshCategoryList 判定一致）
        private List<NativeMethods.Category> GetCategoryShownOrder()
        {
            var uncatCount = (_store.QueryCategory(NativeMethods.UncatId) ?? new()).Count;
            return uncatCount == 0
                ? _categories.Where(c => c.Id != NativeMethods.UncatId).ToList()
                : _categories.ToList();
        }

        // 排序模式：隐藏重命名/删除，显示上移/下移，并出现保存/取消操作栏
        private void SortToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_categorySortMode) return;
            _categorySortMode = true;
            _sortWorking.Clear();
            _sortWorking.AddRange(GetCategoryShownOrder());
            RefreshSortWorking();
            SortBar.Visibility = Visibility.Visible;
            SortToggleBtn.IsEnabled = false;
            AddCatBtn.IsEnabled = false;
            AllScopeBtn.IsEnabled = false;
            SettingsBtn.IsEnabled = false;
        }

        // 依据新顺序刷新每行的箭头可用性并重绑列表
        private void RefreshSortWorking()
        {
            int n = _sortWorking.Count;
            for (int i = 0; i < n; i++)
            {
                var c = _sortWorking[i];
                c.IsEditSort = true;
                c.CanMoveUp = i > 0 && _sortWorking[i - 1].Id != NativeMethods.UncatId;
                c.CanMoveDown = i < n - 1;
            }
            SyncInPlace(_categoryItems, _sortWorking, c => c.Id);
        }

        private void CatMoveUpBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is NativeMethods.Category cat)
            {
                int i = _sortWorking.IndexOf(cat);
                if (i <= 0) return;
                if (_sortWorking[i - 1].Id == NativeMethods.UncatId) return; // 未分类恒居首位
                var oldTops = CaptureCategoryTops();
                (_sortWorking[i - 1], _sortWorking[i]) = (_sortWorking[i], _sortWorking[i - 1]);
                RefreshSortWorking();
                AnimateCategoryMove(oldTops);
            }
        }

        private void CatMoveDownBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is NativeMethods.Category cat)
            {
                int i = _sortWorking.IndexOf(cat);
                if (i < 0 || i >= _sortWorking.Count - 1) return;
                var oldTops = CaptureCategoryTops();
                (_sortWorking[i], _sortWorking[i + 1]) = (_sortWorking[i + 1], _sortWorking[i]);
                RefreshSortWorking();
                AnimateCategoryMove(oldTops);
            }
        }

        // 保存：按工作顺序回写 store，退出排序模式并刷新
        private async void SortSaveBtn_Click(object sender, RoutedEventArgs e)
        {
            bool uncatPinned = _sortWorking.Count > 0 && _sortWorking[0].Id == NativeMethods.UncatId;
            long rc = NativeMethods.KSBOX_OK;
            for (int i = 0; i < _sortWorking.Count; i++)
            {
                var cat = _sortWorking[i];
                if (cat.Id == NativeMethods.UncatId) continue;
                int pos = uncatPinned ? i : i + 1; // "未分类"占位或整体前移
                rc = _store.MoveCategory(cat.Id, pos);
                if (rc != NativeMethods.KSBOX_OK) break;
            }
            if (rc != NativeMethods.KSBOX_OK)
            {
                await ShowError($"保存排序失败（错误码 {rc}）。");
                return;
            }
            int svc = _store.Save();
            ExitCategorySortMode();
            ReloadCategoriesKeepScope();
            if (svc != NativeMethods.KSBOX_OK)
                await ShowError($"排序已生效，但保存失败（错误码 {svc}），重启后可能丢失。");
        }

        // 取消：不写回 store，直接按原顺序刷新
        private void SortCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            ExitCategorySortMode();
            ReloadCategoriesKeepScope();
        }

        private void ExitCategorySortMode()
        {
            _categorySortMode = false;
            SortBar.Visibility = Visibility.Collapsed;
            SortToggleBtn.IsEnabled = true;
            AddCatBtn.IsEnabled = true;
            AllScopeBtn.IsEnabled = true;
            SettingsBtn.IsEnabled = true;
        }

        #endregion

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = CategoryList.SelectedItem as NativeMethods.Category;
            if (sel == null) return;
            _selectedCategory = sel;
            _allScope = false;
            RefreshScopeVisual();
            RefreshEntryList(scopeSwitch: true);
        }

        private async void AddCatBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InputDialog();
            dlg.Init("请输入分类名称：");
            dlg.XamlRoot = Content.XamlRoot;
            ThemeDialog(dlg);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                var name = dlg.Answer.Trim();
                if (string.IsNullOrEmpty(name)) return;
                long rc = _store.AddCategory(name);
                if (rc == NativeMethods.KSBOX_ERR_DUP)
                    await ShowError("已存在同名分类。");
                else if (rc <= 0)
                    await ShowError($"添加分类失败（错误码 {rc}）。");
                else
                {
                    int svc = _store.Save();
                    if (svc != NativeMethods.KSBOX_OK)
                        await ShowError($"分类已加入内存，但保存失败（错误码 {svc}），重启后可能丢失。");
                    ReloadCategoriesKeepScope();
                }
            }
        }

        private async void RenameCatBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is NativeMethods.Category cat)
            {
                var dlg = new InputDialog();
                dlg.Init("请输入新的分类名称：", cat.Name);
                dlg.XamlRoot = Content.XamlRoot;
                ThemeDialog(dlg);
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    var name = dlg.Answer.Trim();
                    if (string.IsNullOrEmpty(name)) return;
                    long rc = _store.RenameCategory(cat.Id, name);
                    if (rc == NativeMethods.KSBOX_OK)
                    {
                        _store.Save();
                        ReloadCategoriesKeepScope();
                    }
                    else await ShowError($"重命名失败（错误码 {rc}）。");
                }
            }
        }

        private async void DelCatBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is NativeMethods.Category cat)
            {
                var dlg = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "删除分类",
                    Content = $"确定删除分类「{cat.Name}」吗？其下条目会一并删除。",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
                ThemeDialog(dlg);
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    long rc = _store.RemoveCategory(cat.Id);
                    if (rc == NativeMethods.KSBOX_OK)
                    {
                        _store.Save();
                        LoadCategories();
                        SetScope(all: true, scopeSwitch: true);
                    }
                    else await ShowError($"删除失败（错误码 {rc}）。");
                }
            }
        }

        #endregion

        #region 视图与搜索

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            _searchText = sender.Text.Trim();
            RefreshEntryList();
        }

        private void RefreshEntryList(bool scopeSwitch = false)
        {
            // 只有明确的分类切换 + 已就绪时才播过渡动画
            if (scopeSwitch && _dataReady)
            {
                RefreshEntryListAnimated();
                return;
            }
            CancelScopeTransition();
            RefreshEntryListNow();
        }

        // 解锁后主界面入场：分类/条目列表淡入上滑
        private void PlayUnlockIntro()
        {
            long ms = AppSettings.AlignMsToFrames(AppSettings.UnlockIntroAnimMs);
            DialogAnim.PlayFadeUp(CategoryList, ms);
            DialogAnim.PlayFadeUp(EntryList, ms);
        }

        // 动画切换
        private void RefreshEntryListAnimated()
        {
            CancelScopeTransition(); // 快速连续切换：先停掉上一段动画
            int seq = _scopeSeq;
            var snap = CaptureEntrySnap();
            var target = BuildEntryListReused();   // 仅构造目标实例列表，暂不改动展示集合
            _scopeSnap = snap;
            _scopeTarget = target;

            var targetIds = new HashSet<long>(target.Select(x => x.Id));
            var goneIds = snap.Idx.Keys.Where(id => !targetIds.Contains(id)).ToList();

            if (goneIds.Count == 0)
            {
                FinishScopeSwap(seq);
                return;
            }

            // 退场：离场条目向右淡出（滑出自身宽度）；停留条目完全不动。
            long exitMs = AppSettings.AlignMsToFrames(AppSettings.ScopeExitAnimMs);
            var exitAnims = new List<ContainerAnim>();
            for (int i = 0; i < EntryList.Items.Count; i++)
            {
                if (EntryList.Items[i] is not NativeMethods.Entry ent) continue;
                if (EntryList.ContainerFromIndex(i) is not FrameworkElement fe) continue;
                if (!goneIds.Contains(ent.Id)) continue; // 停留条目不动
                double off = Math.Max(fe.ActualWidth, 60);
                exitAnims.Add(new ContainerAnim
                {
                    Fe = fe,
                    Move = true, FromX = 0, ToX = off,
                    Fade = true, FromOpacity = 1f, ToOpacity = 0f,
                    DurationMs = exitMs, EaseIn = true
                });
            }
            if (exitAnims.Count == 0)
            {
                FinishScopeSwap(seq);
                return;
            }
            StartContainerAnimations(exitAnims);

            // 退场播完后接着换数据 + 入场动画：由容器动画完成回调驱动，不另设计时器
            _scopeSwapPending = true;
            _scopeSwapSeq = seq;
        }

        // 退场已播完（容器动画完成回调）：就地换数据，再播入场。
        private void FinishScopeSwap(int seq)
        {
            if (seq != _scopeSeq) return;
            RefreshEntryListNow(_scopeTarget); // 复用动画开始前已构造的目标列表，不再二次重建
            BeginScopeEnter(seq);
        }

        private void BeginScopeEnter(int seq)
        {
            if (seq != _scopeSeq) return;

            var snap = _scopeSnap ?? new EntrySnap();
            long durMs = AppSettings.AlignMsToFrames(AppSettings.ScopeEnterAnimMs); // 对齐整数帧
            StopContainerAnimations();
            // 强制一次布局，
            if (EntryList.Items.Count > 0) EntryList.UpdateLayout();
            ResetListVisuals(EntryList);
            var anims = new List<ContainerAnim>();
            for (int i = 0; i < EntryList.Items.Count; i++)
            {
                if (EntryList.Items[i] is not NativeMethods.Entry ent) continue;
                if (EntryList.ContainerFromIndex(i) is not FrameworkElement fe) continue;

                if (snap.Idx.TryGetValue(ent.Id, out _))
                {
                    // 停留条目：位置不变则完全不动；位置变了则从旧位置滑到新位置
                    if (!snap.Tops.TryGetValue(ent.Id, out double oldTop) || double.IsNaN(oldTop)) continue;
                    double delta = oldTop - TopInList(fe, EntryList);
                    if (Math.Abs(delta) < 0.5) continue; // 位置不变，不动
                    anims.Add(new ContainerAnim
                    {
                        Fe = fe,
                        Move = true, FromY = delta, ToY = 0,
                        DurationMs = durMs,
                        EaseIn = false
                    });
                }
                else
                {
                    // 新分类独有条目：从左侧淡入到正常位置
                    double off = Math.Max(fe.ActualWidth, 60);
                    anims.Add(new ContainerAnim
                    {
                        Fe = fe,
                        Move = true, FromX = -off, ToX = 0,
                        Fade = true, FromOpacity = 0f, ToOpacity = 1f,
                        DurationMs = durMs,
                        EaseIn = false
                    });
                }
            }
            StartContainerAnimations(anims);
        }

        // 新增条目后刷新：旧条目原地不动，新条目从左侧淡入（复用容器动画，不依赖分类切换）
        private void RefreshEntryListWithIntro()
        {
            var snap = CaptureEntrySnap(); // 刷新前旧条目快照（当前展示集合）
            RefreshEntryListNow();
            if (EntryList.Items.Count == 0) return;
            EntryList.UpdateLayout(); // 保证新条目容器已实现
            long durMs = AppSettings.AlignMsToFrames(AppSettings.ScopeEnterAnimMs);
            var anims = new List<ContainerAnim>();
            for (int i = 0; i < EntryList.Items.Count; i++)
            {
                if (EntryList.Items[i] is not NativeMethods.Entry ent) continue;
                if (snap.Idx.ContainsKey(ent.Id)) continue; // 旧条目不动
                if (EntryList.ContainerFromIndex(i) is not FrameworkElement fe) continue;
                double off = Math.Max(fe.ActualWidth, 60);
                anims.Add(new ContainerAnim
                {
                    Fe = fe,
                    Move = true, FromX = -off, ToX = 0,
                    Fade = true, FromOpacity = 0f, ToOpacity = 1f,
                    DurationMs = durMs, EaseIn = false
                });
            }
            StartContainerAnimations(anims);
        }

        // prebuilt：分类切换入场时复用动画开始前已构造的目标列表
        private void RefreshEntryListNow(List<NativeMethods.Entry>? prebuilt = null)
        {
            StopContainerAnimations();   // 数据将就地增删改，容器可能被回收复用：先停掉进行中的容器动画
            ResetListVisuals(EntryList); // 并复位所有行容器的透明度 / 平移残留
            var list = prebuilt ?? BuildEntryListReused();
            ApplyEntryMeta(list);

            // 数据源常驻：仅按 Id 对照做原地面增删改，容器/滚动位置保持不变
            SyncInPlace(_entryItems, list, e => e.Id);
            RefreshCategoryList(); // 未分类条目数变化时同步左侧筛选项

            bool empty = list.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = empty
                ? (string.IsNullOrEmpty(_searchText)
                    ? (_allScope ? "保险库还是空的，点击「新增条目」开始吧" : $"「{_selectedCategory!.Name}」分类下暂无条目")
                    : "没有匹配的条目")
                : "";

            string scope = _allScope ? "全部" : _selectedCategory!.Name;
            StatusText.Text = string.IsNullOrEmpty(_searchText)
                ? $"{scope} · 共 {list.Count} 条"
                : $"{scope} · 搜索「{_searchText}」 · {list.Count} 条";
        }

        // 查询当前范围 → 搜索过滤 → 同 Id 复用已展示实例
        private List<NativeMethods.Entry> BuildEntryListReused()
        {
            List<NativeMethods.Entry> baseList = _allScope
                ? (_store.QueryAll() ?? new())
                : (_store.QueryCategory(_selectedCategory!.Id) ?? new());

            List<NativeMethods.Entry> list = baseList;
            if (!string.IsNullOrEmpty(_searchText))
            {
                list = baseList
                    .Where(x => (x.Note ?? "").Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                             || (x.Account ?? "").Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // 建立 Id→索引 映射，避免逐条 O(n) 扫描
            var liveIdx = new Dictionary<long, int>(_entryItems.Count);
            for (int i = 0; i < _entryItems.Count; i++) liveIdx[_entryItems[i].Id] = i;
            for (int i = 0; i < list.Count; i++)
            {
                var ent = list[i];
                if (liveIdx.TryGetValue(ent.Id, out int ex))
                {
                    var live = _entryItems[ex];
                    live.PatchFrom(ent); // 就地更新数据，仅触发变动属性
                    list[i] = live;
                }
            }
            return list;
        }

        // 行内展示字段：多分类名、恢复密钥、排序箭头可用性（全部/分类视图均可用）
        private void ApplyEntryMeta(List<NativeMethods.Entry> list)
        {
            var catNames = new Dictionary<long, string> { [NativeMethods.UncatId] = "未分类" };
            foreach (var c in _categories) catNames[c.Id] = c.Name;
            for (int i = 0; i < list.Count; i++)
            {
                var ent = list[i];
                ent.CategoryName = ent.CategoryIds.Count == 0
                    ? "未分类"
                    : string.Join("、", ent.CategoryIds.Select(id =>
                        catNames.TryGetValue(id, out var n) ? n : "未分类"));
                ent.Recovery = _store.GetRecovery(ent.Id); // 恢复密钥逐条解密填充（瞬时）
                ent.CanMoveUp = i > 0;
                ent.CanMoveDown = i < list.Count - 1;
            }
        }

        // 分类 id 列表 → 展示名
        private string CategoryNamesOf(NativeMethods.Entry ent)
        {
            if (ent.CategoryIds == null || ent.CategoryIds.Count == 0) return "未分类";
            var names = new List<string>();
            foreach (var id in ent.CategoryIds)
            {
                var m = _categories.FirstOrDefault(c => c.Id == id);
                names.Add(m?.Name ?? "未分类");
            }
            return string.Join("、", names);
        }

        #endregion

        #region 条目操作

        private async void AddEntryBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new EntryDialog();
            // 在某个分类下新增时预选该分类；全部视图不预选（保存时归入未分类）
            dlg.Init(_store, _categories, null, _allScope ? null : _selectedCategory?.Id);
            dlg.XamlRoot = Content.XamlRoot;
            ThemeDialog(dlg);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                long rc = _store.AddEntry(dlg.CategoryIds, dlg.Account, dlg.Password, dlg.Note);
                var recovery = dlg.Recovery;
                dlg.ClearSecrets(); // 取走数据后立即擦除明文
                if (rc > 0)
                {
                    _store.SetRecovery(rc, recovery);
                    int svc = _store.Save();
                    LoadCategories(); // 对话框内可能新建了分类
                    RefreshEntryListWithIntro(); // 新条目左侧淡入
                    if (svc != NativeMethods.KSBOX_OK)
                        await ShowError($"条目已加入内存，但保存失败（错误码 {svc}），重启后可能丢失。");
                }
                else await ShowError($"新增失败（错误码 {rc}）。");
            }
        }

        private void EntryList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
            => EntryQueryBtn_Click(sender, e);

        // 条目排序：上移/下移即时生效。
        // 动画优先，动画完成后再写文件，避免 I/O 阻塞。
        private async void EntryMoveUpBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not NativeMethods.Entry row) return;
            int i = IndexOfId(_entryItems, row.Id, e => e.Id);
            if (i <= 0) return;
            await MoveEntryAsync(row, i - 1);
        }

        private async void EntryMoveDownBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not NativeMethods.Entry row) return;
            int i = IndexOfId(_entryItems, row.Id, e => e.Id);
            if (i < 0 || i >= _entryItems.Count - 1) return;
            await MoveEntryAsync(row, i + 1);
        }

        private Task MoveEntryAsync(NativeMethods.Entry row, int to)
        {
            var oldTops = CaptureEntryTops();
            // 先就地移动 + 动画，动画完成后才写文件
            MoveEntryLocal(row.Id, to);
            AnimateEntryMove(oldTops, async () =>
            {
                int rc = _allScope
                    ? _store.MoveAllEntry(row.Id, to)
                    : (_selectedCategory is { } cat ? _store.MoveEntry(row.Id, cat.Id, to) : NativeMethods.KSBOX_OK);
                if (rc != NativeMethods.KSBOX_OK)
                {
                    await ShowError($"排序失败（错误码 {rc}）。");
                    return;
                }
                int svc = _store.Save();
                if (svc != NativeMethods.KSBOX_OK)
                    await ShowError($"排序已生效，但保存失败（错误码 {svc}），重启后可能丢失。");
            });
            return Task.CompletedTask;
        }

        // 把数据源中的一条就地移到新位置，并只刷新受影响行的上/下移箭头可用性
        private void MoveEntryLocal(long id, int to)
        {
            int i = IndexOfId(_entryItems, id, e => e.Id);
            if (i < 0 || i >= _entryItems.Count) return;
            if (to < 0) to = 0;
            if (to >= _entryItems.Count) to = _entryItems.Count - 1;
            _entryItems.Move(i, to);
            int first = Math.Min(i, to), last = Math.Max(i, to);
            for (int k = first; k <= last; k++)
            {
                var e = _entryItems[k];
                e.CanMoveUp = k > 0;
                e.CanMoveDown = k < _entryItems.Count - 1;
            }
        }

        private NativeMethods.Entry? TryGetRow(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is NativeMethods.Entry row) return row;
            return EntryList.SelectedItem as NativeMethods.Entry;
        }

        // 详情模态窗：TextBlock 禁选防划词复制，右侧提供复制按钮
        private async void EntryQueryBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = TryGetRow(sender, e);
            if (row == null) return;
            var full = _store.GetEntry(row.Id);
            if (full == null) { await ShowError("读取条目失败。"); return; }

            var dlg = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children = { MakeField("分类", CategoryNamesOf(full), allowCopy: true),
                                 MakeField("账号", full.Account, allowCopy: true),
                                 MakeField("密码", full.Password, allowCopy: true),
                                 MakeField("备注", full.Note, allowCopy: true) }
                },
                CloseButtonText = "关闭"
            };
            ThemeDialog(dlg);
            // 关闭后卸载内容子树并置空明文字段，供 GC 回收
            NativeMethods.Entry entry = full;
            dlg.Closed += (_, _) =>
            {
                entry.Password = "";
                entry.Account = "";
                entry.Note = "";
                dlg.Content = new Grid();
            };
            await dlg.ShowAsync();
        }

        private async void EntryRecoveryBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = TryGetRow(sender, e);
            if (row == null) return;
            var dlg = new RecoveryDialog();
            dlg.Init(_store, row.Id, row.CategoryName, row.Account);
            dlg.XamlRoot = Content.XamlRoot;
            ThemeDialog(dlg);
            await dlg.ShowAsync();
        }

        private async void EntryEditBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = TryGetRow(sender, e);
            if (row == null) return;
            var full = _store.GetEntry(row.Id);
            if (full == null) { await ShowError("读取条目失败。"); return; }
            full.Recovery = _store.GetRecovery(full.Id); // GetEntry 不携带恢复密钥

            var dlg = new EntryDialog();
            dlg.Init(_store, _categories, full);
            dlg.XamlRoot = Content.XamlRoot;
            ThemeDialog(dlg);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                long rc = _store.UpdateEntry(row.Id, dlg.CategoryIds, dlg.Account, dlg.Password, dlg.Note);
                var recovery = dlg.Recovery;
                dlg.ClearSecrets(); // 取走数据后立即擦除明文
                if (rc == NativeMethods.KSBOX_OK)
                {
                    _store.SetRecovery(row.Id, recovery);
                    int svc = _store.Save();
                    LoadCategories();
                    RefreshEntryList();
                    if (svc != NativeMethods.KSBOX_OK)
                        await ShowError($"条目已更新，但保存失败（错误码 {svc}），重启后可能丢失。");
                }
                else await ShowError($"保存失败（错误码 {rc}）。");
            }
        }

        private async void EntryDelBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = TryGetRow(sender, e);
            if (row == null) return;
            string delLabel = !string.IsNullOrWhiteSpace(row.Account) ? row.Account
                : !string.IsNullOrWhiteSpace(row.CategoryName) ? row.CategoryName : "该";
            var dlg = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "删除条目",
                Content = $"确定删除「{delLabel}」这条记录吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            ThemeDialog(dlg);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                long rc = _store.RemoveEntry(row.Id);
                if (rc == NativeMethods.KSBOX_OK) { _store.Save(); RefreshEntryList(); }
                else await ShowError($"删除失败（错误码 {rc}）。");
            }
        }

        #endregion

        #region 设置

        private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsDialog();
            dlg.Init(_store, ApplyTheme, WinRT.Interop.WindowNative.GetWindowHandle(this), () =>
            {
                LoadCategories();
                RefreshEntryList();
            });
            dlg.XamlRoot = Content.XamlRoot;
            ThemeDialog(dlg);
            await dlg.ShowAsync();
        }

        #endregion

        #region 辅助

        // 只读字段：全部允许划词复制
        private StackPanel MakeField(string label, string value, bool allowCopy)
        {
            var valueText = new TextBlock
            {
                Text = string.IsNullOrEmpty(value) ? "(空)" : value,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            valueText.DoubleTapped += (_, e) => e.Handled = true; // 禁止双击自动选词

            var field = new StackPanel { Spacing = 4 };
            field.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            if (allowCopy)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(valueText, 0);
                var copy = new Button { Content = "复制", VerticalAlignment = VerticalAlignment.Top };
                string textToCopy = value;
                copy.Click += async (_, _) =>
                {
                    var pkg = new DataPackage();
                    pkg.SetText(textToCopy);
                    Clipboard.SetContent(pkg);
                    copy.Content = "已复制";
                    await Task.Delay(1200);
                    copy.Content = "复制";
                };
                Grid.SetColumn(copy, 1);
                row.Children.Add(valueText);
                row.Children.Add(copy);
                field.Children.Add(row);
            }
            else
            {
                field.Children.Add(valueText);
            }
            return field;
        }

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

        #region 动画

        // 记录每个可见容器相对列表顶部的偏移
        private Dictionary<long, double> CaptureTops(ListViewBase list, Func<object, long> idOf)
        {
            var map = new Dictionary<long, double>();
            for (int i = 0; i < list.Items.Count; i++)
            {
                var item = list.Items[i];
                if (item != null && list.ContainerFromIndex(i) is FrameworkElement fe)
                    map[idOf(item)] = TopInList(fe, list);
            }
            return map;
        }

        private Dictionary<long, double> CaptureEntryTops() => CaptureTops(EntryList, x => ((NativeMethods.Entry)x).Id);
        private Dictionary<long, double> CaptureCategoryTops() => CaptureTops(CategoryList, x => ((NativeMethods.Category)x).Id);

        private static double TopInList(FrameworkElement element, ListViewBase list)
            => element.TransformToVisual((UIElement)list).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

        // 切分类前的完整快照：全部条目 Id↔索引 + 可见容器的顶部偏移（容器未实现时为 NaN）
        private EntrySnap CaptureEntrySnap()
        {
            var snap = new EntrySnap();
            for (int i = 0; i < EntryList.Items.Count; i++)
            {
                if (EntryList.Items[i] is not NativeMethods.Entry ent) continue;
                snap.Idx[ent.Id] = i;
                double top = double.NaN;
                if (EntryList.ContainerFromIndex(i) is FrameworkElement fe)
                    top = TopInList(fe, EntryList);
                snap.Tops[ent.Id] = top;
            }
            return snap;
        }

        // 容器动画：定时器逐帧直接赋值 Opacity / 行内文本透明度 / Translation。
        private static void ResetContainerVisual(FrameworkElement fe)
        {
            fe.Opacity = 1f;
            fe.Translation = Vector3.Zero;
        }

        private static void ResetListVisuals(ListViewBase list)
        {
            for (int i = 0; i < list.Items.Count; i++)
                if (list.ContainerFromIndex(i) is FrameworkElement fe) ResetContainerVisual(fe);
        }

        private void StopContainerAnimations()
        {
            if (_containerAnimTimer != null)
            {
                _containerAnimTimer.Stop();
                _containerAnimTimer.Tick -= OnContainerAnimTick;
            }
            _containerAnims.Clear();
        }

        private void StartContainerAnimations(List<ContainerAnim> anims)
        {
            StopContainerAnimations();
            _containerAnims.AddRange(anims);
            if (_containerAnims.Count == 0) return;

            _containerAnimStart = Environment.TickCount64;
            foreach (var a in _containerAnims)
            {
                if (a.Move) a.Fe.Translation = new Vector3((float)a.FromX, (float)a.FromY, 0f);
                if (a.Fade) a.Fe.Opacity = a.FromOpacity;
            }

            _containerAnimTimer ??= DispatcherQueue.CreateTimer();
            _containerAnimTimer.Tick -= OnContainerAnimTick;
            _containerAnimTimer.Tick += OnContainerAnimTick;
            int fps = Math.Max(1, AppSettings.FrameRate);
            _containerAnimTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
            _containerAnimTimer.IsRepeating = true;
            _containerAnimTimer.Start();
        }

        private void OnContainerAnimTick(DispatcherQueueTimer sender, object args)
        {
            if (_containerAnims.Count == 0)
            {
                sender.Stop();
                return;
            }
            long now = Environment.TickCount64;
            long elapsed = now - _containerAnimStart;
            bool allDone = true;
            foreach (var a in _containerAnims)
            {
                double p = a.DurationMs <= 0
                    ? 1
                    : Math.Clamp((double)elapsed / a.DurationMs, 0, 1);
                if (p < 1) allDone = false;
                // 三次缓动用乘法展开
                double q = 1 - p;
                double t = a.EaseIn ? p * p * p : 1 - q * q * q;
                if (a.Move)
                    a.Fe.Translation = new Vector3(
                        (float)(a.FromX + (a.ToX - a.FromX) * t),
                        (float)(a.FromY + (a.ToY - a.FromY) * t), 0f);
                if (a.Fade)
                    a.Fe.Opacity = (float)(a.FromOpacity + (a.ToOpacity - a.FromOpacity) * t);
            }
            if (allDone)
            {
                sender.Stop();
                _containerAnims.Clear();
                if (_scopeSwapPending)
                {
                    _scopeSwapPending = false;
                    FinishScopeSwap(_scopeSwapSeq);
                }
                else if (_moveAnimCompleted != null)
                {
                    var cb = _moveAnimCompleted;
                    _moveAnimCompleted = null;
                    cb.Invoke();
                }
            }
        }

        // 取消进行中的切换动画。
        private void CancelScopeTransition()
        {
            _scopeSeq++;
            _scopeSwapPending = false;
            StopContainerAnimations();
            ResetListVisuals(EntryList);
            if (_moveAnimCompleted != null)
            {
                var cb = _moveAnimCompleted;
                _moveAnimCompleted = null;
                cb.Invoke();
            }
        }

        // 排序移动动画。
        private void AnimateMove(ListViewBase list, Dictionary<long, double> oldTops,
            Func<object, long> idOf, Action? completed = null)
        {
            // 目标时长固定，启动时对齐到当前帧率的整数帧。
            int ms = (int)AppSettings.AlignMsToFrames(AppSettings.SortMoveAnimMs);
            StopContainerAnimations();
            ResetListVisuals(list);

            var anims = new List<ContainerAnim>();
            for (int i = 0; i < list.Items.Count; i++)
            {
                var item = list.Items[i];
                if (item == null) continue;
                if (!oldTops.TryGetValue(idOf(item), out double oldTop) || double.IsNaN(oldTop)) continue;
                if (list.ContainerFromIndex(i) is not FrameworkElement fe) continue;
                double delta = oldTop - TopInList(fe, list);
                if (Math.Abs(delta) < 0.5) continue;
                anims.Add(new ContainerAnim
                {
                    Fe = fe,
                    Move = true,
                    FromY = delta,
                    ToY = 0,
                    DurationMs = ms,
                    EaseIn = false
                });
            }
            _moveAnimCompleted = completed;
            if (anims.Count > 0)
                StartContainerAnimations(anims);
            else
            {
                // 无需动画，直接回调
                _moveAnimCompleted = null;
                completed?.Invoke();
            }
        }

        private void AnimateEntryMove(Dictionary<long, double> oldTops, Action? completed = null)
        {
            EntryList.UpdateLayout(); // 强制布局就绪，使采样到的新位置准确
            AnimateMove(EntryList, oldTops, x => ((NativeMethods.Entry)x).Id, completed);
        }
        private void AnimateCategoryMove(Dictionary<long, double> oldTops)
        {
            CategoryList.UpdateLayout(); // 强制布局就绪，使采样到的新位置准确
            AnimateMove(CategoryList, oldTops, x => ((NativeMethods.Category)x).Id);
        }

        #endregion
    }
}
