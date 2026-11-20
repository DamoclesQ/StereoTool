using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StereoTool.ViewModels;

/// <summary>维护导航选择并发送页面请求，由主窗口统一解析和切换页面。</summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>当前导航项，用于显示选中状态。</summary>
    [ObservableProperty]
    private string _selectedNavItem = "测距";

    /// <summary>页面导航请求，参数为 Calibration、Distance 或 MonoCalibration。</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>进入双目标定页面。</summary>
    [RelayCommand]
    private void NavigateCalibration()
    {
        SelectedNavItem = "标定";
        NavigateRequested?.Invoke("Calibration");
    }

    /// <summary>进入双目测距页面。</summary>
    [RelayCommand]
    private void NavigateDistance()
    {
        SelectedNavItem = "测距";
        NavigateRequested?.Invoke("Distance");
    }

    /// <summary>进入单目标定页面。</summary>
    [RelayCommand]
    private void NavigateMonoCalibration()
    {
        SelectedNavItem = "单目标定";
        NavigateRequested?.Invoke("MonoCalibration");
    }
}
