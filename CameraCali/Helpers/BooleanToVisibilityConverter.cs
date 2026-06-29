using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CameraCali.Helpers;

/// <summary>
/// 布尔值到 Visibility 枚举的转换器，用于 XAML 绑定。
/// true → Visible，false → Collapsed。
/// 常见用途：根据 ViewModel 中的 bool 属性控制 UI 元素的显示/隐藏。
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>正向转换：bool → Visibility</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>反向转换：Visibility → bool</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}
