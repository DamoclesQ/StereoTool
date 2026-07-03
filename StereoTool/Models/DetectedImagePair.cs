using CommunityToolkit.Mvvm.ComponentModel;
using OpenCvSharp;
using System.IO;

namespace StereoTool.Models;

/// <summary>
/// 已缓存角点检测结果的立体图片对。
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

}
