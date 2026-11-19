using System.IO;
using System.Windows.Media.Imaging;
using StereoTool.Models;
using StereoTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using StereoTool.Utils;

namespace StereoTool.ViewModels;

/// <summary>
/// 双目测距页面的 ViewModel。
/// 负责加载标定文件、导入双目图像、执行空间距离测量并展示结果。
/// </summary>
public partial class DistanceViewModel : ObservableObject
{
    private readonly CalibrationService _calibrationService;
    private readonly DistanceService _distanceService;

    /// <summary>测距运算期间禁止替换输入。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>左选点是否来自原始左图。</summary>
    public bool LeftPointIsOriginal { get; set; }

    /// <summary>右选点是否来自原始右图。</summary>
    public bool RightPointIsOriginal { get; set; }

    /// <summary>标定结果文件路径（用户从磁盘加载的 JSON 文件）</summary>
    [ObservableProperty] private string _calibrationFilePath = string.Empty;

    /// <summary>已加载的标定结果对象</summary>
    [ObservableProperty] private StereoCalibrationParams? _loadedCalibration;

    /// <summary>加载的左右图原图</summary>
    [ObservableProperty] private Mat? _leftMat;
    [ObservableProperty] private Mat? _rightMat;

    /// <summary>用于显示的左右图原图 </summary>
    [ObservableProperty] private WriteableBitmap? _leftImage;
    [ObservableProperty] private WriteableBitmap? _rightImage;

    /// <summary>SGBM 计算的视差图</summary>
    [ObservableProperty] private Mat? _disparity;
    /// <summary>SGBM 计算的点云图</summary>
    [ObservableProperty] private Mat? _mapXyz;

    /// <summary>归一化后的视差图</summary>
    [ObservableProperty] private WriteableBitmap? _disparityImage;
    /// <summary>深度图伪彩图</summary>
    [ObservableProperty] private WriteableBitmap? _zImage;

    [ObservableProperty] private StereoRectifyResult? _stereoRectifyParams;

    /// <summary>状态栏文本</summary>
    [ObservableProperty] private string _statusText = "就绪 — 请加载标定文件并导入双目图像";

    [ObservableProperty] double _distanceResult = 0;


    #region 标记点

    /// <summary>用户在左图像上点击的像素坐标（用于距离测量）</summary>
    [ObservableProperty] private System.Drawing.Point _selectedPoint;

    /// <summary>标记点在 Canvas 中的 X 坐标（像素），用于十字准星定位</summary>
    [ObservableProperty] private double _markerX;

    /// <summary>标记点在 Canvas 中的 Y 坐标（像素），用于十字准星定位</summary>
    [ObservableProperty] private double _markerY;

    /// <summary>标记点是否可见（用户已点击选择测量点后为 true）</summary>
    [ObservableProperty] private bool _markerVisible;

    /// <summary>用户在右图像上点击的像素坐标</summary>
    [ObservableProperty] private System.Drawing.Point _rightSelectedPoint;

    /// <summary>右图标记点在 Canvas 中的 X 坐标</summary>
    [ObservableProperty] private double _rightMarkerX;

    /// <summary>右图标记点在 Canvas 中的 Y 坐标</summary>
    [ObservableProperty] private double _rightMarkerY;

    /// <summary>右图标记点是否可见</summary> 
    [ObservableProperty] private bool _rightMarkerVisible;

	#endregion



    public string LeftFocalMm => LoadedCalibration != null && LoadedCalibration.PixelSizeUm > 0
        ? $"{LoadedCalibration.LeftCameraMatrix[0, 0] * LoadedCalibration.PixelSizeUm / 1000.0:F2}" : "--";
    public string RightFocalMm => LoadedCalibration != null && LoadedCalibration.PixelSizeUm > 0
        ? $"{LoadedCalibration.RightCameraMatrix[0, 0] * LoadedCalibration.PixelSizeUm / 1000.0:F2}" : "--";

    // ═══════════════════════════════════════════════
    //  派生属性 — 标定参数展示
    // ═══════════════════════════════════════════════

    /// <summary>重投影误差显示</summary>
    public string ReprojectionErrorDisplay => LoadedCalibration != null ? $"{LoadedCalibration.ReprojectionError:F4} 像素" : "--";

    /// <summary>标定结果变化时，同步刷新所有派生属性</summary>
    partial void OnLoadedCalibrationChanged(StereoCalibrationParams? value)
    {
        OnPropertyChanged(nameof(LeftFocalMm));
        OnPropertyChanged(nameof(RightFocalMm));
        OnPropertyChanged(nameof(ReprojectionErrorDisplay));

        ResetMeasurement();
    }

    /// <summary>更新左图并释放旧图，使旧计算结果失效。</summary>
    /// <param name="oldValue">旧图。</param>
    /// <param name="newValue">新图。</param>
    partial void OnLeftMatChanged(Mat? oldValue, Mat? newValue)
    {
        oldValue?.Dispose();
        LeftImage = LeftMat?.ToWriteableBitmap();
        ResetMeasurement();
    }

    /// <summary>更新右图并释放旧图，使旧计算结果失效。</summary>
    /// <param name="oldValue">旧图。</param>
    /// <param name="newValue">新图。</param>
    partial void OnRightMatChanged(Mat? oldValue, Mat? newValue)
    {
        oldValue?.Dispose();
        RightImage = RightMat?.ToWriteableBitmap();
        ResetMeasurement();
    }

    partial void OnDisparityChanged(Mat? value)
    {
        if (value == null)
        {
            DisparityImage = null;
            return;
        }
        else
        {
            using var normalized = _distanceService.Normlize(value);
            DisparityImage = normalized.ToWriteableBitmap();
        }      
    }

    partial void OnMapXyzChanged(Mat? value)
    {
        if (value == null)
        {
            ZImage = null;
            return;
        }
        else
        {
            using var zMat = new Mat();
            Cv2.ExtractChannel(value, zMat, 2);
            using var color = _distanceService.GetPseudoColorMat(zMat);
            ZImage = color.ToWriteableBitmap();
        }
    }


    /// <summary>
    /// 构造 DistanceViewModel，注入标定服务与测距服务。
    /// </summary>
    /// <param name="calibrationService">标定服务（用于加载 JSON 标定文件）</param>
    /// <param name="distanceService">测距服务（用于执行立体匹配与距离计算）</param>
    public DistanceViewModel(CalibrationService calibrationService, DistanceService distanceService)
    {
        _calibrationService = calibrationService;
        _distanceService = distanceService;
    }

    /// <summary>清空与旧图片或旧标定参数关联的计算结果。</summary>
    private void ResetMeasurement()
    {
        Disparity?.Dispose();
        MapXyz?.Dispose();
        Disparity = null;
        MapXyz = null;
        DistanceResult = double.NaN;
        MarkerVisible = false;
        RightMarkerVisible = false;
        LeftPointIsOriginal = false;
        RightPointIsOriginal = false;
    }

    /// <summary>接收双目标定页的当前有效结果；失败时保留原有标定参数。</summary>
    /// <param name="calibration">双目标定结果。</param>
    /// <param name="source">结果来源显示文本。</param>
    public void UseCalibration(StereoCalibrationParams calibration, string source = "当前双目标定结果")
    {
        if (IsBusy)
            throw new InvalidOperationException("测距正在计算，请完成后再应用新的标定参数。");
        var rectify = _distanceService.GetRectifyParams(calibration);
        StereoRectifyParams?.Dispose();
        StereoRectifyParams = rectify;
        LoadedCalibration = calibration;
        CalibrationFilePath = source;
        StatusText = "标定参数已应用，请导入左右图片并执行 SGBM 匹配。";
    }

    /// <summary>将原图像素转换到校正图坐标，避免直接用原图下标查询校正点云。</summary>
    /// <param name="x">原图像素横坐标。</param>
    /// <param name="y">原图像素纵坐标。</param>
    /// <param name="left">是否为左相机。</param>
    /// <returns>校正后的浮点像素；未加载参数时为 NaN。</returns>
    public (double X, double Y) RectifyPixel(int x, int y, bool left)
    {
        if (StereoRectifyParams == null)
            return (double.NaN, double.NaN);
        using var source = new[] { new Point2f(x, y) }.ToMat();
        using var destination = new Mat();
        var rectify = StereoRectifyParams;
        Cv2.UndistortPoints(source, destination,
            left ? rectify.LeftCameraMatrix : rectify.RightCameraMatrix,
            left ? rectify.LeftDistCoeffs : rectify.RightDistCoeffs,
            left ? rectify.LeftR : rectify.RightR,
            left ? rectify.LeftP : rectify.RightP);
        var point = destination.At<Point2f>(0);
        return (point.X, point.Y);
    }


    /// <summary>
    /// 从磁盘加载标定结果 YAML 文件。
    /// </summary>
    [RelayCommand]
    private void LoadCalibration()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择标定结果文件",
            Filter = "YAML 文件|*.yaml;*.yml|所有文件|*.*",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            UseCalibration(_calibrationService.LoadStereoCalibrationParams(dialog.FileName), dialog.FileName);
            StatusText = $"标定文件已加载：{Path.GetFileName(dialog.FileName)}（{LoadedCalibration!.ImageWidth}×{LoadedCalibration.ImageHeight}）";
        }
        catch (System.Exception ex)
        {
            StatusText = $"标定文件加载失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导入左相机图像。
    /// </summary>
    [RelayCommand]
    private void ImportLeftImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择左相机图像",
            Filter = "图像文件|*.bmp;*.jpg;*.jpeg;*.png;*.tiff;*.tif|所有文件|*.*",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            LeftMat = new Mat(dialog.FileName, ImreadModes.Color);
            StatusText = $"左图像已导入：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导入右相机图像。
    /// </summary>
    [RelayCommand]
    private void ImportRightImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择右相机图像",
            Filter = "图像文件|*.bmp;*.jpg;*.jpeg;*.png;*.tiff;*.tif|所有文件|*.*",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            RightMat = new Mat(dialog.FileName, ImreadModes.Color);
            StatusText = $"右图像已导入：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 执行 SGBM 立体匹配，计算视差图和 3D 点云图
    /// </summary>
    [RelayCommand]
    private async Task SgbmComputeAsync()
    {
        if (LoadedCalibration == null) { StatusText = "错误：请先加载标定参数文件"; return; }
        if (StereoRectifyParams == null) { StatusText = "错误：校正计算参数未生成, 请先加载标定参数文件"; return; }

        if (LeftMat == null || LeftMat.Empty()) { StatusText = "错误：左图像文件不存在"; return; }
        if (RightMat == null || RightMat.Empty()) { StatusText = "错误：右图像文件不存在"; return; }

        StatusText = "正在进行SGBM计算(视差图与3D点云图)...";

        try
        {
            if (LeftMat.Size() != new Size(LoadedCalibration.ImageWidth, LoadedCalibration.ImageHeight) ||
                RightMat.Size() != LeftMat.Size())
                throw new InvalidOperationException("左右图片尺寸必须一致，并与标定图片尺寸相同。");
            IsBusy = true;
            ResetMeasurement();
            (Disparity, MapXyz) = await Task.Run(() => _distanceService.SgbmCompute(StereoRectifyParams, LeftMat, RightMat));

            // 将视差图转换为 8 位灰度 BitmapSource 供 UI 显示
            // 视差图是 CV_32F 格式，先归一化到 0-255 再转换为 CV_8U
            if (Disparity != null && !Disparity.Empty())
            {
                using var normalized = new Mat();
                Cv2.Normalize(Disparity, normalized, 0, 255, NormTypes.MinMax);
                normalized.ConvertTo(normalized, MatType.CV_8U);
                DisparityImage = normalized.ToWriteableBitmap();
            }

            StatusText = "视差图与3D点云图计算完成！";
        }
        catch (System.Exception ex)
        {
            StatusText = $"测量失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SaveDisparityImage()
    {
        if (Disparity == null || Disparity.Empty()) { 
            StatusText = "保存失败：没有可用的视差图";
            return;
        }

        // 让用户选择保存路径
        var dialog = new SaveFileDialog
        {
            Title = "保存视差图",
            Filter = "TIFF 图像|*.tiff",
            DefaultExt = ".tiff",
            FileName = "disparity.tiff",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true)
        {
            StatusText = "已取消保存";
            return;
        }

        Disparity.SaveImage(dialog.FileName);
        StatusText = $"视差图已保存:{dialog.FileName}";
    }

    [RelayCommand]
    private void SaveXyzImage()
    {
        if (MapXyz == null || MapXyz.Empty())
        {
            StatusText = "保存失败：没有可用的 XYZ 点云图";
            return;
        }

        // 让用户选择保存路径
        var dialog = new SaveFileDialog
        {
            Title = "保存 XYZ 点云图",
            Filter = "TIFF 图像|*.tiff",
            DefaultExt = ".tiff",
            FileName = "xyz.tiff",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true)
        {
            StatusText = "已取消保存";
            return;
        }

        MapXyz.SaveImage(dialog.FileName);
        StatusText = $"XYZ 点云图已保存:{dialog.FileName}";
    }

    [RelayCommand]
    private void ManuCompute()
    {
        if (StereoRectifyParams == null || !LeftPointIsOriginal || !RightPointIsOriginal)
        {
            StatusText = "请加载标定参数，并在左右原图分别选择同一个物理点。";
            return;
        }
        try
        {
            var left = RectifyPixel(SelectedPoint.X, SelectedPoint.Y, true);
            var right = RectifyPixel(RightSelectedPoint.X, RightSelectedPoint.Y, false);
            var disparity = left.X - right.X;
            if (Math.Abs(left.Y - right.Y) > 2 || disparity <= 0)
                throw new InvalidOperationException("对应点校正后行差超过 2 px 或视差非正，请重新选点。");
            using var pixel = new[] { left.X, left.Y, disparity, 1.0 }.ToMat();
            using var homogeneous = (StereoRectifyParams.Q * pixel).ToMat();
            var z = homogeneous.At<double>(2) / homogeneous.At<double>(3);
            if (!double.IsFinite(z) || z <= 0)
                throw new InvalidOperationException("对应点无法生成有效深度。");
            DistanceResult = z;
            StatusText = $"手动对应点：去畸变及校正后视差 {disparity:F3} px，深度 Z = {z:F3} mm。";
        }
        catch (Exception ex)
        {
            DistanceResult = double.NaN;
            StatusText = $"手动测距失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 从缓存的 XYZ 点云图（CV_32FC3）中提取指定像素坐标的空间距离。
    /// 由视差图 Tab 的点击事件处理调用。
    /// </summary>
    /// <param name="pixelX">图像像素 X 坐标</param>
    /// <param name="pixelY">图像像素 Y 坐标</param>
    /// <returns>距离值（mm），坐标越界或数据无效时返回 NaN</returns>
    public (double X, double Y, double Z) GetXYZ(int pixelX, int pixelY)
    {
        if (MapXyz == null || MapXyz.Empty())
            return (double.NaN, double.NaN, double.NaN);

        if (pixelX < 0 || pixelX >= MapXyz.Cols || pixelY < 0 || pixelY >= MapXyz.Rows)
            return (double.NaN, double.NaN, double.NaN);
        if (!double.IsFinite(GetDisparity(pixelX, pixelY)))
            return (double.NaN, double.NaN, double.NaN);

        // CV_32FC3 的每个像素包含 (X, Y, Z) 三个 float 分量
        var vec = MapXyz.At<Vec3f>(pixelY, pixelX);

        return (vec.Item0, vec.Item1, vec.Item2);
    }

    /// <summary>
    /// 获取指定点视差
    /// </summary>
    /// <param name="pixelX"></param>
    /// <param name="pixelY"></param>
    /// <returns></returns>
    public double GetDisparity(int pixelX, int pixelY)
    {
        if (Disparity == null || Disparity.Empty())
            return double.NaN;

        if (pixelX < 0 || pixelX >= Disparity.Cols || pixelY < 0 || pixelY >= Disparity.Rows)
            return double.NaN;

        var d = Disparity.At<float>(pixelY, pixelX);

        if (d <= 0 || float.IsNaN(d) || float.IsInfinity(d))
            return double.NaN;

        return d;
    }
}
