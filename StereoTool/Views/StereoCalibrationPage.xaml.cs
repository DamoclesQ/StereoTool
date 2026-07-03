using System.Windows.Controls;
using StereoTool.ViewModels;

namespace StereoTool.Views;

/// <summary>
/// 双目标定页面。通过 DataContext 绑定 CalibrationViewModel，
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
}
