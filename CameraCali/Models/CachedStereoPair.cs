using CommunityToolkit.Mvvm.ComponentModel;
using OpenCvSharp;
using System.IO;

namespace CameraCali.Models;

/// <summary>
/// 已缓存角点检测结果的立体图片对。
/// 用于管理已完成角点检测的图片对，避免标定时重复计算。
/// 继承 ObservableObject 以支持属性变更通知（DataGrid 绑定需要）。
/// </summary>
public partial class CachedStereoPair : ObservableObject
{
    private string _leftPath = string.Empty;
    private string _rightPath = string.Empty;

    /// <summary>左相机图片的完整文件路径。设置时自动同步更新 LeftFileName。</summary>
    public string LeftPath
    {
        get => _leftPath;
        set
        {
            if (SetProperty(ref _leftPath, value))
                LeftFileName = Path.GetFileName(value);
        }
    }

    /// <summary>右相机图片的完整文件路径。设置时自动同步更新 RightFileName。</summary>
    public string RightPath
    {
        get => _rightPath;
        set
        {
            if (SetProperty(ref _rightPath, value))
                RightFileName = Path.GetFileName(value);
        }
    }

    /// <summary>左图已检测的棋盘格角点坐标（亚像素精度）</summary>
    public Point2f[] LeftCorners { get; set; } = [];

    /// <summary>右图已检测的棋盘格角点坐标（亚像素精度）</summary>
    public Point2f[] RightCorners { get; set; } = [];

    /// <summary>图像尺寸（像素），从第一对成功检测的图片获取</summary>
    public Size ImageSize { get; set; }

    /// <summary>序号（1-based），由 ViewModel 在添加到列表时赋值。DataGrid 中作为行号显示。</summary>
    [ObservableProperty]
    private int _index;

    /// <summary>角点检测状态文本，默认为"✓ 成功"</summary>
    [ObservableProperty]
    private string _cornerStatus = "✓ 成功";

    /// <summary>左图文件名（仅显示用，从 LeftPath 赋值时自动提取）</summary>
    [ObservableProperty]
    private string _leftFileName = string.Empty;

    /// <summary>右图文件名（仅显示用，从 RightPath 赋值时自动提取）</summary>
    [ObservableProperty]
    private string _rightFileName = string.Empty;
}
