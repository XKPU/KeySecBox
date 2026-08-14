using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace KeySecBox
{
    public sealed partial class MainWindow : Window
    {
        // 保险库文件路径根（与 C++ 侧 ksbx_* 约定：各文件以该 basename 派生）。
        // 固定存放在程序运行目录下的 vault 子目录，避免文件散布到系统其他位置。
        private static readonly string VaultBase = Path.Combine(
            AppContext.BaseDirectory, "vault", "vault");

        private readonly NativeMethods.Store _store = new();
        private readonly List<NativeMethods.Category> _categories = new();
        private NativeMethods.Category? _selectedCategory;
        private bool _allScope = true;
        private string _searchText = "";

        public MainWindow()
        {
            InitializeComponent();
            // 确保 vault 目录存在，否则首次 Setup/Save 时 C++ 侧 fopen 失败
            Directory.CreateDirectory(Path.GetDirectoryName(VaultBase)!);
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            ApplyTheme(AppSettings.Theme);
            _ = InitializeAsync();
        }

        private void ApplyTheme(ThemeMode mode)
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = mode switch
                {
                    ThemeMode.Light => ElementTheme.Light,
                    ThemeMode.Dark => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }
        }

        #region 解锁

        private async Task InitializeAsync()
        {
            bool firstRun = !File.Exists(VaultBase + ".settings");
            while (true)
            {
                var dlg = new UnlockDialog(this, firstRun);
                if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                {
                    Close();
                    return;
                }

                int rc = firstRun
                    ? _store.Setup(VaultBase, dlg.Password)
                    : _store.Open(VaultBase, dlg.Password);

                if (rc == NativeMethods.KSBOX_OK)
                {
                    if (firstRun) _store.Save();
                    break;
                }

                dlg.ShowError(rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
                    ? "密码错误，请重试。"
                    : $"打开保险库失败（错误码 {rc}）。");
            }

            LoadCategories();
            RefreshEntryList();
        }

        #endregion

        #region 分类

        private void LoadCategories()
        {
            _categories.Clear();
            var list = _store.ListCategories();
            if (list != null) _categories.AddRange(list);
            CategoryList.ItemsSource = null;
            CategoryList.ItemsSource = _categories;
        }

        private void CategoryList_Loaded(object sender, RoutedEventArgs e)
        {
            // 默认范围为“全部”
            SetScope(all: true);
        }

        private void SetScope(bool all)
        {
            _allScope = all;
            if (all)
            {
                _selectedCategory = null;
                CategoryList.SelectedItem = null;
                if (Application.Current.Resources.TryGetValue("ScopeActiveBrush", out var brush))
                    AllScopeBtn.Background = (Microsoft.UI.Xaml.Media.Brush)brush;
            }
            else
            {
                AllScopeBtn.Background = null;
            }
            RefreshEntryList();
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
            AllScopeBtn.Background = null;
            RefreshEntryList();
        }

        private async void AddCatBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InputDialog();
            dlg.Init("请输入分类名称：");
            dlg.XamlRoot = Content.XamlRoot;
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
                    LoadCategories();
                    SetScope(all: true);
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
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    var name = dlg.Answer.Trim();
                    if (string.IsNullOrEmpty(name)) return;
                    long rc = _store.RenameCategory(cat.Id, name);
                    if (rc == NativeMethods.KSBOX_OK)
                    {
                        _store.Save();
                        LoadCategories();
                        SetScope(all: true);
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
            // 先在选中分类范围内取基础集合，再按搜索文本在内存过滤（搜索仅限当前分类）
            List<NativeMethods.Entry> baseList = _allScope
                ? (_store.QueryAll() ?? new())
                : (_store.QueryCategory(_selectedCategory!.Id) ?? new());

            List<NativeMethods.Entry> list = baseList;
            if (!string.IsNullOrEmpty(_searchText))
            {
                list = baseList
                    .Where(x => (x.Note ?? "").Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            foreach (var ent in list)
            {
                var cat = _categories.FirstOrDefault(c => c.Id == ent.CategoryId);
                ent.CategoryName = cat?.Name ?? "未分类";
            }

            EntryList.ItemsSource = null;
            EntryList.ItemsSource = list;

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
            dlg.Init(_categories, null);
            dlg.XamlRoot = Content.XamlRoot;
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                long rc = _store.AddEntry(dlg.CategoryId, dlg.Account, dlg.Password, dlg.Note);
                if (rc > 0) { _store.Save(); RefreshEntryList(); }
                else await ShowError($"新增失败（错误码 {rc}）。");
            }
        }

        private void EntryList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
            => EntryQueryBtn_Click(sender, e);

        private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool has = EntryList.SelectedItem != null;
            EditEntryBtn.IsEnabled = has;
            DelEntryBtn.IsEnabled = has;
        }

        private NativeMethods.Entry? TryGetRow(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is NativeMethods.Entry row) return row;
            return EntryList.SelectedItem as NativeMethods.Entry;
        }

        private async void EntryQueryBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = TryGetRow(sender, e);
            if (row == null) return;
            var full = _store.GetEntry(row.Id);
            if (full == null) { await ShowError("读取条目失败。"); return; }

            var acc = new TextBox { Text = full.Account, IsReadOnly = true, Width = 320 };
            var pwd = new TextBox { Text = full.Password, IsReadOnly = true, Width = 320 };
            var dlg = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = row.NoteDisplay,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "账号", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, acc, MakeCopy(acc) } },
                        new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "密码", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, pwd, MakeCopy(pwd) } }
                    }
                },
                CloseButtonText = "关闭"
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
            await dlg.ShowAsync();
        }

        private async void EntryEditBtn_Click(object sender, RoutedEventArgs e)
        {
            var row = TryGetRow(sender, e);
            if (row == null) return;
            var full = _store.GetEntry(row.Id);
            if (full == null) { await ShowError("读取条目失败。"); return; }

            var dlg = new EntryDialog();
            dlg.Init(_categories, full);
            dlg.XamlRoot = Content.XamlRoot;
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                long rc = _store.UpdateEntry(row.Id, dlg.CategoryId, dlg.Account, dlg.Password, dlg.Note);
                if (rc == NativeMethods.KSBOX_OK) { _store.Save(); RefreshEntryList(); }
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
            dlg.Init(_store, ApplyTheme);
            dlg.XamlRoot = Content.XamlRoot;
            await dlg.ShowAsync();
        }

        #endregion

        #region 辅助

        private Button MakeCopy(TextBox tb)
        {
            var copy = new Button { Content = "复制", Margin = new Thickness(0, 4, 0, 0) };
            copy.Click += async (_, _) =>
            {
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(tb.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                copy.Content = "已复制";
                await Task.Delay(1200);
                copy.Content = "复制";
            };
            return copy;
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
            await dlg.ShowAsync();
        }

        private void AnimateListIn()
        {
            for (int i = 0; i < EntryList.Items.Count; i++)
            {
                if (EntryList.ContainerFromIndex(i) is not ListViewItem item) continue;
                item.Opacity = 0;
                var tf = new TranslateTransform { Y = 12 };
                item.RenderTransform = tf;
                var sb = new Storyboard();
                var oa = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(0.28) };
                oa.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                Storyboard.SetTarget(oa, item);
                Storyboard.SetTargetProperty(oa, "Opacity");
                var ya = new DoubleAnimation { From = 12, To = 0, Duration = TimeSpan.FromSeconds(0.28) };
                ya.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                Storyboard.SetTarget(ya, tf);
                Storyboard.SetTargetProperty(ya, "Y");
                sb.Children.Add(oa);
                sb.Children.Add(ya);
                sb.BeginTime = TimeSpan.FromMilliseconds(i * 25);
                sb.Begin();
            }
        }

        #endregion
    }
}
