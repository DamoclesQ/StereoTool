using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using StereoTool.ViewModels;
using StereoTool.Views;
using Microsoft.Extensions.DependencyInjection;

namespace StereoTool;

/// <summary>
/// 应用程序主窗口。
/// 左侧导航栏通过数据绑定切换页面，右侧 Frame 承载标定页面或测距页面。
/// 通过构造器注入 IServiceProvider 和 MainViewModel。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MainViewModel _viewModel;

    /// <summary>
    /// 初始化主窗口，配置 DI 注入和导航事件订阅。
    /// </summary>
    /// <param name="serviceProvider">DI 容器，用于解析 Page 实例</param>
    /// <param name="viewModel">主窗口 ViewModel</param>
    public MainWindow(IServiceProvider serviceProvider, MainViewModel viewModel)
    {
        InitializeComponent();

        _serviceProvider = serviceProvider;
        _viewModel = viewModel;
        DataContext = _viewModel;

        // 订阅导航请求事件
        _viewModel.NavigateRequested += OnNavigateRequested;

        // 启动后默认导航到测距页面（Loaded 时 Frame 和事件订阅均已就绪）
        Loaded += (_, _) => NavigateTo("Distance");
    }

    /// <summary>
    /// 响应 MainViewModel 的导航请求，从 DI 容器获取对应 Page 并导航。
    /// </summary>
    /// <param name="pageName">页面标识："Calibration" 或 "Distance"</param>
    private void OnNavigateRequested(string pageName)
    {
        NavigateTo(pageName);
    }

    /// <summary>
    /// 根据页面名称执行 Frame 导航。
    /// </summary>
    private void NavigateTo(string pageName)
    {
        Page page = pageName switch
        {
            "Distance" => _serviceProvider.GetRequiredService<DistancePage>(),
            "Calibration" => _serviceProvider.GetRequiredService<CalibrationPage>(),
             "MonoCalibration" => _serviceProvider.GetRequiredService<MonoCalibrationPage>(),
            _ => throw new ArgumentException($"未知页面: {pageName}", nameof(pageName))
        };

        MainFrame.Navigate(page);
        Debug.WriteLine($"[MainWindow] 导航到: {pageName}");
    }
}
