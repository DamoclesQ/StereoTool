using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Input;
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

        pixelX = Math.Clamp(pixelX, 0, (int)imgW - 1);
        pixelY = Math.Clamp(pixelY, 0, (int)imgH - 1);
        return true;
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

        vm.SelectedPoint = new System.Drawing.Point(pixelX, pixelY);

        // 同步设置标记点在 Canvas 中的坐标（将图像像素坐标反算回 Canvas 坐标）
        vm.MarkerX = pixelX * scale + offsetX - 7;  // -7 使圆点居中（Ellipse 宽高 14 的一半）
        vm.MarkerY = pixelY * scale + offsetY - 7;
        vm.MarkerVisible = true;
        vm.StatusText = $"测量点已选择：({pixelX}, {pixelY}) — 就绪，可执行测距";
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

        vm.RightMarkerX = pixelX * scale + offsetX - 7;
        vm.RightMarkerY = pixelY * scale + offsetY - 7;
        vm.RightMarkerVisible = true;
        vm.StatusText = $"右图测量点已选择：({pixelX}, {pixelY}) — 左右点就绪后可执行手动测距";
    }

    /// <summary>
    /// 视差图 Tab 中左图 Canvas 的鼠标点击事件处理。
    /// 提取像素坐标后从 XYZ 缓存直接读取距离值，并同步在视差图上显示标记点。
    /// </summary>
    private void DisparityTab_LeftImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DistanceViewModel vm)
            return;

        // 使用 sender 获取触发事件的 Canvas，支持原图 Tab 和视差图 Tab 共用
        if (sender is not System.Windows.Controls.Canvas canvas)
            return;

        Point clickPos = e.GetPosition(canvas);

        if (vm.LeftImage is not BitmapSource leftBmp)
            return;

        if (!TryGetImagePixelCoords(canvas, leftBmp, clickPos,
                out int pixelX, out int pixelY,
                out double scale, out double offsetX, out double offsetY))
            return;

        // 更新 ViewModel 中的选点坐标
        vm.SelectedPoint = new System.Drawing.Point(pixelX, pixelY);

        // 计算 Canvas 中的标记位置（Ellipse 宽高 14，减 7 使圆心对齐点击点）
        vm.MarkerX = pixelX * scale + offsetX - 7;
        vm.MarkerY = pixelY * scale + offsetY - 7;
        vm.MarkerVisible = true;

        // 从 XYZ 缓存直接读取距离值
        double distance = vm.GetDistanceFromXYZ(pixelX, pixelY);

        if (!double.IsNaN(distance))
        {
            vm.DistanceResult = distance;
            vm.StatusText = $"像素 ({pixelX}, {pixelY}) 的距离：{distance:F2} mm（从XYZ缓存提取）";
        }
        else
        {
            vm.DistanceResult = 0.0;
            vm.StatusText = $"像素 ({pixelX}, {pixelY}) 处无法计算距离（视差无效或越界）";
        }
    }
}
