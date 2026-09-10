using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

/// <summary>选择导入方式：CSV / JSON / 旧版库 1.0.x。</summary>
public sealed partial class ImportMethodDialog : ContentDialog
{
    private ImportMethod? _method;

    public ImportMethodDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    /// <summary>取出选定的导入方式（取走即置空）；用户取消时返回 null。</summary>
    internal ImportMethod? TakeMethod()
    {
        var m = _method;
        _method = null;
        return m;
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (MethodList.SelectedItem is RadioButton { Tag: string tag }
            && Enum.TryParse<ImportMethod>(tag, out var m))
            _method = m;
    }
}
