using CommunityToolkit.Mvvm.ComponentModel;

namespace CameraCali.Models;

/// <summary>
/// 双目图像对模型，存储左右相机图像的文件路径。
/// </summary>
public partial class StereoImagePair : ObservableObject
{
    /// <summary>左相机图像文件路径</summary>
    [ObservableProperty]
    private string _leftImagePath = string.Empty;

    /// <summary>右相机图像文件路径</summary>
    [ObservableProperty]
    private string _rightImagePath = string.Empty;
}
