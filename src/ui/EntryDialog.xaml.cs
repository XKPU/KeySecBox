using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace KeySecBox;

public sealed partial class EntryDialog : ContentDialog
{
    public long CategoryId { get; private set; }
    public string Account { get; private set; } = "";
    public string Password { get; private set; } = "";
    public string Note { get; private set; } = "";

    public EntryDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(List<NativeMethods.Category> categories, NativeMethods.Entry? existing = null)
    {
        CategoryCombo.ItemsSource = categories;
        if (existing != null)
        {
            Title = "编辑条目";
            CategoryCombo.SelectedValue = existing.CategoryId;
            AccountBox.Text = existing.Account;
            PasswordBox.Password = existing.Password;
            NoteBox.Text = existing.Note;
        }
        else
        {
            Title = "新增条目";
            // 默认不预选分类：用户可不选择，此时归入“未分类”
            CategoryCombo.SelectedIndex = -1;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 未选择分类时归入内置“未分类”（id=0）；下拉项本身也含“未分类”可显式选择
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
    }
}