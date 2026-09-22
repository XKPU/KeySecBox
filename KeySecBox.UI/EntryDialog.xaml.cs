using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

// 分类选择 UI：下拉单选 + 下方新建分类输入框，右侧「多分类」打开对话框内面板
public sealed partial class EntryDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    private List<NativeMethods.Category> _categories = new();
    private List<long> _multiCats = new();   // 多分类模式下的选中分类（不含未分类）
    private bool _multiMode;                  // 当前是否处于多分类模式
    private bool _multiSelectionMode;         // 多分类选择面板是否打开
    private readonly List<CheckBox> _multiBoxes = new();

    public List<long> CategoryIds { get; private set; } = new();
    public string Account { get; private set; } = "";
    public string Password { get; private set; } = "";
    public string Note { get; private set; } = "";
    public List<string> Recovery { get; private set; } = new();

    #region 初始化

    public EntryDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        CloseButtonClick += OnCloseButtonClick;
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    // preselectCatId：新增时预选当前浏览的分类（null 时不预选，保存时归入未分类）
    internal void Init(NativeMethods.Store store, List<NativeMethods.Category> categories,
        NativeMethods.Entry? existing = null, long? preselectCatId = null)
    {
        _store = store;
        _categories = categories ?? new();
        CategoryCombo.ItemsSource = _categories;

        var selected = existing != null
            ? existing.CategoryIds
            : (preselectCatId is { } id && _categories.Any(c => c.Id == id)
                ? new List<long> { id }
                : new List<long>());

        if (selected.Count > 1)
        {
            // 旧条目已属于多分类：直接进入多分类模式（过滤掉未分类）
            _multiCats = selected.Where(id => id != NativeMethods.UncatId).ToList();
            if (_multiCats.Count == 0) _multiCats = selected;
            _multiMode = _multiCats.Count > 0;
        }
        else
        {
            _multiMode = false;
            if (selected.Count > 0) CategoryCombo.SelectedValue = selected[0];
        }
        UpdateCatUI();

        if (existing != null)
        {
            Title = "编辑条目";
            AccountBox.Text = existing.Account;
            PasswordBox.Password = existing.Password;
            NoteBox.Text = existing.Note;
            RecoveryBox.Text = string.Join("\n", existing.Recovery);
        }
        else
        {
            Title = "新增条目";
        }
    }

    #endregion

    #region 多分类

    // 当前生效的选中分类：多分类模式用 _multiCats，否则用下拉单选（未分类不算）
    private List<long> CurrentSelection() => _multiMode
        ? _multiCats
        : (CategoryCombo.SelectedItem is NativeMethods.Category c && c.Id != NativeMethods.UncatId
            ? new List<long> { c.Id }
            : new List<long>());

    private void MultiCatBtn_Click(object sender, RoutedEventArgs e)
    {
        // 重建勾选列表（不展示「未分类」），默认选中当前下拉已选分类
        _multiBoxes.Clear();
        MultiCatPanel.Children.Clear();
        var presel = CurrentSelection();
        foreach (var c in _categories)
        {
            if (c.Id == NativeMethods.UncatId) continue; // 本面板不展示「未分类」
            var box = new CheckBox
            {
                Content = c.Name,
                Tag = c.Id,
                IsChecked = presel.Contains(c.Id),
                Margin = new Thickness(0, 2, 0, 2)
            };
            _multiBoxes.Add(box);
            MultiCatPanel.Children.Add(box);
        }
        MultiErrorText.Visibility = Visibility.Collapsed;
        // 互斥显示：隐藏单分类表单，避免透明背景穿透
        EntryPanel.Visibility = Visibility.Collapsed;
        MultiPanel.Visibility = Visibility.Visible;
        // 底部按钮直接替换：保存→确认，取消→返回单分类模式
        PrimaryButtonText = "确认";
        CloseButtonText = "返回单分类模式";
        _multiSelectionMode = true;
    }

    // 面板确认（底部「确认」按钮）：应用多分类 → 下拉变淡、只读，滚动展示全部选中分类
    private bool ApplyMultiSelection()
    {
        var ids = _multiBoxes
            .Where(b => b.IsChecked == true && b.Tag is long id)
            .Select(b => (long)b.Tag!)
            .ToList();
        if (ids.Count == 0)
        {
            MultiErrorText.Text = "请至少选择一个分类。";
            MultiErrorText.Visibility = Visibility.Visible;
            return false;
        }
        EnterMultiMode(ids);
        ExitSelectionPanel();
        return true;
    }

    // 面板「返回单分类模式」（底部「返回单分类模式」按钮）：关闭面板，若已在多分类模式则回到单选
    private void BackToSingleMode()
    {
        ExitSelectionPanel();
        if (_multiMode)
            ExitMultiMode();
    }

    // 关闭选择面板，恢复单分类表单与底部「保存/取消」按钮
    private void ExitSelectionPanel()
    {
        MultiPanel.Visibility = Visibility.Collapsed;
        EntryPanel.Visibility = Visibility.Visible;
        MultiErrorText.Visibility = Visibility.Collapsed;
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        _multiSelectionMode = false;
    }

    // 表单「关闭多分类模式」按钮：默认选中多选第一个分类并解禁下拉
    private void ExitMultiCatBtn_Click(object sender, RoutedEventArgs e)
    {
        ExitMultiMode();
    }

    private void ExitMultiMode()
    {
        if (_multiCats.Count > 0)
        {
            var first = _categories.FirstOrDefault(c => c.Id == _multiCats[0]);
            if (first != null) CategoryCombo.SelectedValue = first.Id;
        }
        _multiCats.Clear();
        _multiMode = false;
        UpdateCatUI();
    }

    private void EnterMultiMode(List<long> ids)
    {
        _multiCats = ids.Distinct().ToList();
        _multiMode = _multiCats.Count > 0;
        UpdateCatUI();
    }

    // 多分类模式 → 下拉隐藏、滚动条展示全部选中分类；单选 → 下拉可用
    private void UpdateCatUI()
    {
        bool multi = _multiMode;
        CategoryCombo.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        CategoryCombo.IsEnabled = !multi;
        MultiCatBtn.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        MultiBar.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        ExitMultiCatBtn.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        if (multi)
        {
            var names = _multiCats
                .Select(id => _categories.FirstOrDefault(c => c.Id == id)?.Name)
                .Where(n => !string.IsNullOrEmpty(n));
            MultiBarText.Text = string.Join("、", names);
        }
    }

    #endregion

    #region 新建分类

    private void NewCatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        long? rc = TryCreateCategory(NewCatBox.Text, out string err);
        if (rc is null)
        {
            ErrorText.Text = err;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (_multiMode)
        {
            _multiCats.Add(rc.Value);
            UpdateCatUI();
        }
        else
        {
            CategoryCombo.SelectedValue = rc.Value;
        }
        NewCatBox.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void NewCatBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            NewCatBtn_Click(sender, e);
            e.Handled = true;
        }
    }

    private void MultiNewCatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        long? rc = TryCreateCategory(MultiNewCatBox.Text, out string err);
        if (rc is null)
        {
            MultiErrorText.Text = err;
            MultiErrorText.Visibility = Visibility.Visible;
            return;
        }
        var box = new CheckBox
        {
            Content = MultiNewCatBox.Text.Trim(),
            Tag = rc.Value,
            IsChecked = true,
            Margin = new Thickness(0, 2, 0, 2)
        };
        _multiBoxes.Add(box);
        MultiCatPanel.Children.Add(box);
        MultiNewCatBox.Text = "";
        MultiErrorText.Visibility = Visibility.Collapsed;
    }

    private void MultiNewCatBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            MultiNewCatBtn_Click(sender, e);
            e.Handled = true;
        }
    }

    // 创建分类；成功返回新 Id 并刷新本地分类列表；失败返回 null 并给出中文错误
    private long? TryCreateCategory(string text, out string error)
    {
        error = "";
        var name = text.Trim();
        if (name.Length == 0)
        {
            error = "请输入分类名称。";
            return null;
        }
        long rc = _store!.AddCategory(name);
        if (rc == NativeMethods.KSBOX_ERR_DUP)
        {
            error = "已存在同名分类。";
            return null;
        }
        if (rc <= 0)
        {
            error = $"新建分类失败（错误码 {rc}）。";
            return null;
        }
        _store.Save();
        _categories = _store.ListCategories() ?? _categories;
        CategoryCombo.ItemsSource = _categories;
        return rc;
    }

    #endregion

    #region 提交

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 多分类选择面板打开时，主按钮为「确认」：仅应用多分类，不关闭对话框
        if (_multiSelectionMode)
        {
            args.Cancel = true;
            ApplyMultiSelection();
            return;
        }
        if (string.IsNullOrWhiteSpace(AccountBox.Text))
        {
            ErrorText.Text = "账户名不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }
        var cats = _multiMode
            ? _multiCats.ToList()
            : (CategoryCombo.SelectedItem is NativeMethods.Category c ? new List<long> { c.Id } : new List<long>());
        if (cats.Count == 0) cats.Add(NativeMethods.UncatId); // 未选择时归入内置「未分类」
        CategoryIds = cats;
        Account = AccountBox.Text.Trim();
        Password = PasswordBox.Password;
        Note = NoteBox.Text;
        Recovery = RecoveryBox.Text
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 多分类选择面板打开时，关闭按钮为「返回单分类模式」：不关闭对话框
        if (_multiSelectionMode)
        {
            args.Cancel = true;
            BackToSingleMode();
        }
    }

    public void ClearSecrets()
    {
        PasswordBox.Password = "";
        RecoveryBox.Text = "";
    }

    #endregion
}