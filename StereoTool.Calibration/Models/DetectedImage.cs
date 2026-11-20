using CommunityToolkit.Mvvm.ComponentModel;
using OpenCvSharp;
using System.IO;

namespace StereoTool.Calibration.Models;

/// <summary>
/// 单目标定图片记录，保留检测结果、失败诊断与参与选项。
/// 用于管理单张已完成角点检测的图像，避免标定时重复计算。
/// 继承 ObservableObject 以支持属性变更通知（DataGrid 绑定需要）。
/// </summary>
public partial class DetectedImage : ObservableObject
{

    /// <summary>图像的完整文件路径。设置时自动同步更新 FileName。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    private string _imagePath = string.Empty;

    /// <summary>文件名（仅显示用，从 ImagePath 赋值时自动提取）</summary>
    public string FileName => Path.GetFileName(ImagePath);

    /// <summary>已检测的棋盘格角点坐标（亚像素精度）</summary>
    public Point2f[] Corners { get; set; } = [];

    /// <summary>图像尺寸（像素），从第一张成功检测的图片获取</summary>
    public Size ImageSize { get; set; }

    /// <summary>是否参与下一次标定；排除图片不会删除原始文件。</summary>
    [ObservableProperty] private bool _isIncluded = true;

    /// <summary>检测诊断，失败图片同样保留在列表中。</summary>
    [ObservableProperty] private string _detectionStatus = "待检测";

    /// <summary>是否成功检测到完整棋盘格。</summary>
    public bool IsDetected => Corners.Length > 0;

    /// <summary>该图片全部角点的平均重投影距离，单位为像素。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartError))]
    [NotifyPropertyChangedFor(nameof(MeanErrorDisplay))]
    private double _meanError = double.NaN;

    /// <summary>该图片所有角点中最大的重投影距离，单位为像素。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxErrorDisplay))]
    private double _maxError = double.NaN;

    /// <summary>最大误差显示值，尚无标定结果时显示为未知。</summary>
    public string MaxErrorDisplay => double.IsFinite(MaxError) ? MaxError.ToString("F4") : "--";

    /// <summary>误差条的数值；尚未计算时为零，比例由页面统一确定。</summary>
    public double ChartError => double.IsFinite(MeanError) ? MeanError : 0;

    /// <summary>尚未标定的记录显示为未知。</summary>
    public string MeanErrorDisplay => double.IsFinite(MeanError) ? MeanError.ToString("F4") : "--";

    /// <summary>本次拟合的投影点，用于和检测点叠加检查。</summary>
    public Point2f[] ProjectedCorners { get; set; } = [];
}
