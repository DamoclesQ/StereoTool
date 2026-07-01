using System.IO;
using StereoTool.Models;
using OpenCvSharp;

namespace StereoTool.Services;

/// <summary>
/// 双目测距服务实现。
/// 基于 SGBM 半全局立体匹配算法，通过标定参数进行立体校正，
/// 计算视差图并通过重投影矩阵 Q 反算像素点对应的空间深度距离。
/// 校正参数（StereoRectifyResult）基于标定结果缓存，避免重复计算。
/// </summary>
public class DistanceService : IDisposable
{
    // ═══════════════════════════════════════════════════════════════════
    // SGBM 参数常量
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>最小视差（通常设为 0）</summary>
    private const int SgbmMinDisparity = 0;
    /// <summary>视差搜索范围（必须是 16 的倍数），值越大能检测越近的物体，但计算量也越大</summary>
    private const int SgbmNumDisparities = 128;
    /// <summary>匹配块大小（3-11 之间的奇数），越大视差图越平滑但细节丢失越多</summary>
    private const int SgbmBlockSize = 9;

    // ═══════════════════════════════════════════════════════════════════
    // 校正参数缓存
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>上次使用的标定结果引用（用于判断是否需要重算）</summary>
    private StereoParameters? _cachedCalib;
    /// <summary>缓存的校正参数，切换标定结果时自动重建</summary>
    private StereoRectifyResult? _cachedRectifyResult;

    // ═══════════════════════════════════════════════════════════════════
    // 公有方法
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 异步执行距离测量。
    /// 使用当前已加载的标定参数、左右图像以及用户在左图上选中的像素坐标进行测量。
    /// </summary>
    /// <param name="leftImagePath">左图像文件路径</param>
    /// <param name="rightImagePath">右图像文件路径</param>
    /// <param name="calib">标定结果</param>
    /// <param name="pixelPoint">左图像上的像素坐标</param>
    /// <returns>测量得到的距离（单位：米）</returns>
    public async Task<double> MeasureDistanceAsync(
        string leftImagePath,
        string rightImagePath,
        StereoParameters calib,
        Point2f pixelPoint)
    {
        // 参数验证放在 Task.Run 外部，尽早暴露错误
        if (!File.Exists(leftImagePath))
            throw new ArgumentException($"左图像文件不存在: {leftImagePath}", nameof(leftImagePath));
        if (!File.Exists(rightImagePath))
            throw new ArgumentException($"右图像文件不存在: {rightImagePath}", nameof(rightImagePath));

        // 耗时的图像加载、校正、视差计算放入后台线程，避免阻塞 UI
        return await Task.Run(() =>
        {
            // ── 1. 加载为灰度图像 ──
            using var leftGray = new Mat(leftImagePath, ImreadModes.Grayscale);
            using var rightGray = new Mat(rightImagePath, ImreadModes.Grayscale);

            if (leftGray.Empty())
                throw new InvalidOperationException($"左图像加载失败: {leftImagePath}");
            if (rightGray.Empty())
                throw new InvalidOperationException($"右图像加载失败: {rightImagePath}");

            // 像素坐标越界检查
            if (pixelPoint.X < 0 || pixelPoint.X >= leftGray.Cols ||
                pixelPoint.Y < 0 || pixelPoint.Y >= leftGray.Rows)
            {
                throw new ArgumentException(
                    $"像素坐标 ({pixelPoint.X}, {pixelPoint.Y}) 超出图像范围 " +
                    $"(0–{leftGray.Cols - 1}, 0–{leftGray.Rows - 1})",
                    nameof(pixelPoint));
            }

            // ── 2. 立体校正（使用缓存，避免每次测距都重算） ──
            var rect = GetRectifyParams(calib);
            {
                // ── 3. 应用校正映射 ──
                using var rectLeft = new Mat();
                using var rectRight = new Mat();
                Cv2.Remap(leftGray, rectLeft, rect.LeftMapX, rect.LeftMapY, InterpolationFlags.Linear);
                Cv2.Remap(rightGray, rectRight, rect.RightMapX, rect.RightMapY, InterpolationFlags.Linear);

                // ── 4. SGBM 视差计算 ──
                using var sgbm = StereoSGBM.Create(
                    SgbmMinDisparity,
                    SgbmNumDisparities,
                    SgbmBlockSize,
                    8 * 3 * SgbmBlockSize * SgbmBlockSize,
                    32 * 3 * SgbmBlockSize * SgbmBlockSize,
                    1,
                    63,
                    10,
                    100,
                    32, 
                    StereoSGBMMode.SGBM);

                using var disparity = new Mat();
                sgbm.Compute(rectLeft, rectRight, disparity);

                // ── 5. 将原始像素坐标映射到校正后坐标，再提取视差 ──
                using var srcMat = new Mat(1, 1, MatType.CV_32FC2);
                srcMat.Set(0, 0, pixelPoint);
                using var dstMat = new Mat();
                Cv2.UndistortPoints(InputArray.Create(srcMat), OutputArray.Create(dstMat), rect.LeftCameraMatrix, rect.LeftDistCoeffs, rect.LeftR, rect.LeftP);
                var rectPt = dstMat.At<Point2f>(0, 0);
                int rectX = (int)Math.Round(rectPt.X);
                int rectY = (int)Math.Round(rectPt.Y);

                if (rectX < 0 || rectX >= disparity.Cols || rectY < 0 || rectY >= disparity.Rows)
                    return double.NaN;

                short rawDisp = disparity.At<short>(rectY, rectX);
                double d = rawDisp / 16.0;

                if (d <= 0)
                    return double.NaN;

                // ── 6. 通过重投影矩阵 Q 反算空间距离 ──
                double q03 = rect.Q.At<double>(0, 3);
                double q13 = rect.Q.At<double>(1, 3);
                double q23 = rect.Q.At<double>(2, 3);
                double q32 = rect.Q.At<double>(3, 2);
                double q33 = rect.Q.At<double>(3, 3);

                double u = rectPt.X;
                double v = rectPt.Y;

                double x = u + q03;
                double y = v + q13;
                double w = q32 * d + q33;

                if (Math.Abs(w) < 1e-10)
                    return double.NaN;

                double z = q23 / w;
                return z;
            }
        });
    }

    /// <summary>
    /// 使用手动输入的左右对应点坐标执行距离测量（无需图像，直接基于标定参数计算）。
    /// </summary>
    /// <param name="calib">双目标定结果，图像尺寸由其中的 ImageWidth/ImageHeight 推导</param>
    /// <param name="leftPoint">左图像上的像素坐标</param>
    /// <param name="rightPoint">右图像上的像素坐标</param>
    /// <returns>空间距离（米），坐标越界或计算失败时返回 NaN</returns>
    public async Task<double> MeasureDistanceFromPointsAsync(StereoParameters calib, Point2f leftPoint, Point2f rightPoint)
    {
        if (leftPoint.X < 0 || leftPoint.X >= calib.ImageWidth ||
            leftPoint.Y < 0 || leftPoint.Y >= calib.ImageHeight)
            return double.NaN;
        if (rightPoint.X < 0 || rightPoint.X >= calib.ImageWidth ||
            rightPoint.Y < 0 || rightPoint.Y >= calib.ImageHeight)
            return double.NaN;

        return await Task.Run(() =>
        {
            // ── 立体校正（使用缓存，避免每次测距都重算） ──
            var rect = GetRectifyParams(calib);
            {
                using var leftSrcMat = new Mat(1, 1, MatType.CV_32FC2);
                using var rightSrcMat = new Mat(1, 1, MatType.CV_32FC2);
                leftSrcMat.Set(0, 0, leftPoint);
                rightSrcMat.Set(0, 0, rightPoint);
                using var leftDstMat = new Mat();
                using var rightDstMat = new Mat();
                Cv2.UndistortPoints(leftSrcMat, leftDstMat, rect.LeftCameraMatrix, rect.LeftDistCoeffs, rect.LeftR, rect.LeftP);
                Cv2.UndistortPoints(rightSrcMat, rightDstMat, rect.RightCameraMatrix, rect.RightDistCoeffs, rect.RightR, rect.RightP);
                double rectLeftX = leftDstMat.At<Point2f>(0, 0).X;
                double rectRightX = rightDstMat.At<Point2f>(0, 0).X;

                double disparity = rectLeftX - rectRightX;
                if (disparity <= 0)
                    return double.NaN;

                double q23 = rect.Q.At<double>(2, 3);
                double q32 = rect.Q.At<double>(3, 2);
                double q33 = rect.Q.At<double>(3, 3);

                double w = q32 * disparity + q33;
                if (Math.Abs(w) < 1e-10)
                    return double.NaN;

                return q23 / w;
            }
        });
    }

    /// <summary>
    /// 计算左右图像的视差图。
    /// </summary>
    public Mat ComputeDisparityMap(
        string leftImagePath,
        string rightImagePath,
        StereoParameters calib)
    {
        if (!File.Exists(leftImagePath))
            throw new ArgumentException($"左图像文件不存在: {leftImagePath}", nameof(leftImagePath));
        if (!File.Exists(rightImagePath))
            throw new ArgumentException($"右图像文件不存在: {rightImagePath}", nameof(rightImagePath));

        using var leftColor = Cv2.ImRead(leftImagePath, ImreadModes.Color);
        using var rightColor = Cv2.ImRead(rightImagePath, ImreadModes.Color);

        if (leftColor.Empty())
            throw new InvalidOperationException($"左图像加载失败: {leftImagePath}");
        if (rightColor.Empty())
            throw new InvalidOperationException($"右图像加载失败: {rightImagePath}");

        using var leftGray = new Mat();
        using var rightGray = new Mat();
        Cv2.CvtColor(leftColor, leftGray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(rightColor, rightGray, ColorConversionCodes.BGR2GRAY);

        var (Q, map1L, map2L, map1R, map2R) = PerformStereoRectify(calib);
        Q.Dispose();
        using (map1L) using (map2L) using (map1R) using (map2R)
        {
            using var rectLeft = new Mat();
            using var rectRight = new Mat();
            Cv2.Remap(leftGray, rectLeft, map1L, map2L, InterpolationFlags.Linear);
            Cv2.Remap(rightGray, rectRight, map1R, map2R, InterpolationFlags.Linear);

            using var sgbm = StereoSGBM.Create(
                SgbmMinDisparity, SgbmNumDisparities, SgbmBlockSize,
                8 * 3 * SgbmBlockSize * SgbmBlockSize,
                32 * 3 * SgbmBlockSize * SgbmBlockSize,
                1, 63, 10, 100, 32, 0);

            var disparity = new Mat();
            sgbm.Compute(rectLeft, rectRight, disparity);
            return disparity;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 私有方法 — 校正参数缓存
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 获取立体校正参数（带缓存）。
    /// 当标定结果未变时直接返回缓存，避免重复执行 StereoRectify 和 InitUndistortRectifyMap。
    /// </summary>
    private StereoRectifyResult GetRectifyParams(StereoParameters calib)
    {
        if (ReferenceEquals(_cachedCalib, calib) && _cachedRectifyResult != null)
            return _cachedRectifyResult;

        _cachedRectifyResult?.Dispose();
        _cachedCalib = calib;
        _cachedRectifyResult = PerformStereoRectifyFull(calib);
        return _cachedRectifyResult;
    }

    /// <summary>释放缓存的校正参数</summary>
    public void Dispose()
    {
        _cachedRectifyResult?.Dispose();
        _cachedRectifyResult = null;
        _cachedCalib = null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 私有方法 — 立体校正
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 执行双目立体校正，返回完整的校正参数。
    /// </summary>
    private static StereoRectifyResult PerformStereoRectifyFull(StereoParameters calib)
    {
        var leftCam = CalibrationService.Array2DToMat(calib.LeftCameraMatrix);
        var leftDist = CalibrationService.Array1DToMat(calib.LeftDistCoeffs);
        var rightCam = CalibrationService.Array2DToMat(calib.RightCameraMatrix);
        var rightDist = CalibrationService.Array1DToMat(calib.RightDistCoeffs);
        using var R = CalibrationService.Array2DToMat(calib.RotationMatrix);
        using var T = CalibrationService.Array1DToMat(calib.TranslationVector);

        var imageSize = new Size(calib.ImageWidth, calib.ImageHeight);
        var R1 = new Mat(); var R2 = new Mat();
        var P1 = new Mat(); var P2 = new Mat();
        var Q = new Mat();

        Cv2.StereoRectify(leftCam, leftDist, rightCam, rightDist,
            imageSize, R, T, R1, R2, P1, P2, Q,
            StereoRectificationFlags.ZeroDisparity, alpha: 0);

        var map1L = new Mat(); var map2L = new Mat();
        var map1R = new Mat(); var map2R = new Mat();

        Cv2.InitUndistortRectifyMap(leftCam, leftDist, R1, P1, imageSize, MatType.CV_32FC1, map1L, map2L);
        Cv2.InitUndistortRectifyMap(rightCam, rightDist, R2, P2, imageSize, MatType.CV_32FC1, map1R, map2R);

        return new StereoRectifyResult(Q, R1, P1, R2, P2,
            leftCam, leftDist, rightCam, rightDist,
            map1L, map2L, map1R, map2R);
    }

    /// <summary>
    /// 执行双目立体校正（精简版），仅返回重映射表和 Q 矩阵。
    /// </summary>
    private static (Mat Q, Mat map1L, Mat map2L, Mat map1R, Mat map2R) PerformStereoRectify(StereoParameters calib)
    {
        using var leftCam = CalibrationService.Array2DToMat(calib.LeftCameraMatrix);
        using var leftDist = CalibrationService.Array1DToMat(calib.LeftDistCoeffs);
        using var rightCam = CalibrationService.Array2DToMat(calib.RightCameraMatrix);
        using var rightDist = CalibrationService.Array1DToMat(calib.RightDistCoeffs);
        using var R = CalibrationService.Array2DToMat(calib.RotationMatrix);
        using var T = CalibrationService.Array1DToMat(calib.TranslationVector);

        var imageSize = new Size(calib.ImageWidth, calib.ImageHeight);
        var R1 = new Mat(); var R2 = new Mat();
        var P1 = new Mat(); var P2 = new Mat();
        var Q = new Mat();

        Cv2.StereoRectify(leftCam, leftDist, rightCam, rightDist,
            imageSize, R, T, R1, R2, P1, P2, Q,
            StereoRectificationFlags.ZeroDisparity, alpha: 0);

        var map1L = new Mat(); var map2L = new Mat();
        var map1R = new Mat(); var map2R = new Mat();
        Cv2.InitUndistortRectifyMap(leftCam, leftDist, R1, P1, imageSize, MatType.CV_32FC1, map1L, map2L);
        Cv2.InitUndistortRectifyMap(rightCam, rightDist, R2, P2, imageSize, MatType.CV_32FC1, map1R, map2R);

        R1.Dispose(); R2.Dispose(); P1.Dispose(); P2.Dispose();

        return (Q, map1L, map2L, map1R, map2R);
    }
}
