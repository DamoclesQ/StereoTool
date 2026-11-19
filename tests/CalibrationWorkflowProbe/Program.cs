using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;
using StereoTool;
using StereoTool.Models;
using StereoTool.Services;
using StereoTool.Utils;
using StereoTool.ViewModels;
using StereoTool.Views;

var workspace = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
var output = Path.Combine(workspace, ".tmp", "calibration-verification");
Directory.CreateDirectory(output);
Exception? failure = null;
var thread = new Thread(() =>
{
    try
    {
        if (args.Contains("--ui"))
            RunUiSmoke(workspace, output);
        else
            RunChecks(workspace, output);
    }
    catch (Exception ex)
    {
        failure = ex;
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (failure != null)
{
    Console.Error.WriteLine(failure);
    return 1;
}
return 0;

/// <summary>运行真实图片、合成几何及 WPF 页面状态检查。</summary>
/// <param name="workspace">仓库根目录。</param>
/// <param name="output">验证输出目录。</param>
static void RunChecks(string workspace, string output)
{
    var service = new CalibrationService();
    var distanceService = new DistanceService();
    var config = new ChessboardConfig
    {
        CornerCols = 9,
        CornerRows = 6,
        SquareSize = 40,
        PixelSizeUm = 2.4
    };
    var paired = service.PairImages(["left01.png", "left02.png", "left10.png"], ["right1.png", "right10.png"]);
    Check(paired.Count == 3 && paired[1].RightPath == "" && paired[2].RightPath == "right10.png",
        "名称配对保留缺图，且编号缺失不会导致后续错位");
    ExpectFailure(() => service.PairImages(["left01.png", "left1.jpg"], ["right1.png"]), "歧义编号");

    var data = Path.Combine(workspace, "资料", "calImage");
    var leftPaths = service.GetImagePaths(Path.Combine(data, "left"));
    var rightPaths = service.GetImagePaths(Path.Combine(data, "right"));
    using (var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.White))
        blank.SaveImage(Path.Combine(output, "blank.png"));
    var images = service.AnalyzeImages(leftPaths.Append(Path.Combine(output, "blank.png")), config);
    Check(!images[^1].IsDetected && !images[^1].IsIncluded && images[^1].DetectionStatus.Contains("棋盘格"),
        "失败图片保留诊断并默认排除");
    var accepted = images.Where(image => image.IsDetected).ToList();
    Check(accepted.Count >= 3, "仓库图片检测成功");
    var mono = service.CameraCalibrate(config, accepted);
    Check(double.IsFinite(mono.MeanReprojectionError) && accepted.All(image => image.ProjectedCorners.Length == 54),
        "单目标定返回逐图误差和投影点");
    Check(Math.Abs(mono.DistCoeffs[2]) < 1e-12 && Math.Abs(mono.DistCoeffs[3]) < 1e-12 &&
        Math.Abs(mono.DistCoeffs[4]) < 1e-12, "默认模型固定切向畸变和 k3");
    service.SaveCalibrationParams(Path.Combine(output, "mono.yaml"), mono);
    var loadedMono = service.LoadCalibrationParams(Path.Combine(output, "mono.yaml"));
    Check(loadedMono.ImageCount == accepted.Count && Math.Abs(loadedMono.MeanReprojectionError - mono.MeanReprojectionError) < 1e-10,
        "单目 YAML 新字段往返一致");
    _ = service.LoadCalibrationParams(Path.Combine(data, "calibrationParams_Left.yaml"));

    var pairs = service.AnalyzePairs(service.PairImages(leftPaths, rightPaths), config)
        .Where(pair => pair.IsDetected).ToList();
    var stereo = service.StereoCalibrate(config, pairs);
    Check(stereo.Baseline > 40 && stereo.Baseline < 90 && pairs.All(pair => double.IsFinite(pair.MeanError)),
        "仓库双目标定基线和逐对误差有效");
    service.SaveStereoCalibrationParams(Path.Combine(output, "stereo.yaml"), stereo);
    var loadedStereo = service.LoadStereoCalibrationParams(Path.Combine(output, "stereo.yaml"));
    Check(loadedStereo.ImageCount == pairs.Count && Math.Abs(loadedStereo.Baseline - stereo.Baseline) < 1e-8,
        "双目 YAML 往返一致");
    var legacy = service.LoadStereoCalibrationParams(Path.Combine(data, "stereoCalibrationParams.yaml"));
    Check(double.IsNaN(legacy.MeanReprojectionError), "旧 YAML 缺少均值时显示未知而非零");
    ExpectFailure(() => service.LoadCalibrationParams(Path.Combine(output, "stereo.yaml")), "双目文件不能当作单目内参");

    var rightImages = service.AnalyzeImages(rightPaths, config).Where(image => image.IsDetected).ToList();
    var rightMono = service.CameraCalibrate(config, rightImages);
    var fixedStereo = service.StereoCalibrate(config, pairs, mono, rightMono);
    Check(fixedStereo.UsedFixedIntrinsics && fixedStereo.LeftCameraMatrix.Cast<double>().SequenceEqual(mono.CameraMatrix.Cast<double>()) &&
        fixedStereo.LeftDistCoeffs.SequenceEqual(mono.DistCoeffs), "固定内参模式保持导入内参与畸变不变");
    CheckSyntheticGeometry(service, config);

    var app = new App();
    app.InitializeComponent();
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    var main = new MainViewModel(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider());
    var distanceVm = new DistanceViewModel(service, distanceService);
    var monoVm = new CalibrationViewModel(service);
    monoVm.ChessboardConfig.SquareSize = 40;
    monoVm.DetectedImages = new System.Collections.ObjectModel.ObservableCollection<DetectedImage>(accepted);
    // 通过重新分析走真实页面发布流程，验证后台计算不会阻塞调度器。
    var uiThread = Environment.CurrentManagedThreadId;
    var dispatcherTicked = false;
    var analyze = monoVm.DetectCornersCommand.ExecuteAsync(null);
    Dispatcher.CurrentDispatcher.BeginInvoke(() => dispatcherTicked = true);
    PumpUntil(analyze);
    Check(dispatcherTicked && Environment.CurrentManagedThreadId == uiThread, "图片分析期间 UI 调度器仍可处理消息");
    PumpUntil(monoVm.StartCalibrationCommand.ExecuteAsync(null));
    Check(monoVm.CalibrationResult != null, "单目页面后台标定完成");
    monoVm.SelectedImage = monoVm.DetectedImages.First(image => image.IsDetected);
    Check(monoVm.UndistortedImage != null, "单目去畸变预览生成");
    RenderPage(new CalibrationPage(monoVm), Path.Combine(output, "mono-page.png"));
    monoVm.DetectedImages.First(image => image.IsDetected).IsIncluded = false;
    Check(monoVm.CalibrationResult == null, "切换参与图片使参数失效");
    PumpUntil(monoVm.StartCalibrationCommand.ExecuteAsync(null));
    monoVm.ChessboardConfig.CornerCols = 8;
    Check(monoVm.CalibrationResult == null && monoVm.DetectedImages.All(image => !image.IsDetected),
        "修改内角点数清除角点缓存但保留图片");

    var stereoVm = new StereoCalibrationViewModel(service, distanceService, distanceVm, main);
    stereoVm.ChessboardConfig.SquareSize = 40;
    stereoVm.ImagePairs = new System.Collections.ObjectModel.ObservableCollection<DetectedImagePair>(pairs);
    PumpUntil(stereoVm.DetectCornersCommand.ExecuteAsync(null));
    PumpUntil(stereoVm.StartCalibrationCommand.ExecuteAsync(null));
    Check(stereoVm.CalibrationResult != null && stereoVm.RectifiedLeftImage != null, "双目页面标定和校正预览完成");
    RenderPage(new StereoCalibrationPage(stereoVm), Path.Combine(output, "stereo-page.png"));

    distanceVm.UseCalibration(legacy);
    distanceVm.LeftMat = Cv2.ImRead(pairs[0].LeftPath, ImreadModes.Color);
    distanceVm.RightMat = Cv2.ImRead(pairs[0].RightPath, ImreadModes.Color);
    PumpUntil(distanceVm.SgbmComputeCommand.ExecuteAsync(null));
    Check(distanceVm.MapXyz != null && distanceVm.Disparity != null, "真实图片 SGBM 测距成功");
    var original = pairs[0].LeftCorners[0];
    var rectified = distanceVm.RectifyPixel((int)original.X, (int)original.Y, true);
    Check(double.IsFinite(rectified.X) && Math.Abs(rectified.X - original.X) > 0.1, "原图坐标转换到校正坐标");
    distanceVm.LeftMat = Cv2.ImRead(pairs[1].LeftPath, ImreadModes.Color);
    Check(distanceVm.MapXyz == null && distanceVm.Disparity == null && double.IsNaN(distanceVm.DistanceResult),
        "更换图片后旧视差、点云和深度失效");
    Console.WriteLine($"真实数据：单目 RMS={mono.ReprojectionError:F4}px，双目 RMS={stereo.ReprojectionError:F4}px，均值={stereo.MeanReprojectionError:F4}px，基线={stereo.Baseline:F3}mm");
    Console.WriteLine($"页面预览与 YAML 输出：{output}");
    RunUiSmoke(workspace, output);
}

/// <summary>使用实际应用容器检查三页导航、预览布局和参数传递。</summary>
/// <param name="workspace">仓库根目录。</param>
/// <param name="output">已有验证结果的输出目录。</param>
static void RunUiSmoke(string workspace, string output)
{
    if (Application.Current == null)
    {
        var app = new App();
        app.InitializeComponent();
    }
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    var configure = typeof(App).GetMethod("ConfigureServices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    using var provider = (ServiceProvider)configure.Invoke(null, null)!;
    var window = provider.GetRequiredService<MainWindow>();
    var navigation = provider.GetRequiredService<MainViewModel>();
    navigation.NavigateMonoCalibrationCommand.Execute(null);
    navigation.NavigateCalibrationCommand.Execute(null);
    var service = provider.GetRequiredService<CalibrationService>();
    var mono = provider.GetRequiredService<CalibrationViewModel>();
    var stereo = provider.GetRequiredService<StereoCalibrationViewModel>();
    var distance = provider.GetRequiredService<DistanceViewModel>();
    var config = stereo.ChessboardConfig;
    config.SquareSize = 40;
    var left = Path.Combine(workspace, "资料", "calImage", "left", "left01.jpg");
    var right = Path.Combine(workspace, "资料", "calImage", "right", "right01.jpg");
    service.TryDetectCorners(left, config, out var leftCorners, out var size);
    service.TryDetectCorners(right, config, out var rightCorners, out _);
    var selected = new DetectedImagePair
    {
        LeftPath = left,
        RightPath = right,
        LeftCorners = leftCorners,
        RightCorners = rightCorners,
        ImageSize = size,
        DetectionStatus = "检测成功"
    };
    stereo.ImagePairs.Add(selected);
    stereo.SelectedPair = selected;
    stereo.CalibrationResult = service.LoadStereoCalibrationParams(Path.Combine(output, "stereo.yaml"));
    var stereoPage = provider.GetRequiredService<StereoCalibrationPage>();
    RenderPage(stereoPage, Path.Combine(output, "stereo-page.png"));
    SelectTab(stereoPage, "双目校正检查");
    RenderPage(stereoPage, Path.Combine(output, "stereo-rectified.png"));
    Check(stereo.RectifiedLeftImage != null && stereo.RectifiedRightImage != null, "真实容器生成左右校正预览");
    stereo.UseForDistanceCommand.Execute(null);
    Check(ReferenceEquals(stereo.CalibrationResult, distance.LoadedCalibration) && navigation.SelectedNavItem == "测距",
        "用于测距命令传递当前参数并完成导航");
    distance.IsBusy = true;
    ExpectFailure(() => distance.UseCalibration(stereo.CalibrationResult!), "测距计算中拒绝替换正在使用的校正映射");
    distance.IsBusy = false;
    mono.ChessboardConfig.SquareSize = 40;
    mono.DetectedImages.Add(new DetectedImage
    {
        ImagePath = left,
        Corners = leftCorners,
        ImageSize = size,
        DetectionStatus = "检测成功"
    });
    mono.SelectedImage = mono.DetectedImages[0];
    mono.CalibrationResult = service.LoadCalibrationParams(Path.Combine(output, "mono.yaml"));
    var monoPage = provider.GetRequiredService<CalibrationPage>();
    RenderPage(monoPage, Path.Combine(output, "mono-page.png"));
    SelectTab(monoPage, "去畸变检查");
    RenderPage(monoPage, Path.Combine(output, "mono-undistorted.png"));
    Check(mono.UndistortedImage != null, "真实容器生成单目去畸变预览");
    window.Close();
}

/// <summary>选择页面中的指定预览标签。</summary>
/// <param name="root">视觉树根节点。</param>
/// <param name="header">标签标题。</param>
static void SelectTab(DependencyObject root, string header)
{
    if (root is TabControl control)
    {
        control.SelectedItem = control.Items.Cast<TabItem>().First(item => (string)item.Header == header);
        return;
    }
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        SelectTab(VisualTreeHelper.GetChild(root, i), header);
}

/// <summary>验证已知几何、尺度与逐对重投影误差，并检查错误配对的残差。</summary>
/// <param name="service">待验证的标定服务。</param>
/// <param name="config">棋盘格配置。</param>
static void CheckSyntheticGeometry(CalibrationService service, ChessboardConfig config)
{
    double[,] camera = { { 900, 0, 320 }, { 0, 910, 240 }, { 0, 0, 1 } };
    var fixedCamera = new CalibrationParams
    {
        CameraMatrix = camera,
        DistCoeffs = new double[5],
        ImageWidth = 640,
        ImageHeight = 480
    };
    var objects = service.CreateWorldPoints(config);
    var pairs = new List<DetectedImagePair>();
    for (var i = 0; i < 8; i++)
    {
        double[] rotation = [0.07 * i - 0.2, 0.03 * i - 0.1, 0.015 * i];
        double[] translation = [-120 + i * 12, -85 + i * 4, 950 + i * 70];
        Cv2.ProjectPoints(objects, rotation, translation, camera, new double[5], out var left, out _);
        translation[0] -= 60;
        Cv2.ProjectPoints(objects, rotation, translation, camera, new double[5], out var right, out _);
        pairs.Add(new DetectedImagePair
        {
            LeftCorners = left,
            RightCorners = right,
            ImageSize = new OpenCvSharp.Size(640, 480)
        });
    }
    var solved = service.StereoCalibrate(config, pairs, fixedCamera, fixedCamera);
    Check(Math.Abs(solved.Baseline - 60) < 0.01 && solved.MeanReprojectionError < 0.001,
        "合成双目几何恢复 60mm 基线与共同位姿低残差");
    var original = pairs[3].RightCorners;
    pairs[3].RightCorners = pairs[5].RightCorners;
    var inconsistent = service.StereoCalibrate(config, pairs, fixedCamera, fixedCamera);
    Check(pairs[3].MeanError > 1 && inconsistent.MeanReprojectionError > 0.1,
        "错误配对产生可见的共同位姿误差");
    pairs[3].RightCorners = original;
}

/// <summary>泵送 UI 消息直到后台命令完成。</summary>
/// <param name="task">待完成命令。</param>
static void PumpUntil(Task task)
{
    var frame = new DispatcherFrame();
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
    timer.Tick += (_, _) =>
    {
        if (!task.IsCompleted)
            return;
        timer.Stop();
        frame.Continue = false;
    };
    timer.Start();
    Dispatcher.PushFrame(frame);
    task.GetAwaiter().GetResult();
}

/// <summary>布局并离屏渲染页面，用于检查 XAML、绑定与布局。</summary>
/// <param name="page">待渲染页面。</param>
/// <param name="path">PNG 输出路径。</param>
static void RenderPage(Page page, string path)
{
    page.Measure(new System.Windows.Size(1280, 860));
    page.Arrange(new System.Windows.Rect(0, 0, 1280, 860));
    page.UpdateLayout();
    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    page.UpdateLayout();
    var bitmap = new RenderTargetBitmap(1280, 860, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(page);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

/// <summary>断言检查结果。</summary>
/// <param name="condition">预期为真的条件。</param>
/// <param name="description">检查内容。</param>
static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
}

/// <summary>验证输入错误会被明确拒绝。</summary>
/// <param name="action">预期失败的操作。</param>
/// <param name="description">检查内容。</param>
static void ExpectFailure(Action action, string description)
{
    try
    {
        action();
    }
    catch (Exception)
    {
        Console.WriteLine($"PASS: {description}");
        return;
    }
    throw new InvalidOperationException($"FAIL: {description}");
}
