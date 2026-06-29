using System.IO;
using CameraCali.Models;
using OpenCvSharp;

namespace CameraCali.Services;

/// <summary>
/// 双目相机标定服务实现。
/// 使用 OpenCV 棋盘格标定算法，依次完成图像分离、角点检测、
/// 单目标定和双目立体标定。标定结果通过 OpenCV FileStorage（YAML）持久化存储。
/// 角点检测失败的图片对自动跳过，不中断整个标定流程。
/// </summary>
public class CalibrationService
{

    // ═══════════════════════════════════════════════════════════════════
    // 公有方法
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<CalibrationResult> CalibrateAsync(
        IEnumerable<string> imagePaths,
        ChessboardConfig config,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var paths = imagePaths.ToList();
        if (paths.Count == 0)
            throw new ArgumentException("标定图片路径列表不能为空", nameof(imagePaths));
        if (paths.Count % 2 != 0)
            throw new ArgumentException(
                $"标定图片必须成对出现（左右各一张），当前共 {paths.Count} 张，无法配对",
                nameof(imagePaths));

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            // ── 1. 分离左右图像 ──
            var (leftPaths, rightPaths) = SeparateStereoImages(paths);
            int totalPairs = leftPaths.Count;

            // ── 2. 角点检测（逐个处理，跳过失败对） ──
            progress?.Report(0);
            var leftCornersList = new List<Point2f[]>(totalPairs);
            var rightCornersList = new List<Point2f[]>(totalPairs);
            Size imageSize = default;

            for (int i = 0; i < totalPairs; i++)
            {
                ct.ThrowIfCancellationRequested();
                bool leftOk = TryDetectCorners(leftPaths[i], config, out var lc, out var sz);
                bool rightOk = TryDetectCorners(rightPaths[i], config, out var rc, out _);

                if (leftOk && rightOk)
                {
                    leftCornersList.Add(lc);
                    rightCornersList.Add(rc);
                    if (imageSize == default)
                        imageSize = sz;
                }
                // 静默跳过失败对，不中断整个标定流程

                // 角点检测阶段：0% → 30%
                progress?.Report((int)(30.0 * (i + 1) / totalPairs));
            }

            int validPairs = leftCornersList.Count;
            int skippedPairs = totalPairs - validPairs;

            // 最少需要 3 对有效图片才能进行立体标定
            if (validPairs < 3)
                throw new InvalidOperationException(
                    $"标定失败：{totalPairs} 对图片中仅 {validPairs} 对检测到棋盘格角点，" +
                    $"至少需要 3 对有效图片。请检查图片质量或棋盘格配置。");

            // ── 3. 构建棋盘格世界坐标（基于有效配对数量） ──
            var objectPointsList = BuildObjectPointsList(config, validPairs);

            // ── 4-5. 单目标定 + 立体标定 ──
            var leftCam = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var leftDist = Mat.Zeros(5, 1, MatType.CV_64FC1).ToMat();
            var rightCam = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var rightDist = Mat.Zeros(5, 1, MatType.CV_64FC1).ToMat();
            // 将点集转换为 Mat 数组以匹配 OpenCvSharp4 API
            var objMats = objectPointsList.Select(pts => Point3fListToMat(pts)).ToArray();
            var leftImgMats = leftCornersList.Select(pts => Point2fArrayToMat(pts)).ToArray();
            var rightImgMats = rightCornersList.Select(pts => Point2fArrayToMat(pts)).ToArray();

            double leftRms = Cv2.CalibrateCamera(objMats, leftImgMats, imageSize, leftCam, leftDist,
                out var rvecsL, out var tvecsL);
            progress?.Report(50);

            double rightRms = Cv2.CalibrateCamera(objMats, rightImgMats, imageSize, rightCam, rightDist,
                out var rvecsR, out var tvecsR);

            // 左右相机平均 RMS 重投影误差（像素）
            double reprojectionError = Math.Sqrt((leftRms * leftRms + rightRms * rightRms) / 2.0);
            progress?.Report(70);

            // 清理单目标定的中间结果
            foreach (var rv in rvecsL) rv.Dispose();
            foreach (var tv in tvecsL) tv.Dispose();
            foreach (var rv in rvecsR) rv.Dispose();
            foreach (var tv in tvecsR) tv.Dispose();

            // 双目立体标定
            using var R = new Mat();
            using var T = new Mat();
            using var E = new Mat();
            using var F = new Mat();
            // 将 Mat[] 显式转换为 InputArray（OpenCvSharp4 StereoCalibrate 要求）
            Cv2.StereoCalibrate(
                objMats.Select(m => (InputArray)m),
                leftImgMats.Select(m => (InputArray)m),
                rightImgMats.Select(m => (InputArray)m),
                leftCam, leftDist, rightCam, rightDist,
                imageSize, R, T, E, F,
                flags: CalibrationFlags.FixIntrinsic);
            progress?.Report(100);

            // ── 6. 封装结果 ──
            var result = new CalibrationResult
            {
                LeftCameraMatrix = MatToArray2D(leftCam),
                LeftDistCoeffs = MatToArray1D(leftDist),
                RightCameraMatrix = MatToArray2D(rightCam),
                RightDistCoeffs = MatToArray1D(rightDist),
                RotationMatrix = MatToArray2D(R),
                TranslationVector = MatToArray1D(T),
                ImageWidth = imageSize.Width,
                ImageHeight = imageSize.Height,
                SkippedPairCount = skippedPairs,     // 记录跳过的图片对数量
                ReprojectionError = reprojectionError
            };

            leftCam.Dispose();
            leftDist.Dispose();
            rightCam.Dispose();
            rightDist.Dispose();

            // 清理点集 Mat
            foreach (var m in objMats) m.Dispose();
            foreach (var m in leftImgMats) m.Dispose();
            foreach (var m in rightImgMats) m.Dispose();

            return result;
        }, ct);
    }

    /// <inheritdoc/>
    public async Task<CalibrationResult> CalibrateFromCachedAsync(
        List<CachedStereoPair> pairs,
        ChessboardConfig config,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (pairs.Count < 3)
            throw new InvalidOperationException(
                $"至少需要 3 对有效图片，当前仅 {pairs.Count} 对。");

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            int validPairs = pairs.Count;
            progress?.Report(0);

            // ── 1. 从缓存提取角点 + 图像尺寸 ──
            var leftCornersList = pairs.Select(p => p.LeftCorners).ToList();
            var rightCornersList = pairs.Select(p => p.RightCorners).ToList();
            Size imageSize = pairs[0].ImageSize;

            // ── 2. 构建棋盘格世界坐标 ──
            var objectPointsList = BuildObjectPointsList(config, validPairs);

            // ── 3-4. 单目标定 ──
            var leftCam = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var leftDist = Mat.Zeros(5, 1, MatType.CV_64FC1).ToMat();
            var rightCam = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var rightDist = Mat.Zeros(5, 1, MatType.CV_64FC1).ToMat();

            var objMats = objectPointsList.Select(pts => Point3fListToMat(pts)).ToArray();
            var leftImgMats = leftCornersList.Select(pts => Point2fArrayToMat(pts)).ToArray();
            var rightImgMats = rightCornersList.Select(pts => Point2fArrayToMat(pts)).ToArray();

            double leftRms = Cv2.CalibrateCamera(objMats, leftImgMats, imageSize,
                leftCam, leftDist, out var rvecsL, out var tvecsL);
            progress?.Report(40);

            ct.ThrowIfCancellationRequested();
            double rightRms = Cv2.CalibrateCamera(objMats, rightImgMats, imageSize,
                rightCam, rightDist, out var rvecsR, out var tvecsR);
            progress?.Report(60);

            double reprojectionError = Math.Sqrt(
                (leftRms * leftRms + rightRms * rightRms) / 2.0);

            // 清理单目标定的中间结果
            foreach (var rv in rvecsL) rv.Dispose();
            foreach (var tv in tvecsL) tv.Dispose();
            foreach (var rv in rvecsR) rv.Dispose();
            foreach (var tv in tvecsR) tv.Dispose();

            // ── 5. 双目立体标定 ──
            using var R = new Mat();
            using var T = new Mat();
            using var E = new Mat();
            using var F = new Mat();
            Cv2.StereoCalibrate(
                objMats.Select(m => (InputArray)m),
                leftImgMats.Select(m => (InputArray)m),
                rightImgMats.Select(m => (InputArray)m),
                leftCam, leftDist, rightCam, rightDist,
                imageSize, R, T, E, F,
                flags: CalibrationFlags.FixIntrinsic);
            progress?.Report(100);

            // ── 6. 封装结果 ──
            var result = new CalibrationResult
            {
                LeftCameraMatrix = MatToArray2D(leftCam),
                LeftDistCoeffs = MatToArray1D(leftDist),
                RightCameraMatrix = MatToArray2D(rightCam),
                RightDistCoeffs = MatToArray1D(rightDist),
                RotationMatrix = MatToArray2D(R),
                TranslationVector = MatToArray1D(T),
                ImageWidth = imageSize.Width,
                ImageHeight = imageSize.Height,
                SkippedPairCount = 0,
                ReprojectionError = reprojectionError
            };

            leftCam.Dispose();
            leftDist.Dispose();
            rightCam.Dispose();
            rightDist.Dispose();

            foreach (var m in objMats) m.Dispose();
            foreach (var m in leftImgMats) m.Dispose();
            foreach (var m in rightImgMats) m.Dispose();

            return result;
        }, ct);
    }

    /// <inheritdoc/>
    public void SaveCalibrationResult(string filePath, CalibrationResult result)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 将 double[][] 转换为 OpenCV Mat，通过 FileStorage 写入 YAML
        using var leftCam = Array2DToMat(result.LeftCameraMatrix);
        using var rightCam = Array2DToMat(result.RightCameraMatrix);
        using var leftDist = Array1DToMat(result.LeftDistCoeffs);
        using var rightDist = Array1DToMat(result.RightDistCoeffs);
        using var R = Array2DToMat(result.RotationMatrix);
        using var T = Array1DToMat(result.TranslationVector);

        using var fs = new FileStorage(filePath, FileStorage.Modes.Write);
        fs.Write("LeftCameraMatrix", leftCam);
        fs.Write("RightCameraMatrix", rightCam);
        fs.Write("LeftDistCoeffs", leftDist);
        fs.Write("RightDistCoeffs", rightDist);
        fs.Write("RotationMatrix", R);
        fs.Write("TranslationVector", T);
        fs.Write("ImageWidth", result.ImageWidth);
        fs.Write("ImageHeight", result.ImageHeight);
        fs.Write("SkippedPairCount", result.SkippedPairCount);
        fs.Write("ReprojectionError", result.ReprojectionError);
    }

    /// <inheritdoc/>
    public CalibrationResult LoadCalibrationResult(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"标定文件未找到: {filePath}", filePath);

        using var fs = new FileStorage(filePath, FileStorage.Modes.Read);
        if (!fs.IsOpened())
            throw new InvalidOperationException($"无法打开标定文件: {filePath}");

        using var leftCam = fs["LeftCameraMatrix"].ReadMat();
        using var rightCam = fs["RightCameraMatrix"].ReadMat();
        using var leftDist = fs["LeftDistCoeffs"].ReadMat();
        using var rightDist = fs["RightDistCoeffs"].ReadMat();
        using var R = fs["RotationMatrix"].ReadMat();
        using var T = fs["TranslationVector"].ReadMat();

        return new CalibrationResult
        {
            LeftCameraMatrix = MatToArray2D(leftCam),
            RightCameraMatrix = MatToArray2D(rightCam),
            LeftDistCoeffs = MatToArray1D(leftDist),
            RightDistCoeffs = MatToArray1D(rightDist),
            RotationMatrix = MatToArray2D(R),
            TranslationVector = MatToArray1D(T),
            ImageWidth = fs["ImageWidth"].ReadInt(),
            ImageHeight = fs["ImageHeight"].ReadInt(),
            SkippedPairCount = fs["SkippedPairCount"].ReadInt(),
            ReprojectionError = fs["ReprojectionError"].ReadDouble()
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // 私有方法 — 图像分离
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 按文件名自动区分左右相机图像。
    /// 策略：先尝试文件名关键词匹配（left/right），失败则交替分配。
    /// </summary>
    private static (List<string> Left, List<string> Right) SeparateStereoImages(List<string> paths)
    {
        paths.Sort(StringComparer.OrdinalIgnoreCase);

        string[] leftKw = ["left", "_l.", "_l_", "左侧", "左相机"];
        string[] rightKw = ["right", "_r.", "_r_", "右侧", "右相机"];

        bool ContainsAny(string path, string[] keywords) =>
            keywords.Any(k => Path.GetFileNameWithoutExtension(path)
                .Contains(k, StringComparison.OrdinalIgnoreCase));

        var leftMatches = paths.Where(p => ContainsAny(p, leftKw)).ToList();
        var rightMatches = paths.Where(p => ContainsAny(p, rightKw)).ToList();

        if (leftMatches.Count > 0 && rightMatches.Count > 0
            && leftMatches.Count + rightMatches.Count == paths.Count
            && leftMatches.Count == rightMatches.Count)
        {
            return (leftMatches, rightMatches);
        }

        // 回退：交替分配
        var left = new List<string>();
        var right = new List<string>();
        for (int i = 0; i < paths.Count; i++)
        {
            if (i % 2 == 0) left.Add(paths[i]);
            else right.Add(paths[i]);
        }

        if (left.Count != right.Count)
            throw new ArgumentException(
                $"无法将 {paths.Count} 张图片平均分配为左右两组。请确保文件名为偶数且可配对。");

        return (left, right);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 私有方法 — 棋盘格世界坐标构建
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 构建所有图像对共享的棋盘格角点世界坐标（3D）。
    /// Z=0，X 向右，Y 向下。
    /// </summary>
    private static List<List<Point3f>> BuildObjectPointsList(ChessboardConfig config, int pairCount)
    {
        var singlePattern = new List<Point3f>(config.PatternWidth * config.PatternHeight);
        for (int row = 0; row < config.PatternHeight; row++)
            for (int col = 0; col < config.PatternWidth; col++)
                singlePattern.Add(new Point3f(
                    col * (float)config.SquareSize,
                    row * (float)config.SquareSize,
                    0f));

        var result = new List<List<Point3f>>(pairCount);
        for (int i = 0; i < pairCount; i++)
            result.Add(new List<Point3f>(singlePattern));

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 公有方法 — 角点检测（供弹窗 ViewModel 调用）
    /// <summary>检测棋盘格角点并进行亚像素精化。成功返回 true。</summary>
    public bool TryDetectCorners(
        string imagePath, ChessboardConfig config,
        out Point2f[] corners, out Size imageSize)
    {
        corners = [];
        imageSize = default;

        using var gray = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
        if (gray.Empty()) return false;

        imageSize = new Size(gray.Cols, gray.Rows);

        if (!Cv2.FindChessboardCorners(gray, new Size(config.PatternWidth, config.PatternHeight),
                out corners, ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage))
            return false;

        Cv2.CornerSubPix(gray, corners, new Size(11, 11), new Size(-1, -1),
            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.001));

        return corners.Length == config.PatternWidth * config.PatternHeight;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 私有方法 — Mat ↔ 数组转换
    /// <summary>将 Point3f 列表转为单列 Mat（CV_32FC3）</summary>
    private static Mat Point3fListToMat(List<Point3f> points)
    {
        var mat = new Mat(points.Count, 1, MatType.CV_32FC3);
        for (int i = 0; i < points.Count; i++)
            mat.Set(i, 0, points[i]);
        return mat;
    }

    /// <summary>将 Point2f 数组转为单列 Mat（CV_32FC2）</summary>
    private static Mat Point2fArrayToMat(Point2f[] points)
    {
        var mat = new Mat(points.Length, 1, MatType.CV_32FC2);
        for (int i = 0; i < points.Length; i++)
            mat.Set(i, 0, points[i]);
        return mat;
    }

    // ═══════════════════════════════════════════════════════════════════

    /// <summary>将 Mat 展平为一维 double[]</summary>
    private static double[] MatToArray1D(Mat mat)
    {
        var result = new double[mat.Rows * mat.Cols];
        int idx = 0;
        for (int r = 0; r < mat.Rows; r++)
            for (int c = 0; c < mat.Cols; c++)
                result[idx++] = mat.Get<double>(r, c);
        return result;
    }

    /// <summary>将 Mat 转换为二维 double[][]</summary>
    private static double[][] MatToArray2D(Mat mat)
    {
        var result = new double[mat.Rows][];
        for (int r = 0; r < mat.Rows; r++)
        {
            result[r] = new double[mat.Cols];
            for (int c = 0; c < mat.Cols; c++)
                result[r][c] = mat.Get<double>(r, c);
        }
        return result;
    }

    /// <summary>将 double[][] 转换回 Mat</summary>
    internal static Mat Array2DToMat(double[][] array)
    {
        int rows = array.Length;
        int cols = rows > 0 ? array[0].Length : 0;
        var mat = new Mat(rows, cols, MatType.CV_64FC1);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                mat.Set(r, c, array[r][c]);
        return mat;
    }

    /// <summary>将 double[] 转换回 Mat 列向量</summary>
    internal static Mat Array1DToMat(double[] array)
    {
        var mat = new Mat(array.Length, 1, MatType.CV_64FC1);
        for (int i = 0; i < array.Length; i++)
            mat.Set(i, 0, array[i]);
        return mat;
    }
}
