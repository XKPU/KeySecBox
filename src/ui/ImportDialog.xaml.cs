using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class ImportDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    public long CategoryId { get; private set; }

    public ImportDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(NativeMethods.Store store, List<NativeMethods.Category> categories, int count)
    {
        _store = store;
        SubText.Text = $"即将导入 {count} 条记录，请选择目标分类：";
        CategoryCombo.ItemsSource = categories;
    }

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
        CategoryCombo.ItemsSource = _store.ListCategories();
        CategoryCombo.SelectedValue = rc;
        NewCatBox.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region 提交

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (CategoryCombo.SelectedValue is long id && id >= 0)
        {
            CategoryId = id;
            return;
        }
        CategoryId = NativeMethods.UncatId; // 未选择分类则归入内置"未分类"
    }

    #endregion
}
