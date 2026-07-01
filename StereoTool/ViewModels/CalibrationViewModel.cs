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
/// 双目标定页面的 ViewModel（合并配置 + 标定）。
/// 棋盘格配置、图像导入、角点检测、标定执行、结果展示一体。
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
    [ObservableProperty]
    private ChessboardConfig _chessboardConfig = new();

    /// <summary>左图待配对路径列表</summary>
    [ObservableProperty]
    private ObservableCollection<string> _leftPendingPaths = [];

    /// <summary>右图待配对路径列表</summary>
    [ObservableProperty]
    private ObservableCollection<string> _rightPendingPaths = [];

    /// <summary>已通过角点检测的有效图片对列表</summary>
    [ObservableProperty]
    private ObservableCollection<CachedStereoPair> _validPairs = [];

    /// <summary>角点检测失败被跳过的图片对数量</summary>
    [ObservableProperty]
    private int _skippedCount;

    /// <summary>列表中当前选中的图片对，用于角点预览</summary>
    [ObservableProperty]
    private CachedStereoPair? _selectedPair;

    // ═══════════════════════════════════════════════
    //  绑定属性 — 标定执行与结果
    // ═══════════════════════════════════════════════

    /// <summary>标定完成后的结果对象</summary>
    [ObservableProperty]
    private StereoParameters? _calibrationResult;

    /// <summary>标定进度值（0-100）</summary>
    [ObservableProperty]
    private int _progressValue;

    /// <summary>状态栏文本</summary>
    [ObservableProperty]
    private string _statusText = "就绪 — 设置棋盘格参数，导入左右图片，点击「检测角点」";

    /// <summary>是否正在执行异步操作</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>角点预览 — 左图（绘制角点后的结果图）</summary>
    [ObservableProperty]
    private BitmapSource? _leftPreviewImage;

    /// <summary>角点预览 — 右图（绘制角点后的结果图）</summary>
    [ObservableProperty]
    private BitmapSource? _rightPreviewImage;

    // ═══════════════════════════════════════════════
    //  派生属性 — 统计
    // ═══════════════════════════════════════════════

    /// <summary>左图待配对数量</summary>
    public int LeftPendingCount => LeftPendingPaths.Count;

    /// <summary>右图待配对数量</summary>
    public int RightPendingCount => RightPendingPaths.Count;

    // ═══════════════════════════════════════════════
    //  派生属性 — 焦距/主点显示
    // ═══════════════════════════════════════════════

    public string LeftFocalMm => CalibrationResult != null && CalibrationResult.PixelSizeUm > 0
        ? $"{CalibrationResult.LeftCameraMatrix[0][0] * CalibrationResult.PixelSizeUm / 1000.0:F2}" : "--";
    public string RightFocalMm => CalibrationResult != null && CalibrationResult.PixelSizeUm > 0
        ? $"{CalibrationResult.RightCameraMatrix[0][0] * CalibrationResult.PixelSizeUm / 1000.0:F2}" : "--";
    public bool HasCalibrationResult => CalibrationResult != null;
    public string ReprojectionErrorDisplay => CalibrationResult != null
        ? $"{CalibrationResult.ReprojectionError:F4} 像素" : "--";

    // ── 外参/畸变的派生属性已由 StereoParameters 内置提供 ──

    /// <summary>标定结果变化时，同步刷新所有派生属性</summary>
    partial void OnCalibrationResultChanged(StereoParameters? value)
    {
        OnPropertyChanged(nameof(LeftFocalMm));
        OnPropertyChanged(nameof(RightFocalMm));
        OnPropertyChanged(nameof(HasCalibrationResult));
        OnPropertyChanged(nameof(ReprojectionErrorDisplay));
    }

    /// <summary>棋盘格配置变化时，清空所有角点缓存</summary>
    partial void OnChessboardConfigChanged(ChessboardConfig value)
    {
        ValidPairs.Clear();
        LeftPendingPaths.Clear();
        RightPendingPaths.Clear();
        SelectedPair = null;
        SkippedCount = 0;
        LeftPreviewImage = null;
        RightPreviewImage = null;
        StatusText = "棋盘格配置已变更，角点缓存已清空，请重新导入图片";
    }

    /// <summary>选中的图片对变化时，自动更新角点预览图</summary>
    partial void OnSelectedPairChanged(CachedStereoPair? value)
    {
        if (value == null)
        {
            LeftPreviewImage = null;
            RightPreviewImage = null;
            return;
        }
        // 使用棋盘格内角点数构建 Size（列数, 行数）
        var patternSize = new OpenCvSharp.Size(ChessboardConfig.CornerCols, ChessboardConfig.CornerRows);
        LeftPreviewImage = DrawCornersPreview(value.LeftPath, value.LeftCorners, patternSize);
        RightPreviewImage = DrawCornersPreview(value.RightPath, value.RightCorners, patternSize);
    }

    // ═══════════════════════════════════════════════
    //  构造器
    // ═══════════════════════════════════════════════

    public CalibrationViewModel(CalibrationService calibrationService)
    {
        _calibrationService = calibrationService;
    }

    // ═══════════════════════════════════════════════
    //  命令 — 导入图片
    // ═══════════════════════════════════════════════

    /// <summary>导入左相机图片文件夹</summary>
    [RelayCommand]
    private void ImportLeftImages()
    {
        var paths = ImportImagesFromFolder("选择左相机标定图片文件夹");
        if (paths.Count == 0) return;
        foreach (var p in paths) LeftPendingPaths.Add(p);
        OnPropertyChanged(nameof(LeftPendingCount));
        StatusText = $"左图已导入 {LeftPendingPaths.Count} 张，共 {RightPendingPaths.Count} 张右图待配对。请点击「检测角点」";
    }

    /// <summary>导入右相机图片文件夹</summary>
    [RelayCommand]
    private void ImportRightImages()
    {
        var paths = ImportImagesFromFolder("选择右相机标定图片文件夹");
        if (paths.Count == 0) return;
        foreach (var p in paths) RightPendingPaths.Add(p);
        OnPropertyChanged(nameof(RightPendingCount));
        StatusText = $"右图已导入 {RightPendingPaths.Count} 张，共 {LeftPendingPaths.Count} 张左图待配对。请点击「检测角点」";
    }

    // ═══════════════════════════════════════════════
    //  命令 — 角点检测（独立按钮）
    // ═══════════════════════════════════════════════

    /// <summary>对左右待配对图片执行角点检测，结果加入 ValidPairs</summary>
    [RelayCommand]
    private async Task DetectCornersAsync()
    {
        int pairCount = System.Math.Min(LeftPendingPaths.Count, RightPendingPaths.Count);
        if (pairCount == 0)
        {
            StatusText = "错误：左右至少各导入 1 张图片才能进行角点检测";
            return;
        }

        IsBusy = true;
        SkippedCount = 0;
        StatusText = $"正在进行角点检测（{pairCount} 对）...";

        try
        {
            await Task.Run(() =>
            {
                var leftPaths = LeftPendingPaths.Take(pairCount).ToList();
                var rightPaths = RightPendingPaths.Take(pairCount).ToList();

                for (int i = 0; i < pairCount; i++)
                {
                    bool leftOk = _calibrationService.TryDetectCorners(leftPaths[i], ChessboardConfig, out var leftCorners, out var leftSize);
                    bool rightOk = _calibrationService.TryDetectCorners(rightPaths[i], ChessboardConfig, out var rightCorners, out var rightSize);

                    if (leftOk && rightOk)
                    {
                        // ObservableCollection 必须在 UI 线程操作
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ValidPairs.Add(new CachedStereoPair
                            {
                                Index = ValidPairs.Count + 1,
                                CornerStatus = "✓ 成功",
                                LeftPath = leftPaths[i],
                                RightPath = rightPaths[i],
                                LeftCorners = leftCorners,
                                RightCorners = rightCorners,
                                ImageSize = leftSize
                            });
                        });
                    }
                    else
                    {
                        // SkippedCount 是 ObservableProperty，也需要 UI 线程
                        System.Windows.Application.Current.Dispatcher.Invoke(() => SkippedCount++);
                    }
                }

                // 移除已配对的图片
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    for (int i = 0; i < pairCount; i++)
                    {
                        LeftPendingPaths.RemoveAt(0);
                        RightPendingPaths.RemoveAt(0);
                    }
                    OnPropertyChanged(nameof(LeftPendingCount));
                    OnPropertyChanged(nameof(RightPendingCount));
                });
            });

            string skipMsg = SkippedCount > 0 ? $"，跳过 {SkippedCount} 对失败" : "";
            StatusText = $"角点检测完成：{ValidPairs.Count} 对成功{skipMsg}" +
                (ValidPairs.Count < 3 ? " ⚠ 至少需要 3 对才能标定" : "，可以开始标定");
        }
        catch (System.Exception ex)
        {
            StatusText = $"角点检测失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        SelectedPair = ValidPairs.FirstOrDefault();
    }

    // ═══════════════════════════════════════════════
    //  命令 — 删除图片对
    // ═══════════════════════════════════════════════

    [RelayCommand]
    private void DeletePair(CachedStereoPair pair)
    {
        if (SelectedPair == pair)
            SelectedPair = null;
        ValidPairs.Remove(pair);
        // 重新编号剩余项，确保序号从 1 开始连续
        for (int i = 0; i < ValidPairs.Count; i++)
            ValidPairs[i].Index = i + 1;
        StatusText = $"已删除 1 对，剩余 {ValidPairs.Count} 对有效图片";
    }

    // ═══════════════════════════════════════════════
    //  命令 — 标定与持久化
    // ═══════════════════════════════════════════════

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task StartCalibrationAsync(CancellationToken ct)
    {
        if (ValidPairs.Count < 3)
        {
            StatusText = $"错误：至少需要 3 对有效图片，当前仅 {ValidPairs.Count} 对";
            return;
        }

        IsBusy = true;
        StatusText = $"正在进行标定（{ValidPairs.Count} 对图片）...";
        ProgressValue = 0;

        try
        {
            var progress = new Progress<int>(value =>
            {
                ProgressValue = value;
                StatusText = $"标定中... {value}%";
            });

            CalibrationResult = await _calibrationService.CalibrateFromCachedAsync(
                [.. ValidPairs],
                ChessboardConfig,
                progress,
                ct);

            ProgressValue = 100;
            StatusText = $"标定完成！共使用 {ValidPairs.Count} 对图片，结果可保存。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "标定已被用户取消";
            ProgressValue = 0;
        }
        catch (System.Exception ex)
        {
            StatusText = $"标定失败：{ex.Message}";
            ProgressValue = 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

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
            Title = "保存标定结果",
            Filter = "YAML 文件|*.yaml;*.yml|所有文件|*.*",
            DefaultExt = ".yaml",
            FileName = "calibration_result.yaml",
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _calibrationService.SaveCalibrationResult(dialog.FileName, CalibrationResult);
            StatusText = $"标定结果已保存至：{dialog.FileName}";
        }
        catch (System.Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>清空所有标定相关数据，用于重新导入图片并标定</summary>
    [RelayCommand]
    private void ClearData()
    {
        ValidPairs.Clear();
        LeftPendingPaths.Clear();
        RightPendingPaths.Clear();
        CalibrationResult = null;
        SelectedPair = null;
        LeftPreviewImage = null;
        RightPreviewImage = null;
        SkippedCount = 0;
        ProgressValue = 0;
        OnPropertyChanged(nameof(LeftPendingCount));
        OnPropertyChanged(nameof(RightPendingCount));
        StatusText = "数据已清空，请重新导入左右图片并开始标定";
    }

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
            CalibrationResult = _calibrationService.LoadCalibrationResult(dialog.FileName);
            StatusText = $"已加载标定结果：{Path.GetFileName(dialog.FileName)}";
        }
        catch (System.Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════
    //  私有方法 — 图片导入辅助
    // ═══════════════════════════════════════════════

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
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ═══════════════════════════════════════════════
    //  私有方法 — 角点预览
    // ═══════════════════════════════════════════════

    /// <summary>在图片上绘制棋盘格角点并返回 WPF BitmapSource</summary>
    private static BitmapSource DrawCornersPreview(
        string imagePath, Point2f[] corners, OpenCvSharp.Size patternSize)
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
