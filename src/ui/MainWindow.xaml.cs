using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;

namespace KeySecBox
{
    public sealed partial class MainWindow : Window
    {
        // 保险库 basename（与 C++ 侧约定：各文件由该 basename 派生）
        private static readonly string VaultBase = AppPaths.VaultBase;

        private readonly NativeMethods.Store _store = new();
        private readonly List<NativeMethods.Category> _categories = new();
        private NativeMethods.Category? _selectedCategory;
        private bool _allScope = true;
        private string _searchText = "";

        #region 初始化

        public MainWindow()
        {
            InitializeComponent();
            AppPaths.EnsureDataDir(); // 数据目录必须存在，否则首次 Setup/Save 时 fopen 失败
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            ApplyTheme(AppSettings.Theme);
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
            // WinUI3 Activated 时 XamlRoot 可能未就绪，ContentDialog 会因 XamlRoot=null 抛异常；
            // 改在根元素 Loaded 后启动解锁。
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
            dlg.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(12); // 对话框背景以此为圆角
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
                bool firstRun = !File.Exists(VaultBase + ".settings");
                var dlg = new UnlockDialog(this, firstRun);
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
                        if (res != ContentDialogResult.Primary)
                        {
                            dlg.ClearSecrets();
                            Close();
                            return;
                        }
                        pwd = dlg.Password;
                        dlg.ClearSecrets(); // 取值后立即擦除明文
                    }

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
            }
            catch (Exception ex)
            {
                Trace($"EX: {ex.GetType().Name}: {ex.Message}");
                try { await ShowError($"初始化失败：{ex.Message}"); } catch { }
            }
        }

        // 忘记密码恢复方式配置（首次创建后 / 设置中重配共用）
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

        // 改密/取回复原后同步取回库（旧记录仍对应旧主密码，只允许更新）
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
            var uncatCount = (_store.QueryCategory(NativeMethods.UncatId) ?? new()).Count;
            var shown = uncatCount == 0
                ? _categories.Where(c => c.Id != NativeMethods.UncatId).ToList()
                : _categories.ToList();
            CategoryList.ItemsSource = null;
            CategoryList.ItemsSource = shown;
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
                SetScope(all: true);
                return;
            }
            var match = keepId != null ? _categories.FirstOrDefault(c => c.Id == keepId) : null;
            if (match != null)
            {
                CategoryList.SelectedItem = match;   // 触发 SelectionChanged 保持当前分类
            }
            else
            {
                SetScope(all: true);
            }
        }

        private void SetScope(bool all)
        {
            _allScope = all;
            if (all)
            {
                _selectedCategory = null;
                CategoryList.SelectedItem = null;
            }
            RefreshScopeVisual();
            RefreshEntryList();
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
            SetScope(all: true);
        }

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = CategoryList.SelectedItem as NativeMethods.Category;
            if (sel == null) return;
            _selectedCategory = sel;
            _allScope = false;
            RefreshScopeVisual();
            RefreshEntryList();
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
                        SetScope(all: true);
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

        private void RefreshEntryList()
        {
            // 先取当前范围的条目集合，再按搜索文本在内存过滤
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

            foreach (var ent in list)
            {
                var cat = _categories.FirstOrDefault(c => c.Id == ent.CategoryId);
                ent.CategoryName = cat?.Name ?? "未分类";
                ent.Recovery = _store.GetRecovery(ent.Id); // 恢复密钥逐条解密填充（瞬时）
            }

            EntryList.ItemsSource = null;
            EntryList.ItemsSource = list;

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
            AnimateListIn();
        }

        #endregion

        #region 条目操作

        private async void AddEntryBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new EntryDialog();
            dlg.Init(_store, _categories, null);
            dlg.XamlRoot = Content.XamlRoot;
            ThemeDialog(dlg);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                long rc = _store.AddEntry(dlg.CategoryId, dlg.Account, dlg.Password, dlg.Note);
                var recovery = dlg.Recovery;
                dlg.ClearSecrets(); // 取走数据后立即擦除明文
                if (rc > 0)
                {
                    _store.SetRecovery(rc, recovery);
                    int svc = _store.Save();
                    LoadCategories(); // 对话框内可能新建了分类
                    RefreshEntryList();
                    if (svc != NativeMethods.KSBOX_OK)
                        await ShowError($"条目已加入内存，但保存失败（错误码 {svc}），重启后可能丢失。");
                }
                else await ShowError($"新增失败（错误码 {rc}）。");
            }
        }

        private void EntryList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
            => EntryQueryBtn_Click(sender, e);

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
                Title = row.NoteDisplay,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children = { MakeField("账号", full.Account, allowCopy: true),
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
            dlg.Init(_store, row.Id, row.NoteDisplay);
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
                long rc = _store.UpdateEntry(row.Id, dlg.CategoryId, dlg.Account, dlg.Password, dlg.Note);
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
            var dlg = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "删除条目",
                Content = $"确定删除「{row.NoteDisplay}」这条记录吗？",
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

        // 只读字段：TextBlock 禁选防划词复制，可选右侧复制按钮
        private StackPanel MakeField(string label, string value, bool allowCopy)
        {
            var valueText = new TextBlock
            {
                Text = string.IsNullOrEmpty(value) ? "(空)" : value,
                IsTextSelectionEnabled = false,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

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

        private void AnimateListIn()
        {
            for (int i = 0; i < EntryList.Items.Count; i++)
            {
                if (EntryList.ContainerFromIndex(i) is not ListViewItem item) continue;

                var ct = new CompositeTransform { TranslateY = 10 };
                item.RenderTransform = ct;
                item.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

                var sb = new Storyboard();
                var oa = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(0.24) };
                oa.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                Storyboard.SetTarget(oa, item);
                Storyboard.SetTargetProperty(oa, "Opacity");
                var ya = new DoubleAnimation { From = 10, To = 0, Duration = TimeSpan.FromSeconds(0.24) };
                ya.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                Storyboard.SetTarget(ya, item);
                Storyboard.SetTargetProperty(ya, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
                sb.Children.Add(oa);
                sb.Children.Add(ya);
                sb.BeginTime = TimeSpan.FromMilliseconds(i * 22);
                sb.Begin();
            }
        }

        #endregion
    }
}
