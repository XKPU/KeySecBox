using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KeySecBox;

namespace KeySecBox.Views;

/// <summary>
/// 保险库页（WPF 移植版，源：KeySecBox.UI/VaultPage.xaml[.cs]，WinUI 3）。
///
/// 逻辑与 WinUI 版逐条对齐，仅替换 WinUI 专有 API：
///   - Page → UserControl；
///   - Grid.ColumnSpacing / StackPanel.Spacing → 子项 Margin；
///   - UIElement.Translation(Vector3) → TranslateTransform；
///   - Storyboard.SetTarget 目标必须是 FrameworkElement（Transform 不是 FE）；
///   - WriteableBitmap 快照 + Composition 渲染不移植，改写 <c>VaultSnapshot.html</c>
///     并由默认浏览器打开（主机不可用时回退剪贴板 + 记事本）；
///   - ContentDialog → 本项目的 ContentDialogBase / MessageDialog；
///   - DataPackage 剪贴板 → System.Windows.Clipboard。
/// </summary>
public partial class VaultPage : UserControl
{
    // 保险库 basename
    private static readonly string VaultBase = AppPaths.VaultBase;

    // 由 MainWindow 解锁成功后注入（窗口侧负责开库与本页生命周期）
    private NativeMethods.Store _store = null!;

    // 可观察集合：作为分类下拉的 ItemsSource，LoadCategories 就地更新时能通知绑定刷新
    // （普通 List 的 Clear/AddRange 不发通知，会导致下拉框显示已删除的分类）
    private readonly ObservableCollection<NativeMethods.Category> _categories = new();
    private NativeMethods.Category? _selectedCategory;
    private bool _allScope = true;
    private string _searchText = "";

    // 分类排序模式：仅改内存工作副本，点"保存"才写回 store
    private readonly List<NativeMethods.Category> _sortWorking = new();
    private bool _categorySortMode;

    // 范围是否已完成首次初始化：页面实例被窗口缓存复用，Loaded 会反复触发，
    // 若无此标记则每次切回库页面都会把分类筛选重置为「全部」
    private bool _scopeInitialized;

    // 上一次已渲染的分类 id 集合：用于判定「新建分类」并播放入场动画
    private HashSet<long> _knownCategoryIds = new();

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
    // 排序移动 = TranslateTransform 平移。
    private sealed class ContainerAnim
    {
        public FrameworkElement Fe = null!;
        public long DurationMs;
        public bool EaseIn;
        public bool Move;                  // 平移：排序/停留条目垂直滑动、出入场水平滑动
        public double FromX, ToX;          // 水平平移（新条目左侧淡入、离场条目向右淡出）
        public double FromY, ToY;          // 垂直平移（排序滑动、停留条目位置滑动）
        public bool Fade;                  // 整框透明度
        public double FromOpacity, ToOpacity;
    }
    private readonly List<ContainerAnim> _containerAnims = new();
    private DispatcherTimer? _containerAnimTimer;
    private long _containerAnimStart;

    #region 初始化

    public VaultPage()
    {
        InitializeComponent();
        CategoryList.ItemsSource = _categoryItems;
        EntryList.ItemsSource = _entryItems;

        // 系统明暗切换时「全部」高亮跟随实际生效明暗（标题栏配色由窗口侧处理）
        RootGrid.Loaded += (_, _) =>
        {
            try { RefreshScopeVisual(); } catch { }
        };
    }

    // 由窗口注入保险库并加载数据
    internal void Init(NativeMethods.Store store)
    {
        _store = store;
        _scopeInitialized = true; // 初始范围由本方法设定，Loaded 不再重复重置
        SetScope(all: true);
        _dataReady = true; // 首次数据加载完成后才允许切换动画
        PlayUnlockIntro(); // 解锁后主界面入场：列表淡入上滑（整表淡入，故无需逐行淡入）
    }

    // 外部数据变更后刷新
    internal void Refresh()
    {
        LoadCategories();
        RefreshEntryList();
    }

    #region 详情 / 恢复密钥展开区

    // 展开状态按条目 id 记录：允许多个条目的详情/恢复密钥同时展开
    private readonly Dictionary<long, bool> _detailOpen = new();
    private readonly Dictionary<long, bool> _recoveryOpen = new();
    // 恢复密钥对话框按条目 id 缓存：窗口缓存复用，重复「恢复」复用同一实例（不再二次 Init）
    private readonly Dictionary<long, RecoveryDialog> _recoveryDialogs = new();

    // 找所属条目容器（WPF 的容器类型是 ListBoxItem，且沿逻辑/视觉树向上查找都可行）
    private static FrameworkElement? FindEntryHost(DependencyObject? start)
    {
        var node = start;
        while (node != null)
        {
            if (node is ListBoxItem item) return item;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // 按 x:Name 查找子元素
    private static T? FindNamed<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T fe && fe.Name == name) return fe;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
            if (FindNamed<T>(VisualTreeHelper.GetChild(parent, i), name) is { } hit) return hit;
        return null;
    }

    private static void FillFields(StackPanel panel, IEnumerable<(string Label, string Value)> fields)
    {
        panel.Children.Clear();
        foreach (var (label, value) in fields)
            panel.Children.Add(MakeField(label, value, allowCopy: true));
    }

    // 同步展开区
    private static void SyncEntryExpand(FrameworkElement host)
    {
        var expand = FindNamed<Border>(host, "EntryExpand");
        if (expand == null) return;

        var detail = FindNamed<StackPanel>(host, "EntryDetailPanel");
        var recovery = FindNamed<StackPanel>(host, "EntryRecoveryPanel");
        var edit = FindNamed<StackPanel>(host, "EntryEditPanel");

        bool any = detail?.Visibility == Visibility.Visible
            || recovery?.Visibility == Visibility.Visible
            || edit?.Visibility == Visibility.Visible;

        if (any)
        {
            expand.Visibility = Visibility.Visible;
            SlideDown(expand);
        }
        else
        {
            expand.Visibility = Visibility.Collapsed;
        }
    }

    // 收起条目展开内容
    private static void CollapseEntry(FrameworkElement host)
    {
        foreach (var name in new[] { "EntryDetailPanel", "EntryRecoveryPanel", "EntryEditPanel" })
            if (FindNamed<StackPanel>(host, name) is { } p)
            {
                p.Visibility = Visibility.Collapsed;
                p.Children.Clear();
            }
        if (FindNamed<Border>(host, "EntryExpand") is { } expand)
            expand.Visibility = Visibility.Collapsed;
    }

    // 供外部（刷新列表）清理展开状态
    internal void ClearExpandState()
    {
        _detailOpen.Clear();
        _recoveryOpen.Clear();
    }

    #endregion
    #endregion

    #region 新建 / 编辑上滑区

    private bool _editMode;
    private long _editingId = -1;

    // 上滑出新建输入区
    internal void BeginCreateEntry()
    {
        // 面板已展开且处于新建模式：只把焦点移回输入框，
        // 绝不清空字段（右下角浮动按钮在面板展开时仍可点击，重复点击会丢弃已输入内容）
        if (CreateArea.Visibility == Visibility.Visible && !_editMode)
        {
            NewAccountBox.Focus();
            return;
        }

        _editMode = false;
        _editingId = -1;
        CreateTitle.Text = "新建条目";

        NewCategoryCombo.ItemsSource = _categories;
        NewAccountBox.Text = "";
        NewPasswordBox.Password = "";
        NewNoteBox.Text = "";
        CreateErrorText.Visibility = Visibility.Collapsed;

        // 在某个分类下新增时预选该分类；全部视图不预选
        if (!_allScope && _selectedCategory != null)
            NewCategoryCombo.SelectedValue = _selectedCategory.Id;
        else
            NewCategoryCombo.SelectedIndex = -1;

        CreateArea.Visibility = Visibility.Visible;
        SlideUp(CreateAreaTransform, CreateArea);
        NewAccountBox.Focus();
    }

    // 构建条目内编辑表单
    private void BuildEditForm(StackPanel panel, FrameworkElement host, long id,
        string account, string password, string note, long? categoryId)
    {
        panel.Children.Clear();

        var accBox = new TextBox { Text = account, VerticalContentAlignment = VerticalAlignment.Center };
        var pwdBox = new PasswordBox { Password = password };
        var noteBox = new TextBox { Text = note, AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        var combo = new ComboBox
        {
            Width = 180, DisplayMemberPath = "Name", SelectedValuePath = "Id",
            VerticalContentAlignment = VerticalAlignment.Center,
            ItemsSource = _categories
        };
        if (categoryId.HasValue) combo.SelectedValue = categoryId.Value;

        var err = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("SystemFillColorCriticalBrush")
        };

        panel.Children.Add(Labeled("账户名", accBox));
        panel.Children.Add(Labeled("密码", pwdBox));
        panel.Children.Add(Labeled("备注", noteBox));
        panel.Children.Add(Labeled("分类", combo));

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "保存", Padding = new Thickness(14, 5, 14, 5) };
        try { save.Style = (Style)FindResource("AccentButtonStyle"); } catch { }
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        bar.Children.Add(save);
        bar.Children.Add(cancel);
        panel.Children.Add(bar);
        panel.Children.Add(err);

        save.Click += async (_, _) =>
        {
            string acc = accBox.Text.Trim();
            if (acc.Length == 0)
            {
                err.Text = "账户名不能为空。";
                err.Visibility = Visibility.Visible;
                return;
            }
            var cats = new List<long>();
            if (combo.SelectedValue is long cid && cid > 0) cats.Add(cid);
            if (cats.Count == 0) cats.Add(NativeMethods.UncatId);

            long rc = _store.UpdateEntry(id, cats, acc, pwdBox.Password, noteBox.Text.Trim());
            if (rc != NativeMethods.KSBOX_OK)
            {
                err.Text = $"保存失败（错误码 {rc}）。";
                err.Visibility = Visibility.Visible;
                return;
            }
            int svc = _store.Save();
            pwdBox.Password = ""; // 明文立即出栈
            panel.Visibility = Visibility.Collapsed;
            panel.Children.Clear();
            SyncEntryExpand(host);
            LoadCategories();
            RefreshEntryList();
            if (svc != NativeMethods.KSBOX_OK)
                await ShowError($"条目已更新，但保存失败（错误码 {svc}），重启后可能丢失。");
        };

        cancel.Click += (_, _) =>
        {
            pwdBox.Password = "";
            panel.Visibility = Visibility.Collapsed;
            panel.Children.Clear();
            SyncEntryExpand(host);
        };
    }

    private static StackPanel Labeled(string label, UIElement input)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x88, 0x88, 0x88))
        });
        sp.Children.Add(input);
        return sp;
    }

    private async void CreateSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string account = NewAccountBox.Text.Trim();
        if (account.Length == 0)
        {
            CreateErrorText.Text = "账户名不能为空。";
            CreateErrorText.Visibility = Visibility.Visible;
            return;
        }

        var cats = new List<long>();
        if (NewCategoryCombo.SelectedValue is long cid && cid > 0) cats.Add(cid);
        if (cats.Count == 0) cats.Add(NativeMethods.UncatId);

        string password = NewPasswordBox.Password;
        string note = NewNoteBox.Text.Trim();

        if (_editMode)
        {
            long rc = _store.UpdateEntry(_editingId, cats, account, password, note);
            if (rc != NativeMethods.KSBOX_OK) { await ShowError($"保存失败（错误码 {rc}）。"); return; }
        }
        else
        {
            if (_store.AddEntry(cats, account, password, note) <= 0)
            {
                await ShowError("新增条目失败。");
                return;
            }
        }

        int svc = _store.Save();
        bool wasEdit = _editMode; // HideCreateArea 会复位 _editMode，先捕获
        NewPasswordBox.Password = ""; // 明文立即出栈
        HideCreateArea();
        LoadCategories();
        // 新增时让新条目从左侧淡入（旧条目原地不动）；编辑时原地刷新即可
        if (wasEdit) RefreshEntryList();
        else RefreshEntryListWithIntro();

        if (svc != NativeMethods.KSBOX_OK)
            await ShowError($"条目已保存，但写入失败（错误码 {svc}），重启后可能丢失。");
    }

    private void CreateCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        NewPasswordBox.Password = "";
        HideCreateArea(); // 由下往上滑入，关闭时向下滑出
    }

    // 关闭：先向下滑出，动画结束再隐藏并复位
    private void HideCreateArea()
    {
        if (CreateArea.Visibility != Visibility.Visible)
        {
            FinishHideCreateArea();
            return;
        }
        SlideDownOut(CreateAreaTransform, CreateArea, FinishHideCreateArea);
    }

    private void FinishHideCreateArea()
    {
        _editMode = false;
        _editingId = -1;
        CreateArea.Visibility = Visibility.Collapsed;
        CreateArea.Opacity = 1;
        CreateAreaTransform.Y = 0;
    }

    #endregion

    #region 动画基元

    // 淡入 / 淡出到指定透明度
    private static void FadeTo(UIElement element, double to)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180))
        };
        Storyboard.SetTarget(anim, element);
        Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(anim);
        sb.Begin();
    }

    // 向下展开：条目展开区未预置命名变换，此处按需创建
    private static void SlideDown(UIElement element)
    {
        var transform = element.RenderTransform as TranslateTransform;
        if (transform == null)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }
        SlideDown(transform, element);
    }

    // 向下展开（详情 / 恢复密钥）
    // 注意：WPF 的 Storyboard 目标必须是 FrameworkElement，Transform 不是，
    // 故改为在具名变换上直接 BeginAnimation（WinUI 版用 Storyboard 设置 Transform 目标）。
    private static void SlideDown(TranslateTransform transform, UIElement element)
    {
        var move = new DoubleAnimation
        {
            From = -14,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(TranslateTransform.YProperty, move);

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(280)) };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // 由下往上滑入（新建条目面板）：起始位移取面板自身高度，形成从底部滑入的观感
    private static void SlideUp(TranslateTransform transform, UIElement element)
    {
        double from = element is FrameworkElement { ActualHeight: > 0 } fe ? fe.ActualHeight + 16 : 240;

        var move = new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(340)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(TranslateTransform.YProperty, move);

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(300)) };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // 由上往下滑出（关闭）：动画结束后再真正隐藏
    private static void SlideDownOut(TranslateTransform transform, UIElement element, Action onCompleted)
    {
        double to = element is FrameworkElement { ActualHeight: > 0 } fe ? fe.ActualHeight + 16 : 240;

        var sb = new Storyboard();

        var move = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(move, element);
        Storyboard.SetTargetProperty(move,
            new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        sb.Children.Add(move);

        var fade = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(240)) };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(fade);

        sb.Completed += (_, _) => onCompleted();
        sb.Begin();
    }

    // 详情内容向左滑出
    private static void SlideOutLeft(TranslateTransform transform)
    {
        var move = new DoubleAnimation
        {
            To = -320,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        transform.BeginAnimation(TranslateTransform.XProperty, move);
    }

    // 详情内容滑回原位
    private static void SlideBackCenter(TranslateTransform transform)
    {
        var move = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(TranslateTransform.XProperty, move);
    }

    // 编辑表单自右侧滑入
    private static void SlideInRight(TranslateTransform transform, UIElement element)
    {
        var move = new DoubleAnimation
        {
            From = 320,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(TranslateTransform.XProperty, move);

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(300)) };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    #endregion

    // 主题由窗口统一切换（窗口侧替换配色字典）；本页只依赖已生效的 DynamicResource，
    // 故此处仅同步「全部」按钮的明暗配色，无需再设置主题。
    internal void ApplyTheme(ThemeMode mode) => RefreshScopeVisual();

    // ContentDialog 弹出不会继承窗口主题（WinUI）；WPF 的 ContentDialogBase 是独立 Window，
    // 需要显式把窗口字体/属主传下去。原 ThemeDialog 的 RequestedTheme + CornerRadius
    // 由对话框自身模板与 AppSettings.DialogCornerRadius 承担，这里只做属主与字体对齐。
    private void ThemeDialog(Window dlg)
    {
        try
        {
            dlg.Owner = OwnerWindow();
            dlg.FontFamily = FontFamily;
        }
        catch { }
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

    // 实际生效明暗：由窗口侧写入的配色字典标识判定，再回落系统注册表
    private ElementTheme ResolveEffectiveTheme(ThemeMode mode)
    {
        if (mode == ThemeMode.Dark) return ElementTheme.Dark;
        if (mode == ThemeMode.Light) return ElementTheme.Light;
        return IsSystemLight() ? ElementTheme.Light : ElementTheme.Dark;
    }

    // WPF 版用本枚举承担 WinUI ElementTheme 的角色（仅本页内部使用）
    private enum ElementTheme { Default, Light, Dark }

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

    #region 解锁

    private static void Trace(string msg)
    {
        if (!AppPaths.TraceEnabled) return; // 仅诊断模式下记录
        try { File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    #endregion

    #region 分类

    private void LoadCategories()
    {
        _categories.Clear();
        var list = _store.ListCategories();
        if (list != null)
            foreach (var c in list) _categories.Add(c);
        RefreshCategoryList();
    }

    // 没有未分类条目时不展示「未分类」筛选（_categories 保持完整供映射与下拉使用）
    private void RefreshCategoryList()
    {
        // 新建分类入场动画的基线：与 store 的差集，而非与 UI 展示集合做差。
        // 排序模式会提前返回而不更新展示集合，若以 _categoryItems 为基线会错位，
        // 导致新分类不播淡入或已存在的行重复播放淡入。
        var knownIds = _knownCategoryIds;
        var nowIds = _categories.Select(c => c.Id).ToHashSet();
        _knownCategoryIds = nowIds;

        if (_categorySortMode) return; // 排序模式下保持工作副本，不被覆盖

        var uncatCount = (_store.QueryCategory(NativeMethods.UncatId) ?? new()).Count;
        var shown = uncatCount == 0
            ? _categories.Where(c => c.Id != NativeMethods.UncatId).ToList()
            : _categories.ToList();
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
        // 新建分类入场：仅当本次确有新的分类 id 出现。
        // 首次加载（knownIds 为空）不逐行淡入——整体入场动画由 PlayUnlockIntro 负责，
        // 逐行淡入会与之叠加。
        var isFirstLoad = knownIds.Count == 0;
        var newIds = isFirstLoad ? new HashSet<long>() : nowIds.Where(id => !knownIds.Contains(id)).ToHashSet();
        if (newIds.Count > 0)
        {
            long durMs = AppSettings.AlignMsToFrames(AppSettings.ScopeEnterAnimMs);
            CategoryList.UpdateLayout(); // 保证新分类容器已实现
            for (int i = 0; i < CategoryList.Items.Count; i++)
            {
                if (CategoryList.Items[i] is not NativeMethods.Category cat) continue;
                if (!newIds.Contains(cat.Id)) continue;
                if (ContainerFromIndex(CategoryList, i) is not FrameworkElement fe) continue;
                PlayFadeIn(fe, durMs);
            }
        }
    }

    // 自包含淡入：结束自动归位，不依赖全局容器动画系统
    private void PlayFadeIn(UIElement target, long ms)
    {
        target.Opacity = 0;
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) => target.Opacity = 1; // 兜底：确保永不滞留透明
        target.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void CategoryList_Loaded(object sender, RoutedEventArgs e)
    {
        // 仅在 Init 之前（数据尚未注入）兜底设置默认范围；
        // 页面被缓存复用时 Loaded 会再次触发，此时必须保留用户当前选择。
        if (_scopeInitialized) return;
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
        bool dark = ResolveEffectiveTheme(AppSettings.Theme) == ElementTheme.Dark;
        Brush? bg, fg;
        if (_allScope)
        {
            bg = new SolidColorBrush(dark
                ? Color.FromArgb(255, 0xE4, 0xD7, 0xFF)
                : Color.FromArgb(255, 0x67, 0x50, 0xA4));
            fg = new SolidColorBrush(dark
                ? Color.FromArgb(255, 0x21, 0x00, 0x5D)
                : Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            bg = Brushes.Transparent;
            fg = new SolidColorBrush(dark
                ? Color.FromArgb(255, 0xC4, 0xC7, 0xC5)
                : Color.FromArgb(255, 0x5C, 0x5F, 0x66));
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
        // 记录保存前 store 中的既有顺序，失败时据此回滚（MoveCategory 无事务性）
        var originalOrder = GetCategoryShownOrder().Select(c => c.Id).Where(id => id != NativeMethods.UncatId).ToList();

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
            // 前面已成功移动的分类必须回滚，否则内存顺序与「保存失败」提示自相矛盾
            RestoreCategoryOrder(originalOrder);
            ExitCategorySortMode();
            ReloadCategoriesKeepScope();
            await ShowError($"保存排序失败（错误码 {rc}），已恢复原顺序。");
            return;
        }

        int svc = _store.Save();
        ExitCategorySortMode();
        ReloadCategoriesKeepScope();
        if (svc != NativeMethods.KSBOX_OK)
            await ShowError($"排序已生效，但保存失败（错误码 {svc}），重启后可能丢失。");
    }

    // 按给定 id 顺序回写 store（用于排序保存失败后的回滚）。
    // 逆序回写：从末尾开始定位，避免先写入的项被后续 insert 挤走。
    private void RestoreCategoryOrder(IReadOnlyList<long> order)
    {
        for (int i = order.Count - 1; i >= 0; i--)
        {
            if (_store.MoveCategory(order[i], i + 1) != NativeMethods.KSBOX_OK)
                Trace($"RestoreCategoryOrder: rollback failed at index {i} id={order[i]}");
        }
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
        ThemeDialog(dlg);
        if (dlg.ShowDialogAsync(OwnerWindow()) == true)
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
            ThemeDialog(dlg);
            if (dlg.ShowDialogAsync(OwnerWindow()) == true)
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

    private void DelCatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not NativeMethods.Category cat) return;

        // ContentDialog → MessageDialog；WinUI 的 DefaultButton=Close 在 WPF 遮罩对话框中
        // 不适用（MessageDialog 默认聚焦「确定」以支持 Enter 关闭）
        var msg = $"确定删除分类「{cat.Name}」吗？其下条目会一并删除。";
        if (new MessageDialog(msg, "删除分类", "删除").ShowDialogAsync(OwnerWindow()) != true) return;

        long rc = _store.RemoveCategory(cat.Id);
        if (rc == NativeMethods.KSBOX_OK)
        {
            _store.Save();
            LoadCategories();
            SetScope(all: true, scopeSwitch: true);
        }
        else _ = ShowError($"删除失败（错误码 {rc}）。");
    }

    #endregion

    #region 视图与搜索

    private bool _searchOpen;

    // 搜索按钮：点击展开输入框
    private void SearchToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _searchOpen = !_searchOpen;

        // 展开宽度取父容器剩余宽度，保证一直铺到最右侧
        double available = SearchBox.Parent is FrameworkElement row ? row.ActualWidth : 0;
        double target = _searchOpen
            ? Math.Max(200, available - SearchToggleBtn.ActualWidth - 8)
            : 0;

        var width = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SearchBox.BeginAnimation(FrameworkElement.WidthProperty, width);

        var fade = new DoubleAnimation
        {
            To = _searchOpen ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220))
        };
        SearchBox.BeginAnimation(UIElement.OpacityProperty, fade);

        if (_searchOpen) SearchBox.Focus();
        else
        {
            // 收起即清空筛选：程序性赋值触发的 TextChanged 会被 _searchUpdating 拦截，
            // 因此必须在此显式清空状态并刷新列表，
            // 否则列表会保留一个用户看不见、也无法清除的筛选条件。
            _searchUpdating = true;
            try { SearchBox.Text = ""; }
            finally { _searchUpdating = false; }
            _searchText = "";
            RefreshEntryList();
        }
    }

    // 程序性改写 SearchBox.Text 时置位，等价 WinUI 的
    // AutoSuggestBoxTextChangedEventArgs.Reason != UserInput 判定
    private bool _searchUpdating;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchUpdating) return; // 非用户输入，忽略
        _searchText = SearchBox.Text.Trim();
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
            if (ContainerFromIndex(EntryList, i) is not FrameworkElement fe) continue;
            if (!goneIds.Contains(ent.Id)) continue; // 停留条目不动
            double off = Math.Max(fe.ActualWidth, 60);
            exitAnims.Add(new ContainerAnim
            {
                Fe = fe,
                Move = true, FromX = 0, ToX = off,
                Fade = true, FromOpacity = 1, ToOpacity = 0,
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
            if (ContainerFromIndex(EntryList, i) is not FrameworkElement fe) continue;

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
                    Fade = true, FromOpacity = 0, ToOpacity = 1,
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
            if (ContainerFromIndex(EntryList, i) is not FrameworkElement fe) continue;
            double off = Math.Max(fe.ActualWidth, 60);
            anims.Add(new ContainerAnim
            {
                Fe = fe,
                Move = true, FromX = -off, ToX = 0,
                Fade = true, FromOpacity = 0, ToOpacity = 1,
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
        // _selectedCategory 与 _allScope 的不变式由状态写入点维持，此处做防御性取值
        string scopeName = _allScope ? "全部" : (_selectedCategory?.Name ?? "全部");
        EmptyText.Text = empty
            ? (string.IsNullOrEmpty(_searchText)
                ? (_allScope ? "保险库还是空的，点击「新增条目」开始吧" : $"「{scopeName}」分类下暂无条目")
                : "没有匹配的条目")
            : "";

        StatusText.Text = string.IsNullOrEmpty(_searchText)
            ? $"{scopeName} · 共 {list.Count} 条"
            : $"{scopeName} · 搜索「{_searchText}」 · {list.Count} 条";
    }

    // 查询当前范围 → 搜索过滤 → 同 Id 复用已展示实例
    private List<NativeMethods.Entry> BuildEntryListReused()
    {
        // 防御：若 _allScope 为假但 _selectedCategory 意外为空，退回「全部」而非抛异常
        if (!_allScope && _selectedCategory == null) _allScope = true;

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

    // 命令栏新增：与导航「新建条目」按钮一致，上滑出输入区
    private void AddEntryBtn_Click(object sender, RoutedEventArgs e)
        => BeginCreateEntry();

    // WinUI DoubleTapped → WPF MouseDoubleClick（事件的 e 可被当成 RoutedEventArgs 传给
    // EntryQueryBtn_Click，行为与原来一致）
    private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
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

    // 只认按钮自带的 Tag（=该行的 Entry）。
    // 不再回退到 EntryList.SelectedItem：点击行内按钮通常不会改变选中项，
    // 回退会导致删除/编辑/查询作用于「另一行」，在密码保险库中风险极高。
    // Tag 异常时快速失败并提示，绝不猜测目标。
    private NativeMethods.Entry? TryGetRow(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NativeMethods.Entry row }) return row;

        Trace("TryGetRow: button Tag is not an Entry, aborting to avoid acting on the wrong row");
        _ = ShowError("无法确定该条目，请重新打开列表后重试。");
        return null;
    }

    // 详情：在条目下方展开区呈现（向下展开，可与恢复密钥同时展开）
    private async void EntryQueryBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender, e);
        if (row == null) return;
        var host = FindEntryHost(sender as DependencyObject);
        if (host == null) return;

        // 再次点击同一条目则收起详情
        if (_detailOpen.TryGetValue(row.Id, out var opened) && opened)
        {
            _detailOpen[row.Id] = false;
            if (FindNamed<StackPanel>(host, "EntryDetailPanel") is { } opened2)
            {
                opened2.Visibility = Visibility.Collapsed;
                opened2.Children.Clear();
            }
            SyncEntryExpand(host);
            return;
        }

        var full = _store.GetEntry(row.Id);
        if (full == null) { await ShowError("读取条目失败。"); return; }

        var panel = FindNamed<StackPanel>(host, "EntryDetailPanel");
        if (panel == null) return;

        FillFields(panel, new[]
        {
            ("分类", CategoryNamesOf(full)),
            ("账号", full.Account),
            ("密码", full.Password),
            ("备注", full.Note)
        });

        _detailOpen[row.Id] = true;
        panel.Visibility = Visibility.Visible;
        SyncEntryExpand(host);
    }

    // 恢复密钥：与详情同属条目下方展开区，可同时展开
    private void EntryRecoveryBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender, e);
        if (row == null) return;
        var host = FindEntryHost(sender as DependencyObject);
        if (host == null) return;

        // 再次点击同一条目则收起恢复密钥
        if (_recoveryOpen.TryGetValue(row.Id, out var opened) && opened)
        {
            _recoveryOpen[row.Id] = false;
            if (FindNamed<StackPanel>(host, "EntryRecoveryPanel") is { } opened2)
            {
                opened2.Visibility = Visibility.Collapsed;
                opened2.Children.Clear();
            }
            SyncEntryExpand(host);
            return;
        }

        // WinUI 版把恢复密钥对话内联在条目下方展开区（RecoveryDialog 的 Content 被摘出并
        // 填入 EntryRecoveryPanel）。WPF 的 RecoveryDialog 是独立窗口，无法摘出内容，
        // 因此改为：展开区给出「管理恢复密钥…」面板，点击打开同一对话框。
        // 展开/收起状态仍按条目 id 记录，可与详情/编辑同时展开（_recoveryOpen 语义不变）。
        var panel = FindNamed<StackPanel>(host, "EntryRecoveryPanel");
        if (panel == null) return;

        panel.Children.Clear();
        panel.Children.Add(MakeRecoveryPanel(row, host));

        _recoveryOpen[row.Id] = true;
        panel.Visibility = Visibility.Visible;
        SyncEntryExpand(host);
    }

    // 恢复密钥面板：账号摘要 + 密钥列表 + 打开对话框
    private UIElement MakeRecoveryPanel(NativeMethods.Entry row, FrameworkElement host)
    {
        var keys = _store.GetRecovery(row.Id);
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = keys is { Count: > 0 } ? $"恢复密钥：{keys.Count} 把" : "（未设置恢复密钥）",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x88, 0x88, 0x88))
        });

        if (keys is { Count: > 0 })
        {
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < keys.Count; i++)
                list.Children.Add(MakeField($"密钥 {i + 1}", keys[i], allowCopy: true));
            stack.Children.Add(list);
        }

        var manage = new Button
        {
            Content = "管理恢复密钥…",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        manage.Click += (_, _) =>
        {
            var dlg = new RecoveryDialog();
            ThemeDialog(dlg);
            dlg.Init(_store, row.Id, row.CategoryName, row.Account);
            dlg.ShowDialogAsync(OwnerWindow());
            // 关闭后刷新该条目展开区摘要（密钥可能已增删）
            if (_recoveryOpen.TryGetValue(row.Id, out var still) && still)
            {
                var p = FindNamed<StackPanel>(host, "EntryRecoveryPanel");
                if (p != null)
                {
                    p.Children.Clear();
                    p.Children.Add(MakeRecoveryPanel(row, host));
                }
            }
        };
        stack.Children.Add(manage);
        return stack;
    }

    private async void EntryEditBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender, e);
        if (row == null) return;
        var host = FindEntryHost(sender as DependencyObject);
        if (host == null) return;

        var full = _store.GetEntry(row.Id);
        if (full == null) { await ShowError("读取条目失败。"); return; }

        var editPanel = FindNamed<StackPanel>(host, "EntryEditPanel");
        if (editPanel == null) return;

        // 编辑表单显示在详情位置（同一条目的展开区内），详情让位
        if (FindNamed<StackPanel>(host, "EntryDetailPanel") is { } detail)
        {
            detail.Visibility = Visibility.Collapsed;
            detail.Children.Clear();
            _detailOpen[row.Id] = false;
        }

        BuildEditForm(editPanel, host, row.Id, full.Account, full.Password, full.Note,
            full.CategoryIds is { Count: > 0 } ? full.CategoryIds[0] : (long?)null);

        editPanel.Visibility = Visibility.Visible;
        SyncEntryExpand(host);

        // 取值后立即断开明文引用
        full.Password = ""; full.Account = ""; full.Note = "";
    }

    // 删除：渐隐其余按键，本键位置展开为「取消 / 确认删除」
    private void EntryDelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button delBtn || delBtn.Parent is not StackPanel panel) return;

        foreach (var child in panel.Children)
        {
            if (child is not Button b || ReferenceEquals(b, delBtn)) continue;
            if (IsConfirmButton(b))
            {
                b.Visibility = Visibility.Visible;
                b.IsEnabled = true;
                FadeTo(b, 1);
            }
            else
            {
                // 其余按键渐隐：必须同时禁用，否则透明度为 0 的按钮仍参与命中测试，
                // 用户在「空白处」点击会意外触发详情/编辑等操作
                b.IsEnabled = false;
                FadeTo(b, 0);
            }
        }
        delBtn.Visibility = Visibility.Collapsed;
    }

    private void EntryDelCancelBtn_Click(object sender, RoutedEventArgs e)
        => RestoreEntryButtons(sender);

    private async void EntryDelConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        // 先取目标条目再复原按键：复原会重新启用按钮，但不影响已取到的行引用
        var row = TryGetRow(sender, e);
        RestoreEntryButtons(sender);
        if (row == null) return;

        long rc = _store.RemoveEntry(row.Id);
        if (rc == NativeMethods.KSBOX_OK)
        {
            _store.Save();
            // 删除后收起该条目的展开区并清掉其展开状态
            if (FindEntryHost(sender as DependencyObject) is { } host) CollapseEntry(host);
            _detailOpen.Remove(row.Id);
            _recoveryOpen.Remove(row.Id);
            _recoveryDialogs.Remove(row.Id);
            RefreshEntryList();
        }
        else await ShowError($"删除失败（错误码 {rc}）。");
    }

    // 按 x:Name 识别确认/取消按键（不再依赖 Content 中文字面量，
    // 避免文案调整或本地化后静默失效）
    private static bool IsConfirmButton(Button b)
        => b.Name is "EntryDelCancelBtn" or "EntryDelConfirmBtn";

    // 取消或完成后：确认键收起，其余按键渐出（渐显回来）
    private void RestoreEntryButtons(object sender)
    {
        if (sender is not Button btn || btn.Parent is not StackPanel panel) return;

        foreach (var child in panel.Children)
        {
            if (child is not Button b) continue;
            if (IsConfirmButton(b))
            {
                b.Visibility = Visibility.Collapsed;
                b.Opacity = 1;
                b.IsEnabled = true;
            }
            else
            {
                b.Visibility = Visibility.Visible;
                FadeTo(b, 1);
            }
        }
    }

    #endregion

    #region 设置

    // 设置已由窗口导航切换到独立设置页，本页不再承载入口

    #endregion

    #region 辅助

    // 只读字段：全部允许划词复制
    // 只读字段：全部允许划词复制（静态：不依赖实例状态，便于静态辅助方法复用）
    private static StackPanel MakeField(string label, string value, bool allowCopy)
    {
        var valueText = new TextBlock
        {
            Text = string.IsNullOrEmpty(value) ? "(空)" : value,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        // WinUI 的 IsTextSelectionEnabled 在 WPF 无对应开关（TextBlock 默认不可选），
        // 划词复制由「复制」按钮承担；双击不再需要拦截冒泡。

        var field = new StackPanel();
        field.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        if (allowCopy)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(valueText, 0);
            valueText.Margin = new Thickness(0, 0, 8, 0);
            var copy = new Button
            {
                Content = "复制",
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(12, 4, 12, 4)
            };
            string textToCopy = value;
            copy.Click += async (_, _) =>
            {
                try { Clipboard.SetText(textToCopy); } catch { }
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
        new MessageDialog(msg).ShowDialogAsync(OwnerWindow());
        await Task.CompletedTask;
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

    // 归属窗口：优先用实际宿主窗口，回退应用主窗口
    // （与已完成的 SettingsPage 保持一致：非空返回，避免每个调用点都要判空）
    private Window OwnerWindow()
        => Window.GetWindow(this) ?? Application.Current.MainWindow;

    #endregion

    #region 动画

    // WPF 的 ListBox 没有 ListViewBase.ContainerFromIndex，用 ItemContainerGenerator 等价实现。
    // 容器未实现（虚拟化外）时返回 null，调用方与 WinUI 版一样跳过该行。
    private static FrameworkElement? ContainerFromIndex(ItemsControl list, int index)
    {
        if (index < 0 || index >= list.Items.Count) return null;
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement fe) return fe;
        // 强制生成：等价 WinUI 的 ContainerFromIndex（其内部会实现容器）
        list.UpdateLayout();
        return list.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
    }

    // 记录每个可见容器相对列表顶部的偏移
    private Dictionary<long, double> CaptureTops(ItemsControl list, Func<object, long> idOf)
    {
        var map = new Dictionary<long, double>();
        for (int i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (item != null && ContainerFromIndex(list, i) is FrameworkElement fe)
                map[idOf(item)] = TopInList(fe, list);
        }
        return map;
    }

    private Dictionary<long, double> CaptureEntryTops() => CaptureTops(EntryList, x => ((NativeMethods.Entry)x).Id);
    private Dictionary<long, double> CaptureCategoryTops() => CaptureTops(CategoryList, x => ((NativeMethods.Category)x).Id);

    // WPF 的 TransformToVisual 返回 GeneralTransform，用 Transform(new Point(0,0)) 取点
    private static double TopInList(FrameworkElement element, ItemsControl list)
        => element.TransformToVisual((UIElement)list).Transform(new Point(0, 0)).Y;

    // 切分类前的完整快照：全部条目 Id↔索引 + 可见容器的顶部偏移（容器未实现时为 NaN）
    private EntrySnap CaptureEntrySnap()
    {
        var snap = new EntrySnap();
        for (int i = 0; i < EntryList.Items.Count; i++)
        {
            if (EntryList.Items[i] is not NativeMethods.Entry ent) continue;
            snap.Idx[ent.Id] = i;
            double top = double.NaN;
            if (ContainerFromIndex(EntryList, i) is FrameworkElement fe)
                top = TopInList(fe, EntryList);
            snap.Tops[ent.Id] = top;
        }
        return snap;
    }

    // 容器动画：定时器逐帧直接赋值 Opacity 与 TranslateTransform（替代 WinUI 的 Translation）。
    // 变换按需挂到一个 TransformGroup 上，只让本页创建的变换参与，避免覆盖模板变换。
    private static void ResetContainerVisual(FrameworkElement fe)
    {
        fe.Opacity = 1;
        var tt = TranslateOf(fe, create: false);
        if (tt != null)
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.X = 0;
            tt.Y = 0;
        }
    }

    private static void ResetListVisuals(ItemsControl list)
    {
        for (int i = 0; i < list.Items.Count; i++)
            if (ContainerFromIndex(list, i) is FrameworkElement fe) ResetContainerVisual(fe);
    }

    // 取得（或按需创建）容器上的平移变换：包在 TransformGroup 里，不改动其它变换
    private static TranslateTransform? TranslateOf(FrameworkElement fe, bool create)
    {
        if (fe.RenderTransform is TransformGroup g)
        {
            foreach (var child in g.Children)
                if (child is TranslateTransform tt) return tt;
            if (!create) return null;
            var added = new TranslateTransform();
            g.Children.Add(added);
            return added;
        }
        if (fe.RenderTransform is TranslateTransform single) return single;
        if (!create) return null;

        var tt2 = new TranslateTransform();
        var group = fe.RenderTransform is { } existing
            ? new TransformGroup { Children = { existing, tt2 } }
            : new TransformGroup { Children = { tt2 } };
        fe.RenderTransform = group;
        return tt2;
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
            if (a.Move && TranslateOf(a.Fe, create: true) is { } tt)
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                tt.X = a.FromX;
                tt.Y = a.FromY;
            }
            if (a.Fade) a.Fe.Opacity = a.FromOpacity;
        }

        // WinUI 的 DispatcherQueueTimer.IsRepeating=true 等价于 WPF DispatcherTimer 默认重复；
        // 结束条件统一由 Tick 内的逻辑（allDone / 列表空）Stop() 控制。
        _containerAnimTimer ??= new DispatcherTimer();
        _containerAnimTimer.Tick -= OnContainerAnimTick;
        _containerAnimTimer.Tick += OnContainerAnimTick;
        int fps = Math.Max(1, AppSettings.FrameRate);
        _containerAnimTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        _containerAnimTimer.Start();
    }

    private void OnContainerAnimTick(object? sender, EventArgs e)
    {
        if (_containerAnims.Count == 0)
        {
            _containerAnimTimer?.Stop();
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
            if (a.Move && TranslateOf(a.Fe, create: true) is { } tt)
            {
                tt.X = a.FromX + (a.ToX - a.FromX) * t;
                tt.Y = a.FromY + (a.ToY - a.FromY) * t;
            }
            if (a.Fade)
                a.Fe.Opacity = a.FromOpacity + (a.ToOpacity - a.FromOpacity) * t;
        }
        if (allDone)
        {
            _containerAnimTimer?.Stop();
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
    private void AnimateMove(ItemsControl list, Dictionary<long, double> oldTops,
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
            if (ContainerFromIndex(list, i) is not FrameworkElement fe) continue;
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
