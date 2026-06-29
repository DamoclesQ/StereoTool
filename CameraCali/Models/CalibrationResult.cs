using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CameraCali.Models;

/// <summary>
/// 双目相机标定结果，包含左右相机的内参矩阵、畸变系数及双目外参。
/// 支持 JSON 序列化/反序列化，用于标定结果的持久化存储。
/// </summary>
public partial class CalibrationResult : ObservableObject
{
    /// <summary>左相机内参矩阵 (3x3)</summary>
    [ObservableProperty]
    private double[][] _leftCameraMatrix = [];

    /// <summary>左相机畸变系数 (5 个：k1, k2, p1, p2, k3)</summary>
    [ObservableProperty]
    private double[] _leftDistCoeffs = [];

    /// <summary>右相机内参矩阵 (3x3)</summary>
    [ObservableProperty]
    private double[][] _rightCameraMatrix = [];

    /// <summary>右相机畸变系数 (5 个：k1, k2, p1, p2, k3)</summary>
    [ObservableProperty]
    private double[] _rightDistCoeffs = [];

    /// <summary>双目旋转矩阵 R (3x3)</summary>
    [ObservableProperty]
    private double[][] _rotationMatrix = [];

    /// <summary>双目平移向量 T (3x1)</summary>
    [ObservableProperty]
    private double[] _translationVector = [];

    /// <summary>标定图像的宽度（像素）</summary>
    [ObservableProperty]
    private int _imageWidth;

    /// <summary>标定图像的高度（像素）</summary>
    [ObservableProperty]
    private int _imageHeight;

    /// <summary>标定过程中跳过的图片对数量（角点检测失败的）</summary>
    public int SkippedPairCount { get; set; }

    /// <summary>左右相机平均重投影误差 RMS（像素），值越小标定越精确</summary>
    [ObservableProperty]
    private double _reprojectionError;
}
