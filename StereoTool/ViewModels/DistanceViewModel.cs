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
/// 负责加载标定文件、导入双目图像、执行空间距离测量并展示结果。
/// </summary>
public partial class DistanceViewModel : ObservableObject
{
    private readonly CalibrationService _calibrationService;
    private readonly DistanceService _distanceService;

    // ═══════════════════════════════════════════════
    //  绑定属性
    // ═══════════════════════════════════════════════

    /// <summary>标定结果文件路径（用户从磁盘加载的 JSON 文件）</summary>
    [ObservableProperty] private string _calibrationFilePath = string.Empty;

    /// <summary>左相机图像文件路径</summary>
    [ObservableProperty] private string _leftImagePath = string.Empty;

    /// <summary>右相机图像文件路径</summary>
    [ObservableProperty] private string _rightImagePath = string.Empty;

    /// <summary>已加载的标定结果对象</summary>
    [ObservableProperty] private StereoCalibrationParams? _loadedCalibration;

    /// <summary>用户在左图像上点击的像素坐标（用于距离测量）</summary>
    [ObservableProperty] private System.Drawing.Point _selectedPoint;

    /// <summary>测距结果（单位：）</summary>
    [ObservableProperty] private double _distanceResult;

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

    /// <summary>手动输入：左图 X 坐标（像素）</summary>
    [ObservableProperty] private string _manualLeftX = "";

    /// <summary>手动输入：左图 Y 坐标（像素）</summary>
    [ObservableProperty] private string _manualLeftY = "";

    /// <summary>手动输入：右图 X 坐标（像素）</summary>
    [ObservableProperty] private string _manualRightX = "";

    /// <summary>手动输入：右图 Y 坐标（像素）</summary>
    [ObservableProperty] private string _manualRightY = "";

    /// <summary>用户在右图像上点击的像素坐标</summary>
    [ObservableProperty] private System.Drawing.Point _rightSelectedPoint;

    /// <summary>右图标记点在 Canvas 中的 X 坐标</summary>
    [ObservableProperty] private double _rightMarkerX;

    /// <summary>右图标记点在 Canvas 中的 Y 坐标</summary>
    [ObservableProperty] private double _rightMarkerY;

    /// <summary>右图标记点是否可见</summary>
    [ObservableProperty] private bool _rightMarkerVisible;

    // ═══════════════════════════════════════════════
    //  派生属性 — 标定参数展示
    // ═══════════════════════════════════════════════

    public bool HasCalibration => LoadedCalibration != null;

    // ── 内参/畸变/外参的派生属性已由 StereoParameters 内置提供，通过 LoadedCalibration 访问 ──

    /// <summary>重投影误差显示</summary>
    public string ReprojectionErrorDisplay => LoadedCalibration != null
        ? $"{LoadedCalibration.ReprojectionError:F4} 像素" : "--";

    /// <summary>标定结果变化时，同步刷新所有派生属性</summary>
    partial void OnLoadedCalibrationChanged(StereoCalibrationParams? value)
    {
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
    public DistanceViewModel(
        CalibrationService calibrationService,
        DistanceService distanceService)
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
        StatusText = $"左图像已导入：{Path.GetFileName(dialog.FileName)}";
        // 切换图像时清除旧结果
        HasDistanceResult = false;
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
        StatusText = $"右图像已导入：{Path.GetFileName(dialog.FileName)}";
        // 切换图像时清除旧结果
        HasDistanceResult = false;
    }

    /// <summary>
    /// 异步执行距离测量。
    /// 使用当前已加载的标定参数、左右图像以及用户在左图上选中的像素坐标进行测量。
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task MeasureDistanceAsync(CancellationToken ct)
    {
        // ── 前置条件校验 ──
        if (LoadedCalibration == null) { StatusText = "错误：请先加载标定结果文件"; return; }
        if (string.IsNullOrEmpty(LeftImagePath) || string.IsNullOrEmpty(RightImagePath)) 
        { StatusText = "错误：请先导入左右相机图像"; return; }
        if (!File.Exists(LeftImagePath)) { StatusText = "错误：左图像文件不存在"; return; }
        if (!File.Exists(RightImagePath)) { StatusText = "错误：右图像文件不存在"; return; }

        // ── 执行测距 ──
        IsBusy = true;
        StatusText = "正在进行立体匹配与距离计算...";

        try
        {
            DistanceResult = await _distanceService.MeasureDistanceAsync(LeftImagePath, RightImagePath, LoadedCalibration, new Point2f(SelectedPoint.X, SelectedPoint.Y));

            HasDistanceResult = true;
            StatusText = $"测量完成！像素 ({SelectedPoint.X}, {SelectedPoint.Y}) 的距离：{DistanceResult:F2} mm";
        }
        catch (OperationCanceledException)
        {
            StatusText = "测量已被用户取消";
            HasDistanceResult = false;
        }
        catch (System.Exception ex)
        {
            StatusText = $"测量失败：{ex.Message}";
            HasDistanceResult = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 使用手动输入的左右对应点坐标执行距离测量（跳过 SGBM 匹配）。
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task MeasureManualDistanceAsync(CancellationToken ct)
    {
        if (LoadedCalibration == null) { StatusText = "错误：请先加载标定结果文件"; return; }
        if (!int.TryParse(ManualLeftX, out int lx) || !int.TryParse(ManualLeftY, out int ly))
        { StatusText = "错误：左图坐标格式无效，请输入整数值"; return; }
        if (!int.TryParse(ManualRightX, out int rx) || !int.TryParse(ManualRightY, out int ry))
        { StatusText = "错误：右图坐标格式无效，请输入整数值"; return; }

        IsBusy = true;
        StatusText = $"正在进行三角测量... 左({lx},{ly}) 右({rx},{ry})";

        try
        {
            DistanceResult = await _distanceService.MeasureDistanceFromPointsAsync(LoadedCalibration, new Point2f(lx, ly), new Point2f(rx, ry));

            HasDistanceResult = true;
            if (double.IsNaN(DistanceResult))
                StatusText = "测量失败：无法计算距离（可能对应点不在有效视差范围内或越界）";
            else
                StatusText = $"手动测量完成！({lx},{ly})→({rx},{ry}) 距离：{DistanceResult:F2} mm";
        }
        catch (System.Exception ex)
        { StatusText = $"测量失败：{ex.Message}"; HasDistanceResult = false; }
        finally
        { IsBusy = false; }
    }

    /// <summary>
    /// 处理用户点击图像的逻辑。将控件坐标映射为原始图像像素坐标并保存为 SelectedPoint。
    /// 由 View 的代码后置 (DistancePage.xaml.cs) 在 MouseDown 事件中调用。
    /// </summary>
    /// <param name="clickPosition">鼠标相对于 Image 控件的点击位置</param>
    /// <param name="displayWidth">Image 控件实际渲染宽度</param>
    /// <param name="displayHeight">Image 控件实际渲染高度</param>
    public void OnImageClicked(System.Windows.Point clickPosition, double displayWidth, double displayHeight)
    {
        if (LeftImage == null)
            return;

        // 将控件显示坐标映射为原始图像像素坐标
        double scaleX = LeftImage.PixelWidth / displayWidth;
        double scaleY = LeftImage.PixelHeight / displayHeight;

        int pixelX = (int)(clickPosition.X * scaleX);
        int pixelY = (int)(clickPosition.Y * scaleY);

        // 确保坐标不越界
        pixelX = Math.Clamp(pixelX, 0, (int)LeftImage.PixelWidth - 1);
        pixelY = Math.Clamp(pixelY, 0, (int)LeftImage.PixelHeight - 1);

        SelectedPoint = new System.Drawing.Point(pixelX, pixelY);
        HasDistanceResult = false;
        StatusText = $"测量点已选择：({pixelX}, {pixelY}) — 就绪，可执行测距";
    }
}
