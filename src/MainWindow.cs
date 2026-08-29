using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KeySecBox;

public sealed partial class MainWindow : Window
{
    private readonly IVaultService _vault;
    private readonly IClipboardService _clipboard;
    private readonly ICsvService _csv;
    private readonly IAppConfigurationService _appConfig;
    private readonly IMasterRecoveryService _masterRecovery;
    private readonly IRecoveryService _recovery;
    private readonly ICryptoService _crypto;
    private readonly IFileIOService _fileIO;
    private readonly IBinaryFormatService _binary;
    private readonly IJsonSerializationService _json;
    private readonly IDiagnosticService _diag;

    private readonly ObservableCollection<CategoryItem> _categoryItems = new();
    private readonly ObservableCollection<EntryItem> _entryItems = new();
    private CategoryItem? _selectedCategory;
    private bool _allScope = true;
    private string _searchText = "";

    // 分类排序模式：仅改内存工作副本，点"保存"才写回保险库
    private readonly List<CategoryItem> _sortWorking = new();
    private bool _categorySortMode;

    private static readonly string VaultBase = AppPaths.VaultBase;

    public MainWindow(
        IVaultService vault, IClipboardService clipboard, ICsvService csv,
        IAppConfigurationService appConfig, IMasterRecoveryService masterRecovery,
        IRecoveryService recovery, ICryptoService crypto, IFileIOService fileIO,
        IBinaryFormatService binary, IJsonSerializationService json, IDiagnosticService diag)
    {
        _vault = vault; _clipboard = clipboard; _csv = csv;
        _appConfig = appConfig; _masterRecovery = masterRecovery;
        _recovery = recovery; _crypto = crypto; _fileIO = fileIO;
        _binary = binary; _json = json; _diag = diag;

        InitializeComponent();
        BuildLoadingIndicator();
        CategoryList.ItemsSource = _categoryItems;
        EntryList.ItemsSource = _entryItems;
        SystemBackdrop = new MicaBackdrop();

        // 系统明暗切换或应用内切主题时，标题栏与「全部」高亮跟随实际生效明暗
        if (Content is FrameworkElement themeRoot)
            themeRoot.ActualThemeChanged += (_, _) =>
            {
                try
                {
                    UpdateTitleBarColors(ResolveEffectiveTheme(_appConfig.Theme));
                    RefreshScopeVisual();
                }
                catch { }
            };

        Closed += (_, _) =>
        {
            _appConfig.WindowBounds = (AppWindow.Position.X, AppWindow.Position.Y,
                (int)Bounds.Width, (int)Bounds.Height);
            _appConfig.Save();
            _vault.Dispose();
        };

        RootGrid.Loaded += async (_, _) =>
        {
            ApplyTheme(_appConfig.Theme); // 元素载入后 ActualTheme 才可靠
            RestoreWindowBounds();
            await ShowUnlockDialog();
        };
    }

    // 进度环在代码里构造，不走 XAML 标记编译路径。
    private void BuildLoadingIndicator()
    {
        var ring = new ProgressRing { IsActive = true, Width = 40, Height = 40 };
        LoadingPanel.Children.Insert(0, ring);
    }

    #region 主题

    private void ApplyTheme(ThemeMode theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                ThemeMode.Light => ElementTheme.Light,
                ThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
        // 标题栏不随 RequestedTheme 变色，按实际生效明暗手动着色
        UpdateTitleBarColors(ResolveEffectiveTheme(theme));
        RefreshScopeVisual();
    }

    // ContentDialog 弹出不会继承窗口根元素的 RequestedTheme，统一按生效明暗套主题
    internal ContentDialog ThemeDialog(ContentDialog dlg)
    {
        dlg.RequestedTheme = ResolveEffectiveTheme(_appConfig.Theme);
        dlg.CornerRadius = new CornerRadius(12); // 对话框背景以此为圆角
        return dlg;
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

    // 「全部」按钮状态配色：选中主色实底，未选中透明底+次级文字
    private void RefreshScopeVisual()
    {
        if (AllScopeBtn == null) return;
        bool dark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        Brush? bg, fg;
        if (_allScope)
        {
            bg = new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 0xE4, 0xD7, 0xFF)
                : Windows.UI.Color.FromArgb(255, 0x67, 0x50, 0xA4));
            fg = new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 0x21, 0x00, 0x5D)
                : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            bg = null;
            fg = new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 0xC4, 0xC7, 0xC5)
                : Windows.UI.Color.FromArgb(255, 0x5C, 0x5F, 0x66));
        }
        AllScopeBtn.Background = bg;
        AllScopeBtn.Foreground = fg;
        if (AllScopeIcon != null) AllScopeIcon.Foreground = fg;
        if (AllScopeText != null) AllScopeText.Foreground = fg;
    }

    #endregion

    private void RestoreWindowBounds()
    {
        var (x, y, w, h) = _appConfig.WindowBounds;
        if (w > 0 && h > 0)
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    #region 解锁

    private async System.Threading.Tasks.Task ShowUnlockDialog()
    {
        var unlocked = await TryUnlock();
        if (!unlocked) { Close(); return; }
        RefreshAll();
        _dataReady = true; // 首次数据加载完成后才允许切换动画
        PlayUnlockIntro(); // 解锁后主界面入场：列表淡入上滑
    }

    private async System.Threading.Tasks.Task<bool> TryUnlock()
    {
        while (true)
        {
            var result = _vault.Open(VaultBase, "");
            bool needSetup = result == ErrorCodes.NoVault;

            if (result == ErrorCodes.Legacy)
            {
                var dlg = new UnlockDialog(this, false);
                if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;
                var pwd = dlg.Password; dlg.ClearSecrets();

                var legSvc = new LegacyVaultService(_crypto, _fileIO, _binary, _json, _diag);
                var legRes = legSvc.OpenLegacy(VaultBase, pwd, out var legVault);
                if (legRes != ErrorCodes.Ok || legVault == null)
                {
                    await ShowError("无法打开旧版保险库。");
                    continue;
                }
                ImportLegacy(legVault);
                return true;
            }

            var dialog = new UnlockDialog(this, needSetup);
            var dlResult = await dialog.ShowAsync();
            if (dlResult != ContentDialogResult.Primary)
            {
                if (dialog.ForgotHandled) { await ShowForgotPassword(); continue; }
                return false;
            }

            var password = dialog.Password; dialog.ClearSecrets();

            if (needSetup)
            {
                if (_vault.Setup(VaultBase, password) != ErrorCodes.Ok)
                { await ShowError("创建保险库失败。"); continue; }
            }
            else
            {
                var openRes = _vault.Open(VaultBase, password);
                if (openRes == ErrorCodes.WrongPassword)
                { await ShowError("主密码错误。"); continue; }
                if (openRes != ErrorCodes.Ok) return false;
            }
            return true;
        }
    }

    private void ImportLegacy(IVaultService legVault)
    {
        foreach (var cat in legVault.ListCategories())
        {
            if (cat.Id != VaultStore.UncatId &&
                !_vault.ListCategories().Any(c => c.Name == cat.Name))
                _vault.AddCategory(cat.Name);
        }

        foreach (var entry in legVault.QueryAll())
        {
            var detail = legVault.GetEntry(entry.Id);
            if (detail == null) continue;
            var catIds = detail.CategoryIds
                .Select(cid => _vault.ListCategories().FirstOrDefault(c => c.Id == cid)?.Id ?? VaultStore.UncatId)
                .Distinct().ToList();
            if (catIds.Count == 0) catIds.Add(VaultStore.UncatId);
            _vault.AddEntry(catIds, detail.Account, detail.Password, detail.Note);
        }
        _vault.Save();
    }

    private async System.Threading.Tasks.Task ShowForgotPassword()
    {
        var dlg = new ForgotPasswordDialog(this, _masterRecovery);
        await dlg.ShowAsync();
        if (dlg.RecoveredPassword != null)
        {
            _vault.Open(VaultBase, dlg.RecoveredPassword);
            RefreshAll();
        }
    }

    #endregion

    #region 刷新

    internal void RefreshAll()
    {
        RefreshCategories();
        RefreshEntries();
    }

    // 没有未分类条目时不展示「未分类」筛选
    private void RefreshCategoryList()
    {
        if (_categorySortMode) return; // 排序模式下保持工作副本，不被覆盖
        var uncatCount = _vault.QueryCategory(VaultStore.UncatId).Count;
        var all = _vault.ListCategories();
        var shown = uncatCount == 0
            ? all.Where(c => c.Id != VaultStore.UncatId).ToList()
            : all.ToList();

        var items = new List<CategoryItem>();
        for (int i = 0; i < shown.Count; i++)
        {
            items.Add(new CategoryItem
            {
                Id = shown[i].Id,
                Name = shown[i].Name,
                CanMoveUp = false,
                CanMoveDown = false,
                ShowSortArrows = false,
                ShowActionButtons = true
            });
        }
        SyncInPlace(_categoryItems, items, c => c.Id);
    }

    // 新增/重命名分类后仅刷新列表，保留当前范围
    private void ReloadCategoriesKeepScope()
    {
        bool keepAll = _allScope;
        long? keepId = _selectedCategory?.Id;
        RefreshCategoryList();
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
            RefreshEntries(); // 重命名/新建分类后条目分类名同步刷新（非切换，不动画）
        }
        else
        {
            SetScope(all: true, scopeSwitch: true);
        }
    }

    internal void RefreshCategories()
    {
        RefreshCategoryList();
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
        RefreshEntries(scopeSwitch);
    }

    internal void RefreshEntries(bool scopeSwitch = false)
    {
        // 只有明确的分类切换 + 已就绪时才播过渡动画
        if (scopeSwitch && _dataReady)
        {
            RefreshEntriesAnimated();
            return;
        }
        CancelScopeTransition();
        RefreshEntriesNow();
    }

    // 查询当前范围 → 搜索过滤
    private List<EntryItem> BuildEntryList()
    {
        List<EntrySummary> baseList;
        if (!string.IsNullOrEmpty(_searchText))
        {
            baseList = _vault.Search(_searchText);
        }
        else if (_allScope)
        {
            baseList = _vault.QueryAll();
        }
        else if (_selectedCategory != null)
        {
            baseList = _vault.QueryCategory(_selectedCategory.Id);
        }
        else
        {
            baseList = new List<EntrySummary>();
        }

        var list = new List<EntryItem>();
        foreach (var e in baseList) list.Add(MapSummary(e));
        ApplyEntryMeta(list);
        return list;
    }

    // 行内展示字段：多分类名、排序箭头可用性（全部/分类视图均可用）
    private void ApplyEntryMeta(List<EntryItem> list)
    {
        var catNames = new Dictionary<long, string> { [VaultStore.UncatId] = "未分类" };
        foreach (var c in _vault.ListCategories()) catNames[c.Id] = c.Name;
        for (int i = 0; i < list.Count; i++)
        {
            var ent = list[i];
            ent.CategoryName = ent.CategoryIds.Count == 0
                ? "未分类"
                : string.Join("、", ent.CategoryIds.Select(id =>
                    catNames.TryGetValue(id, out var n) ? n : "未分类"));
            ent.CanMoveUp = i > 0;
            ent.CanMoveDown = i < list.Count - 1;
        }
    }

    private void RefreshEntriesNow(List<EntryItem>? prebuilt = null)
    {
        StopContainerAnimations();   // 数据将就地增删改，容器可能被回收复用：先停掉进行中的容器动画
        ResetListVisuals(EntryList); // 并复位所有行容器的透明度 / 平移残留
        var list = prebuilt ?? BuildEntryList();

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

    private static EntryItem MapSummary(EntrySummary e) => new()
    {
        Id = e.Id, Account = e.Account, Note = e.Note, CategoryIds = e.CategoryIds
    };

    #endregion

    #region 辅助

    private async System.Threading.Tasks.Task ShowError(string msg)
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

    // 把 target 原地同步为 source（按 Id 对照增删改），保持容器与滚动位置
    internal static void SyncInPlace<T>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, long> idOf)
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

    internal static int IndexOfId<T>(IReadOnlyList<T> list, long id, Func<T, long> idOf)
    {
        for (int i = 0; i < list.Count; i++)
            if (idOf(list[i]) == id) return i;
        return -1;
    }

    #endregion
}
