using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class MainWindow
{
    private void CategoryList_Loaded(object sender, RoutedEventArgs e)
    {
        SetScope(all: true); // 默认范围为「全部」
    }

    private void AllScopeBtn_Click(object sender, RoutedEventArgs e)
    {
        SetScope(all: true, scopeSwitch: true);
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = CategoryList.SelectedItem as CategoryItem;
        if (sel == null) return;
        _selectedCategory = sel;
        _allScope = false;
        RefreshScopeVisual();
        RefreshEntries(scopeSwitch: true);
    }

    #region 分类排序模式

    // 当前界面展示的分类顺序（与 RefreshCategoryList 判定一致）
    private List<CategoryItem> GetCategoryShownOrder()
    {
        var uncatCount = _vault.QueryCategory(VaultStore.UncatId).Count;
        var all = _vault.ListCategories();
        var shown = uncatCount == 0
            ? all.Where(c => c.Id != VaultStore.UncatId)
            : all;
        return shown.Select(c => new CategoryItem
        {
            Id = c.Id, Name = c.Name
        }).ToList();
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
            c.ShowSortArrows = true;
            c.ShowActionButtons = false;
            c.CanMoveUp = i > 0 && _sortWorking[i - 1].Id != VaultStore.UncatId;
            c.CanMoveDown = i < n - 1;
        }
        SyncInPlace(_categoryItems, _sortWorking, c => c.Id);
    }

    private void CatMoveUpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is CategoryItem cat)
        {
            int i = IndexOfId(_sortWorking, cat.Id, c => c.Id);
            if (i <= 0) return;
            if (_sortWorking[i - 1].Id == VaultStore.UncatId) return; // 未分类恒居首位
            var oldTops = CaptureCategoryTops();
            (_sortWorking[i - 1], _sortWorking[i]) = (_sortWorking[i], _sortWorking[i - 1]);
            RefreshSortWorking();
            AnimateCategoryMove(oldTops);
        }
    }

    private void CatMoveDownBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is CategoryItem cat)
        {
            int i = IndexOfId(_sortWorking, cat.Id, c => c.Id);
            if (i < 0 || i >= _sortWorking.Count - 1) return;
            var oldTops = CaptureCategoryTops();
            (_sortWorking[i], _sortWorking[i + 1]) = (_sortWorking[i + 1], _sortWorking[i]);
            RefreshSortWorking();
            AnimateCategoryMove(oldTops);
        }
    }

    // 保存：按工作顺序回写保险库，退出排序模式并刷新
    private async void SortSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        bool uncatPinned = _sortWorking.Count > 0 && _sortWorking[0].Id == VaultStore.UncatId;
        ErrorCodes rc = ErrorCodes.Ok;
        for (int i = 0; i < _sortWorking.Count; i++)
        {
            var cat = _sortWorking[i];
            if (cat.Id == VaultStore.UncatId) continue;
            int pos = uncatPinned ? i : i + 1; // "未分类"占位或整体前移
            rc = _vault.MoveCategory(cat.Id, pos);
            if (rc != ErrorCodes.Ok) break;
        }
        if (rc != ErrorCodes.Ok)
        {
            await ShowError($"保存排序失败（错误码 {rc}）。");
            return;
        }
        var svc = _vault.Save();
        ExitCategorySortMode();
        ReloadCategoriesKeepScope();
        if (svc != ErrorCodes.Ok)
            await ShowError($"排序已生效，但保存失败（错误码 {svc}），重启后可能丢失。");
    }

    // 取消：不写回保险库，直接按原顺序刷新
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

    #region 增删改

    private async void AddCatBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "新建分类",
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var box = new TextBox { PlaceholderText = "分类名称" };
        dlg.Content = box;
        ThemeDialog(dlg);
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = box.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            // AddCategory 重名时返回 (long)ErrorCodes.Dup，而新分类 Id 也同为正数区间，
            // 故先自行查重，避免把错误码误判为新 Id。
            if (name == VaultStore.UncatName || _vault.ListCategories().Any(c => c.Name == name))
            {
                await ShowError("已存在同名分类。");
                return;
            }
            var id = _vault.AddCategory(name);
            if (id <= 0)
            {
                await ShowError(id == -(long)ErrorCodes.Dup ? "已存在同名分类。" : "新建分类失败。");
                return;
            }
            var svc = _vault.Save();
            if (svc != ErrorCodes.Ok)
                await ShowError($"分类已加入内存，但保存失败（错误码 {svc}），重启后可能丢失。");
            ReloadCategoriesKeepScope();
        }
    }

    private async void RenameCatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not CategoryItem cat) return;

        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "重命名分类",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var box = new TextBox { Text = cat.Name };
        dlg.Content = box;
        ThemeDialog(dlg);
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = box.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var rc = _vault.RenameCategory(cat.Id, name);
            if (rc == ErrorCodes.Ok)
            {
                _vault.Save();
                ReloadCategoriesKeepScope();
            }
            else await ShowError($"重命名失败（错误码 {rc}）。");
        }
    }

    private async void DelCatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not CategoryItem cat) return;

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
            var rc = _vault.RemoveCategory(cat.Id);
            if (rc == ErrorCodes.Ok)
            {
                _vault.Save();
                SetScope(all: true, scopeSwitch: true);
            }
            else await ShowError($"删除失败（错误码 {rc}）。");
        }
    }

    #endregion

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            var dlg = new SettingsDialog(this, _appConfig);
            ThemeDialog(dlg);
            await dlg.ShowAsync();
            ApplyTheme(_appConfig.Theme);
        });
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchText = sender.Text.Trim();
        RefreshEntries();
    }
}
