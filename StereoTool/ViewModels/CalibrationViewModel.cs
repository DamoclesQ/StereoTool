using System.Collections.ObjectModel;
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
/// 单目标定页面的 ViewModel（合并配置 + 标定）。
/// 棋盘格配置、图像导入、角点检测、标定执行、结果展示一体。
/// 与双目标定相比，仅处理单相机图像序列，不区分左右。
/// </summary>
public partial class CalibrationViewModel : ObservableObject
{
    private readonly CalibrationService _calibrationService;

    /// <summary>支持的图片扩展名</summary>
    private static readonly string[] ImageExtensions =
        [".bmp", ".jpg", ".jpeg", ".png", ".tiff", ".tif"];

    // ═══════════════════════════════════════════════
    //  绑定属性 — 配置
    // ═══════════════════════════════════════════════

    /// <summary>棋盘格配置参数</summary>
    [ObservableProperty] private ChessboardConfig _chessboardConfig = new();

    /// <summary>待检测角点的图像路径列表</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingCount))]
    private ObservableCollection<string> _imagePaths = [];

    /// <summary>已通过角点检测的有效图像列表</summary>
    [ObservableProperty] private ObservableCollection<DetectedImage> _detectedImages = [];

    /// <summary>列表中当前选中的图像，用于角点预览</summary>
    [ObservableProperty] private DetectedImage? _selectedImage;

    // ═══════════════════════════════════════════════
    //  绑定属性 — 标定执行与结果
    // ═══════════════════════════════════════════════

    /// <summary>标定完成后的结果对象</summary>
    [ObservableProperty] private CalibrationParams? _calibrationResult;

    /// <summary>状态栏文本</summary>
    [ObservableProperty] private string _statusText = "就绪 — 设置棋盘格参数，导入图片，点击「检测角点」";

    /// <summary>是否正在执行异步操作</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>角点预览图（绘制角点后的结果图）</summary>
    [ObservableProperty] private BitmapSource? _previewImage;

    // ═══════════════════════════════════════════════
    //  派生属性 — 统计
    // ═══════════════════════════════════════════════

    /// <summary>待检测数量</summary>
    public int PendingCount => ImagePaths.Count;

    // ═══════════════════════════════════════════════
    //  派生属性 — 焦距/主点显示
    // ═══════════════════════════════════════════════

    /// <summary>物理焦距（mm），由 fx × 像元尺寸 / 1000 计算得出</summary>
    public string FocalMm => CalibrationResult != null && CalibrationResult.PixelSizeUm > 0
        ? $"{CalibrationResult.CameraMatrix[0,0] * CalibrationResult.PixelSizeUm / 1000.0:F2}" : "--";

    // ── Fx/Fy/Cx/Cy/K1/K2/P1/P2/K3 派生属性已由 CameraParameters 内置提供 ──
    public bool HasCalibrationResult => CalibrationResult != null;
    public string ReprojectionErrorDisplay => CalibrationResult != null
        ? $"{CalibrationResult.ReprojectionError:F4} 像素" : "--";

    // ═══════════════════════════════════════════════
    //  属性变更回调
    // ═══════════════════════════════════════════════

    /// <summary>标定结果变化时，同步刷新所有派生属性</summary>
    partial void OnCalibrationResultChanged(CalibrationParams? value)
    {
        OnPropertyChanged(nameof(FocalMm));
        OnPropertyChanged(nameof(HasCalibrationResult));
        OnPropertyChanged(nameof(ReprojectionErrorDisplay));
    }

    /// <summary>棋盘格配置变化时，清空所有角点缓存</summary>
    partial void OnChessboardConfigChanged(ChessboardConfig value)
    {
        DetectedImages.Clear();
        ImagePaths.Clear();
        SelectedImage = null;
        PreviewImage = null;
        StatusText = "棋盘格配置已变更，角点缓存已清空，请重新导入图片";
    }

    /// <summary>选中的图像变化时，自动更新角点预览图</summary>
    partial void OnSelectedImageChanged(DetectedImage? value)
    {
        if (value == null)
        {
            PreviewImage = null;
            return;
        }
        // 使用棋盘格内角点数构建 Size（列数, 行数）
        var patternSize = new Size(ChessboardConfig.CornerCols, ChessboardConfig.CornerRows);
        PreviewImage = DrawCornersPreview(value.ImagePath, value.Corners, patternSize);
    }

    // ═══════════════════════════════════════════════
    //  构造器
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 构造 MonoCalibrationViewModel，注入标定服务。
    /// </summary>
    /// <param name="calibrationService">标定服务（由 DI 容器注入）</param>
    public CalibrationViewModel(CalibrationService calibrationService)
    {
        _calibrationService = calibrationService;
    }

    // ═══════════════════════════════════════════════
    //  命令 — 导入图片
    // ═══════════════════════════════════════════════

    /// <summary>导入标定图片文件夹</summary>
    [RelayCommand]
    private void ImportImages()
    {
        var paths = ImportImagesFromFolder("选择标定图片文件夹");
        if (paths.Count == 0) return;
        foreach (var p in paths) ImagePaths.Add(p);
        StatusText = $"已导入 {ImagePaths.Count} 张图片，请点击「检测角点」";
    }

    // ═══════════════════════════════════════════════
    //  命令 — 角点检测
    // ═══════════════════════════════════════════════

    /// <summary>对待检测图片执行角点检测，结果加入 ValidImages</summary>
    [RelayCommand]
    private async Task DetectCornersAsync()
    {
        if (ImagePaths.Count == 0)
        {
            StatusText = "错误：至少导入 1 张图片才能进行角点检测";
            return;
        }

        IsBusy = true;
        StatusText = $"正在进行角点检测（{ImagePaths.Count} 张）...";

        try
        {
            var detectedImages = _calibrationService.DetectImages(ImagePaths, ChessboardConfig);

            DetectedImages.Clear();
            foreach (var detectedImage in detectedImages)
            {
                DetectedImages.Add(detectedImage);
            }

            var rejectCounts = ImagePaths.Count - DetectedImages.Count;
            StatusText = $"角点检测完成：Accepted: {DetectedImages.Count}，Rejected: {rejectCounts}";
        }
        catch (System.Exception ex)
        {
            StatusText = $"角点检测失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        SelectedImage = DetectedImages.FirstOrDefault();
    }

    // ═══════════════════════════════════════════════
    //  命令 — 删除图像
    // ═══════════════════════════════════════════════

    /// <summary>从有效列表中删除指定图像</summary>
    [RelayCommand]
    private void DeleteImage(DetectedImage image)
    {
        if (SelectedImage == image)
            SelectedImage = null;
        DetectedImages.Remove(image);
        StatusText = $"已删除 1 张，剩余 {DetectedImages.Count} 张有效图片";
    }

    // ═══════════════════════════════════════════════
    //  命令 — 标定与持久化
    // ═══════════════════════════════════════════════

    /// <summary>对有效图像执行单目标定</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task StartCalibrationAsync(CancellationToken ct)
    {
        if (DetectedImages.Count < 3)
        {
            StatusText = $"错误：至少需要 3 张有效图片，当前 {DetectedImages.Count} 张";
            return;
        }

        IsBusy = true;
        StatusText = $"正在进行单目标定（{DetectedImages.Count} 张图片）...";

        try
        {
            CalibrationResult = _calibrationService.CameraCalibrate(ChessboardConfig, DetectedImages);

            StatusText = $"标定完成！共使用 {DetectedImages.Count} 张图片，结果可保存。";
        }
        catch (System.Exception ex)
        {
            StatusText = $"标定失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>保存标定结果到 YAML 文件</summary>
    [RelayCommand]
    private void SaveCalibrationFile()
    {
        if (CalibrationResult == null)
        {
            StatusText = "错误：尚未完成标定，没有可保存的结果";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存单目标定结果",
            Filter = "YAML 文件|*.yaml;*.yml|所有文件|*.*",
            DefaultExt = ".yaml",
            FileName = "calibrationParams.yaml",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _calibrationService.SaveCalibrationParams(dialog.FileName, CalibrationResult);
            StatusText = $"标定结果已保存至：{dialog.FileName}";
        }
        catch (System.Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>从 YAML 文件加载标定结果</summary>
    [RelayCommand]
    private void LoadCalibrationFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "加载标定结果文件",
            Filter = "YAML 文件|*.yaml;*.yml|所有文件|*.*",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            CalibrationResult = _calibrationService.LoadCalibrationParams(dialog.FileName);
            StatusText = $"已加载标定结果：{Path.GetFileName(dialog.FileName)}";
        }
        catch (System.Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>清空所有标定相关数据，用于重新导入图片并标定</summary>
    [RelayCommand]
    private void ClearData()
    {
        DetectedImages.Clear();
        ImagePaths.Clear();
        CalibrationResult = null;
        SelectedImage = null;
        PreviewImage = null;
        StatusText = "数据已清空，请重新导入图片并开始标定";
    }

    // ═══════════════════════════════════════════════
    //  私有方法 — 图片导入辅助
    // ═══════════════════════════════════════════════

    /// <summary>弹出文件夹选择对话框并返回排序后的图片路径列表</summary>
    private static List<string> ImportImagesFromFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = "D:\\0_Data\\个人资料\\Projects\\StereoTool"
        };

        if (dialog.ShowDialog() != true)
            return [];

        return Directory.EnumerateFiles(dialog.FolderName)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ═══════════════════════════════════════════════
    //  私有方法 — 角点预览
    // ═══════════════════════════════════════════════

    /// <summary>在图片上绘制棋盘格角点并返回 WPF BitmapSource</summary>
    private static BitmapSource DrawCornersPreview(string imagePath, Point2f[] corners, Size patternSize)
    {
        using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (mat.Empty())
            return null!;

        // 绘制检测到的角点（绿色十字 + 彩色连线）
        Cv2.DrawChessboardCorners(mat, patternSize, corners, true);

        // Mat → PNG 字节流 → BitmapImage
        using var ms = new MemoryStream();
        // OpenCvSharp4 中 ImWrite(string, Stream) 是 Mat 扩展方法，不可用；改用 ImEncode 编码为字节数组写入
        byte[] pngBytes = mat.ImEncode(".png");
        ms.Write(pngBytes, 0, pngBytes.Length);
        ms.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = ms;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze(); // 允许跨线程访问
        return bitmap;
    }
}
