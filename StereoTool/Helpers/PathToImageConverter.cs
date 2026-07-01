using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace StereoTool.Helpers;

/// <summary>
/// 将文件路径字符串转换为 WPF BitmapImage 的转换器。
/// 用于 Image 控件的 Source 属性绑定图片路径。
/// 使用 CacheOption.OnLoad 避免文件锁定问题。
/// </summary>
[ValueConversion(typeof(string), typeof(BitmapImage))]
public class PathToImageConverter : IValueConverter
{
    /// <summary>将文件路径转换为 BitmapImage</summary>
    /// <param name="value">图片文件的完整路径字符串</param>
    /// <returns>BitmapImage 对象，路径无效或文件不存在时返回 null</returns>
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;  // 加载后立即释放文件锁
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();  // 允许跨线程使用
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>不实现反向转换</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("PathToImageConverter 不支持反向转换");
    }
}
