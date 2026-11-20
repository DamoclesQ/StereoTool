using System.Reflection;
using System.Runtime.Versioning;
using StereoTool.Calibration.Models;
using StereoTool.Calibration.Services;

var workspace = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
var output = Path.Combine(workspace, ".tmp", "calibration-service-verification");
Directory.CreateDirectory(output);

var service = new CalibrationService();
var config = new ChessboardConfig
{
    CornerCols = 9,
    CornerRows = 6,
    SquareSize = 40
};
var data = Path.Combine(workspace, "资料", "calImage");
var leftPaths = service.GetImagePaths(Path.Combine(data, "left")).Take(6).ToList();
var rightPaths = service.GetImagePaths(Path.Combine(data, "right")).Take(6).ToList();

var images = service.AnalyzeImages(leftPaths, config);
var accepted = images.Where(image => image.IsIncluded && image.IsDetected).ToList();
var mono = service.CameraCalibrate(config, accepted);
Check(accepted.Count >= 3 && double.IsFinite(mono.MeanReprojectionError) &&
    accepted.All(image => image.ProjectedCorners.Length == 54),
    "纯控制台完成单目角点分析、标定及逐图重投影");

var pairs = service.AnalyzePairs(service.PairImages(leftPaths, rightPaths), config)
    .Where(pair => pair.IsIncluded && pair.IsDetected).ToList();
var stereo = service.StereoCalibrate(config, pairs);
Check(double.IsFinite(stereo.Baseline) && stereo.Baseline > 0 &&
    pairs.All(pair => double.IsFinite(pair.MeanError) && pair.LeftProjectedCorners.Length == 54),
    "纯控制台完成双目标定及逐对误差计算");

var monoPath = Path.Combine(output, "mono.yaml");
var stereoPath = Path.Combine(output, "stereo.yaml");
service.SaveCalibrationParams(monoPath, mono);
service.SaveStereoCalibrationParams(stereoPath, stereo);
var loadedMono = service.LoadCalibrationParams(monoPath);
var loadedStereo = service.LoadStereoCalibrationParams(stereoPath);
Check(loadedMono.CameraMatrix.Cast<double>().SequenceEqual(mono.CameraMatrix.Cast<double>()) &&
    loadedMono.ImageCount == accepted.Count &&
    Math.Abs(loadedStereo.Baseline - stereo.Baseline) < 1e-8 &&
    loadedStereo.ImageCount == pairs.Count,
    "独立服务完成单目和双目 YAML 保存与加载");

using var preview = service.CreatePreview(accepted[0].ImagePath, accepted[0].Corners,
    accepted[0].ProjectedCorners, config);
using var undistorted = service.CreatePreview(accepted[0].ImagePath, [], [], config, loadedMono);
Check(preview.Width == mono.ImageWidth && undistorted.Height == mono.ImageHeight,
    "无 WPF 的宿主生成角点与去畸变 Mat 预览");

var assembly = typeof(CalibrationService).Assembly;
Console.WriteLine($"服务程序集：{assembly.GetName().Name}");
Console.WriteLine($"目标框架：{assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName}");
Console.WriteLine($"标定结果输出：{output}");

/// <summary>检查独立宿主调用标定服务的运行结果。</summary>
/// <param name="condition">期望满足的条件。</param>
/// <param name="description">检查内容。</param>
static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
}
