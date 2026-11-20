using CommunityToolkit.Mvvm.ComponentModel;

namespace StereoTool.Calibration.Models;

/// <summary>
/// 棋盘格标定板配置参数
/// </summary>
public partial class ChessboardConfig : ObservableObject
{
    /// <summary>棋盘格内角点列数（宽度方向），默认为 9</summary>
    [ObservableProperty]
    private int _cornerCols = 9;

    /// <summary>棋盘格内角点行数（高度方向），默认为 6</summary>
    [ObservableProperty]
    private int _cornerRows = 6;

    /// <summary>每个格子的物理尺寸（单位：毫米），默认为 20.0mm</summary>
    [ObservableProperty]
    private double _squareSize = 20.0;

    /// <summary>相机传感器像元尺寸元数据，默认 24 μm，保留用于 YAML 兼容；不参与标定求解。</summary>
    [ObservableProperty]
    private double _pixelSizeUm = 24.0;

    /// <summary>是否估计第三个径向畸变系数；默认使用 k1、k2。</summary>
    [ObservableProperty]
    private bool _estimateK3;

    /// <summary>是否估计切向畸变 p1、p2；默认固定为零。</summary>
    [ObservableProperty]
    private bool _estimateTangentialDistortion;
}
