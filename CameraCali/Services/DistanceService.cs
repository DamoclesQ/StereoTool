using System.IO;
using CameraCali.Models;
using OpenCvSharp;

namespace CameraCali.Services;

/// <summary>
/// 双目测距服务实现。
/// 基于 SGBM 半全局立体匹配算法，通过标定参数进行立体校正，
/// 计算视差图并通过重投影矩阵 Q 反算像素点对应的空间深度距离。
/// </summary>
public class DistanceService
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
    // 公有方法 — IDistanceService 实现
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<double> MeasureDistanceAsync(
        string leftImagePath,
        string rightImagePath,
        CalibrationResult calib,
        System.Drawing.Point pixelPoint)
    {
        // 参数验证放在 Task.Run 外部，尽早暴露错误
        if (!File.Exists(leftImagePath))
            throw new ArgumentException($"左图像文件不存在: {leftImagePath}", nameof(leftImagePath));
        if (!File.Exists(rightImagePath))
            throw new ArgumentException($"右图像文件不存在: {rightImagePath}", nameof(rightImagePath));

        // 耗时的图像加载、校正、视差计算放入后台线程，避免阻塞 UI
        return await Task.Run(() =>
        {
            // ── 1. 加载图像并转为灰度 ──
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

            // 像素坐标越界检查
            if (pixelPoint.X < 0 || pixelPoint.X >= leftGray.Cols ||
                pixelPoint.Y < 0 || pixelPoint.Y >= leftGray.Rows)
            {
                throw new ArgumentException(
                    $"像素坐标 ({pixelPoint.X}, {pixelPoint.Y}) 超出图像范围 " +
                    $"(0–{leftGray.Cols - 1}, 0–{leftGray.Rows - 1})",
                    nameof(pixelPoint));
            }

            // ── 2. 立体校正 ──
            var (Q, R1, P1, R2, P2, leftCam, leftDist, rightCam, rightDist,
                map1L, map2L, map1R, map2R) =
                PerformStereoRectifyFull(leftGray, rightGray, calib);
            using (R1) using (P1) using (R2) using (P2)
            using (leftCam) using (leftDist) using (rightCam) using (rightDist)

            using (Q)
            using (map1L) using (map2L)
            using (map1R) using (map2R)
            {
                // ── 3. 应用校正映射 ──
                using var rectLeft = new Mat();
                using var rectRight = new Mat();
                Cv2.Remap(leftGray, rectLeft, map1L, map2L, InterpolationFlags.Linear);
                Cv2.Remap(rightGray, rectRight, map1R, map2R, InterpolationFlags.Linear);

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
                    0);  // MODE_SGBM — 标准 SGBM 模式（SGBM3Way 在 OpenCV 新版中已移除）

                using var disparity = new Mat();
                sgbm.Compute(rectLeft, rectRight, disparity);

                // ── 5. 将原始像素坐标映射到校正后坐标，再提取视差 ──
                using var srcMat = new Mat(1, 1, MatType.CV_32FC2);
                srcMat.Set(0, 0, new Point2f(pixelPoint.X, pixelPoint.Y));
                using var dstMat = new Mat();
                Cv2.UndistortPoints(InputArray.Create(srcMat), OutputArray.Create(dstMat),
                    leftCam, leftDist, R1, P1);
                var rectPt = dstMat.At<Point2f>(0, 0);
                int rectX = (int)Math.Round(rectPt.X);
                int rectY = (int)Math.Round(rectPt.Y);

                if (rectX < 0 || rectX >= disparity.Cols || rectY < 0 || rectY >= disparity.Rows)
                    return double.NaN;

                short rawDisp = disparity.At<short>(rectY, rectX);

                double d = rawDisp / 16.0;

                // 无效视差（<= 0），无法计算距离
                if (d <= 0)
                    return double.NaN;

                // ── 6. 通过重投影矩阵 Q 反算空间距离 ──
                // Q 矩阵标准形式:
                //     [ 1  0  0  -cx     ]
                // Q = [ 0  1  0  -cy     ]
                //     [ 0  0  0   f      ]
                //     [ 0  0  1/Tx (cx-cx')/Tx ]
                // 三维重建: [X, Y, Z, W]^T = Q * [u, v, d, 1]^T
                // 深度距离 = Z / W = f * Tx / (d - (cx - cx'))
                double q03 = Q.At<double>(0, 3);  // -cx
                double q13 = Q.At<double>(1, 3);  // -cy
                double q23 = Q.At<double>(2, 3);  // f（焦距）
                double q32 = Q.At<double>(3, 2);  // 1/Tx
                double q33 = Q.At<double>(3, 3);  // (cx - cx')/Tx（对齐时为 0）

                double u = rectPt.X;
                double v = rectPt.Y;

                // 齐次坐标计算
                double x = u + q03;          // u - cx
                double y = v + q13;          // v - cy
                double w = q32 * d + q33;    // d/Tx + (cx-cx')/Tx

                if (Math.Abs(w) < 1e-10)
                    return double.NaN;

                double z = q23 / w;  // f * Tx / (d + ...) 与标定 SquareSize 单位一致

                return z / 1000.0;  // mm → 米
            }
        });
    }

    /// <inheritdoc/>
    public async Task<double> MeasureDistanceFromPointsAsync(
        string leftImagePath,
        string rightImagePath,
        CalibrationResult calib,
        System.Drawing.Point leftPoint,
        System.Drawing.Point rightPoint)
    {
        if (!File.Exists(leftImagePath))
            throw new ArgumentException($"左图像文件不存在: {leftImagePath}", nameof(leftImagePath));
        if (!File.Exists(rightImagePath))
            throw new ArgumentException($"右图像文件不存在: {rightImagePath}", nameof(rightImagePath));

        return await Task.Run(() =>
        {
            using var leftGray = Cv2.ImRead(leftImagePath, ImreadModes.Grayscale);
            using var rightGray = Cv2.ImRead(rightImagePath, ImreadModes.Grayscale);

            int imgW = leftGray.Cols;
            int imgH = leftGray.Rows;

            if (leftPoint.X < 0 || leftPoint.X >= imgW || leftPoint.Y < 0 || leftPoint.Y >= imgH)
                return double.NaN;
            if (rightPoint.X < 0 || rightPoint.X >= imgW || rightPoint.Y < 0 || rightPoint.Y >= imgH)
                return double.NaN;

            // ── 立体校正（通过共享方法，消除代码重复） ──
            var (Q, R1, P1, R2, P2, leftCam, leftDist, rightCam, rightDist,
                _, _, _, _) =
                PerformStereoRectifyFull(leftGray, rightGray, calib);
            using (R1) using (P1) using (R2) using (P2)
            using (leftCam) using (leftDist) using (rightCam) using (rightDist)
            using (R1) using (R2)
            using (P1) using (P2)
            {
                // ── 使用 UndistortPoints 将原始坐标映射到校正后坐标 ──
                // UndistortPoints 将原始坐标映射到校正后图像坐标
                // OpenCvSharp4 API 使用 InputArray/OutputArray（Mat 承载 Point2f）
                using var leftSrcMat = new Mat(1, 1, MatType.CV_32FC2);
                using var rightSrcMat = new Mat(1, 1, MatType.CV_32FC2);
                leftSrcMat.Set(0, 0, new Point2f(leftPoint.X, leftPoint.Y));
                rightSrcMat.Set(0, 0, new Point2f(rightPoint.X, rightPoint.Y));
                using var leftDstMat = new Mat();
                using var rightDstMat = new Mat();
                Cv2.UndistortPoints(InputArray.Create(leftSrcMat), OutputArray.Create(leftDstMat),
                    leftCam, leftDist, R1, P1);
                Cv2.UndistortPoints(InputArray.Create(rightSrcMat), OutputArray.Create(rightDstMat),
                    rightCam, rightDist, R2, P2);
                double rectLeftX = leftDstMat.At<Point2f>(0, 0).X;
                double rectRightX = rightDstMat.At<Point2f>(0, 0).X;

                // 校正后对应点应在同一扫描线上（Y 坐标基本一致）
                double disparity = rectLeftX - rectRightX;
                if (disparity <= 0)
                    return double.NaN;

                // ── Q 矩阵反算深度 ──
                double q23 = Q.At<double>(2, 3);  // f
                double q32 = Q.At<double>(3, 2);  // 1/Tx
                double q33 = Q.At<double>(3, 3);  // (cx-cx')/Tx ≈ 0

                double w = q32 * disparity + q33;
                if (Math.Abs(w) < 1e-10)
                    return double.NaN;

                return q23 / w / 1000.0;  // mm → 米
            }
        });
    }

    /// <inheritdoc/>
    public Mat ComputeDisparityMap(
        string leftImagePath,
        string rightImagePath,
        CalibrationResult calib)
    {
        if (!File.Exists(leftImagePath))
            throw new ArgumentException($"左图像文件不存在: {leftImagePath}", nameof(leftImagePath));
        if (!File.Exists(rightImagePath))
            throw new ArgumentException($"右图像文件不存在: {rightImagePath}", nameof(rightImagePath));

        // ── 1. 加载图像 ──
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

        // ── 2. 立体校正 ──
        var (Q, map1L, map2L, map1R, map2R) =
            PerformStereoRectify(leftGray, rightGray, calib);

        // Q 矩阵在此方法中不需要，立即释放
        Q.Dispose();

        using (map1L) using (map2L)
        using (map1R) using (map2R)
        {
            using var rectLeft = new Mat();
            using var rectRight = new Mat();
            Cv2.Remap(leftGray, rectLeft, map1L, map2L, InterpolationFlags.Linear);
            Cv2.Remap(rightGray, rectRight, map1R, map2R, InterpolationFlags.Linear);

            // ── 3. SGBM 视差计算 ──
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
                0);  // MODE_SGBM — 标准 SGBM 模式

            // 返回的 Mat 由调用方负责 Dispose，此处不释放
            var disparity = new Mat();
            sgbm.Compute(rectLeft, rectRight, disparity);
            return disparity;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 私有方法 — 立体校正
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 执行双目立体校正，返回完整的校正参数（含左右相机内参/畸变、R1/P1/R2/P2 和重映射表）。
    /// 调用方负责 Dispose 所有返回的 Mat 对象。
    /// </summary>
    private static (Mat Q, Mat R1, Mat P1, Mat R2, Mat P2,
        Mat leftCam, Mat leftDist, Mat rightCam, Mat rightDist,
        Mat map1L, Mat map2L, Mat map1R, Mat map2R)
        PerformStereoRectifyFull(Mat leftGray, Mat rightGray, CalibrationResult calib)
    {
        var leftCam = CalibrationService.Array2DToMat(calib.LeftCameraMatrix);
        var leftDist = CalibrationService.Array1DToMat(calib.LeftDistCoeffs);
        var rightCam = CalibrationService.Array2DToMat(calib.RightCameraMatrix);
        var rightDist = CalibrationService.Array1DToMat(calib.RightDistCoeffs);
        using var R = CalibrationService.Array2DToMat(calib.RotationMatrix);
        using var T = CalibrationService.Array1DToMat(calib.TranslationVector);

        var imageSize = new Size(leftGray.Cols, leftGray.Rows);
        var R1 = new Mat(); var R2 = new Mat();
        var P1 = new Mat(); var P2 = new Mat();
        var Q = new Mat();

        Cv2.StereoRectify(leftCam, leftDist, rightCam, rightDist,
            imageSize, R, T, R1, R2, P1, P2, Q,
            StereoRectificationFlags.ZeroDisparity, alpha: 0);

        var map1L = new Mat(); var map2L = new Mat();
        var map1R = new Mat(); var map2R = new Mat();

        Cv2.InitUndistortRectifyMap(leftCam, leftDist, R1, P1,
            imageSize, MatType.CV_32FC1, map1L, map2L);
        Cv2.InitUndistortRectifyMap(rightCam, rightDist, R2, P2,
            imageSize, MatType.CV_32FC1, map1R, map2R);

        return (Q, R1, P1, R2, P2, leftCam, leftDist, rightCam, rightDist,
            map1L, map2L, map1R, map2R);
    }

    /// <summary>
    /// 执行双目立体校正，计算校正变换矩阵和重投影矩阵 Q，
    /// 并生成左右相机的像素重映射表。
    /// </summary>
    /// <param name="leftGray">左相机灰度图像（用于获取图像尺寸）</param>
    /// <param name="rightGray">右相机灰度图像（用于获取图像尺寸）</param>
    /// <param name="calib">双目标定结果</param>
    /// <returns>
    /// 返回元组包含:
    /// - Q: 视差-深度映射矩阵 (4x4)
    /// - map1L/map2L: 左相机 X/Y 方向校正映射表
    /// - map1R/map2R: 右相机 X/Y 方向校正映射表
    /// 调用方负责 Dispose 所有返回的 Mat 对象。
    /// </returns>
    private static (Mat Q, Mat map1L, Mat map2L, Mat map1R, Mat map2R)
        PerformStereoRectify(Mat leftGray, Mat rightGray, CalibrationResult calib)
    {
        // 从标定结果恢复 OpenCV Mat 格式的相机参数
        using var leftCam = CalibrationService.Array2DToMat(calib.LeftCameraMatrix);
        using var leftDist = CalibrationService.Array1DToMat(calib.LeftDistCoeffs);
        using var rightCam = CalibrationService.Array2DToMat(calib.RightCameraMatrix);
        using var rightDist = CalibrationService.Array1DToMat(calib.RightDistCoeffs);
        using var R = CalibrationService.Array2DToMat(calib.RotationMatrix);
        using var T = CalibrationService.Array1DToMat(calib.TranslationVector);

        var imageSize = new Size(leftGray.Cols, leftGray.Rows);

        // ── StereoRectify: 计算 Bouguet 校正变换 ──
        // R1/R2: 左右相机的校正旋转矩阵 (3x3)
        // P1/P2: 左右相机的校正投影矩阵 (3x4)
        // Q: 视差到深度的重投影矩阵 (4x4)
        var R1 = new Mat();
        var R2 = new Mat();
        var P1 = new Mat();
        var P2 = new Mat();
        var Q = new Mat();

        // StereoRectify 末尾有 out Rect 参数用于返回有效像素区域
        Cv2.StereoRectify(
            leftCam, leftDist,
            rightCam, rightDist,
            imageSize,
            R, T,
            R1, R2,
            P1, P2,
            Q,
            StereoRectificationFlags.ZeroDisparity,
            alpha: 0);     // alpha=0: 裁剪到有效区域（无黑边）；不使用 out 参数重载

        // ── InitUndistortRectifyMap: 生成校正像素重映射表 ──
        // 传入 R1/R2 作为校正旋转，P1/P2 作为校正投影矩阵
        var map1L = new Mat();
        var map2L = new Mat();
        var map1R = new Mat();
        var map2R = new Mat();

        Cv2.InitUndistortRectifyMap(
            leftCam, leftDist, R1, P1,
            imageSize, MatType.CV_32FC1,
            map1L, map2L);
        Cv2.InitUndistortRectifyMap(
            rightCam, rightDist, R2, P2,
            imageSize, MatType.CV_32FC1,
            map1R, map2R);

        // 清理中间结果（注意：必须在 InitUndistortRectifyMap 之后才能释放 R1/R2/P1/P2）
        R1.Dispose();
        R2.Dispose();
        P1.Dispose();
        P2.Dispose();

        return (Q, map1L, map2L, map1R, map2R);
    }
}
