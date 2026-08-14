using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KeySecBox;

// 内置“未分类”分类（Id = 0）不可改名/删除，隐藏行内按钮
internal sealed class CategoryActionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is long id && id == NativeMethods.UncatId ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new System.NotImplementedException();
}
