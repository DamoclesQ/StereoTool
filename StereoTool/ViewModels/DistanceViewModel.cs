using System.IO;
using System.Windows.Media.Imaging;
using StereoTool.Models;
using StereoTool.Calibration.Models;
using StereoTool.Calibration.Services;
using StereoTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using StereoTool.Calibration.Utils;

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompute))]
    [NotifyPropertyChangedFor(nameof(CanManualMeasure))]
    [NotifyPropertyChangedFor(nameof(WorkflowHint))]
    private bool _isBusy;

    /// <summary>左选点是否来自原始左图。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftOriginalMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(LeftResultMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(CanManualMeasure))]
    [NotifyPropertyChangedFor(nameof(LeftSelectionDisplay))]
    private bool _leftPointIsOriginal;

    /// <summary>右选点是否来自原始右图。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightOriginalMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(RightResultMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(CanManualMeasure))]
    [NotifyPropertyChangedFor(nameof(RightSelectionDisplay))]
    private bool _rightPointIsOriginal;

    /// <summary>标定结果文件路径（用户从磁盘加载的 YAML 文件）</summary>
    [ObservableProperty] private string _calibrationFilePath = string.Empty;

    /// <summary>已加载的标定结果对象</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalibration))]
    [NotifyPropertyChangedFor(nameof(CanCompute))]
    [NotifyPropertyChangedFor(nameof(CanManualMeasure))]
    [NotifyPropertyChangedFor(nameof(WorkflowHint))]
    private StereoCalibrationParams? _loadedCalibration;

    /// <summary>加载的左右图原图</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompute))]
    [NotifyPropertyChangedFor(nameof(WorkflowHint))]
    [NotifyPropertyChangedFor(nameof(LeftImageSummary))]
    private Mat? _leftMat;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompute))]
    [NotifyPropertyChangedFor(nameof(WorkflowHint))]
    [NotifyPropertyChangedFor(nameof(RightImageSummary))]
    private Mat? _rightMat;

    /// <summary>用于显示的左右图原图 </summary>
    [ObservableProperty] private WriteableBitmap? _leftImage;
    [ObservableProperty] private WriteableBitmap? _rightImage;

    /// <summary>左相机立体校正预览，水平参考线仅用于显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRectifiedPreview))]
    [NotifyPropertyChangedFor(nameof(RectificationHint))]
    private WriteableBitmap? _rectifiedLeftImage;

    /// <summary>右相机立体校正预览，与左图使用同一高度的参考线。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRectifiedPreview))]
    [NotifyPropertyChangedFor(nameof(RectificationHint))]
    private WriteableBitmap? _rectifiedRightImage;

    /// <summary>SGBM 计算的视差图</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisparity))]
    [NotifyPropertyChangedFor(nameof(WorkflowHint))]
    private Mat? _disparity;
    /// <summary>SGBM 计算的点云图</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPointCloud))]
    private Mat? _mapXyz;

    /// <summary>归一化后的视差图</summary>
    [ObservableProperty] private WriteableBitmap? _disparityImage;
    /// <summary>深度图伪彩图</summary>
    [ObservableProperty] private WriteableBitmap? _zImage;

    [ObservableProperty] private StereoRectifyResult? _stereoRectifyParams;

    /// <summary>状态栏文本</summary>
    [ObservableProperty] private string _statusText = "就绪 — 请加载标定文件并导入双目图像";

    /// <summary>当前测量点的深度 Z（mm）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DepthDisplay))]
    private double _distanceResult = double.NaN;

    /// <summary>当前测量点的 X 坐标（mm）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PointXDisplay))]
    private double _pointXResult = double.NaN;

    /// <summary>当前测量点的 Y 坐标（mm）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PointYDisplay))]
    private double _pointYResult = double.NaN;

    /// <summary>导入的左图路径。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftImageSummary))]
    private string _leftImagePath = string.Empty;

    /// <summary>导入的右图路径。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightImageSummary))]
    private string _rightImagePath = string.Empty;


    #region 标记点

    /// <summary>用户在左图像上点击的像素坐标（用于距离测量）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftSelectionDisplay))]
    private System.Drawing.Point _selectedPoint;

    /// <summary>标记点在 Canvas 中的 X 坐标（像素），用于十字准星定位</summary>
    [ObservableProperty] private double _markerX;

    /// <summary>标记点在 Canvas 中的 Y 坐标（像素），用于十字准星定位</summary>
    [ObservableProperty] private double _markerY;

    /// <summary>标记点是否可见（用户已点击选择测量点后为 true）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftOriginalMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(LeftResultMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(CanManualMeasure))]
    [NotifyPropertyChangedFor(nameof(LeftSelectionDisplay))]
    private bool _markerVisible;

    /// <summary>用户在右图像上点击的像素坐标</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightSelectionDisplay))]
    private System.Drawing.Point _rightSelectedPoint;

    /// <summary>右图标记点在 Canvas 中的 X 坐标</summary>
    [ObservableProperty] private double _rightMarkerX;

    /// <summary>右图标记点在 Canvas 中的 Y 坐标</summary>
    [ObservableProperty] private double _rightMarkerY;

    /// <summary>右图标记点是否可见</summary> 
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightOriginalMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(RightResultMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(CanManualMeasure))]
    [NotifyPropertyChangedFor(nameof(RightSelectionDisplay))]
    private bool _rightMarkerVisible;

	#endregion



    // ═══════════════════════════════════════════════
    //  派生属性 — 标定参数展示
    // ═══════════════════════════════════════════════

    /// <summary>标定参数已加载。</summary>
    public bool HasCalibration => LoadedCalibration != null;

    /// <summary>图像和标定参数齐全时允许立体匹配。</summary>
    public bool CanCompute => !IsBusy && HasCalibration &&
        LeftMat is { Width: > 0 } && RightMat is { Width: > 0 };

    /// <summary>两个原图对应点都已选择时允许手动测距。</summary>
    public bool CanManualMeasure => !IsBusy && HasCalibration &&
        MarkerVisible && RightMarkerVisible && LeftPointIsOriginal && RightPointIsOriginal;

    /// <summary>视差数据可导出。</summary>
    public bool HasDisparity => Disparity != null;

    /// <summary>XYZ 数据可导出。</summary>
    public bool HasPointCloud => MapXyz != null;

    /// <summary>当前输入已生成完整的左右校正预览。</summary>
    public bool HasRectifiedPreview => RectifiedLeftImage != null && RectifiedRightImage != null;

    /// <summary>说明自动校正所需输入及水平参考线的检查方式。</summary>
    public string RectificationHint
    {
        get
        {
            if (HasRectifiedPreview)
                return "绿色线标记相同图像行。同一物理点应位于相同高度，水平方向仍保留视差。Ctrl + 滚轮同步缩放左右图。";
            if (LoadedCalibration == null)
                return "加载双目标定参数并导入左右图后，自动显示去畸变与极线校正结果。";
            return $"请导入尺寸均为 {LoadedCalibration.ImageWidth} × {LoadedCalibration.ImageHeight} px 的左右图，自动生成校正预览。";
        }
    }

    /// <summary>左原图标记的可见性。</summary>
    public bool LeftOriginalMarkerVisible => MarkerVisible && LeftPointIsOriginal;

    /// <summary>视差图标记的可见性。</summary>
    public bool LeftResultMarkerVisible => MarkerVisible && !LeftPointIsOriginal;

    /// <summary>右原图标记的可见性。</summary>
    public bool RightOriginalMarkerVisible => RightMarkerVisible && RightPointIsOriginal;

    /// <summary>深度图标记的可见性。</summary>
    public bool RightResultMarkerVisible => RightMarkerVisible && !RightPointIsOriginal;

    /// <summary>深度显示，无效值显示占位符。</summary>
    public string DepthDisplay => double.IsFinite(DistanceResult) ? DistanceResult.ToString("F3") : "--";

    /// <summary>X 坐标显示。</summary>
    public string PointXDisplay => double.IsFinite(PointXResult) ? PointXResult.ToString("F3") : "--";

    /// <summary>Y 坐标显示。</summary>
    public string PointYDisplay => double.IsFinite(PointYResult) ? PointYResult.ToString("F3") : "--";

    /// <summary>左图名称和尺寸。</summary>
    public string LeftImageSummary => LeftMat == null ? "尚未导入左图" :
        $"{Path.GetFileName(LeftImagePath)}  {LeftMat.Width} × {LeftMat.Height} px";

    /// <summary>右图名称和尺寸。</summary>
    public string RightImageSummary => RightMat == null ? "尚未导入右图" :
        $"{Path.GetFileName(RightImagePath)}  {RightMat.Width} × {RightMat.Height} px";

    /// <summary>左侧选点所在图像及像素坐标。</summary>
    public string LeftSelectionDisplay => !MarkerVisible ? "尚未选点" :
        $"{(LeftPointIsOriginal ? "左原图" : "校正视差图")} ({SelectedPoint.X}, {SelectedPoint.Y})";

    /// <summary>右侧选点所在图像及像素坐标。</summary>
    public string RightSelectionDisplay => !RightMarkerVisible ? "尚未选点" :
        $"{(RightPointIsOriginal ? "右原图" : "校正深度图")} ({RightSelectedPoint.X}, {RightSelectedPoint.Y})";

    /// <summary>根据输入与运算状态提示下一步。</summary>
    public string WorkflowHint
    {
        get
        {
            if (IsBusy)
                return "正在计算视差和 XYZ，请稍候…";
            if (!HasCalibration)
                return "01  加载双目 YAML，或从双目标定页应用结果";
            if (LeftMat == null || RightMat == null)
                return "02  分别导入左右图像";
            return HasDisparity
                ? "05  点击视差 / 深度图查看 XYZ，或在左右原图选择对应点"
                : "03  查看“立体校正检查”，确认对齐后执行 SGBM 匹配";
        }
    }

    /// <summary>同步发布测量坐标，无效深度不保留旧坐标。</summary>
    /// <param name="x">校正左相机坐标系 X（mm）。</param>
    /// <param name="y">校正左相机坐标系 Y（mm）。</param>
    /// <param name="z">校正左相机坐标系深度 Z（mm）。</param>
    public void SetMeasurement(double x, double y, double z)
    {
        var valid = double.IsFinite(z) && z > 0;
        PointXResult = valid ? x : double.NaN;
        PointYResult = valid ? y : double.NaN;
        DistanceResult = valid ? z : double.NaN;
    }

    /// <summary>重投影误差显示</summary>
    public string ReprojectionErrorDisplay => LoadedCalibration != null ? $"{LoadedCalibration.ReprojectionError:F4} 像素" : "--";

    /// <summary>标定结果变化时刷新结果摘要并清空测量。</summary>
    /// <param name="value">新的数据。</param>
    partial void OnLoadedCalibrationChanged(StereoCalibrationParams? value)
    {
        OnPropertyChanged(nameof(ReprojectionErrorDisplay));
        OnPropertyChanged(nameof(RectificationHint));

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
        UpdateRectifiedPreview();
    }

    /// <summary>更新右图并释放旧图，使旧计算结果失效。</summary>
    /// <param name="oldValue">旧图。</param>
    /// <param name="newValue">新图。</param>
    partial void OnRightMatChanged(Mat? oldValue, Mat? newValue)
    {
        oldValue?.Dispose();
        RightImage = RightMat?.ToWriteableBitmap();
        ResetMeasurement();
        UpdateRectifiedPreview();
    }

    /// <summary>视差缓存变化时更新灰度预览。</summary>
    /// <param name="value">新的数据。</param>
    partial void OnDisparityChanged(Mat? value)
    {
        if (value == null)
        {
            DisparityImage = null;
            return;
        }
        using var normalized = _distanceService.Normlize(value);
        DisparityImage = normalized.ToWriteableBitmap();
    }

    /// <summary>XYZ 缓存变化时更新深度伪彩图。</summary>
    /// <param name="value">新的数据。</param>
    partial void OnMapXyzChanged(Mat? value)
    {
        if (value == null)
        {
            ZImage = null;
            return;
        }
        using var zMat = new Mat();
        Cv2.ExtractChannel(value, zMat, 2);
        using var color = _distanceService.GetPseudoColorMat(zMat);
        ZImage = color.ToWriteableBitmap();
    }


    /// <summary>
    /// 构造 DistanceViewModel，注入标定服务与测距服务。
    /// </summary>
    /// <param name="calibrationService">标定服务（用于加载 YAML 标定文件）</param>
    /// <param name="distanceService">测距服务（用于执行立体匹配与距离计算）</param>
    public DistanceViewModel(CalibrationService calibrationService, DistanceService distanceService)
    {
        _calibrationService = calibrationService;
        _distanceService = distanceService;
    }

    /// <summary>输入改变后重新生成校正预览，复用匹配的映射且不修改原图。</summary>
    private void UpdateRectifiedPreview()
    {
        RectifiedLeftImage = null;
        RectifiedRightImage = null;
        if (LoadedCalibration == null || StereoRectifyParams == null || LeftMat == null || RightMat == null)
            return;

        var expectedSize = new Size(LoadedCalibration.ImageWidth, LoadedCalibration.ImageHeight);
        if (LeftMat.Size() != expectedSize || RightMat.Size() != expectedSize)
            return;

        var rectified = _distanceService.RectifyImages(StereoRectifyParams, LeftMat, RightMat);
        using var left = rectified.Left;
        using var right = rectified.Right;
        var height = left.Rows;
        var width = left.Cols;
        for (var row = 40; row < height; row += 40)
        {
            Cv2.Line(left, new Point(0, row), new Point(width - 1, row), Scalar.LimeGreen);
            Cv2.Line(right, new Point(0, row), new Point(width - 1, row), Scalar.LimeGreen);
        }
        RectifiedLeftImage = left.ToWriteableBitmap();
        RectifiedRightImage = right.ToWriteableBitmap();
        RectifiedLeftImage.Freeze();
        RectifiedRightImage.Freeze();
    }

    /// <summary>清空与旧图片或旧标定参数关联的计算结果。</summary>
    private void ResetMeasurement()
    {
        Disparity?.Dispose();
        MapXyz?.Dispose();
        Disparity = null;
        MapXyz = null;
        SetMeasurement(double.NaN, double.NaN, double.NaN);
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
        ResetMeasurement();
        UpdateRectifiedPreview();
        CalibrationFilePath = source;
        StatusText = "标定参数已应用，导入左右图片后可查看立体校正结果，再执行 SGBM 匹配。";
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
            LeftImagePath = dialog.FileName;
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
            RightImagePath = dialog.FileName;
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

            StatusText = "匹配完成：点击视差或深度图查看 XYZ；黑色区域无有效深度。";
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

    /// <summary>导出保留浮点数值的视差 TIFF。</summary>
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

        try
        {
            Disparity.SaveImage(dialog.FileName);
            StatusText = $"视差图已保存：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>导出三通道 XYZ TIFF，坐标单位为毫米。</summary>
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

        try
        {
            MapXyz.SaveImage(dialog.FileName);
            StatusText = $"XYZ 点云图已保存：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>将左右原图对应点校正后，通过 Q 计算三维坐标。</summary>
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
            var w = homogeneous.At<double>(3);
            SetMeasurement(homogeneous.At<double>(0) / w, homogeneous.At<double>(1) / w, z);
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
