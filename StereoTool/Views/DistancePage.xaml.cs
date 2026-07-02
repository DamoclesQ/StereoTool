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
    /// 左图像 Canvas 上的鼠标点击事件处理。
    /// 将用户点击的 Canvas 坐标转换为图像坐标系下的像素坐标，
    /// 然后通过 ViewModel 的 OnImageClickedWithDistance 完成选点记录和即时测距。
    /// </summary>
    private void LeftImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DistanceViewModel vm)
            return;

        // 获取相对于 Canvas 的点击位置
        Point clickPos = e.GetPosition(LeftImageCanvas);

        if (vm.LeftImage is not BitmapSource leftBmp)
            return;

        // 从 Canvas 坐标反算图像像素坐标（考虑 Stretch=Uniform 缩放）
        double canvasW = LeftImageCanvas.ActualWidth;
        double canvasH = LeftImageCanvas.ActualHeight;
        double imgW = leftBmp.PixelWidth;
        double imgH = leftBmp.PixelHeight;

        if (canvasW <= 0 || canvasH <= 0 || imgW <= 0 || imgH <= 0)
            return;

        // 计算 Uniform 缩放后的图像尺寸
        double scaleX = canvasW / imgW;
        double scaleY = canvasH / imgH;
        double scale = Math.Min(scaleX, scaleY);

        double renderedW = imgW * scale;
        double renderedH = imgH * scale;

        // 图像在 Canvas 中的偏移（居中）
        double offsetX = (canvasW - renderedW) / 2.0;
        double offsetY = (canvasH - renderedH) / 2.0;

        // 将点击坐标映射到图像像素坐标（边界裁剪）
        int pixelX = (int)((clickPos.X - offsetX) / scale);
        int pixelY = (int)((clickPos.Y - offsetY) / scale);

        pixelX = Math.Clamp(pixelX, 0, (int)imgW - 1);
        pixelY = Math.Clamp(pixelY, 0, (int)imgH - 1);

        // 同步设置标记点在 Canvas 中的坐标（将图像像素坐标反算回 Canvas 坐标）
        vm.MarkerX = pixelX * scale + offsetX - 7;  // -7 使圆点居中（Ellipse 宽高 14 的一半）
        vm.MarkerY = pixelY * scale + offsetY - 7;
        vm.MarkerVisible = true;

        // 选点后立即从缓存的 XYZ 数据中提取距离（即时测距）
        // OnImageClickedWithDistance 内部处理 SelectedPoint 设置、距离计算和 StatusText
        vm.OnImageClickedWithDistance(pixelX, pixelY);
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

        double canvasW = RightImageCanvas.ActualWidth;
        double canvasH = RightImageCanvas.ActualHeight;
        double imgW = rightBmp.PixelWidth;
        double imgH = rightBmp.PixelHeight;

        if (canvasW <= 0 || canvasH <= 0 || imgW <= 0 || imgH <= 0)
            return;

        double scaleX = canvasW / imgW;
        double scaleY = canvasH / imgH;
        double scale = Math.Min(scaleX, scaleY);

        double offsetX = (canvasW - imgW * scale) / 2.0;
        double offsetY = (canvasH - imgH * scale) / 2.0;

        int pixelX = (int)((clickPos.X - offsetX) / scale);
        int pixelY = (int)((clickPos.Y - offsetY) / scale);

        pixelX = Math.Clamp(pixelX, 0, (int)imgW - 1);
        pixelY = Math.Clamp(pixelY, 0, (int)imgH - 1);

        vm.RightSelectedPoint = new System.Drawing.Point(pixelX, pixelY);

        vm.RightMarkerX = pixelX * scale + offsetX - 7;
        vm.RightMarkerY = pixelY * scale + offsetY - 7;
        vm.RightMarkerVisible = true;
        vm.StatusText = $"右图测量点已选择：({pixelX}, {pixelY}) — 左右点就绪后可执行手动测距";
    }
}
