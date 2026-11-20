using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using StereoTool.ViewModels;

namespace StereoTool.Views;

/// <summary>
/// 双目测距页面。通过 DataContext 绑定 DistanceViewModel，
/// 提供标定文件加载、双目图像导入、像素点选择及距离测量的 UI 交互。
/// </summary>
public partial class DistancePage : Page
{
    /// <summary>
    /// 构造测距页面，接收 ViewModel 实例（由 DI 容器注入）。
    /// </summary>
    /// <param name="viewModel">双目测距页面的 ViewModel</param>
    public DistancePage(DistanceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// 将 Canvas 上的鼠标点击位置转换为对应图像的像素坐标。
    /// 考虑 Stretch=Uniform 模式下的缩放和居中偏移。
    /// </summary>
    /// <param name="canvas">接收点击事件的 Canvas 控件</param>
    /// <param name="bitmap">Canvas 下方绑定的图像 BitmapSource</param>
    /// <param name="clickPos">鼠标相对于 Canvas 的点击位置</param>
    /// <param name="pixelX">输出：映射后的图像像素 X 坐标</param>
    /// <param name="pixelY">输出：映射后的图像像素 Y 坐标</param>
    /// <param name="scale">输出：Uniform 缩放比例</param>
    /// <param name="offsetX">输出：图像在 Canvas 中的 X 偏移</param>
    /// <param name="offsetY">输出：图像在 Canvas 中的 Y 偏移</param>
    /// <returns>映射是否成功</returns>
    private static bool TryGetImagePixelCoords(
        System.Windows.Controls.Canvas canvas,
        BitmapSource bitmap,
        Point clickPos,
        out int pixelX, out int pixelY,
        out double scale, out double offsetX, out double offsetY)
    {
        pixelX = pixelY = 0;
        scale = offsetX = offsetY = 0;

        double canvasW = canvas.ActualWidth;
        double canvasH = canvas.ActualHeight;
        double imgW = bitmap.PixelWidth;
        double imgH = bitmap.PixelHeight;

        if (canvasW <= 0 || canvasH <= 0 || imgW <= 0 || imgH <= 0)
            return false;

        // 计算 Uniform 缩放后的图像尺寸
        double scaleX = canvasW / imgW;
        double scaleY = canvasH / imgH;
        scale = Math.Min(scaleX, scaleY);

        double renderedW = imgW * scale;
        double renderedH = imgH * scale;

        // 图像在 Canvas 中的偏移（居中）
        offsetX = (canvasW - renderedW) / 2.0;
        offsetY = (canvasH - renderedH) / 2.0;

        // 将点击坐标映射到图像像素坐标（边界裁剪）
        pixelX = (int)((clickPos.X - offsetX) / scale);
        pixelY = (int)((clickPos.Y - offsetY) / scale);

        return clickPos.X >= offsetX && clickPos.Y >= offsetY &&
            clickPos.X < offsetX + renderedW && clickPos.Y < offsetY + renderedH;
    }

    /// <summary>工作区尺寸改变后，按当前图像缩放比例重新定位选点标记。</summary>
    /// <param name="sender">尺寸变化的图像画布。</param>
    /// <param name="e">尺寸变化事件。</param>
    private void ImageCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (sender is not Canvas canvas || DataContext is not DistanceViewModel vm)
            return;

        var left = canvas == LeftImageCanvas || canvas == DisparityImageCanvas;
        var original = canvas == LeftImageCanvas || canvas == RightImageCanvas;
        var visible = left
            ? vm.MarkerVisible && vm.LeftPointIsOriginal == original
            : vm.RightMarkerVisible && vm.RightPointIsOriginal == original;
        if (!visible || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
            return;

        BitmapSource? bitmap = canvas.Name switch
        {
            nameof(LeftImageCanvas) => vm.LeftImage,
            nameof(RightImageCanvas) => vm.RightImage,
            nameof(DisparityImageCanvas) => vm.DisparityImage,
            nameof(ZImageCanvas) => vm.ZImage,
            _ => null
        };
        if (bitmap == null)
            return;

        var point = left ? vm.SelectedPoint : vm.RightSelectedPoint;
        var scale = Math.Min(canvas.ActualWidth / bitmap.PixelWidth, canvas.ActualHeight / bitmap.PixelHeight);
        var x = point.X * scale + (canvas.ActualWidth - bitmap.PixelWidth * scale) / 2 - 7;
        var y = point.Y * scale + (canvas.ActualHeight - bitmap.PixelHeight * scale) / 2 - 7;
        if (left)
        {
            vm.MarkerX = x;
            vm.MarkerY = y;
            return;
        }
        vm.RightMarkerX = x;
        vm.RightMarkerY = y;
    }

    /// <summary>
    /// 左图像 Canvas 上的鼠标点击事件处理。
    /// 将用户点击的 Canvas 坐标转换为图像坐标系下的像素坐标，
    /// 并更新 ViewModel 中的 SelectedPoint 以触发距离测量。
    /// </summary>
    private void LeftImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DistanceViewModel vm)
            return;

        Point clickPos = e.GetPosition(LeftImageCanvas);

        if (vm.LeftImage is not BitmapSource leftBmp)
            return;

        if (!TryGetImagePixelCoords(LeftImageCanvas, leftBmp, clickPos,
                out int pixelX, out int pixelY,
                out double scale, out double offsetX, out double offsetY))
            return;

        // 更新 ViewModel 中的选点坐标
        vm.SelectedPoint = new System.Drawing.Point(pixelX, pixelY);
        vm.LeftPointIsOriginal = true;

        // 同步设置标记点在 Canvas 中的坐标（将图像像素坐标反算回 Canvas 坐标）
        vm.MarkerX = pixelX * scale + offsetX - 7;  // -7 使圆点居中（Ellipse 宽高 14 的一半）
        vm.MarkerY = pixelY * scale + offsetY - 7;
        vm.MarkerVisible = true;

        var rectified = vm.RectifyPixel(pixelX, pixelY, true);
        var (x, y, z) = double.IsFinite(rectified.X) && double.IsFinite(rectified.Y)
            ? vm.GetXYZ((int)Math.Round(rectified.X), (int)Math.Round(rectified.Y))
            : (double.NaN, double.NaN, double.NaN);

        if (z <= 0 || double.IsNaN(z) || double.IsInfinity(z))
            z = double.NaN;
        vm.SetMeasurement(x, y, z);

        vm.StatusText = $"测量点已选择({pixelX}, {pixelY}): (x, y, z)=({x:F3}, {y:F3}, {z:F3})";
    }

    /// <summary>
    /// 右图像 Canvas 上的鼠标点击事件处理。
    /// 与 LeftImage_MouseDown 对称，将 Canvas 坐标映射为图像像素坐标。
    /// </summary>
    private void RightImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DistanceViewModel vm)
            return;

        Point clickPos = e.GetPosition(RightImageCanvas);

        if (vm.RightImage is not BitmapSource rightBmp)
            return;

        if (!TryGetImagePixelCoords(RightImageCanvas, rightBmp, clickPos,
                out int pixelX, out int pixelY,
                out double scale, out double offsetX, out double offsetY))
            return;

        vm.RightSelectedPoint = new System.Drawing.Point(pixelX, pixelY);
        vm.RightPointIsOriginal = true;

        vm.RightMarkerX = pixelX * scale + offsetX - 7;
        vm.RightMarkerY = pixelY * scale + offsetY - 7;
        vm.RightMarkerVisible = true;
        vm.SetMeasurement(double.NaN, double.NaN, double.NaN);
        vm.StatusText = $"右图测量点已选择：({pixelX}, {pixelY}) — 左右点就绪后可执行手动测距";
    }

    /// <summary>读取校正视差图中的像素视差及三维坐标。</summary>
    /// <param name="sender">视差图画布。</param>
    /// <param name="e">鼠标事件。</param>
    private void DisparityImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DistanceViewModel vm)
            return;

        Point clickPos = e.GetPosition(DisparityImageCanvas);

        if (vm.DisparityImage is not BitmapSource disparityImg)
            return;

        if (!TryGetImagePixelCoords(DisparityImageCanvas, disparityImg, clickPos,
                out int pixelX, out int pixelY,
                out double scale, out double offsetX, out double offsetY))
            return;

        // 更新 ViewModel 中的选点坐标
        vm.SelectedPoint = new System.Drawing.Point(pixelX, pixelY);
        vm.LeftPointIsOriginal = false;

        // 同步设置标记点在 Canvas 中的坐标（将图像像素坐标反算回 Canvas 坐标）
        vm.MarkerX = pixelX * scale + offsetX - 7;  // -7 使圆点居中（Ellipse 宽高 14 的一半）
        vm.MarkerY = pixelY * scale + offsetY - 7;
        vm.MarkerVisible = true;

        // 从 XYZ 缓存直接读取距离值
        double d = vm.GetDisparity(pixelX, pixelY);
        var (x, y, z) = vm.GetXYZ(pixelX, pixelY);
        vm.SetMeasurement(x, y, z);
        
        vm.StatusText = $"测量点已选择：({pixelX}, {pixelY}), 视差：{d}";
    }

    /// <summary>读取校正深度图中的三维坐标。</summary>
    /// <param name="sender">深度图画布。</param>
    /// <param name="e">鼠标事件。</param>
    private void ZImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DistanceViewModel vm)
            return;

        Point clickPos = e.GetPosition(ZImageCanvas);

        if (vm.ZImage is not BitmapSource zBmp)
            return;

        if (!TryGetImagePixelCoords(ZImageCanvas, zBmp, clickPos,
                out int pixelX, out int pixelY,
                out double scale, out double offsetX, out double offsetY))
            return;

        vm.RightSelectedPoint = new System.Drawing.Point(pixelX, pixelY);
        vm.RightPointIsOriginal = false;

        vm.RightMarkerX = pixelX * scale + offsetX - 7;
        vm.RightMarkerY = pixelY * scale + offsetY - 7;
        vm.RightMarkerVisible = true;

        var (x, y, z) = vm.GetXYZ(pixelX, pixelY);

        if (z <= 0 || double.IsNaN(z) || double.IsInfinity(z))
            z = double.NaN;
        vm.SetMeasurement(x, y, z);

        vm.StatusText = $"3D点云图已选择点({pixelX}, {pixelY}): (x, y, z)=({x:F3}, {y:F3}, {z:F3})";
    }
}
