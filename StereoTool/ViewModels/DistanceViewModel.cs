using System.IO;
using System.Windows.Media.Imaging;
using StereoTool.Models;
using StereoTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenCvSharp;

namespace StereoTool.ViewModels;

/// <summary>
/// 双目测距页面的 ViewModel。
/// 负责加载标定文件、导入双目图像、自动生成全图视差图、缓存 XYZ 空间坐标，
/// 实现选点即时测距和视差图前端展示。
/// </summary>
public partial class DistanceViewModel : ObservableObject, IDisposable
{
    private readonly CalibrationService _calibrationService;
    private readonly DistanceService _distanceService;

    // ═══════════════════════════════════════════════
    //  视差图缓存（私有字段，非绑定）
    // ═══════════════════════════════════════════════

    /// <summary>视差图生成结果（含 CV_32F 视差和 Q 矩阵）</summary>
    private DisparityResult? _disparityResult;

    /// <summary>全图 XYZ 空间坐标缓存（CV_32FC3），点击时直接索引读取距离</summary>
    private Mat? _cachedXYZ;

    // ═══════════════════════════════════════════════
    //  绑定属性
    // ═══════════════════════════════════════════════

    /// <summary>标定结果文件路径（用户从磁盘加载的文件）</summary>
    [ObservableProperty] private string _calibrationFilePath = string.Empty;

    /// <summary>左相机图像文件路径</summary>
    [ObservableProperty] private string _leftImagePath = string.Empty;

    /// <summary>右相机图像文件路径</summary>
    [ObservableProperty] private string _rightImagePath = string.Empty;

    /// <summary>已加载的标定结果对象</summary>
    [ObservableProperty] private StereoParameters? _loadedCalibration;

    /// <summary>用户在左图像上点击的像素坐标（用于距离测量）</summary>
    [ObservableProperty] private System.Drawing.Point _selectedPoint;

    /// <summary>测距结果</summary>
    [ObservableProperty] private double _distanceResult;

    /// <summary>手动测距结果</summary>
    [ObservableProperty] private double _manuDistanceResult;

    /// <summary>是否已有有效的测距结果（用于 UI 显示/隐藏结果行）</summary>
    [ObservableProperty] private bool _hasDistanceResult;

    /// <summary>状态栏文本</summary>
    [ObservableProperty] private string _statusText = "就绪 — 请加载标定文件并导入双目图像";

    /// <summary>是否正在执行异步操作</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>左相机图像源（WPF 绑定用 BitmapSource）</summary>
    [ObservableProperty] private BitmapSource? _leftImage;

    /// <summary>右相机图像源（WPF 绑定用 BitmapSource）</summary>
    [ObservableProperty] private BitmapSource? _rightImage;

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

    /// <summary>当前视差图显示图像（灰度或伪彩色）</summary>
    [ObservableProperty] private BitmapSource? _disparityImage;

    /// <summary>是否开启伪彩色模式（true=伪彩色，false=灰度）</summary>
    [ObservableProperty] private bool _isColorMapEnabled = true;

    /// <summary>视差图是否已生成（控制 UI 显隐）</summary>
    [ObservableProperty] private bool _hasDisparity;

    // ═══════════════════════════════════════════════
    //  派生属性 — 标定参数展示
    // ═══════════════════════════════════════════════

    public string LeftFocalMm => LoadedCalibration != null && LoadedCalibration.PixelSizeUm > 0 ? $"{LoadedCalibration.LeftCameraMatrix[0][0] * LoadedCalibration.PixelSizeUm / 1000.0:F2}" : "--";
    public string RightFocalMm => LoadedCalibration != null && LoadedCalibration.PixelSizeUm > 0 ? $"{LoadedCalibration.RightCameraMatrix[0][0] * LoadedCalibration.PixelSizeUm / 1000.0:F2}" : "--";

    public bool HasCalibration => LoadedCalibration != null;

    /// <summary>重投影误差显示</summary>
    public string ReprojectionErrorDisplay => LoadedCalibration != null
        ? $"{LoadedCalibration.ReprojectionError:F4} 像素" : "--";

    /// <summary>标定结果变化时，同步刷新所有派生属性</summary>
    partial void OnLoadedCalibrationChanged(StereoParameters? value)
    {
        OnPropertyChanged(nameof(LeftFocalMm));
        OnPropertyChanged(nameof(RightFocalMm));
        OnPropertyChanged(nameof(HasCalibration));
        OnPropertyChanged(nameof(ReprojectionErrorDisplay));
    }

    // ═══════════════════════════════════════════════
    //  构造器
    // ═══════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════
    //  命令
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 从磁盘加载标定结果 JSON 文件。
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
            LoadedCalibration = _calibrationService.LoadCalibrationResult(dialog.FileName);
            CalibrationFilePath = dialog.FileName;
            StatusText = $"标定文件已加载：{Path.GetFileName(dialog.FileName)}（{LoadedCalibration.ImageWidth}×{LoadedCalibration.ImageHeight}）";
        }
        catch (System.Exception ex)
        {
            StatusText = $"标定文件加载失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导入左相机图像，并检查是否可自动触发生成视差图。
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

        LeftImagePath = dialog.FileName;
        // 同步加载为 BitmapSource 供 UI 显示
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(dialog.FileName);
            bitmap.EndInit();
            bitmap.Freeze();
            LeftImage = bitmap;
        }
        catch
        {
            LeftImage = null;
        }
        // 切换图像时清除旧结果
        ClearCaches();
        StatusText = $"左图像已导入：{Path.GetFileName(dialog.FileName)}";

        // 左右图均已导入 → 自动触发生成视差图
        TryAutoGenerateDisparity();
    }

    /// <summary>
    /// 导入右相机图像，并检查是否可自动触发生成视差图。
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

        RightImagePath = dialog.FileName;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(dialog.FileName);
            bitmap.EndInit();
            bitmap.Freeze();
            RightImage = bitmap;
        }
        catch
        {
            RightImage = null;
        }
        // 切换图像时清除旧结果
        ClearCaches();
        StatusText = $"右图像已导入：{Path.GetFileName(dialog.FileName)}";

        // 左右图均已导入 → 自动触发生成视差图
        TryAutoGenerateDisparity();
    }

    /// <summary>
    /// 手动触发全图视差图和 XYZ 缓存生成。
    /// 需要已加载标定文件和左右图像，否则提示用户。
    /// </summary>
    [RelayCommand]
    private async Task GenerateDisparity()
    {
        if (LoadedCalibration == null) { StatusText = "错误：请先加载标定结果文件"; return; }
        if (string.IsNullOrEmpty(LeftImagePath) || string.IsNullOrEmpty(RightImagePath))
        { StatusText = "错误：请先导入左右相机图像"; return; }

        await GenerateDisparityAsync();
    }

    /// <summary>
    /// 将当前 CV_32F 视差图保存为 TIFF 文件到本地磁盘。
    /// </summary>
    [RelayCommand]
    private void SaveDisparity()
    {
        if (_disparityResult?.Disparity == null || _disparityResult.Disparity.Empty())
        { StatusText = "错误：视差图尚未生成，无法保存"; return; }

        var dialog = new SaveFileDialog
        {
            Title = "保存视差图（32位浮点）",
            Filter = "TIFF 文件|*.tiff;*.tif|所有文件|*.*",
            DefaultExt = ".tiff",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Cv2.ImWrite(dialog.FileName, _disparityResult.Disparity);
            StatusText = $"视差图已保存：{Path.GetFileName(dialog.FileName)}";
        }
        catch (System.Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 将当前 CV_32FC3 XYZ 空间坐标图保存为 TIFF 文件到本地磁盘。
    /// </summary>
    [RelayCommand]
    private void SaveXYZ()
    {
        if (_cachedXYZ == null || _cachedXYZ.Empty())
        { StatusText = "错误：XYZ 空间坐标尚未生成，无法保存"; return; }

        var dialog = new SaveFileDialog
        {
            Title = "保存 XYZ 空间坐标图（32位浮点）",
            Filter = "TIFF 文件|*.tiff;*.tif|所有文件|*.*",
            DefaultExt = ".tiff",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Cv2.ImWrite(dialog.FileName, _cachedXYZ);
            StatusText = $"XYZ 空间坐标图已保存：{Path.GetFileName(dialog.FileName)}";
        }
        catch (System.Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 使用在左右图像上点击选择的对应点进行三角测量（fb/d 公式）。
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task MeasureManualDistanceAsync(CancellationToken ct)
    {
        if (LoadedCalibration == null) { StatusText = "错误：请先加载标定结果文件"; return; }

        IsBusy = true;

        try
        {
            /// 要求用户已分别在左图和右图上点击选中测量点。
            // 使用图像上点击选择的点（而非手动文本框输入）
            if (!MarkerVisible) { StatusText = "错误：请先在左图上点击选择测量点"; return; }
            if (!RightMarkerVisible) { StatusText = "错误：请先在右图上点击选择对应点"; return; }

            var leftPt = SelectedPoint;
            var rightPt = RightSelectedPoint;
            StatusText = $"正在进行三角测量... 左({leftPt.X},{leftPt.Y}) 右({rightPt.X},{rightPt.Y})";
            ManuDistanceResult = await _distanceService.MeasureDistanceFromPointsAsync(
                LoadedCalibration, new Point2f(leftPt.X, leftPt.Y),
                new Point2f(rightPt.X, rightPt.Y)
                );
            StatusText = $"测量完成！({leftPt.X},{leftPt.Y})→({rightPt.X},{rightPt.Y}) 距离：{DistanceResult:F4} mm";
            HasDistanceResult = true;
            if (double.IsNaN(ManuDistanceResult))
                StatusText = "测量失败：无法计算距离（可能对应点不在有效视差范围内或越界）";
            else
                StatusText = $"手动测量完成！({leftPt.X},{leftPt.Y}) -> ({rightPt.X},{rightPt.Y}) 距离：{DistanceResult:F4} mm";
        }
        catch (System.Exception ex)
        { StatusText = $"测量失败：{ex.Message}"; HasDistanceResult = false; }
        finally
        { IsBusy = false; }
    }

    /// <summary>
    /// 处理用户点击左图图像的逻辑。
    /// 保存像素坐标后，立即从缓存的 XYZ 数据中读取距离并显示（即时测距）。
    /// 由 View 的代码后置 (DistancePage.xaml.cs) 在 MouseDown 事件中调用。
    /// </summary>
    /// <param name="pixelX">图像像素 X 坐标</param>
    /// <param name="pixelY">图像像素 Y 坐标</param>
    public void OnImageClickedWithDistance(int pixelX, int pixelY)
    {
        SelectedPoint = new System.Drawing.Point(pixelX, pixelY);

        // 尝试从缓存的 XYZ 数据中提取距离
        if (_cachedXYZ != null &&
            pixelX >= 0 && pixelX < _cachedXYZ.Cols &&
            pixelY >= 0 && pixelY < _cachedXYZ.Rows)
        {
            var vec3 = _cachedXYZ.At<Vec3f>(pixelY, pixelX);
            float z = vec3.Item2; // Z 分量即深度/距离（米）

            if (z > 0 && !float.IsNaN(z) && !float.IsInfinity(z))
            {
                DistanceResult = z;
                HasDistanceResult = true;
                StatusText = $"即时测距完成！像素 ({pixelX}, {pixelY}) 距离：{z:F4} m";
                return;
            }
        }

        // XYZ 缓存不可用或读取失败
        HasDistanceResult = false;
        StatusText = $"测量点已选择：({pixelX}, {pixelY}) — 暂无有效视差数据，请先生成视差图";
    }

    // ═══════════════════════════════════════════════
    //  私有方法 — 视差图生成与缓存
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 当左右图像和标定文件都已就绪时，自动触发生成视差图和 XYZ 缓存。
    /// </summary>
    private void TryAutoGenerateDisparity()
    {
        if (LoadedCalibration == null ||
            string.IsNullOrEmpty(LeftImagePath) ||
            string.IsNullOrEmpty(RightImagePath))
            return;

        // 延迟触发异步生成，避免阻塞 UI 线程
        _ = GenerateDisparityAsync();
    }

    /// <summary>
    /// 后台异步生成全图视差图和 XYZ 空间坐标缓存。
    /// 生成完成后自动更新前端视差图显示和状态提示。
    /// </summary>
    private async Task GenerateDisparityAsync()
    {
        // 清除旧缓存
        ClearCaches();

        IsBusy = true;
        HasDisparity = false;
        StatusText = "正在生成全图视差图，请稍候...";

        try
        {
            await Task.Run(() =>
            {
                // ── 1. 生成视差图（CV_32F）──
                _disparityResult = _distanceService.GenerateDisparity(LeftImagePath, RightImagePath, LoadedCalibration!);

                // ── 2. 使用 Q 矩阵反算全图 XYZ 空间坐标 ──
                _cachedXYZ = new Mat();
                Cv2.ReprojectImageTo3D(_disparityResult.Disparity, _cachedXYZ, _disparityResult.Q);

            });

            HasDisparity = true;
            StatusText = $"视差图生成完成（{_cachedXYZ!.Cols}×{_cachedXYZ.Rows}），点击左图即可即时测距";
        }
        catch (System.Exception ex)
        {
            ClearCaches();
            StatusText = $"视差图生成失败：{ex.Message}";
            HasDisparity = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 清除视差图、XYZ 缓存及相关显示状态。
    /// 在导入新图像或关闭页面时调用。
    /// </summary>
    private void ClearCaches()
    {
        _disparityResult?.Dispose();
        _disparityResult = null;

        _cachedXYZ?.Dispose();
        _cachedXYZ = null;

        HasDisparity = false;
        HasDistanceResult = false;
        IsColorMapEnabled = false;
        DisparityImage = null;
    }

    // ═══════════════════════════════════════════════
    //  IDisposable — 释放所有非托管 Mat 资源
    // ═══════════════════════════════════════════════

    /// <summary>释放内部 Mat 缓存和 DisparityResult</summary>
    public void Dispose()
    {
        ClearCaches();
    }
}
