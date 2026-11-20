using System.Windows;
using StereoTool.Services;
using StereoTool.Calibration.Services;
using StereoTool.ViewModels;
using StereoTool.Views;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace StereoTool;

/// <summary>
/// 应用程序入口，负责配置依赖注入容器并启动主窗口。
/// 使用 Microsoft.Extensions.DependencyInjection 构建 DI 容器，
/// 并通过 CommunityToolkit.Mvvm 的 Ioc.Default 提供全局访问点。
/// </summary>
public partial class App : Application
{
    /// <summary>全局 DI 服务提供者，供非注入场景使用</summary>
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    /// <summary>
    /// 应用程序启动时调用，配置 DI 容器并显示主窗口。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 构建 DI 容器
        ServiceProvider = ConfigureServices();

        // 配置 CommunityToolkit.Mvvm 的全局 IoC（供 design-time 及非 DI 场景使用）
        Ioc.Default.ConfigureServices(ServiceProvider);

        // 从 DI 容器获取主窗口并显示
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    /// <summary>
    /// 配置 DI 容器，注册所有服务、ViewModel 和 View。
    /// </summary>
    /// <returns>构建完成的 IServiceProvider</returns>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // ── 注册服务（Singleton，全局共享） ──
        services.AddSingleton<CalibrationService>();
        services.AddSingleton<DistanceService>();

        // ── 注册 ViewModel ──
        // MainViewModel 为 Singleton（导航状态需要全局保持）
        services.AddSingleton<MainViewModel>();
        // 页面与 ViewModel 共享生命周期，切换页面后保留当前工作状态。
        services.AddSingleton<StereoCalibrationViewModel>();
        services.AddSingleton<DistanceViewModel>();
        services.AddSingleton<CalibrationViewModel>();
        services.AddSingleton<CalibrationPage>();

        // ── 注册 View（Page / Window） ──
        services.AddSingleton<StereoCalibrationPage>();
        services.AddSingleton<DistancePage>();
        services.AddSingleton<MainWindow>(); // 主窗口为 Singleton（整个应用生命周期只有一个）

        return services.BuildServiceProvider();
    }
}
