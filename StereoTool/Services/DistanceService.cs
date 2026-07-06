using System.IO;
using System.Runtime.Intrinsics.Arm;
using OpenCvSharp;
using StereoTool.Models;
using StereoTool.Utils;

namespace StereoTool.Services;

/// <summary>
/// 双目测距服务实现。
/// 基于 SGBM 半全局立体匹配算法，通过标定参数进行立体校正，
/// 计算视差图并通过重投影矩阵 Q 反算像素点对应的空间深度距离。
/// 校正参数（StereoRectifyResult）基于标定结果缓存，避免重复计算。
/// </summary>
public class DistanceService : IDisposable
{
    /// <summary>最小视差（通常设为 0）</summary>
    private const int SgbmMinDisparity = 0;
    /// <summary>视差搜索范围（必须是 16 的倍数），值越大能检测越近的物体，但计算量也越大</summary>
    private const int SgbmNumDisparities = 128;
    /// <summary>匹配块大小（3-11 之间的奇数），越大视差图越平滑但细节丢失越多</summary>
    private const int SgbmBlockSize = 9;

    /// <summary>
    /// 将单通道 32F 浮点 Mat 转为伪彩色 Mat，用于可视化深度图。
    /// </summary>
    /// <param name="mat32F"></param>
    /// <returns></returns>
    public Mat GetPseudoColorMat(Mat mat32F)
    {
        // src32f : CV_32FC1
        using var gray8 = new Mat();

        var color = new Mat();

        // 将32F归一化到0~255
        Cv2.Normalize(mat32F, gray8, 0, 255, NormTypes.MinMax, (int)MatType.CV_8UC1);

        // 转为伪彩
        Cv2.ApplyColorMap(gray8, color, ColormapTypes.Jet);

        return color;
    }

    /// <summary>
    /// 将三通道 XYZ Mat 拆分为单通道 X、Y、Z Mat。
    /// </summary>
    /// <param name="xyz"></param>
    /// <returns></returns>
    public (Mat X, Mat Y, Mat Z) Split(Mat xyz)
    {
        var mats = Cv2.Split(xyz);
        return (mats[0], mats[1], mats[2]);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="mat32f"></param>
    /// <returns></returns>
    public Mat Normlize(Mat mat32f)
    {
        var mat8u = new Mat();
        Cv2.Normalize(mat32f, mat8u, 0, 255, NormTypes.MinMax, (int)MatType.CV_8UC1);
        return mat8u;
    }

    /// <summary>
    /// 计算视差图和三维点云地图
    /// </summary>
    /// <param name="stereoRectifyParams"></param>
    /// <param name="leftMat"></param>
    /// <param name="rightMat"></param>
    /// <returns></returns>
    public (Mat Disparity, Mat MapXYZ) SgbmCompute(StereoRectifyResult stereoRectifyParams, Mat leftMat, Mat rightMat)
    {
        // ── 应用校正映射 ──
        using var rectLeft = new Mat();
        using var rectRight = new Mat();
        Cv2.Remap(leftMat, rectLeft, stereoRectifyParams.LeftMapX, stereoRectifyParams.LeftMapY, InterpolationFlags.Linear);
        Cv2.Remap(rightMat, rectRight, stereoRectifyParams.RightMapX, stereoRectifyParams.RightMapY, InterpolationFlags.Linear);

        // ── SGBM 视差计算 ──
        using var sgbm = StereoSGBM.Create(
            minDisparity:       SgbmMinDisparity,
            numDisparities:     SgbmNumDisparities,
            blockSize:          SgbmBlockSize,
            p1:                 8 * 3 * SgbmBlockSize * SgbmBlockSize,
            p2:                 32 * 3 * SgbmBlockSize * SgbmBlockSize,
            disp12MaxDiff:      1,
            preFilterCap:       63,
            uniquenessRatio:    10,
            speckleWindowSize:  100,
            speckleRange:       32,
            mode:               StereoSGBMMode.SGBM);

        using var disparity = new Mat();
        sgbm.Compute(rectLeft, rectRight, disparity);

        var disparity32F = new Mat();
        disparity.ConvertTo(disparity32F, MatType.CV_32F, 1.0 / 16.0);

        var xyz = new Mat();
        Cv2.ReprojectImageTo3D(disparity32F, xyz, stereoRectifyParams.Q);

        return (disparity32F, xyz);
    }

    /// <summary>
    /// 执行双目立体校正，返回完整的校正参数。
    /// </summary>
    public StereoRectifyResult GetRectifyParams(StereoCalibrationParams calib)
    {
        var leftCam = calib.LeftCameraMatrix.ToMat();
        var leftDist = calib.LeftDistCoeffs.ToMat();
        var rightCam = calib.RightCameraMatrix.ToMat();
        var rightDist = calib.RightDistCoeffs.ToMat();

        using var R = calib.RotationMatrix.ToMat();
        using var T = calib.TranslationVector.ToMat();

        var imageSize = new Size(calib.ImageWidth, calib.ImageHeight);
        
        var R1 = new Mat(); 
        var R2 = new Mat();
        var P1 = new Mat();
        var P2 = new Mat();
        var Q = new Mat();

        Cv2.StereoRectify(leftCam, leftDist, rightCam, rightDist,
            imageSize, R, T, R1, R2, P1, P2, Q,
            StereoRectificationFlags.ZeroDisparity, alpha: 0);

        var map1L = new Mat();
        var map2L = new Mat();
        var map1R = new Mat();
        var map2R = new Mat();

        Cv2.InitUndistortRectifyMap(leftCam, leftDist, R1, P1, imageSize, MatType.CV_32FC1, map1L, map2L);
        Cv2.InitUndistortRectifyMap(rightCam, rightDist, R2, P2, imageSize, MatType.CV_32FC1, map1R, map2R);

        return new StereoRectifyResult(Q, R1, P1, R2, P2,
            leftCam, leftDist, rightCam, rightDist,
            map1L, map2L, map1R, map2R);
    }

    /// <summary>
    /// 释放资源。当前 DistanceService 未持有需要手动释放的非托管资源，
    /// 保留 IDisposable 接口以保持未来扩展性（如缓存 SGBM 对象等）。
    /// </summary>
    public void Dispose()
    {
        // 当前无资源需要释放
    }
}