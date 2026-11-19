using CommunityToolkit.Mvvm.ComponentModel;
using OpenCvSharp;
using System.IO;

namespace StereoTool.Models;

/// <summary>
/// 双目标定图片对，保留检测结果、失败诊断与参与选项。
/// 用于管理已完成角点检测的图片对，避免标定时重复计算。
/// 继承 ObservableObject 以支持属性变更通知（DataGrid 绑定需要）。
/// </summary>
public partial class DetectedImagePair : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftFileName))]
    private string _leftPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightFileName))]
    private string _rightPath = string.Empty;

    public string LeftFileName => Path.GetFileName(LeftPath);
    public string RightFileName => Path.GetFileName(RightPath);


    /// <summary>左图已检测的棋盘格角点坐标（亚像素精度）</summary>
    public Point2f[] LeftCorners { get; set; } = [];

    /// <summary>右图已检测的棋盘格角点坐标（亚像素精度）</summary>
    public Point2f[] RightCorners { get; set; } = [];

    /// <summary>图像尺寸（像素），从第一对成功检测的图片获取</summary>
    public Size ImageSize { get; set; }

    /// <summary>是否参与下一次标定；保留被排除的图片对供恢复。</summary>
    [ObservableProperty] private bool _isIncluded = true;

    /// <summary>图片配对与角点检测的诊断信息。</summary>
    [ObservableProperty] private string _detectionStatus = "待检测";

    /// <summary>左右图是否均检测到完整棋盘格。</summary>
    public bool IsDetected => LeftCorners.Length > 0 && RightCorners.Length > 0;

    /// <summary>该图片对在共同棋盘格位姿下的平均重投影距离，单位为像素。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorBarWidth))]
    [NotifyPropertyChangedFor(nameof(MeanErrorDisplay))]
    private double _meanError = double.NaN;

    /// <summary>左图平均重投影距离，单位为像素。</summary>
    [ObservableProperty] private double _leftMeanError = double.NaN;

    /// <summary>右图平均重投影距离，单位为像素。</summary>
    [ObservableProperty] private double _rightMeanError = double.NaN;

    /// <summary>误差条宽度，100 显示单位对应 1 像素，最大显示 3 像素。</summary>
    public double ErrorBarWidth => double.IsFinite(MeanError) ? Math.Min(MeanError * 100, 300) : 0;

    /// <summary>尚未标定的记录显示为未知。</summary>
    public string MeanErrorDisplay => double.IsFinite(MeanError) ? MeanError.ToString("F4") : "--";

    /// <summary>由共同棋盘格位姿投影到左图的角点。</summary>
    public Point2f[] LeftProjectedCorners { get; set; } = [];

    /// <summary>由共同棋盘格位姿投影到右图的角点。</summary>
    public Point2f[] RightProjectedCorners { get; set; } = [];

}
