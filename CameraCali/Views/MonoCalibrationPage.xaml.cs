using System.Windows.Controls;
using CameraCali.ViewModels;

namespace CameraCali.Views;

/// <summary>
/// 单目标定页面。通过 DataContext 绑定 MonoCalibrationViewModel，
/// 提供棋盘格配置、图片导入、角点检测、标定执行及结果保存的 UI 交互。
/// </summary>
public partial class MonoCalibrationPage : Page
{
    /// <summary>
    /// 构造单目标定页面，接收 ViewModel 实例（由 DI 容器注入）。
    /// </summary>
    /// <param name="viewModel">单目标定页面的 ViewModel</param>
    public MonoCalibrationPage(MonoCalibrationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
