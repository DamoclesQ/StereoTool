using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StereoTool.Views;
using Microsoft.Extensions.DependencyInjection;

namespace StereoTool.ViewModels;

/// <summary>
/// 主窗口的 ViewModel，负责页面导航逻辑。
/// 通过 Frame 容器在标定页面与测距页面之间切换。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;

    // ═══════════════════════════════════════════════
    //  绑定属性
    // ═══════════════════════════════════════════════

    /// <summary>当前显示的页面对象，用于 Frame 绑定</summary>
    [ObservableProperty]
    private object? _currentPage;
    /// <summary>当前选中的导航项标识（"标定"、"测距" 或 "单目标定"），用于导航按钮高亮</summary>
    [ObservableProperty]
    private string _selectedNavItem = "测距";


    /// <summary>导航请求事件，参数为页面标识（"Calibration"、"Distance" 或 "MonoCalibration"）</summary>
    public event Action<string>? NavigateRequested;

    // ═══════════════════════════════════════════════
    //  构造器
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 构造 MainViewModel，注入 IServiceProvider 用于从 DI 容器解析页面实例。
    /// </summary>
    /// <param name="serviceProvider">DI 服务提供者</param>
    public MainViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        // SelectedNavItem 已设为 "测距"，保证按钮高亮
        // 实际导航由 MainWindow.Loaded 在事件订阅完成后触发
    }

    // ═══════════════════════════════════════════════
    //  命令
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 导航到双目标定页面。
    /// </summary>
    [RelayCommand]
    private void NavigateCalibration()
    {
        SelectedNavItem = "标定";
        var page = _serviceProvider.GetService<StereoCalibrationPage>();
        CurrentPage = page;
        NavigateRequested?.Invoke("Calibration");
    }

    /// <summary>
    /// 导航到双目测距页面。
    /// </summary>
    [RelayCommand]
    private void NavigateDistance()
    {
        SelectedNavItem = "测距";
        var page = _serviceProvider.GetService<DistancePage>();
        CurrentPage = page;
        NavigateRequested?.Invoke("Distance");
    }

    /// <summary>
    /// 导航到单目标定页面。
    /// </summary>
    [RelayCommand]
    private void NavigateMonoCalibration()
    {
        SelectedNavItem = "单目标定";
        var page = _serviceProvider.GetService<CalibrationPage>();
        CurrentPage = page;
        NavigateRequested?.Invoke("MonoCalibration");
    }
}
