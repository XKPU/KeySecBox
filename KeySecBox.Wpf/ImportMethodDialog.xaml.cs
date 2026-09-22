using System;
using System.Windows;
using System.Windows.Controls;

namespace KeySecBox;

/// <summary>选择导入方式：CSV / JSON / 旧版库 1.0.x。</summary>
public partial class ImportMethodDialog : ContentDialogBase
{
    private ImportMethod? _method;

    public ImportMethodDialog()
    {
        InitializeComponent();
        CloseButtonText = "";          // 用自定义「取消」按钮
        AttachButtons(OkBtn, CancelBtn);

        // 原 PrimaryButtonClick 在点主按钮时读取选中项；WPF 下等价订阅基类事件
        PrimaryButtonClick += OnPrimaryButtonClick;

        Loaded += (_, _) => DialogAnim.Play(this);
    }

    /// <summary>取出选定的导入方式（取走即置空）；用户取消时返回 null。</summary>
    internal ImportMethod? TakeMethod()
    {
        var m = _method;
        _method = null;
        return m;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => HideDialog();

    private void OnPrimaryButtonClick(object? sender, DialogButtonClickEventArgs args)
    {
        // WinUI: RadioButtons.SelectedItem 是选中的 RadioButton
        if (SelectedTag() is { } tag && Enum.TryParse<ImportMethod>(tag, out var m))
            _method = m;
    }

    private string? SelectedTag()
    {
        if (CsvRadio.IsChecked == true) return CsvRadio.Tag as string;
        if (JsonRadio.IsChecked == true) return JsonRadio.Tag as string;
        if (LegacyRadio.IsChecked == true) return LegacyRadio.Tag as string;
        return null;
    }
}
