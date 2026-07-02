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

        // ── 立体校正（使用缓存，避免每次测距都重算） ──
        var rect = GetRectifyParams(calib);

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
        // fb/d：f=校正焦距(像素)，b=基线(标定单位)，d=视差(像素)
        double disparity = rectLeftX - rectRightX;
        if (disparity <= 0)
            return double.NaN;
        double fx = rect.LeftP.At<double>(0, 0);              // 校正后焦距（像素）
        double baseline = Math.Abs(calib.TranslationVector[0]); // 基线（与标定单位一致）
        var distance = fx * baseline / disparity;

        return await Task.FromResult(distance);
    }

    /// <summary>
    /// 生成全图视差图（CV_32F），同时返回校正参数（含 Q 矩阵）供调用方执行 ReprojectImageTo3D。
    /// 使用 GetRectifyParams 的缓存机制，避免重复执行昂贵的 StereoRectify。
    /// </summary>
    /// <param name="leftImagePath">左图像文件路径</param>
    /// <param name="rightImagePath">右图像文件路径</param>
    /// <param name="calib">双目标定结果</param>
    /// <returns>包含 CV_32F 视差图、Q 矩阵和校正参数的 DisparityResult</returns>
    public DisparityResult GenerateDisparity(string leftImagePath, string rightImagePath, StereoParameters calib)
    {
        // ── 参数验证 ──
        if (!File.Exists(leftImagePath))
            throw new ArgumentException($"左图像文件不存在: {leftImagePath}", nameof(leftImagePath));
        if (!File.Exists(rightImagePath))
            throw new ArgumentException($"右图像文件不存在: {rightImagePath}", nameof(rightImagePath));

        // ── 加载灰度图 ──
        using var leftGray = new Mat(leftImagePath, ImreadModes.Grayscale);
        using var rightGray = new Mat(rightImagePath, ImreadModes.Grayscale);

        if (leftGray.Empty())
            throw new InvalidOperationException($"左图像加载失败: {leftImagePath}");
        if (rightGray.Empty())
            throw new InvalidOperationException($"右图像加载失败: {rightImagePath}");

        // ── 立体校正（带缓存）──
        var rect = GetRectifyParams(calib);

        using var rectLeft = new Mat();
        using var rectRight = new Mat();
        Cv2.Remap(leftGray, rectLeft, rect.LeftMapX, rect.LeftMapY, InterpolationFlags.Linear);
        Cv2.Remap(rightGray, rectRight, rect.RightMapX, rect.RightMapY, InterpolationFlags.Linear);

        // ── SGBM 视差计算 ──
        using var sgbm = StereoSGBM.Create(
            SgbmMinDisparity, SgbmNumDisparities, SgbmBlockSize,
            8 * 3 * SgbmBlockSize * SgbmBlockSize,
            32 * 3 * SgbmBlockSize * SgbmBlockSize,
            1, 63, 10, 100, 32, 0);

        using var disparity16 = new Mat();
        sgbm.Compute(rectLeft, rectRight, disparity16);

        // 将 CV_16SC1 视差图转为 CV_32F，同时除以 16 得到实际视差值
        var disparity32F = new Mat();
        disparity16.ConvertTo(disparity32F, MatType.CV_32F, 1.0 / 16.0);

        // 克隆 Q 矩阵（不持有对缓存的引用，允许调用方独立 Dispose）
        return new DisparityResult(disparity32F, rect.Q.Clone());
    }

    /// <summary>
    /// 将 CV_32F 视差图归一化为 CV_8UC1 灰度图（0-255）。
    /// 视差值 ≤0 的区域映射为 0（黑色），有效区域按 MinMax 归一化。
    /// </summary>
    /// <param name="disparity32F">CV_32F 视差图</param>
    /// <returns>CV_8UC1 归一化灰度图</returns>
    public static Mat NormalizeDisparityTo8U(Mat disparity32F)
    {
        var normalized = new Mat();
        Cv2.Normalize(disparity32F, normalized, 0, 255, NormTypes.MinMax, (int)MatType.CV_8UC1);

        return normalized;
    }

    /// <summary>
    /// 对 CV_8UC1 灰度视差图应用伪彩色映射（Jet 色图），
    /// 输出 CV_8UC3 BGR 彩色图像，用于前端渲染更直观的深度视觉效果。
    /// </summary>
    /// <param name="gray8U">CV_8UC1 灰度视差图</param>
    /// <returns>CV_8UC3 伪彩色图像</returns>
    public static Mat ApplyColorMap(Mat gray8U)
    {
        var colorMat = new Mat();
        Cv2.ApplyColorMap(gray8U, colorMat, ColormapTypes.Jet);
        return colorMat;
    }

    /// <summary>
    /// 从 CV_32FC3 XYZ 空间坐标中提取 Z 深度通道，归一化为 CV_8UC1 灰度图。
    /// 无效深度（≤0 或 NaN）映射为黑色（0），有效深度按 MinMax 缩放到 0-255。
    /// </summary>
    /// <param name="xyz">ReprojectImageTo3D 输出的 CV_32FC3 空间坐标</param>
    /// <returns>CV_8UC1 归一化深度灰度图</returns>
    public static Mat ExtractZ(Mat xyz)
    {
        // 提取 Z 深度通道（CV_32FC3 → CV_32FC1，通道索引 2）
        using var zMat = new Mat();
        Cv2.ExtractChannel(xyz, zMat, 2);

        return zMat;
    }

    /// <summary>
    /// 将 CV_32F 视差图保存为 TIFF 文件（支持 32 位浮点精度）。
    /// </summary>
    /// <param name="disparity32F">CV_32F 视差图</param>
    /// <param name="filePath">保存路径（建议 .tiff 或 .tif 扩展名）</param>
    public static void SaveDisparity32F(Mat disparity32F, string filePath)
    {
        Cv2.ImWrite(filePath, disparity32F);
    }

    /// <summary>
    /// 将 CV_32FC3 XYZ 空间坐标图保存为 TIFF 文件（支持 32 位浮点精度）。
    /// </summary>
    /// <param name="xyz">CV_32FC3 XYZ 空间坐标图</param>
    /// <param name="filePath">保存路径（建议 .tiff 或 .tif 扩展名）</param>
    public static void SaveXYZ32F(Mat xyz, string filePath)
    {
        Cv2.ImWrite(filePath, xyz);
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
}
