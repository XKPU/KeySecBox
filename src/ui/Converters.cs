using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KeySecBox;

// bool -> Visibility（排序模式下按钮/工具栏显隐）
internal sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new System.NotImplementedException();
}
