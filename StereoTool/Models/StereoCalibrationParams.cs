using CommunityToolkit.Mvvm.ComponentModel;
using OpenCvSharp;

namespace StereoTool.Models;

/// <summary>
/// 双目标定参数模型。
/// 包含左右相机的内参矩阵和畸变系数、双目外参（旋转矩阵R、平移向量T）、
/// 本质矩阵E、基础矩阵F、重投影误差、标定板世界坐标和图像尺寸。
/// 内置用于 WPF 绑定的派生属性，当原始数据变化时自动刷新所有显示值。
/// </summary>
public partial class StereoCalibrationParams : ObservableObject
{
    // ═══════════════════════════════════════════════
    //  左相机内参
    // ═══════════════════════════════════════════════

    /// <summary>左相机内参矩阵 (3x3)</summary>
    [ObservableProperty] private double[,] _leftCameraMatrix = new double[3,3];

    /// <summary>左相机畸变系数 (5 个：k1, k2, p1, p2, k3)</summary>
    [ObservableProperty] private double[] _leftDistCoeffs = [];

    // ═══════════════════════════════════════════════
    //  右相机内参
    // ═══════════════════════════════════════════════

    /// <summary>右相机内参矩阵 (3x3)</summary>
    [ObservableProperty] private double[,] _rightCameraMatrix = new double[3, 3];

    /// <summary>右相机畸变系数 (5 个：k1, k2, p1, p2, k3)</summary>
    [ObservableProperty] private double[] _rightDistCoeffs = [];

    // ═══════════════════════════════════════════════
    //  双目外参
    // ═══════════════════════════════════════════════

    /// <summary>双目旋转矩阵 R (3x3)</summary>
    [ObservableProperty] private double[,] _rotationMatrix = new double[3, 3];

    /// <summary>双目平移向量 T（3 个元素）</summary>
    [ObservableProperty] private double[] _translationVector = [];

    /// <summary>本质矩阵 E (3x3)，编码双目相对位姿（R 和 T 的组合）</summary>
    [ObservableProperty] private double[,] _essentialMatrix = new double[3, 3];

    /// <summary>基础矩阵 F (3x3)，将左图像素映射到右图极线</summary>
    [ObservableProperty] private double[,] _fundamentalMatrix = new double[3, 3];

    // ═══════════════════════════════════════════════
    //  通用字段
    // ═══════════════════════════════════════════════

    /// <summary>OpenCV StereoCalibrate 返回的全局重投影 RMS（像素）。</summary>
    [ObservableProperty] private double _reprojectionError;

    /// <summary>逐对共同位姿拟合后，全部角点的平均重投影距离（像素）。</summary>
    public double MeanReprojectionError { get; set; } = double.NaN;

    /// <summary>实际用于标定的图片对数量。</summary>
    public int ImageCount { get; set; }

    /// <summary>是否使用外部导入的固定内参。</summary>
    public bool UsedFixedIntrinsics { get; set; }

    /// <summary>联合估计内参时是否估计 k3。</summary>
    public bool EstimateK3 { get; set; }

    /// <summary>联合估计内参时是否估计切向畸变。</summary>
    public bool EstimateTangentialDistortion { get; set; }

    /// <summary>双目基线长度（mm）。</summary>
    public double Baseline => Math.Sqrt(TranslationVector.Sum(value => value * value));

    /// <summary>棋盘格内角点列数（宽度方向）</summary>
    [ObservableProperty] private int _cornerCols;

    /// <summary>棋盘格内角点行数（高度方向）</summary>
    [ObservableProperty] private int _cornerRows;

    /// <summary>棋盘格每格物理尺寸（单位：mm），用于像素坐标到物理坐标的换算</summary>
    [ObservableProperty] private double _squareSize;

    /// <summary>相机传感器像元尺寸元数据（μm），保留用于 YAML 兼容；不参与标定求解。</summary>
    [ObservableProperty] private double _pixelSizeUm;

    /// <summary>标定图像的宽度（像素）</summary>
    [ObservableProperty] private int _imageWidth;

    /// <summary>标定图像的高度（像素）</summary>
    [ObservableProperty] private int _imageHeight;

    // ═══════════════════════════════════════════════
    //  派生属性 — 左相机 WPF 绑定用（string 格式化）
    // ═══════════════════════════════════════════════

    /// <summary>左相机 X 方向焦距（像素）</summary>
    public string LeftFx => LeftCameraMatrix.Length == 9 ? $"{LeftCameraMatrix[0,0]:F2}" : "--";

    /// <summary>左相机 Y 方向焦距（像素）</summary>
    public string LeftFy => LeftCameraMatrix.Length == 9 ? $"{LeftCameraMatrix[1,1]:F2}" : "--";

    /// <summary>左相机主点 X 坐标（像素）</summary>
    public string LeftCx => LeftCameraMatrix.Length == 9 ? $"{LeftCameraMatrix[0,2]:F2}" : "--";

    /// <summary>左相机主点 Y 坐标（像素）</summary>
    public string LeftCy => LeftCameraMatrix.Length == 9 ? $"{LeftCameraMatrix[1,2]:F2}" : "--";

    // ── 左相机畸变 ──

    /// <summary>左相机二阶径向畸变系数 k1</summary>
    public string LeftK1 => LeftDistCoeffs.Length > 0 ? $"{LeftDistCoeffs[0]:F6}" : "--";

    /// <summary>左相机四阶径向畸变系数 k2</summary>
    public string LeftK2 => LeftDistCoeffs.Length > 1 ? $"{LeftDistCoeffs[1]:F6}" : "--";

    /// <summary>左相机一阶切向畸变系数 p1</summary>
    public string LeftP1 => LeftDistCoeffs.Length > 2 ? $"{LeftDistCoeffs[2]:F6}" : "--";

    /// <summary>左相机二阶切向畸变系数 p2</summary>
    public string LeftP2 => LeftDistCoeffs.Length > 3 ? $"{LeftDistCoeffs[3]:F6}" : "--";

    /// <summary>左相机六阶径向畸变系数 k3</summary>
    public string LeftK3 => LeftDistCoeffs.Length > 4 ? $"{LeftDistCoeffs[4]:F6}" : "--";

    // ═══════════════════════════════════════════════
    //  派生属性 — 右相机 WPF 绑定用（string 格式化）
    // ═══════════════════════════════════════════════

    /// <summary>右相机 X 方向焦距（像素）</summary>
    public string RightFx => RightCameraMatrix.Length == 9 ? $"{RightCameraMatrix[0,0]:F2}" : "--";

    /// <summary>右相机 Y 方向焦距（像素）</summary>
    public string RightFy => RightCameraMatrix.Length == 9 ? $"{RightCameraMatrix[1,1]:F2}" : "--";

    /// <summary>右相机主点 X 坐标（像素）</summary>
    public string RightCx => RightCameraMatrix.Length == 9 ? $"{RightCameraMatrix[0,2]:F2}" : "--";

    /// <summary>右相机主点 Y 坐标（像素）</summary>
    public string RightCy => RightCameraMatrix.Length == 9 ? $"{RightCameraMatrix[1,2]:F2}" : "--";

    // ── 右相机畸变 ──

    /// <summary>右相机二阶径向畸变系数 k1</summary>
    public string RightK1 => RightDistCoeffs.Length > 0 ? $"{RightDistCoeffs[0]:F6}" : "--";

    /// <summary>右相机四阶径向畸变系数 k2</summary>
    public string RightK2 => RightDistCoeffs.Length > 1 ? $"{RightDistCoeffs[1]:F6}" : "--";

    /// <summary>右相机一阶切向畸变系数 p1</summary>
    public string RightP1 => RightDistCoeffs.Length > 2 ? $"{RightDistCoeffs[2]:F6}" : "--";

    /// <summary>右相机二阶切向畸变系数 p2</summary>
    public string RightP2 => RightDistCoeffs.Length > 3 ? $"{RightDistCoeffs[3]:F6}" : "--";

    /// <summary>右相机六阶径向畸变系数 k3</summary>
    public string RightK3 => RightDistCoeffs.Length > 4 ? $"{RightDistCoeffs[4]:F6}" : "--";

    // ═══════════════════════════════════════════════
    //  派生属性 — 旋转矩阵 R (3×3)，9 个分量
    // ═══════════════════════════════════════════════

    /// <summary>旋转矩阵 R 第1行第1列</summary>
    public string R11 => RotationMatrix.Length == 9 ? $"{RotationMatrix[0,0]:F6}" : "--";

    /// <summary>旋转矩阵 R 第1行第2列</summary>
    public string R12 => RotationMatrix.Length == 9 ? $"{RotationMatrix[0,1]:F6}" : "--";

    /// <summary>旋转矩阵 R 第1行第3列</summary>
    public string R13 => RotationMatrix.Length == 9 ? $"{RotationMatrix[0,2]:F6}" : "--";

    /// <summary>旋转矩阵 R 第2行第1列</summary>
    public string R21 => RotationMatrix.Length == 9 ? $"{RotationMatrix[1,0]:F6}" : "--";

    /// <summary>旋转矩阵 R 第2行第2列</summary>
    public string R22 => RotationMatrix.Length == 9 ? $"{RotationMatrix[1,1]:F6}" : "--";

    /// <summary>旋转矩阵 R 第2行第3列</summary>
    public string R23 => RotationMatrix.Length == 9 ? $"{RotationMatrix[1,2]:F6}" : "--";

    /// <summary>旋转矩阵 R 第3行第1列</summary>
    public string R31 => RotationMatrix.Length == 9 ? $"{RotationMatrix[2,0]:F6}" : "--";

    /// <summary>旋转矩阵 R 第3行第2列</summary>
    public string R32 => RotationMatrix.Length == 9 ? $"{RotationMatrix[2,1]:F6}" : "--";

    /// <summary>旋转矩阵 R 第3行第3列</summary>
    public string R33 => RotationMatrix.Length == 9 ? $"{RotationMatrix[2,2]:F6}" : "--";

    // ═══════════════════════════════════════════════
    //  派生属性 — 平移向量 T (3 个分量)
    // ═══════════════════════════════════════════════

    /// <summary>平移向量 X 分量（mm）</summary>
    public string Tx => TranslationVector.Length > 0 ? $"{TranslationVector[0]:F4}" : "--";

    /// <summary>平移向量 Y 分量（mm）</summary>
    public string Ty => TranslationVector.Length > 1 ? $"{TranslationVector[1]:F4}" : "--";

    /// <summary>平移向量 Z 分量（mm）</summary>
    public string Tz => TranslationVector.Length > 2 ? $"{TranslationVector[2]:F4}" : "--";

    // ═══════════════════════════════════════════════
    //  派生属性 — 本质矩阵 E (3×3)，9 个分量
    // ═══════════════════════════════════════════════

    /// <summary>本质矩阵 E 第1行第1列</summary>
    public string E11 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[0,0]:F6}" : "--";

    /// <summary>本质矩阵 E 第1行第2列</summary>
    public string E12 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[0,1]:F6}" : "--";

    /// <summary>本质矩阵 E 第1行第3列</summary>
    public string E13 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[0,2]:F6}" : "--";

    /// <summary>本质矩阵 E 第2行第1列</summary>
    public string E21 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[1,0]:F6}" : "--";

    /// <summary>本质矩阵 E 第2行第2列</summary>
    public string E22 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[1,1]:F6}" : "--";

    /// <summary>本质矩阵 E 第2行第3列</summary>
    public string E23 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[1,2]:F6}" : "--";

    /// <summary>本质矩阵 E 第3行第1列</summary>
    public string E31 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[2,0]:F6}" : "--";

    /// <summary>本质矩阵 E 第3行第2列</summary>
    public string E32 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[2,1]:F6}" : "--";

    /// <summary>本质矩阵 E 第3行第3列</summary>
    public string E33 => EssentialMatrix.Length == 9 ? $"{EssentialMatrix[2,2]:F6}" : "--";

    // ═══════════════════════════════════════════════
    //  派生属性 — 基础矩阵 F (3×3)，9 个分量
    // ═══════════════════════════════════════════════

    /// <summary>基础矩阵 F 第1行第1列</summary>
    public string F11 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[0,0]:F6}" : "--";

    /// <summary>基础矩阵 F 第1行第2列</summary>
    public string F12 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[0,1]:F6}" : "--";

    /// <summary>基础矩阵 F 第1行第3列</summary>
    public string F13 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[0,2]:F6}" : "--";

    /// <summary>基础矩阵 F 第2行第1列</summary>
    public string F21 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[1,0]:F6}" : "--";

    /// <summary>基础矩阵 F 第2行第2列</summary>
    public string F22 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[1,1]:F6}" : "--";

    /// <summary>基础矩阵 F 第2行第3列</summary>
    public string F23 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[1,2]:F6}" : "--";

    /// <summary>基础矩阵 F 第3行第1列</summary>
    public string F31 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[2,0]:F6}" : "--";

    /// <summary>基础矩阵 F 第3行第2列</summary>
    public string F32 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[2,1]:F6}" : "--";

    /// <summary>基础矩阵 F 第3行第3列</summary>
    public string F33 => FundamentalMatrix.Length == 9 ? $"{FundamentalMatrix[2,2]:F6}" : "--";

    // ═══════════════════════════════════════════════
    //  属性变更回调
    // ═══════════════════════════════════════════════

    /// <summary>LeftCameraMatrix 变化时，通知左相机内参派生属性更新</summary>
    partial void OnLeftCameraMatrixChanged(double[,] value)
    {
        OnPropertyChanged(nameof(LeftFx));
        OnPropertyChanged(nameof(LeftFy));
        OnPropertyChanged(nameof(LeftCx));
        OnPropertyChanged(nameof(LeftCy));
    }

    /// <summary>LeftDistCoeffs 变化时，通知左相机畸变派生属性更新</summary>
    partial void OnLeftDistCoeffsChanged(double[] value)
    {
        OnPropertyChanged(nameof(LeftK1));
        OnPropertyChanged(nameof(LeftK2));
        OnPropertyChanged(nameof(LeftP1));
        OnPropertyChanged(nameof(LeftP2));
        OnPropertyChanged(nameof(LeftK3));
    }

    /// <summary>RightCameraMatrix 变化时，通知右相机内参派生属性更新</summary>
    partial void OnRightCameraMatrixChanged(double[,] value)
    {
        OnPropertyChanged(nameof(RightFx));
        OnPropertyChanged(nameof(RightFy));
        OnPropertyChanged(nameof(RightCx));
        OnPropertyChanged(nameof(RightCy));
    }

    /// <summary>RightDistCoeffs 变化时，通知右相机畸变派生属性更新</summary>
    partial void OnRightDistCoeffsChanged(double[] value)
    {
        OnPropertyChanged(nameof(RightK1));
        OnPropertyChanged(nameof(RightK2));
        OnPropertyChanged(nameof(RightP1));
        OnPropertyChanged(nameof(RightP2));
        OnPropertyChanged(nameof(RightK3));
    }

    /// <summary>RotationMatrix 变化时，通知所有 R 分量派生属性更新</summary>
    partial void OnRotationMatrixChanged(double[,] value)
    {
        OnPropertyChanged(nameof(R11)); OnPropertyChanged(nameof(R12)); OnPropertyChanged(nameof(R13));
        OnPropertyChanged(nameof(R21)); OnPropertyChanged(nameof(R22)); OnPropertyChanged(nameof(R23));
        OnPropertyChanged(nameof(R31)); OnPropertyChanged(nameof(R32)); OnPropertyChanged(nameof(R33));
    }

    /// <summary>TranslationVector 变化时，通知所有 T 分量派生属性更新</summary>
    partial void OnTranslationVectorChanged(double[] value)
    {
        OnPropertyChanged(nameof(Baseline));
        OnPropertyChanged(nameof(Tx));
        OnPropertyChanged(nameof(Ty));
        OnPropertyChanged(nameof(Tz));
    }

    /// <summary>EssentialMatrix 变化时，通知所有 E 分量派生属性更新</summary>
    partial void OnEssentialMatrixChanged(double[,] value)
    {
        OnPropertyChanged(nameof(E11)); OnPropertyChanged(nameof(E12)); OnPropertyChanged(nameof(E13));
        OnPropertyChanged(nameof(E21)); OnPropertyChanged(nameof(E22)); OnPropertyChanged(nameof(E23));
        OnPropertyChanged(nameof(E31)); OnPropertyChanged(nameof(E32)); OnPropertyChanged(nameof(E33));
    }

    /// <summary>FundamentalMatrix 变化时，通知所有 F 分量派生属性更新</summary>
    partial void OnFundamentalMatrixChanged(double[,] value)
    {
        OnPropertyChanged(nameof(F11)); OnPropertyChanged(nameof(F12)); OnPropertyChanged(nameof(F13));
        OnPropertyChanged(nameof(F21)); OnPropertyChanged(nameof(F22)); OnPropertyChanged(nameof(F23));
        OnPropertyChanged(nameof(F31)); OnPropertyChanged(nameof(F32)); OnPropertyChanged(nameof(F33));
    }
}
