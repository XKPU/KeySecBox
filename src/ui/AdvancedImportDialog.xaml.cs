using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

/// <summary>
/// 高级导入：左侧指定各字段对应的键名（分类亦可固定为某个分类），右侧展示原始内容供查看复制。
/// 源数据为本程序标准格式时自动填入键名，用户可再修改。
/// </summary>
public sealed partial class AdvancedImportDialog : ContentDialog
{
    // 原始内容展示上限
    private const int MaxPreviewChars = 200_000;

    // 对话框期望宽度；实际取「窗口可用宽度 × 0.96」与它的较小值，避免超出窗口被裁剪
    private const double PreferredWidth = 1800;
    private const double MinDialogWidth = 1200;

    private NativeMethods.Store? _store;
    private FieldMapping? _mapping;

    public AdvancedImportDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DialogAnim.Play(this);

        // ContentDialog 宽度由 MinWidth 决定（内容为星号列、无固有宽度），
        // 此处按窗口可用宽度尽量放宽，同时留出余量避免超出窗口被裁剪。
        try
        {
            double avail = XamlRoot?.Size.Width ?? 0;
            if (avail > 0)
                MinWidth = Math.Min(PreferredWidth, Math.Max(MinDialogWidth, avail * 0.96));
        }
        catch
        {
            // 取不到窗口尺寸时沿用 XAML 中的默认值
        }
    }

    internal void Init(NativeMethods.Store store, ImportSource source, string fileName)
    {
        _store = store;
        Title = $"高级导入 - {fileName}";
        CategoryCombo.ItemsSource = store.ListCategories();

        HeadersHint.Text = source.Headers.Count > 0
            ? $"检测到的字段（{source.Count} 条记录）：{string.Join("、", source.Headers)}"
            : $"未检测到字段名（{source.Count} 条记录）。";

        RawContent.Text = source.RawText.Length <= MaxPreviewChars
            ? source.RawText
            : source.RawText.Substring(0, MaxPreviewChars) + $"{Environment.NewLine}…（内容过长，已截断预览）";

        if (source.TryAutoMap() is { } auto)
        {
            // 自动映射已按 ${key} 规范格式化
            CategoryKeyBox.Text = auto.CategoryKey?.Raw ?? "";
            AccountKeyBox.Text = auto.AccountKey.Raw;
            PasswordKeyBox.Text = auto.PasswordKey.Raw;
            NoteKeysBox.Text = string.Join("\n", auto.NoteKeys.Select(k => k.Raw));
            RecoveryKeysBox.Text = string.Join("\n", auto.RecoveryKeys.Select(k => k.Raw));
            AutoFillHint.Visibility = Visibility.Visible;
        }
    }

    /// <summary>取出字段映射（取走即置空）；取消或校验未通过时返回 null。</summary>
    internal FieldMapping? TakeMapping()
    {
        var m = _mapping;
        _mapping = null;
        return m;
    }

    #region 新建分类

    private void NewCatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;

        var name = NewCatBox.Text.Trim();
        if (name.Length == 0) { ShowError("请输入分类名称。"); return; }

        long rc = _store.AddCategory(name);
        if (rc == NativeMethods.KSBOX_ERR_DUP) { ShowError("已存在同名分类。"); return; }
        if (rc <= 0) { ShowError($"新建分类失败（错误码 {rc}）。"); return; }

        _store.Save();
        CategoryCombo.ItemsSource = _store.ListCategories();
        CategoryCombo.SelectedValue = rc;
        NewCatBox.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region 提交

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var account = AccountKeyBox.Text.Trim();
        var password = PasswordKeyBox.Text.Trim();
        if (account.Length == 0 || password.Length == 0)
        {
            args.Cancel = true; // 必填校验不通过，保持对话框打开
            ShowError("账号名称与账户密码为必填项，请填入对应的键名。");
            return;
        }

        _mapping = new FieldMapping
        {
            CategoryKey = string.IsNullOrWhiteSpace(CategoryKeyBox.Text) ? null : KeyExpression.Parse(CategoryKeyBox.Text),
            FixedCategoryId = CategoryCombo.SelectedValue is long id && id >= 0 ? id : null,
            AccountKey = KeyExpression.Parse(account),
            PasswordKey = KeyExpression.Parse(password),
            NoteKeys = KeyExpression.ParseLines(NoteKeysBox.Text),
            RecoveryKeys = KeyExpression.ParseLines(RecoveryKeysBox.Text)
        };
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    #endregion
}
