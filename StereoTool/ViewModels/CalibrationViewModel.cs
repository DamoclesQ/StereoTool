using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenCvSharp.WpfExtensions;
using StereoTool.Models;
using StereoTool.Services;

namespace StereoTool.ViewModels;

/// <summary>单目标定工作流：添加分析、选择样本、标定、评估、改进和导出。</summary>
public partial class CalibrationViewModel : ObservableObject
{
    private readonly CalibrationService _calibrationService;
    private CancellationTokenSource? _analysisCancellation;

    /// <summary>棋盘格与针孔畸变模型设置。</summary>
    public ChessboardConfig ChessboardConfig { get; } = new();

    /// <summary>包含成功、失败和用户排除图片的数据浏览器。</summary>
    [ObservableProperty] private ObservableCollection<DetectedImage> _detectedImages = [];

    /// <summary>当前预览图片。</summary>
    [ObservableProperty] private DetectedImage? _selectedImage;

    /// <summary>当前有效的标定结果；数据或模型改变后立即失效。</summary>
    [ObservableProperty] private CalibrationParams? _calibrationResult;

    /// <summary>角点和重投影点预览。</summary>
    [ObservableProperty] private BitmapSource? _previewImage;

    /// <summary>去畸变预览。</summary>
    [ObservableProperty] private BitmapSource? _undistortedImage;

    /// <summary>是否正在分析或标定。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>分析进度，范围为 0 到 100。</summary>
    [ObservableProperty] private double _progress;

    /// <summary>操作结果和采集提示。</summary>
    [ObservableProperty] private string _statusText = "设置棋盘格，添加图片后自动分析；建议采集 10～20 张不同位置和倾角的图片。";

    /// <summary>用于批量排除的平均误差阈值（像素）。</summary>
    [ObservableProperty] private double _errorThreshold = 1;

    /// <summary>是否存在可导出的当前结果。</summary>
    public bool HasCalibrationResult => CalibrationResult != null;

    /// <summary>当前数据浏览器统计。</summary>
    public string DatasetSummary => $"共 {DetectedImages.Count} 张 · 检测成功 {DetectedImages.Count(image => image.IsDetected)} 张 · 参与 {DetectedImages.Count(image => image.IsDetected && image.IsIncluded)} 张";

    /// <summary>标定全局质量统计；均值和 RMS 分开展示。</summary>
    public string ResultSummary => CalibrationResult == null ? "尚未标定，或数据已改变，请重新标定。" :
        $"图片数 {(CalibrationResult.ImageCount > 0 ? CalibrationResult.ImageCount.ToString() : "文件未记录")} · " +
        $"平均误差 {(double.IsFinite(CalibrationResult.MeanReprojectionError) ? CalibrationResult.MeanReprojectionError.ToString("F4") : "--")} px · " +
        $"RMS {CalibrationResult.ReprojectionError:F4} px";

    /// <summary>以毫米表示的物理焦距。</summary>
    public string FocalMm => CalibrationResult != null && CalibrationResult.PixelSizeUm > 0
        ? $"{CalibrationResult.CameraMatrix[0, 0] * CalibrationResult.PixelSizeUm / 1000:F3} mm" : "未提供像元尺寸";

    /// <summary>创建页面并监听配置内部属性的变化。</summary>
    /// <param name="calibrationService">标定与图片分析服务。</param>
    public CalibrationViewModel(CalibrationService calibrationService)
    {
        _calibrationService = calibrationService;
        ChessboardConfig.PropertyChanged += OnConfigChanged;
    }

    /// <summary>结果变化时刷新统计和图像。</summary>
    /// <param name="value">当前结果。</param>
    partial void OnCalibrationResultChanged(CalibrationParams? value)
    {
        OnPropertyChanged(nameof(HasCalibrationResult));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(FocalMm));
        UpdatePreview();
    }

    /// <summary>选中图片后显示原图检测点、重投影点及去畸变结果。</summary>
    /// <param name="value">选中的图片。</param>
    partial void OnSelectedImageChanged(DetectedImage? value)
    {
        UpdatePreview();
    }

    /// <summary>设置变化后使结果失效；只有角点行列变化需要重新检测。</summary>
    /// <param name="sender">配置对象。</param>
    /// <param name="e">变更字段。</param>
    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChessboardConfig.CornerCols) or nameof(ChessboardConfig.CornerRows))
        {
            ReplaceImages(DetectedImages.Select(image => new DetectedImage { ImagePath = image.ImagePath }).ToList());
        }
        InvalidateResult(true);
        StatusText = "配置已改变，请重新标定；内角点行列变化后需重新分析。";
    }

    /// <summary>更新图片浏览器并订阅参与选项。</summary>
    /// <param name="images">全部图片记录。</param>
    private void ReplaceImages(IEnumerable<DetectedImage> images)
    {
        foreach (var image in DetectedImages)
            image.PropertyChanged -= OnImageChanged;
        SelectedImage = null;
        DetectedImages = new ObservableCollection<DetectedImage>(images);
        foreach (var image in DetectedImages)
            image.PropertyChanged += OnImageChanged;
        OnPropertyChanged(nameof(DatasetSummary));
        SelectedImage = DetectedImages.FirstOrDefault();
    }

    /// <summary>改变参与选项后使旧参数失效，保留上一轮误差以便继续筛选。</summary>
    /// <param name="sender">图片记录。</param>
    /// <param name="e">变更字段。</param>
    private void OnImageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DetectedImage.IsIncluded))
            return;
        InvalidateResult(false);
        OnPropertyChanged(nameof(DatasetSummary));
        StatusText = "参与图片已改变，请重新标定。误差条保留上一轮结果供筛选。";
    }

    /// <summary>清除旧结果以及可选的诊断缓存。</summary>
    /// <param name="clearErrors">是否清除上一轮误差。</param>
    private void InvalidateResult(bool clearErrors)
    {
        foreach (var image in DetectedImages)
        {
            image.ProjectedCorners = [];
            if (clearErrors)
                image.MeanError = double.NaN;
        }
        CalibrationResult = null;
        UpdatePreview();
    }

    /// <summary>从文件夹追加图片，跳过重复路径，并自动分析全部图片。</summary>
    [RelayCommand]
    private async Task ImportImagesAsync()
    {
        var dialog = new OpenFolderDialog { Title = "添加单目标定图片文件夹" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var paths = DetectedImages.Select(image => image.ImagePath)
                .Concat(_calibrationService.GetImagePaths(dialog.FolderName))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ReplaceImages(paths.Select(path => new DetectedImage { ImagePath = path }));
            await DetectCornersAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"添加图片失败：{ex.Message}";
        }
    }

    /// <summary>后台分析图片，进度与最终记录在 UI 线程发布。</summary>
    /// <param name="ct">图片之间生效的取消令牌。</param>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DetectCornersAsync(CancellationToken ct)
    {
        if (DetectedImages.Count == 0)
        {
            StatusText = "请先添加图片。";
            return;
        }
        IsBusy = true;
        Progress = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _analysisCancellation = cancellation;
        ct = cancellation.Token;
        InvalidateResult(true);
        var paths = DetectedImages.Select(image => image.ImagePath).ToList();
        var progress = new Progress<int>(count =>
        {
            Progress = count * 100.0 / paths.Count;
            StatusText = $"正在分析图片 {count}/{paths.Count}...";
        });
        try
        {
            var images = await Task.Run(() => _calibrationService.AnalyzeImages(paths, ChessboardConfig, progress, ct), ct);
            ReplaceImages(images);
            StatusText = $"分析完成：{DatasetSummary}。检查检测点后开始标定。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "分析已取消，可以重新分析。";
        }
        catch (Exception ex)
        {
            StatusText = $"图片分析失败：{ex.Message}";
        }
        finally
        {
            _analysisCancellation = null;
            IsBusy = false;
        }
    }

    /// <summary>请求在当前图片检测结束后停止分析。</summary>
    [RelayCommand]
    private void CancelAnalysis()
    {
        _analysisCancellation?.Cancel();
    }

    /// <summary>后台求解后一次性发布逐图误差和有效参数。</summary>
    [RelayCommand]
    private async Task StartCalibrationAsync()
    {
        var images = DetectedImages.Where(image => image.IsIncluded && image.IsDetected)
            .Select(image => new DetectedImage
            {
                ImagePath = image.ImagePath,
                ImageSize = image.ImageSize,
                Corners = image.Corners
            }).ToList();
        IsBusy = true;
        InvalidateResult(true);
        StatusText = "正在标定；完成后请检查逐图误差和去畸变图。";
        try
        {
            var result = await Task.Run(() => _calibrationService.CameraCalibrate(ChessboardConfig, images));
            foreach (var image in images)
            {
                var target = DetectedImages.First(item => item.ImagePath == image.ImagePath);
                target.MeanError = image.MeanError;
                target.ProjectedCorners = image.ProjectedCorners;
            }
            CalibrationResult = result;
            Progress = 100;
            StatusText = "标定完成。可排除高误差图片并重新标定，确认后导出 YAML。";
        }
        catch (Exception ex)
        {
            StatusText = $"标定失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>排除上一轮平均误差超过阈值的图片，允许勾选恢复。</summary>
    [RelayCommand]
    private void ExcludeHighError()
    {
        var images = DetectedImages.Where(image => image.IsIncluded && image.MeanError > ErrorThreshold).ToList();
        foreach (var image in images)
            image.IsIncluded = false;
        StatusText = $"已排除 {images.Count} 张图片，请重新标定；可通过“参与”复选框恢复。";
    }

    /// <summary>从当前数据集移除指定记录，不删除磁盘文件。</summary>
    /// <param name="image">待移除记录。</param>
    [RelayCommand]
    private void DeleteImage(DetectedImage image)
    {
        ReplaceImages(DetectedImages.Where(item => item != image).ToList());
        InvalidateResult(true);
    }

    /// <summary>导出当前有效标定结果。</summary>
    [RelayCommand]
    private void SaveCalibrationFile()
    {
        if (CalibrationResult == null)
        {
            StatusText = "当前结果无效，请先重新标定。";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "导出单目参数",
            Filter = "OpenCV YAML|*.yaml;*.yml",
            FileName = "calibrationParams.yaml"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            _calibrationService.SaveCalibrationParams(dialog.FileName, CalibrationResult);
            StatusText = $"参数已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>加载参数用于查看与去畸变；不将导入结果冒充当前图片的拟合结果。</summary>
    [RelayCommand]
    private void LoadCalibrationFile()
    {
        var dialog = new OpenFileDialog { Title = "加载单目参数", Filter = "OpenCV YAML|*.yaml;*.yml" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            InvalidateResult(true);
            CalibrationResult = _calibrationService.LoadCalibrationParams(dialog.FileName);
            StatusText = $"已加载 {Path.GetFileName(dialog.FileName)}，逐图误差需要使用当前数据重新标定。";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>清空当前数据与结果。</summary>
    [RelayCommand]
    private void ClearData()
    {
        ReplaceImages([]);
        InvalidateResult(true);
        Progress = 0;
        StatusText = "数据已清空。";
    }

    /// <summary>更新预览，并在页面交互边界处理图片读取异常。</summary>
    private void UpdatePreview()
    {
        PreviewImage = null;
        UndistortedImage = null;
        if (SelectedImage == null)
            return;
        try
        {
            using var preview = _calibrationService.CreatePreview(SelectedImage.ImagePath, SelectedImage.Corners,
                SelectedImage.ProjectedCorners, ChessboardConfig);
            PreviewImage = preview.ToBitmapSource();
            PreviewImage.Freeze();
            if (CalibrationResult == null || preview.Width != CalibrationResult.ImageWidth || preview.Height != CalibrationResult.ImageHeight)
                return;
            using var undistorted = _calibrationService.CreatePreview(SelectedImage.ImagePath, [], [], ChessboardConfig, CalibrationResult);
            UndistortedImage = undistorted.ToBitmapSource();
            UndistortedImage.Freeze();
        }
        catch (Exception ex)
        {
            StatusText = $"预览失败：{ex.Message}";
        }
    }
}
