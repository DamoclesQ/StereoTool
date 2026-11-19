using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using StereoTool.Models;
using StereoTool.Services;

namespace StereoTool.ViewModels;

/// <summary>双目标定工作流：名称配对、分析、内参选择、标定、误差筛选和校正检查。</summary>
public partial class StereoCalibrationViewModel : ObservableObject
{
    private readonly CalibrationService _calibrationService;
    private readonly DistanceService _distanceService;
    private readonly DistanceViewModel _distanceViewModel;
    private readonly MainViewModel _mainViewModel;
    private CalibrationParams? _fixedLeft;
    private CalibrationParams? _fixedRight;
    private StereoRectifyResult? _previewRectify;
    private CancellationTokenSource? _analysisCancellation;

    /// <summary>棋盘格与针孔畸变设置。</summary>
    public ChessboardConfig ChessboardConfig { get; } = new();

    /// <summary>全部图片对，包含缺图、检测失败及被排除的记录。</summary>
    [ObservableProperty] private ObservableCollection<DetectedImagePair> _imagePairs = [];

    /// <summary>当前检查的图片对。</summary>
    [ObservableProperty] private DetectedImagePair? _selectedPair;

    /// <summary>当前有效结果。</summary>
    [ObservableProperty] private StereoCalibrationParams? _calibrationResult;

    /// <summary>是否使用导入的固定左右内参。</summary>
    [ObservableProperty] private bool _useFixedIntrinsics;

    /// <summary>固定内参来源。</summary>
    [ObservableProperty] private string _intrinsicsSummary = "联合估计左右内参和双目外参";

    /// <summary>左右检测和重投影预览。</summary>
    [ObservableProperty] private BitmapSource? _leftPreviewImage;
    [ObservableProperty] private BitmapSource? _rightPreviewImage;

    /// <summary>左右校正预览，附带同高度水平线。</summary>
    [ObservableProperty] private BitmapSource? _rectifiedLeftImage;
    [ObservableProperty] private BitmapSource? _rectifiedRightImage;

    /// <summary>是否正在分析或标定。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>分析进度。</summary>
    [ObservableProperty] private double _progress;

    /// <summary>操作状态。</summary>
    [ObservableProperty] private string _statusText = "设置棋盘格，添加左右文件夹后自动配对分析；建议使用 10～20 对图片。";

    /// <summary>平均重投影误差的排除阈值（像素）。</summary>
    [ObservableProperty] private double _errorThreshold = 1;

    /// <summary>是否存在可导出的有效结果。</summary>
    public bool HasCalibrationResult => CalibrationResult != null;

    /// <summary>畸变设置在固定内参模式下不可编辑。</summary>
    public bool CanEditDistortion => !UseFixedIntrinsics;

    /// <summary>图片对统计。</summary>
    public string DatasetSummary => $"共 {ImagePairs.Count} 对 · 检测成功 {ImagePairs.Count(pair => pair.IsDetected)} 对 · 参与 {ImagePairs.Count(pair => pair.IsDetected && pair.IsIncluded)} 对";

    /// <summary>整体质量统计。</summary>
    public string ResultSummary => CalibrationResult == null ? "尚未标定，或数据已改变，请重新标定。" :
        $"图片对数 {(CalibrationResult.ImageCount > 0 ? CalibrationResult.ImageCount.ToString() : "文件未记录")} · " +
        $"平均误差 {(double.IsFinite(CalibrationResult.MeanReprojectionError) ? CalibrationResult.MeanReprojectionError.ToString("F4") : "--")} px · " +
        $"RMS {CalibrationResult.ReprojectionError:F4} px · 基线 {CalibrationResult.Baseline:F3} mm";

    /// <summary>左相机的物理焦距。</summary>
    public string LeftFocalMm => CalibrationResult != null && CalibrationResult.PixelSizeUm > 0
        ? $"{CalibrationResult.LeftCameraMatrix[0, 0] * CalibrationResult.PixelSizeUm / 1000:F3} mm" : "--";

    /// <summary>右相机的物理焦距。</summary>
    public string RightFocalMm => CalibrationResult != null && CalibrationResult.PixelSizeUm > 0
        ? $"{CalibrationResult.RightCameraMatrix[0, 0] * CalibrationResult.PixelSizeUm / 1000:F3} mm" : "--";

    /// <summary>创建页面并注入测距页面用于接收当前结果。</summary>
    /// <param name="calibrationService">标定服务。</param>
    /// <param name="distanceService">校正服务。</param>
    /// <param name="distanceViewModel">测距状态。</param>
    /// <param name="mainViewModel">导航状态。</param>
    public StereoCalibrationViewModel(CalibrationService calibrationService, DistanceService distanceService,
        DistanceViewModel distanceViewModel, MainViewModel mainViewModel)
    {
        _calibrationService = calibrationService;
        _distanceService = distanceService;
        _distanceViewModel = distanceViewModel;
        _mainViewModel = mainViewModel;
        ChessboardConfig.PropertyChanged += OnConfigChanged;
    }

    /// <summary>切换内参模式后重新标定。</summary>
    /// <param name="value">是否固定内参。</param>
    partial void OnUseFixedIntrinsicsChanged(bool value)
    {
        InvalidateResult(true);
        OnPropertyChanged(nameof(CanEditDistortion));
        IntrinsicsSummary = value ? "请导入左右两份单目标定 YAML（固定内参）" : "联合估计左右内参和双目外参";
    }

    /// <summary>刷新结果显示、释放旧校正映射。</summary>
    /// <param name="value">当前结果。</param>
    partial void OnCalibrationResultChanged(StereoCalibrationParams? value)
    {
        _previewRectify?.Dispose();
        _previewRectify = null;
        OnPropertyChanged(nameof(HasCalibrationResult));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(LeftFocalMm));
        OnPropertyChanged(nameof(RightFocalMm));
        UpdatePreview();
    }

    /// <summary>选中图片对后更新角点与校正预览。</summary>
    /// <param name="value">选中记录。</param>
    partial void OnSelectedPairChanged(DetectedImagePair? value)
    {
        UpdatePreview();
    }

    /// <summary>配置变化时使旧参数失效，内角点行列变化时重新检测。</summary>
    /// <param name="sender">配置对象。</param>
    /// <param name="e">变更字段。</param>
    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChessboardConfig.CornerCols) or nameof(ChessboardConfig.CornerRows))
        {
            ReplacePairs(ImagePairs.Select(pair => new DetectedImagePair
            {
                LeftPath = pair.LeftPath,
                RightPath = pair.RightPath
            }).ToList());
        }
        InvalidateResult(true);
        StatusText = "配置已改变，请重新标定；内角点行列变化后需重新分析。";
    }

    /// <summary>发布浏览器记录并监听参与选项。</summary>
    /// <param name="pairs">全部图片对。</param>
    private void ReplacePairs(IEnumerable<DetectedImagePair> pairs)
    {
        foreach (var pair in ImagePairs)
            pair.PropertyChanged -= OnPairChanged;
        SelectedPair = null;
        ImagePairs = new ObservableCollection<DetectedImagePair>(pairs);
        foreach (var pair in ImagePairs)
            pair.PropertyChanged += OnPairChanged;
        OnPropertyChanged(nameof(DatasetSummary));
        SelectedPair = ImagePairs.FirstOrDefault();
    }

    /// <summary>修改参与选项后使旧参数失效，保留上一轮误差供筛选。</summary>
    /// <param name="sender">图片对。</param>
    /// <param name="e">变更字段。</param>
    private void OnPairChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DetectedImagePair.IsIncluded))
            return;
        InvalidateResult(false);
        OnPropertyChanged(nameof(DatasetSummary));
        StatusText = "参与图片对已改变，请重新标定。误差条保留上一轮结果供筛选。";
    }

    /// <summary>使旧结果失效并按需清除逐对误差。</summary>
    /// <param name="clearErrors">是否清除误差。</param>
    private void InvalidateResult(bool clearErrors)
    {
        foreach (var pair in ImagePairs)
        {
            pair.LeftProjectedCorners = [];
            pair.RightProjectedCorners = [];
            if (!clearErrors)
                continue;
            pair.MeanError = double.NaN;
            pair.LeftMeanError = double.NaN;
            pair.RightMeanError = double.NaN;
        }
        CalibrationResult = null;
        UpdatePreview();
    }

    /// <summary>一次选择左右文件夹，按名称配对并追加，随后自动检测。</summary>
    [RelayCommand]
    private async Task ImportImagePairsAsync()
    {
        var leftDialog = new OpenFolderDialog { Title = "添加图片对：选择左相机文件夹" };
        if (leftDialog.ShowDialog() != true)
            return;
        var rightDialog = new OpenFolderDialog { Title = "添加图片对：选择右相机文件夹" };
        if (rightDialog.ShowDialog() != true)
            return;
        try
        {
            var added = _calibrationService.PairImages(_calibrationService.GetImagePaths(leftDialog.FolderName),
                _calibrationService.GetImagePaths(rightDialog.FolderName));
            var pairs = ImagePairs.Concat(added)
                .DistinctBy(pair => (pair.LeftPath.ToUpperInvariant(), pair.RightPath.ToUpperInvariant())).ToList();
            ReplacePairs(pairs);
            await DetectCornersAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"添加失败：{ex.Message}。请检查是否有重名编号及 left/right 命名。";
        }
    }

    /// <summary>后台检测图片对，保留失败原因供检查。</summary>
    /// <param name="ct">图片对之间生效的取消令牌。</param>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DetectCornersAsync(CancellationToken ct)
    {
        if (ImagePairs.Count == 0)
        {
            StatusText = "请先添加左右图片对。";
            return;
        }
        IsBusy = true;
        Progress = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _analysisCancellation = cancellation;
        ct = cancellation.Token;
        InvalidateResult(true);
        var pairs = ImagePairs.ToList();
        var progress = new Progress<int>(count =>
        {
            Progress = count * 100.0 / pairs.Count;
            StatusText = $"正在分析图片对 {count}/{pairs.Count}...";
        });
        try
        {
            var analyzed = await Task.Run(() => _calibrationService.AnalyzePairs(pairs, ChessboardConfig, progress, ct), ct);
            ReplacePairs(analyzed);
            StatusText = $"分析完成：{DatasetSummary}。检查左右角点原点是否对应后开始标定。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "分析已取消，可以重新分析。";
        }
        catch (Exception ex)
        {
            StatusText = $"分析失败：{ex.Message}";
        }
        finally
        {
            _analysisCancellation = null;
            IsBusy = false;
        }
    }

    /// <summary>请求在当前图片对检测结束后停止分析。</summary>
    [RelayCommand]
    private void CancelAnalysis()
    {
        _analysisCancellation?.Cancel();
    }

    /// <summary>后台求解并发布逐对误差；固定内参时不重新估计畸变。</summary>
    [RelayCommand]
    private async Task StartCalibrationAsync()
    {
        var pairs = ImagePairs.Where(pair => pair.IsIncluded && pair.IsDetected)
            .Select(pair => new DetectedImagePair
            {
                LeftPath = pair.LeftPath,
                RightPath = pair.RightPath,
                LeftCorners = pair.LeftCorners,
                RightCorners = pair.RightCorners,
                ImageSize = pair.ImageSize
            }).ToList();
        IsBusy = true;
        InvalidateResult(true);
        StatusText = "正在标定并计算逐对误差...";
        try
        {
            if (UseFixedIntrinsics && (_fixedLeft == null || _fixedRight == null))
                throw new InvalidOperationException("请先导入左右两份单目标定文件。");
            var result = await Task.Run(() => _calibrationService.StereoCalibrate(ChessboardConfig, pairs,
                UseFixedIntrinsics ? _fixedLeft : null, UseFixedIntrinsics ? _fixedRight : null));
            foreach (var pair in pairs)
            {
                var target = ImagePairs.First(item => item.LeftPath == pair.LeftPath && item.RightPath == pair.RightPath);
                target.MeanError = pair.MeanError;
                target.LeftMeanError = pair.LeftMeanError;
                target.RightMeanError = pair.RightMeanError;
                target.LeftProjectedCorners = pair.LeftProjectedCorners;
                target.RightProjectedCorners = pair.RightProjectedCorners;
            }
            CalibrationResult = result;
            Progress = 100;
            StatusText = "标定完成。检查逐对误差和校正图；排除问题图片对后可重新标定。";
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

    /// <summary>导入左右两份单目内参；只有两份均读取成功才替换当前配置。</summary>
    [RelayCommand]
    private void LoadFixedIntrinsics()
    {
        var leftDialog = new OpenFileDialog { Title = "选择左相机单目标定 YAML", Filter = "OpenCV YAML|*.yaml;*.yml" };
        if (leftDialog.ShowDialog() != true)
            return;
        var rightDialog = new OpenFileDialog { Title = "选择右相机单目标定 YAML", Filter = "OpenCV YAML|*.yaml;*.yml" };
        if (rightDialog.ShowDialog() != true)
            return;
        try
        {
            var left = _calibrationService.LoadCalibrationParams(leftDialog.FileName);
            var right = _calibrationService.LoadCalibrationParams(rightDialog.FileName);
            _fixedLeft = left;
            _fixedRight = right;
            UseFixedIntrinsics = true;
            InvalidateResult(true);
            IntrinsicsSummary = $"左：{Path.GetFileName(leftDialog.FileName)} / 右：{Path.GetFileName(rightDialog.FileName)}";
            StatusText = "固定内参已导入。标定只求双目外参，图片尺寸必须与内参文件一致。";
        }
        catch (Exception ex)
        {
            StatusText = $"导入内参失败：{ex.Message}";
        }
    }

    /// <summary>排除超过平均误差阈值的图片对，允许重新勾选。</summary>
    [RelayCommand]
    private void ExcludeHighError()
    {
        var pairs = ImagePairs.Where(pair => pair.IsIncluded && pair.MeanError > ErrorThreshold).ToList();
        foreach (var pair in pairs)
            pair.IsIncluded = false;
        StatusText = $"已排除 {pairs.Count} 对，请重新标定；可勾选“参与”恢复。";
    }

    /// <summary>从数据集移除记录，不删除原图。</summary>
    /// <param name="pair">待移除图片对。</param>
    [RelayCommand]
    private void DeletePair(DetectedImagePair pair)
    {
        ReplacePairs(ImagePairs.Where(item => item != pair).ToList());
        InvalidateResult(true);
    }

    /// <summary>导出当前有效的双目标定结果。</summary>
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
            Title = "导出双目参数",
            Filter = "OpenCV YAML|*.yaml;*.yml",
            FileName = "stereoCalibrationParams.yaml"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            _calibrationService.SaveStereoCalibrationParams(dialog.FileName, CalibrationResult);
            StatusText = $"参数已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>加载已有双目参数用于查看、校正预览或测距。</summary>
    [RelayCommand]
    private void LoadCalibrationFile()
    {
        var dialog = new OpenFileDialog { Title = "加载双目参数", Filter = "OpenCV YAML|*.yaml;*.yml" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            InvalidateResult(true);
            CalibrationResult = _calibrationService.LoadStereoCalibrationParams(dialog.FileName);
            StatusText = "已加载双目参数；逐对误差需要使用当前图片重新标定。";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>将当前有效结果传入测距页并切换页面。</summary>
    [RelayCommand]
    private void UseForDistance()
    {
        if (CalibrationResult == null)
            return;
        try
        {
            _distanceViewModel.UseCalibration(CalibrationResult);
            _mainViewModel.NavigateDistanceCommand.Execute(null);
        }
        catch (Exception ex)
        {
            StatusText = $"应用测距参数失败：{ex.Message}";
        }
    }

    /// <summary>清空图片和拟合结果，保留固定内参设置。</summary>
    [RelayCommand]
    private void ClearData()
    {
        ReplacePairs([]);
        InvalidateResult(true);
        Progress = 0;
        StatusText = "图片对和标定结果已清空。";
    }

    /// <summary>更新左右角点及校正预览；异常在页面交互边界处理。</summary>
    private void UpdatePreview()
    {
        LeftPreviewImage = null;
        RightPreviewImage = null;
        RectifiedLeftImage = null;
        RectifiedRightImage = null;
        if (SelectedPair == null)
            return;
        try
        {
            if (File.Exists(SelectedPair.LeftPath))
            {
                using var left = _calibrationService.CreatePreview(SelectedPair.LeftPath, SelectedPair.LeftCorners,
                    SelectedPair.LeftProjectedCorners, ChessboardConfig);
                LeftPreviewImage = left.ToBitmapSource();
                LeftPreviewImage.Freeze();
            }
            if (File.Exists(SelectedPair.RightPath))
            {
                using var right = _calibrationService.CreatePreview(SelectedPair.RightPath, SelectedPair.RightCorners,
                    SelectedPair.RightProjectedCorners, ChessboardConfig);
                RightPreviewImage = right.ToBitmapSource();
                RightPreviewImage.Freeze();
            }
            if (CalibrationResult == null || LeftPreviewImage == null || RightPreviewImage == null)
                return;
            if (LeftPreviewImage.PixelWidth != CalibrationResult.ImageWidth || LeftPreviewImage.PixelHeight != CalibrationResult.ImageHeight ||
                RightPreviewImage.PixelWidth != CalibrationResult.ImageWidth || RightPreviewImage.PixelHeight != CalibrationResult.ImageHeight)
                return;
            _previewRectify ??= _distanceService.GetRectifyParams(CalibrationResult);
            using var leftSource = Cv2.ImRead(SelectedPair.LeftPath, ImreadModes.Color);
            using var rightSource = Cv2.ImRead(SelectedPair.RightPath, ImreadModes.Color);
            using var rectLeft = new Mat();
            using var rectRight = new Mat();
            Cv2.Remap(leftSource, rectLeft, _previewRectify.LeftMapX, _previewRectify.LeftMapY, InterpolationFlags.Linear);
            Cv2.Remap(rightSource, rectRight, _previewRectify.RightMapX, _previewRectify.RightMapY, InterpolationFlags.Linear);
            var imageHeight = rectLeft.Height;
            var imageWidth = rectLeft.Width;
            for (var y = 20; y < imageHeight; y += 40)
            {
                Cv2.Line(rectLeft, new Point(0, y), new Point(imageWidth - 1, y), Scalar.LimeGreen);
                Cv2.Line(rectRight, new Point(0, y), new Point(imageWidth - 1, y), Scalar.LimeGreen);
            }
            RectifiedLeftImage = rectLeft.ToBitmapSource();
            RectifiedRightImage = rectRight.ToBitmapSource();
            RectifiedLeftImage.Freeze();
            RectifiedRightImage.Freeze();
        }
        catch (Exception ex)
        {
            StatusText = $"预览失败：{ex.Message}";
        }
    }
}
