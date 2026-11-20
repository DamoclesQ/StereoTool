using System.Windows.Controls;
using StereoTool.ViewModels;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace StereoTool.Views;

/// <summary>
/// 双目标定页面。通过 DataContext 绑定 StereoCalibrationViewModel，
/// 提供棋盘格配置、图片导入、标定执行及结果保存的 UI 交互。
/// </summary>
public partial class StereoCalibrationPage : Page
{
    /// <summary>
    /// 构造标定页面，接收 ViewModel 实例（由 DI 容器注入）。
    /// </summary>
    /// <param name="viewModel">双目标定页面的 ViewModel</param>
    public StereoCalibrationPage(StereoCalibrationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>左右预览更新后等待布局完成，再适配各自缩放区域。</summary>
    /// <param name="sender">预览图像控件。</param>
    /// <param name="e">图像绑定更新事件。</param>
    private void PreviewImage_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var parent = VisualTreeHelper.GetParent((Image)sender);
            while (parent != null && parent is not Xceed.Wpf.Toolkit.Zoombox.Zoombox)
                parent = VisualTreeHelper.GetParent(parent);
            if (parent is Xceed.Wpf.Toolkit.Zoombox.Zoombox zoombox)
                zoombox.FitToBounds();
        }));
    }
}
