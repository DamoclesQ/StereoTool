using System.Globalization;
using System.Windows.Data;

namespace CameraCali.Helpers;

/// <summary>
/// 布尔值取反转换器，用于 XAML 绑定。
/// 当绑定的布尔值为 true 时返回 false，反之亦然。
/// 常见用途：在 IsBusy 为 true 时禁用按钮。
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public class BooleanInvertConverter : IValueConverter
{
    /// <summary>正向转换：取反布尔值</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }

    /// <summary>反向转换：取反布尔值（对称操作）</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }
}
