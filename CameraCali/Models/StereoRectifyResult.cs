using OpenCvSharp;

namespace CameraCali.Models;

/// <summary>
/// 双目立体校正计算结果，包含左右相机的校正变换矩阵、重投影矩阵 Q 和像素重映射表。
/// 实现 IDisposable 以统一管理所有 OpenCV Mat 对象的生命周期。
/// </summary>
public sealed class StereoRectifyResult : IDisposable
{
    /// <summary>视差-深度重投影矩阵 (4x4)</summary>
    public Mat Q { get; }

    /// <summary>左相机校正旋转矩阵 (3x3)</summary>
    public Mat LeftR { get; }
    /// <summary>左相机校正投影矩阵 (3x4)</summary>
    public Mat LeftP { get; }
    /// <summary>左相机内参矩阵 (3x3)</summary>
    public Mat LeftCameraMatrix { get; }
    /// <summary>左相机畸变系数 (1x5 或 1x8)</summary>
    public Mat LeftDistCoeffs { get; }
    /// <summary>左相机 X 方向像素重映射表</summary>
    public Mat LeftMapX { get; }
    /// <summary>左相机 Y 方向像素重映射表</summary>
    public Mat LeftMapY { get; }

    /// <summary>右相机校正旋转矩阵 (3x3)</summary>
    public Mat RightR { get; }
    /// <summary>右相机校正投影矩阵 (3x4)</summary>
    public Mat RightP { get; }
    /// <summary>右相机内参矩阵 (3x3)</summary>
    public Mat RightCameraMatrix { get; }
    /// <summary>右相机畸变系数 (1x5 或 1x8)</summary>
    public Mat RightDistCoeffs { get; }
    /// <summary>右相机 X 方向像素重映射表</summary>
    public Mat RightMapX { get; }
    /// <summary>右相机 Y 方向像素重映射表</summary>
    public Mat RightMapY { get; }

    /// <summary>
    /// 构造立体校正结果，接收所有由 PerformStereoRectifyFull 计算出的 Mat 对象。
    /// </summary>
    public StereoRectifyResult(
        Mat q,
        Mat leftR, Mat leftP,
        Mat rightR, Mat rightP,
        Mat leftCameraMatrix, Mat leftDistCoeffs,
        Mat rightCameraMatrix, Mat rightDistCoeffs,
        Mat leftMapX, Mat leftMapY,
        Mat rightMapX, Mat rightMapY)
    {
        Q = q;
        LeftR = leftR;
        LeftP = leftP;
        RightR = rightR;
        RightP = rightP;
        LeftCameraMatrix = leftCameraMatrix;
        LeftDistCoeffs = leftDistCoeffs;
        RightCameraMatrix = rightCameraMatrix;
        RightDistCoeffs = rightDistCoeffs;
        LeftMapX = leftMapX;
        LeftMapY = leftMapY;
        RightMapX = rightMapX;
        RightMapY = rightMapY;
    }

    /// <summary>释放所有 Mat 对象</summary>
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
}
