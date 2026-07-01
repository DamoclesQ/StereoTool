using OpenCvSharp;

namespace StereoTool.Models;

/// <summary>
/// 立体校正结果，封装 OpenCV StereoRectify 产生的所有校正参数。
/// 用于避免每次测距时重复执行昂贵的立体校正计算。
/// </summary>
public class StereoRectifyResult : IDisposable
{
    /// <summary>
    /// 构造函数，接收 StereoRectify 和 InitUndistortRectifyMap 产生的所有校正参数。
    /// </summary>
    public StereoRectifyResult(Mat q, Mat r1, Mat p1, Mat r2, Mat p2,
        Mat leftCameraMatrix, Mat leftDistCoeffs, Mat rightCameraMatrix, Mat rightDistCoeffs,
        Mat leftMapX, Mat leftMapY, Mat rightMapX, Mat rightMapY)
    {
        Q = q;
        LeftR = r1;
        LeftP = p1;
        RightR = r2;
        RightP = p2;
        LeftCameraMatrix = leftCameraMatrix;
        LeftDistCoeffs = leftDistCoeffs;
        RightCameraMatrix = rightCameraMatrix;
        RightDistCoeffs = rightDistCoeffs;
        LeftMapX = leftMapX;
        LeftMapY = leftMapY;
        RightMapX = rightMapX;
        RightMapY = rightMapY;
    }

    /// <summary>释放所有内部 Mat 对象</summary>
    public void Dispose()
    {
        Q?.Dispose();
        LeftR?.Dispose();
        LeftP?.Dispose();
        RightR?.Dispose();
        RightP?.Dispose();
        LeftCameraMatrix?.Dispose();
        LeftDistCoeffs?.Dispose();
        RightCameraMatrix?.Dispose();
        RightDistCoeffs?.Dispose();
        LeftMapX?.Dispose();
        LeftMapY?.Dispose();
        RightMapX?.Dispose();
        RightMapY?.Dispose();
    }
    /// <summary>左图像 X 方向重映射查找表</summary>
    public Mat LeftMapX { get; set; } = null!;

    /// <summary>左图像 Y 方向重映射查找表</summary>
    public Mat LeftMapY { get; set; } = null!;

    /// <summary>右图像 X 方向重映射查找表</summary>
    public Mat RightMapX { get; set; } = null!;

    /// <summary>右图像 Y 方向重映射查找表</summary>
    public Mat RightMapY { get; set; } = null!;

    /// <summary>重投影矩阵 Q (4x4)，用于从视差反算空间 3D 坐标</summary>
    public Mat Q { get; set; } = null!;

    /// <summary>左相机内参矩阵 (3x3)，用于 UndistortPoints</summary>
    public Mat LeftCameraMatrix { get; set; } = null!;

    /// <summary>左相机畸变系数，用于 UndistortPoints</summary>
    public Mat LeftDistCoeffs { get; set; } = null!;

    /// <summary>左相机校正旋转矩阵 (3x3)，用于 UndistortPoints</summary>
    public Mat LeftR { get; set; } = null!;

    /// <summary>左相机校正投影矩阵 (3x4)，用于 UndistortPoints</summary>
    public Mat LeftP { get; set; } = null!;

    /// <summary>右相机内参矩阵 (3x3)，用于 UndistortPoints</summary>
    public Mat RightCameraMatrix { get; set; } = null!;

    /// <summary>右相机畸变系数，用于 UndistortPoints</summary>
    public Mat RightDistCoeffs { get; set; } = null!;

    /// <summary>右相机校正旋转矩阵 (3x3)，用于 UndistortPoints</summary>
    public Mat RightR { get; set; } = null!;

    /// <summary>右相机校正投影矩阵 (3x4)，用于 UndistortPoints</summary>
    public Mat RightP { get; set; } = null!;
}
