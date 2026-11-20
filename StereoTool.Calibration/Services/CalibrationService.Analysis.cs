using System.IO;
using System.Text.RegularExpressions;
using OpenCvSharp;
using StereoTool.Calibration.Models;
using StereoTool.Calibration.Utils;

namespace StereoTool.Calibration.Services;

/// <summary>标定服务中的图片分析、误差计算与预览功能。</summary>
public partial class CalibrationService
{
    /// <summary>读取文件夹图片并进行自然排序。</summary>
    /// <param name="folder">图片文件夹。</param>
    /// <returns>完整文件路径。</returns>
    public List<string> GetImagePaths(string folder)
    {
        string[] extensions = [".bmp", ".jpg", ".jpeg", ".png", ".tiff", ".tif"];
        return Directory.EnumerateFiles(folder)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => Regex.Replace(Path.GetFileName(path), @"\d+", match => match.Value.PadLeft(12, '0')),
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>按文件名去除 left/right 前缀后配对，保留缺图记录，歧义命名直接报错。</summary>
    /// <param name="leftPaths">左图路径。</param>
    /// <param name="rightPaths">右图路径。</param>
    /// <returns>图片对列表。</returns>
    public List<DetectedImagePair> PairImages(IEnumerable<string> leftPaths, IEnumerable<string> rightPaths)
    {
        var left = leftPaths.ToDictionary(GetPairKey, StringComparer.OrdinalIgnoreCase);
        var right = rightPaths.ToDictionary(GetPairKey, StringComparer.OrdinalIgnoreCase);
        var keys = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => Regex.Replace(key, @"\d+", match => match.Value.PadLeft(12, '0')),
                StringComparer.OrdinalIgnoreCase);
        return keys.Select(key => new DetectedImagePair
        {
            LeftPath = left.GetValueOrDefault(key, ""),
            RightPath = right.GetValueOrDefault(key, ""),
            DetectionStatus = left.ContainsKey(key) && right.ContainsKey(key) ? "待检测" : "缺少对应图",
            IsIncluded = left.ContainsKey(key) && right.ContainsKey(key)
        }).ToList();
    }

    /// <summary>获取配对键，支持 left01/right01、left_01/right_01 和同名双目录。</summary>
    /// <param name="path">图片路径。</param>
    /// <returns>去除相机前缀及数字前导零的键。</returns>
    private static string GetPairKey(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        name = Regex.Replace(name, @"^(left|right)[_\-\s]*", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"\d+", match => match.Value.TrimStart('0').PadLeft(1, '0'));
    }

    /// <summary>分析单目图片，保留未检出和尺寸错误的记录。</summary>
    /// <param name="paths">图片路径快照。</param>
    /// <param name="config">棋盘格配置。</param>
    /// <param name="progress">已处理数量。</param>
    /// <param name="ct">图片之间生效的取消令牌。</param>
    /// <returns>全部检测记录。</returns>
    public List<DetectedImage> AnalyzeImages(IEnumerable<string> paths, ChessboardConfig config,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ValidateConfig(config);
        var results = new List<DetectedImage>();
        Size? expectedSize = null;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var found = TryDetectCorners(path, config, out var corners, out var size);
            var status = found ? "检测成功" : "未检测到完整棋盘格";
            if (size.Width == 0)
                status = "图片无法读取";
            if (found && expectedSize != null && size != expectedSize.Value)
            {
                found = false;
                status = "图像尺寸与数据集不一致";
            }
            if (found)
                expectedSize ??= size;
            results.Add(new DetectedImage
            {
                ImagePath = path,
                ImageSize = size,
                Corners = found ? corners : [],
                DetectionStatus = status,
                IsIncluded = found
            });
            progress?.Report(results.Count);
        }
        return results;
    }

    /// <summary>分析图片对并保留失败诊断，检测过程中不改动界面绑定对象。</summary>
    /// <param name="pairs">待检测图片对快照。</param>
    /// <param name="config">棋盘格配置。</param>
    /// <param name="progress">已处理对数。</param>
    /// <param name="ct">图片对之间生效的取消令牌。</param>
    /// <returns>全部检测记录。</returns>
    public List<DetectedImagePair> AnalyzePairs(IEnumerable<DetectedImagePair> pairs, ChessboardConfig config,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ValidateConfig(config);
        var results = new List<DetectedImagePair>();
        Size? expectedSize = null;
        foreach (var source in pairs)
        {
            ct.ThrowIfCancellationRequested();
            var pair = new DetectedImagePair
            {
                LeftPath = source.LeftPath,
                RightPath = source.RightPath
            };
            results.Add(pair);
            if (string.IsNullOrEmpty(pair.LeftPath) || string.IsNullOrEmpty(pair.RightPath))
            {
                pair.IsIncluded = false;
                pair.DetectionStatus = "缺少对应图";
                progress?.Report(results.Count);
                continue;
            }
            var leftOk = TryDetectCorners(pair.LeftPath, config, out var leftCorners, out var leftSize);
            var rightOk = TryDetectCorners(pair.RightPath, config, out var rightCorners, out var rightSize);
            pair.ImageSize = leftSize;
            pair.DetectionStatus = "检测成功";
            if (!leftOk || !rightOk)
                pair.DetectionStatus = !leftOk && !rightOk ? "左右图均未检测成功" : (!leftOk ? "左图未检测成功" : "右图未检测成功");
            if (leftSize != rightSize)
                pair.DetectionStatus = "左右图像尺寸不同";
            if (leftOk && rightOk && expectedSize != null && leftSize != expectedSize.Value)
                pair.DetectionStatus = "图像尺寸与数据集不一致";
            pair.IsIncluded = pair.DetectionStatus == "检测成功";
            if (pair.IsIncluded)
            {
                expectedSize ??= leftSize;
                pair.LeftCorners = leftCorners;
                pair.RightCorners = rightCorners;
            }
            progress?.Report(results.Count);
        }
        return results;
    }

    /// <summary>生成针孔畸变模型标志，默认只估计 k1、k2。</summary>
    /// <param name="config">畸变选项。</param>
    /// <returns>OpenCV 标定标志。</returns>
    private static CalibrationFlags GetCalibrationFlags(ChessboardConfig config)
    {
        var flags = config.EstimateK3 ? CalibrationFlags.None : CalibrationFlags.FixK3;
        if (!config.EstimateTangentialDistortion)
            flags |= CalibrationFlags.ZeroTangentDist;
        return flags;
    }

    /// <summary>检查标定所需的内角点及物理尺度。</summary>
    /// <param name="config">棋盘格配置。</param>
    private static void ValidateConfig(ChessboardConfig config)
    {
        if (config.CornerCols < 2 || config.CornerRows < 2 || !double.IsFinite(config.SquareSize) || config.SquareSize <= 0)
            throw new InvalidOperationException("内角点行列数至少为 2，格子尺寸必须为正数（mm）。");
    }

    /// <summary>检查标定所需的数量、统一图片尺寸和角点数。</summary>
    /// <param name="config">棋盘格配置。</param>
    /// <param name="sizes">图片尺寸。</param>
    /// <param name="counts">角点数量。</param>
    private static void ValidateDataset(ChessboardConfig config, IEnumerable<Size> sizes, IEnumerable<int> counts)
    {
        ValidateConfig(config);
        var imageSizes = sizes.ToList();
        if (imageSizes.Count < 3)
            throw new InvalidOperationException("至少需要 3 张有效图片或 3 对有效图片。");
        if (imageSizes.Distinct().Count() != 1)
            throw new InvalidOperationException("参与标定的图片尺寸必须一致。");
        if (counts.Any(count => count != config.CornerCols * config.CornerRows))
            throw new InvalidOperationException("角点缓存与棋盘格配置不同，请重新检测。");
    }

    /// <summary>计算角点欧氏距离均值，与 RMS 分开展示。</summary>
    /// <param name="observed">检测点。</param>
    /// <param name="projected">模型投影点。</param>
    /// <returns>平均距离（像素）。</returns>
    private static double GetMeanError(Point2f[] observed, Point2f[] projected)
    {
        return observed.Select((point, i) =>
            Math.Sqrt(Math.Pow(point.X - projected[i].X, 2) + Math.Pow(point.Y - projected[i].Y, 2))).Average();
    }

    /// <summary>计算所有角点重投影距离中的最大值。</summary>
    /// <param name="observed">检测点。</param>
    /// <param name="projected">模型投影点。</param>
    /// <returns>最大欧氏距离，单位为像素。</returns>
    private static double GetMaxError(Point2f[] observed, Point2f[] projected)
    {
        return observed.Select((point, i) =>
            Math.Sqrt(Math.Pow(point.X - projected[i].X, 2) + Math.Pow(point.Y - projected[i].Y, 2))).Max();
    }

    /// <summary>拟合每对图片的共同棋盘格位姿后更新逐对误差。</summary>
    /// <param name="config">棋盘格配置。</param>
    /// <param name="pairs">参与标定的图片对快照。</param>
    /// <param name="calib">双目标定结果。</param>
    private void UpdateStereoErrors(ChessboardConfig config, List<DetectedImagePair> pairs, StereoCalibrationParams calib)
    {
        var objects = CreateWorldPoints(config);
        foreach (var pair in pairs)
        {
            var projected = FitStereoBoardPose(objects, pair, calib);
            pair.LeftProjectedCorners = projected.Left;
            pair.RightProjectedCorners = projected.Right;
            pair.LeftMeanError = GetMeanError(pair.LeftCorners, projected.Left);
            pair.RightMeanError = GetMeanError(pair.RightCorners, projected.Right);
            pair.MeanError = (pair.LeftMeanError + pair.RightMeanError) / 2;
            pair.MaxError = Math.Max(
                GetMaxError(pair.LeftCorners, projected.Left),
                GetMaxError(pair.RightCorners, projected.Right));
        }
        calib.MeanReprojectionError = pairs.Average(pair => pair.MeanError);
    }

    /// <summary>固定双目参数，以阻尼最小二乘拟合六自由度棋盘格位姿。</summary>
    /// <param name="objects">棋盘格三维点。</param>
    /// <param name="pair">左右观测点。</param>
    /// <param name="calib">双目参数。</param>
    /// <returns>左右图上的共同位姿投影。</returns>
    private static (Point2f[] Left, Point2f[] Right) FitStereoBoardPose(List<Point3f> objects,
        DetectedImagePair pair, StereoCalibrationParams calib)
    {
        var rvec = new double[3];
        var tvec = new double[3];
        Cv2.SolvePnP(objects, pair.LeftCorners, calib.LeftCameraMatrix, calib.LeftDistCoeffs,
            ref rvec, ref tvec);
        var pose = rvec.Concat(tvec).ToArray();
        var observed = pair.LeftCorners.Concat(pair.RightCorners).ToArray();
        var damping = 0.001;
        // 左右图共享同一位姿，避免独立 PnP 掩盖错误配对。
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var current = ProjectStereoBoard(objects, pose, calib);
            var projected = current.Left.Concat(current.Right).ToArray();
            var residual = new double[observed.Length * 2];
            using var jacobian = new Mat(residual.Length, 6, MatType.CV_64F);
            for (var i = 0; i < observed.Length; i++)
            {
                residual[2 * i] = observed[i].X - projected[i].X;
                residual[2 * i + 1] = observed[i].Y - projected[i].Y;
            }
            for (var column = 0; column < 6; column++)
            {
                var step = column < 3 ? 1e-4 : Math.Max(1e-3, Math.Abs(pose[column]) * 1e-5);
                var shifted = (double[])pose.Clone();
                shifted[column] += step;
                var shiftedProjection = ProjectStereoBoard(objects, shifted, calib);
                var points = shiftedProjection.Left.Concat(shiftedProjection.Right).ToArray();
                for (var row = 0; row < points.Length; row++)
                {
                    jacobian.Set(2 * row, column, (points[row].X - projected[row].X) / step);
                    jacobian.Set(2 * row + 1, column, (points[row].Y - projected[row].Y) / step);
                }
            }
            using var transpose = jacobian.T().ToMat();
            using var normal = (transpose * jacobian).ToMat();
            for (var axis = 0; axis < 6; axis++)
                normal.Set(axis, axis, normal.At<double>(axis, axis) * (1 + damping) + 1e-9);
            using var residualMat = residual.ToMat();
            using var rhs = (transpose * residualMat).ToMat();
            using var delta = new Mat();
            Cv2.Solve(normal, rhs, delta, DecompTypes.SVD);
            var candidate = pose.Select((value, axis) => value + delta.At<double>(axis)).ToArray();
            var next = ProjectStereoBoard(objects, candidate, calib);
            var nextPoints = next.Left.Concat(next.Right).ToArray();
            var oldCost = residual.Sum(value => value * value);
            var newCost = observed.Select((point, i) =>
                Math.Pow(point.X - nextPoints[i].X, 2) + Math.Pow(point.Y - nextPoints[i].Y, 2)).Sum();
            if (newCost >= oldCost)
            {
                damping *= 10;
                continue;
            }
            pose = candidate;
            damping = Math.Max(damping / 10, 1e-9);
            if (oldCost - newCost < 1e-8)
                break;
        }
        return ProjectStereoBoard(objects, pose, calib);
    }

    /// <summary>通过 Xright = R·Xleft + T 投影同一块棋盘格。</summary>
    /// <param name="objects">标定板坐标。</param>
    /// <param name="pose">左相机下的 Rodrigues 旋转和平移。</param>
    /// <param name="calib">双目参数。</param>
    /// <returns>左右投影。</returns>
    private static (Point2f[] Left, Point2f[] Right) ProjectStereoBoard(List<Point3f> objects,
        double[] pose, StereoCalibrationParams calib)
    {
        var rvec = pose[..3];
        var tvec = pose[3..];
        Cv2.ProjectPoints(objects, rvec, tvec, calib.LeftCameraMatrix, calib.LeftDistCoeffs, out var left, out _);
        using var leftRvec = rvec.ToMat();
        using var leftRotation = new Mat();
        Cv2.Rodrigues(leftRvec, leftRotation);
        using var stereoRotation = calib.RotationMatrix.ToMat();
        using var rightRotation = (stereoRotation * leftRotation).ToMat();
        using var rightRvec = new Mat();
        Cv2.Rodrigues(rightRotation, rightRvec);
        var rightTranslation = new double[3];
        for (var row = 0; row < 3; row++)
        {
            rightTranslation[row] = calib.TranslationVector[row];
            for (var col = 0; col < 3; col++)
                rightTranslation[row] += calib.RotationMatrix[row, col] * tvec[col];
        }
        Cv2.ProjectPoints(objects, rightRvec.ToDoubleArray(), rightTranslation,
            calib.RightCameraMatrix, calib.RightDistCoeffs, out var right, out _);
        return (left, right);
    }

    /// <summary>生成角点/投影点叠加或去畸变图，返回 Mat 由调用者释放。</summary>
    /// <param name="path">图片路径。</param>
    /// <param name="corners">检测点。</param>
    /// <param name="projected">投影点，绘为红色十字。</param>
    /// <param name="config">棋盘格配置。</param>
    /// <param name="calib">不为空时生成去畸变图。</param>
    /// <returns>预览图。</returns>
    public Mat CreatePreview(string path, Point2f[] corners, Point2f[] projected,
        ChessboardConfig config, CalibrationParams? calib = null)
    {
        using var source = Cv2.ImRead(path, ImreadModes.Color);
        if (source.Empty())
            throw new InvalidOperationException($"无法读取图片：{path}");
        var preview = new Mat();
        if (calib != null)
        {
            using var camera = calib.CameraMatrix.ToMat();
            using var distortion = calib.DistCoeffs.ToMat();
            Cv2.Undistort(source, preview, camera, distortion);
            return preview;
        }
        source.CopyTo(preview);
        if (corners.Length == config.CornerCols * config.CornerRows)
            Cv2.DrawChessboardCorners(preview, new Size(config.CornerCols, config.CornerRows), corners, true);
        foreach (var point in projected)
            Cv2.DrawMarker(preview, new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)),
                Scalar.Red, MarkerTypes.Cross, 8, 1);
        if (corners.Length > 0)
            Cv2.Circle(preview, new Point((int)corners[0].X, (int)corners[0].Y), 8, Scalar.Yellow, 2);
        return preview;
    }
}
