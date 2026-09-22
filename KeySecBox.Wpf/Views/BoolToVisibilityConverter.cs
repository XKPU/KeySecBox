using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KeySecBox.Views;

/// <summary>
/// bool → Visibility（排序/操作模式下按钮可用性显隐）。
/// 移植自 WinUI 版的 <c>KeySecBox.BoolToVisibilityConverter</c>；
/// WPF 版 IValueConverter 接口多了 CultureInfo 参数。
///
/// 之所以放在 KeySecBox.Views 而不是复用全局转换器：条目模板会离开 VaultPage
/// 的可视树被独立解析，模板内的 StaticResource 必须在页面资源内可解析，
/// 故这里就地注册一份（语义与 WinUI 版完全一致）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
