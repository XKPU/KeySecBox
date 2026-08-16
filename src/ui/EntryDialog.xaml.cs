using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class EntryDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    public long CategoryId { get; private set; }
    public string Account { get; private set; } = "";
    public string Password { get; private set; } = "";
    public string Note { get; private set; } = "";
    public List<string> Recovery { get; private set; } = new();

    #region 初始化

    public EntryDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(NativeMethods.Store store, List<NativeMethods.Category> categories, NativeMethods.Entry? existing = null)
    {
        _store = store;
        CategoryCombo.ItemsSource = categories;
        if (existing != null)
        {
            Title = "编辑条目";
            CategoryCombo.SelectedValue = existing.CategoryId;
            AccountBox.Text = existing.Account;
            PasswordBox.Password = existing.Password;
            NoteBox.Text = existing.Note;
            RecoveryBox.Text = string.Join("\n", existing.Recovery);
        }
        else
        {
            Title = "新增条目";
            CategoryCombo.SelectedIndex = -1; // 不预选：未选择时归入"未分类"
        }
    }

    #endregion

    #region 新建分类

    private void NewCatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        var name = NewCatBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "请输入分类名称。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        long rc = _store.AddCategory(name);
        if (rc == NativeMethods.KSBOX_ERR_DUP)
        {
            ErrorText.Text = "已存在同名分类。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (rc <= 0)
        {
            ErrorText.Text = $"新建分类失败（错误码 {rc}）。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _store.Save();
        // 刷新下拉并选中新分类
        CategoryCombo.ItemsSource = _store.ListCategories();
        CategoryCombo.SelectedValue = rc;
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

    #endregion

    #region 提交

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 未选择分类时归入内置"未分类"(id=0)
        long catId = CategoryCombo.SelectedValue is long id ? id : NativeMethods.UncatId;
        if (string.IsNullOrWhiteSpace(AccountBox.Text))
        {
            ErrorText.Text = "账户名不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }
        CategoryId = catId;
        Account = AccountBox.Text.Trim();
        Password = PasswordBox.Password;
        Note = NoteBox.Text;
        // 恢复密钥：每行一个，忽略空行
        Recovery = RecoveryBox.Text
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    // 取走密码/恢复密钥后调用：清空输入框，断开敏感数据引用
    public void ClearSecrets()
    {
        PasswordBox.Password = "";
        RecoveryBox.Text = "";
    }

    #endregion
}
