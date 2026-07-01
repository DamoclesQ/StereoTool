using OpenCvSharp;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StereoTool.Models;

/// <summary>
/// 单目标定参数模型。
/// 包含相机内参矩阵和畸变系数、重投影误差和图像尺寸。
/// 内置用于 WPF 绑定的派生属性，当原始数据变化时自动刷新所有显示值。
/// </summary>
public partial class CameraParameters : ObservableObject
{
    /// <summary>相机内参矩阵 (3x3)</summary>
    [ObservableProperty]
    private double[][] _cameraMatrix = [];

    /// <summary>相机畸变系数 (5 个：k1, k2, p1, p2, k3)</summary>
    [ObservableProperty]
    private double[] _distCoeffs = [];

    /// <summary>标定图像的宽度（像素）</summary>
    [ObservableProperty]
    private int _imageWidth;

    /// <summary>标定图像的高度（像素）</summary>
    [ObservableProperty]
    private int _imageHeight;

    /// <summary>重投影误差 RMS（像素），值越小标定越精确</summary>
    [ObservableProperty]
    private double _reprojectionError;

    /// <summary>棋盘格内角点列数（宽度方向）</summary>
    [ObservableProperty]
    private int _cornerCols;

    /// <summary>棋盘格内角点行数（高度方向）</summary>
    [ObservableProperty]
    private int _cornerRows;

    /// <summary>棋盘格每格物理尺寸（单位：mm）</summary>
    [ObservableProperty]
    private double _squareSize;

    /// <summary>相机传感器像元尺寸（单位：μm），设为 0 表示未提供</summary>
    [ObservableProperty]
    private double _pixelSizeUm;

    // ═══════════════════════════════════════════════
    //  派生属性 — WPF 绑定用（string 格式化）
    // ═══════════════════════════════════════════════

    /// <summary>X 方向焦距（像素）</summary>
    public string Fx => CameraMatrix.Length > 0 ? $"{CameraMatrix[0][0]:F2}" : "--";

    /// <summary>Y 方向焦距（像素）</summary>
    public string Fy => CameraMatrix.Length > 1 && CameraMatrix[1].Length > 1
        ? $"{CameraMatrix[1][1]:F2}" : "--";

    /// <summary>主点 X 坐标（像素）</summary>
    public string Cx => CameraMatrix.Length > 0 && CameraMatrix[0].Length > 2
        ? $"{CameraMatrix[0][2]:F2}" : "--";

    /// <summary>主点 Y 坐标（像素）</summary>
    public string Cy => CameraMatrix.Length > 1 && CameraMatrix[1].Length > 2
        ? $"{CameraMatrix[1][2]:F2}" : "--";

    // ── 畸变系数 ──

    /// <summary>二阶径向畸变系数 k1</summary>
    public string K1 => DistCoeffs.Length > 0 ? $"{DistCoeffs[0]:F6}" : "--";

    /// <summary>四阶径向畸变系数 k2</summary>
    public string K2 => DistCoeffs.Length > 1 ? $"{DistCoeffs[1]:F6}" : "--";

    /// <summary>一阶切向畸变系数 p1</summary>
    public string P1 => DistCoeffs.Length > 2 ? $"{DistCoeffs[2]:F6}" : "--";

    /// <summary>二阶切向畸变系数 p2</summary>
    public string P2 => DistCoeffs.Length > 3 ? $"{DistCoeffs[3]:F6}" : "--";

    /// <summary>六阶径向畸变系数 k3</summary>
    public string K3 => DistCoeffs.Length > 4 ? $"{DistCoeffs[4]:F6}" : "--";

    // ═══════════════════════════════════════════════
    //  属性变更回调
    // ═══════════════════════════════════════════════

    /// <summary>CameraMatrix 变化时，通知所有内参派生属性更新</summary>
    partial void OnCameraMatrixChanged(double[][] value)
    {
        OnPropertyChanged(nameof(Fx));
        OnPropertyChanged(nameof(Fy));
        OnPropertyChanged(nameof(Cx));
        OnPropertyChanged(nameof(Cy));
    }

    /// <summary>DistCoeffs 变化时，通知所有畸变派生属性更新</summary>
    partial void OnDistCoeffsChanged(double[] value)
    {
        OnPropertyChanged(nameof(K1));
        OnPropertyChanged(nameof(K2));
        OnPropertyChanged(nameof(P1));
        OnPropertyChanged(nameof(P2));
        OnPropertyChanged(nameof(K3));
    }
}
