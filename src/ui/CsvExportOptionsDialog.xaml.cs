using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

/// <summary>CSV 导出选项：分类筛选、是否保留分类列、字段名自定义。</summary>
public sealed partial class CsvExportOptionsDialog : ContentDialog
{
    private CsvExportPreferences? _prefs;

    public CsvExportOptionsDialog()
    {
        InitializeComponent();
        // WinUI 3 无法在 XAML 中直接给 bool? 属性赋字面量，故在此设置默认值
        IncludeCategoryBox.IsChecked = true;
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(NativeMethods.Store store)
    {
        var items = new List<CategoryItem>();
        foreach (var c in store.ListCategories())
        {
            if (c.Id == NativeMethods.UncatId) continue; // 「未分类」单独置于末尾
            items.Add(new CategoryItem { Id = c.Id, Name = c.Name });
        }
        items.Add(new CategoryItem { Id = NativeMethods.UncatId, Name = "未分类" });
        CategoryList.ItemsSource = items;

        // 字段名默认填英文，留空时回退到默认英文
        CategoryHeader.Text = ExportNames.Category;
        AccountHeader.Text = ExportNames.Account;
        PasswordHeader.Text = ExportNames.Password;
        NoteHeader.Text = ExportNames.Note;
        RecoveryHeader.Text = ExportNames.Recovery;
    }

    /// <summary>收集偏好；用户取消（关闭按钮）时返回 null。</summary>
    internal CsvExportPreferences? TakePrefs()
    {
        var p = _prefs;
        _prefs = null;
        return p;
    }

    private void IncludeCategoryBox_Changed(object sender, RoutedEventArgs e)
        => CategoryHeader.IsEnabled = IncludeCategoryBox.IsChecked == true;

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var selected = CategoryList.SelectedItems
            .Cast<CategoryItem>()
            .Select(c => c.Id)
            .ToHashSet();

        var names = new[]
        {
            string.IsNullOrWhiteSpace(CategoryHeader.Text) ? ExportNames.Category : CategoryHeader.Text.Trim(),
            string.IsNullOrWhiteSpace(AccountHeader.Text) ? ExportNames.Account : AccountHeader.Text.Trim(),
            string.IsNullOrWhiteSpace(PasswordHeader.Text) ? ExportNames.Password : PasswordHeader.Text.Trim(),
            string.IsNullOrWhiteSpace(NoteHeader.Text) ? ExportNames.Note : NoteHeader.Text.Trim(),
            string.IsNullOrWhiteSpace(RecoveryHeader.Text) ? ExportNames.Recovery : RecoveryHeader.Text.Trim(),
        };

        _prefs = new CsvExportPreferences
        {
            // 未选中任何分类 => 导出全部分类（null 语义）
            CategoryIds = selected.Count > 0 ? selected : null,
            IncludeCategory = IncludeCategoryBox.IsChecked != false,
            HeaderNames = names
        };
    }
}

/// <summary>分类选择项（ListView 绑定用）。</summary>
internal sealed class CategoryItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}
