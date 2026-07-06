using System.IO;
using System.Windows.Media.Imaging;
using StereoTool.Models;
using StereoTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace StereoTool.ViewModels;

/// <summary>
/// 双目测距页面的 ViewModel。
/// 负责加载标定文件、导入双目图像、执行空间距离测量并展示结果。
/// </summary>
public partial class DistanceViewModel : ObservableObject
{
    private readonly CalibrationService _calibrationService;
    private readonly DistanceService _distanceService;

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

        if(LoadedCalibration != null)
            StereoRectifyParams = _distanceService.GetRectifyParams(LoadedCalibration);
    }

    partial void OnLeftMatChanged(Mat? value)
    {
        LeftImage = LeftMat?.ToWriteableBitmap();
    }

    partial void OnRightMatChanged(Mat? value)
    {
        RightImage = RightMat?.ToWriteableBitmap();
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
            DisparityImage = _distanceService.Normlize(value).ToWriteableBitmap();
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
            var zMat = _distanceService.Split(value).Z;
            ZImage = _distanceService.GetPseudoColorMat(zMat).ToWriteableBitmap();
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
            LoadedCalibration = _calibrationService.LoadStereoCalibrationParams(dialog.FileName);
            CalibrationFilePath = dialog.FileName;
            StatusText = $"标定文件已加载：{Path.GetFileName(dialog.FileName)}（{LoadedCalibration.ImageWidth}×{LoadedCalibration.ImageHeight}）";
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

        LeftMat = new Mat(dialog.FileName, ImreadModes.Unchanged);
        StatusText = $"左图像已导入：{Path.GetFileName(dialog.FileName)}";
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

        RightMat = new Mat(dialog.FileName, ImreadModes.Unchanged);
        StatusText = $"右图像已导入：{Path.GetFileName(dialog.FileName)}";
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
            (Disparity, MapXyz) = _distanceService.SgbmCompute(StereoRectifyParams, LeftMat, RightMat);

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
        if (LoadedCalibration == null)
        {
            return;
        }
        var fx = LoadedCalibration.LeftCameraMatrix[0, 0];
        var baseline = LoadedCalibration.TranslationVector[0];
        var d = (SelectedPoint.X - RightSelectedPoint.X) * 1.0;
        var distance = fx * Math.Abs(baseline) / d;

        StatusText = $"不考虑畸变，不做极线校正：f*b/d = {distance}";
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
