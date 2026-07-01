using CommunityToolkit.Mvvm.ComponentModel;

namespace CameraCalibrator.Models;

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

    /// <summary>相机传感器像元尺寸（单位：微米 μm）。用于将像素焦距换算为物理焦距（mm）。设为 0 表示未提供。</summary>
    [ObservableProperty]
    private double _pixelSizeUm = 0.0;
}
