using CommunityToolkit.Mvvm.ComponentModel;
using OpenCvSharp;
using System.IO;

namespace StereoTool.Models;

/// <summary>
/// 已缓存角点检测结果的单目标定图像。
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
}
