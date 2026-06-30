using CommunityToolkit.Mvvm.ComponentModel;
using OpenCvSharp;
using System.IO;

namespace CameraCali.Models;

/// <summary>
/// 已缓存角点检测结果的单目标定图像。
/// 用于管理单张已完成角点检测的图像，避免标定时重复计算。
/// 继承 ObservableObject 以支持属性变更通知（DataGrid 绑定需要）。
/// </summary>
public partial class CachedMonoImage : ObservableObject
{
    private string _imagePath = string.Empty;

    /// <summary>图像的完整文件路径。设置时自动同步更新 FileName。</summary>
    public string ImagePath
    {
        get => _imagePath;
        set
        {
            if (SetProperty(ref _imagePath, value))
                FileName = Path.GetFileName(value);
        }
    }

    /// <summary>已检测的棋盘格角点坐标（亚像素精度）</summary>
    public Point2f[] Corners { get; set; } = [];

    /// <summary>图像尺寸（像素），从第一张成功检测的图片获取</summary>
    public Size ImageSize { get; set; }

    /// <summary>序号（1-based），由 ViewModel 在添加到列表时赋值。DataGrid 中作为行号显示。</summary>
    [ObservableProperty]
    private int _index;

    /// <summary>角点检测状态文本，默认为"✓ 成功"</summary>
    [ObservableProperty]
    private string _cornerStatus = "✓ 成功";

    /// <summary>文件名（仅显示用，从 ImagePath 赋值时自动提取）</summary>
    [ObservableProperty]
    private string _fileName = string.Empty;
}
