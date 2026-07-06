using System.IO;
using OpenCvSharp;
using StereoTool.Models;
using StereoTool.Utils;

namespace StereoTool.Services;

/// <summary>
/// 双目相机标定服务，基于 OpenCV 棋盘格标定算法实现。
/// </summary>
/// <remarks>
/// 标定流程分五个阶段：
/// <list type="number">
///   <item><b>图像分离</b> — 按文件名关键词（left/right）自动区分左右相机图像，失败则按排序后交替分配。</item>
///   <item><b>角点检测</b> — 对每对图像的左图和右图分别检测棋盘格内角点，并进行亚像素精化。
///         检测失败的图片对会被静默跳过（记录数量），不中断整体流程。</item>
///   <item><b>单目标定</b> — 调用 <c>Cv2.CalibrateCamera</c> 分别估计左、右相机的内参矩阵和畸变系数。</item>
///   <item><b>双目立体标定</b> — 调用 <c>Cv2.StereoCalibrate</c>（FixIntrinsic 模式）求解双目外参 R、T。</item>
///   <item><b>持久化</b> — 通过 OpenCV <c>FileStorage</c> 将标定结果写入 YAML 文件，支持完整读写往返。</item>
/// </list>
/// </remarks>
public class CalibrationService
{
    #region 相机标定

    /// <summary>
    /// 双目标定
    /// </summary>
    /// <param name="chessBoard"></param>
    /// <param name="detectedImagePairs"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public StereoCalibrationParams StereoCalibrate(ChessboardConfig chessBoard, IEnumerable<DetectedImagePair> detectedImagePairs)
    {
        if (detectedImagePairs.Count() < 3)
            throw new InvalidOperationException($"至少需要 3 张有效图片进行单目标定，当前仅 {detectedImagePairs.Count()} 张。");

        var objectPoints = CreateObjectPoints(chessBoard, detectedImagePairs.Count());

        var leftPoints = detectedImagePairs.Select(p => p.LeftCorners.ToList()).ToList();
        var rightPoints = detectedImagePairs.Select(p => p.RightCorners.ToList()).ToList();
        var imageSize = detectedImagePairs.First().ImageSize;

        var leftCameraMatrix = new double[3, 3];
        var leftDistCoeffs = new double[5];
        var rightCameraMatrix = new double[3, 3];
        var rightDistCoeffs = new double[5];

        // 左右相机分别进行单目标定，得到内参矩阵和畸变系数
        var rms1 = Cv2.CalibrateCamera(objectPoints, leftPoints, imageSize, leftCameraMatrix, leftDistCoeffs, out _, out _);
        var rms2 = Cv2.CalibrateCamera(objectPoints, rightPoints, imageSize, rightCameraMatrix, rightDistCoeffs, out _, out _);

        var R = new Mat();
        var T = new Mat();
        var E = new Mat();
        var F = new Mat();

        // 双目立体标定，FixIntrinsic 模式：内参矩阵和畸变系数固定不变，仅求解 R、T、E、F
        var rms = Cv2.StereoCalibrate(objectPoints,
            leftPoints, rightPoints, 
            leftCameraMatrix, leftDistCoeffs, 
            rightCameraMatrix, rightDistCoeffs, 
            imageSize,
            R, T, E, F);

        var result = new StereoCalibrationParams
        {
            LeftCameraMatrix = leftCameraMatrix,
            LeftDistCoeffs = leftDistCoeffs,
            RightCameraMatrix = rightCameraMatrix,
            RightDistCoeffs = rightDistCoeffs,
            RotationMatrix = R.ToDouble2D(),
            TranslationVector = T.ToDoubleArray(),
            EssentialMatrix = E.ToDouble2D(),
            FundamentalMatrix = F.ToDouble2D(),
            ImageWidth = imageSize.Width,
            ImageHeight = imageSize.Height,
            ReprojectionError = rms,
            CornerCols = chessBoard.CornerCols,
            CornerRows = chessBoard.CornerRows,
            SquareSize = chessBoard.SquareSize,
            PixelSizeUm = chessBoard.PixelSizeUm,
        };

        return result;
    }

    /// <summary>
    /// 从已缓存的角点检测结果执行单目相机标定（仅标定单个相机，不涉及双目外参）。
    /// </summary>
    /// <param name="cornersList">每张标定图像已检测的角点坐标列表。</param>
    /// <param name="config">棋盘格标定板配置参数（内角点数、格子物理尺寸）。</param>
    /// <param name="imageSize">标定图像的像素尺寸（所有图像必须一致）。</param>
    /// <param name="progress">进度报告器（0→50→100）。可传 null 忽略。</param>
    /// <param name="ct">协作取消令牌。</param>
    /// <returns>
    /// 包含相机内参矩阵和畸变系数的标定结果。
    /// 仅填充 LeftCameraMatrix 和 LeftDistCoeffs 字段，
    /// 右相机和外参相关字段均为空数组。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// cornersList 有效数量少于 3 时抛出——单目标定至少需要 3 张有效图片。
    /// </exception>
    public CalibrationParams CameraCalibrate(ChessboardConfig chessBoard, IEnumerable<DetectedImage> detectedImages)
    { 
        if(detectedImages.Count() < 3)
            throw new InvalidOperationException($"至少需要 3 张有效图片进行单目标定，当前仅 {detectedImages.Count()} 张。");

        var objectPoints = CreateObjectPoints(chessBoard, detectedImages.Count());

        var imagePoints = detectedImages.Select(p=>p.Corners.ToList()).ToList();
        var imageSize = detectedImages.First().ImageSize;

        var cameraMatrix = new double[3, 3];
        var distCoeffs = new double[5];
        var rms = Cv2.CalibrateCamera(
            objectPoints,
            imagePoints, 
            imageSize, 
            cameraMatrix, 
            distCoeffs, 
            out _, 
            out _);

        var result = new CalibrationParams
        {
            CameraMatrix = cameraMatrix,
            DistCoeffs = distCoeffs,
            ImageWidth = imageSize.Width,
            ImageHeight = imageSize.Height,
            ReprojectionError = rms,
            CornerCols = chessBoard.CornerCols,
            CornerRows = chessBoard.CornerRows,
            SquareSize = chessBoard.SquareSize,
            PixelSizeUm = chessBoard.PixelSizeUm,
        };

        return result;
    }

    #endregion


    #region 角点检测

    /// <summary>
    /// 对多张图片进行棋盘格角点检测，并进行亚像素精化。
    /// </summary>
    /// <param name="imgPaths">图片文件的完整路径数组。</param>
    /// <param name="config">棋盘格配置参数（内角点列数、行数）。</param>
    /// <param name="progress">进度报告器（0→N）。可传 null 忽略。</param>
    /// <returns>包含每张图片检测结果的列表。</returns>
    public List<DetectedImagePair> DetectImagePairs(IEnumerable<string> leftImgPaths, IEnumerable<string> rightImgPaths, ChessboardConfig config)
    {
        int pairCount = System.Math.Min(leftImgPaths.Count(), rightImgPaths.Count());
        if (pairCount == 0)
        {
            throw new ArgumentException("错误：左右至少各导入 1 张图片才能进行角点检测");
        }

        var imgResults = new List<DetectedImagePair>();

        for (int i = 0; i < pairCount; i++)
        {
            bool okLeft = TryDetectCorners(leftImgPaths.ElementAt(i), config, out var leftCorners, out var leftSize);
            bool okRight = TryDetectCorners(rightImgPaths.ElementAt(i), config, out var rightCorners, out var rightSize);

            if (okLeft && okRight && leftSize == rightSize)
            {
                imgResults.Add(new DetectedImagePair
                {
                    LeftPath = leftImgPaths.ElementAt(i),
                    RightPath = rightImgPaths.ElementAt(i),
                    LeftCorners = leftCorners,
                    RightCorners = rightCorners,
                    ImageSize = leftSize
                });
            }
        }

        return imgResults;
    }

    /// <summary>
    /// 对多张图片进行棋盘格角点检测，并进行亚像素精化。
    /// </summary>
    /// <param name="imgPaths">图片文件的完整路径数组。</param>
    /// <param name="config">棋盘格配置参数（内角点列数、行数）。</param>
    /// <param name="progress">进度报告器（0→N）。可传 null 忽略。</param>
    /// <returns>包含每张图片检测结果的列表。</returns>
    public List<DetectedImage> DetectImages(IEnumerable<string> imgPaths, ChessboardConfig config)
    {
        var imgResults = new List<DetectedImage>();

        for (int i = 0; i < imgPaths.Count(); i++)
        {
            bool ok = TryDetectCorners(imgPaths.ElementAt(i), config, out var corners, out var size);

            if (ok)
            {
                imgResults.Add(new DetectedImage
                {
                    ImagePath = imgPaths.ElementAt(i),
                    Corners = corners,
                    ImageSize = size
                });
            }
        }

        return imgResults;
    }

    /// <summary>
    /// 对单张图片进行棋盘格角点检测，并进行亚像素精化。
    /// </summary>
    /// <param name="imagePath">图片文件的完整路径。</param>
    /// <param name="config">棋盘格配置参数（内角点列数、行数）。</param>
    /// <param name="corners">
    /// 输出参数：检测到的角点坐标数组（亚像素精度）。
    /// 检测失败时为空数组。
    /// </param>
    /// <param name="imageSize">
    /// 输出参数：图像的像素尺寸（Width × Height）。
    /// 检测失败时为 default。
    /// </param>
    /// <returns>
    /// <c>true</c> — 成功检测到全部 <c>PatternWidth × PatternHeight</c> 个角点；
    /// <c>false</c> — 图像加载失败、棋盘格检测失败或角点数量不匹配。
    /// </returns>
    /// <remarks>
    /// <para>检测流程：</para>
    /// <list type="number">
    ///   <item>以灰度模式加载图像（<c>ImreadModes.Grayscale</c>）。</item>
    ///   <item>调用 <c>Cv2.FindChessboardCorners</c>（AdaptiveThresh + NormalizeImage 标志）
    ///       进行初定位。</item>
    ///   <item>调用 <c>Cv2.CornerSubPix</c> 进行亚像素精化（11×11 窗口，
    ///       最多 30 次迭代，精度阈值 0.001）。</item>
    ///   <item>校验角点数量 = PatternWidth × PatternHeight。</item>
    /// </list>
    /// </remarks>
    public bool TryDetectCorners(string imagePath, ChessboardConfig config, out Point2f[] corners, out Size imageSize)
    {
        corners = [];
        imageSize = default;

        // 以灰度模式加载图像
        using var gray = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
        if (gray.Empty()) return false;

        imageSize = new Size(gray.Cols, gray.Rows);

        // 棋盘格角点初定位
        // AdaptiveThresh：自适应阈值，对光照不均更鲁棒
        // NormalizeImage：先归一化再检测，提高对比度较差图像的检测率
        var found = Cv2.FindChessboardCorners(gray, new Size(config.CornerCols, config.CornerRows), out corners, ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage);
        if (!found) { return false; }


        // 亚像素精化：在 11×11 窗口内迭代优化角点位置
        // CriteriaTypes.Eps | MaxIter：同时满足精度 ≤0.001 或 30 次迭代时停止
        corners = Cv2.CornerSubPix(gray, corners, new Size(11, 11), new Size(-1, -1), new TermCriteria(CriteriaTypes.MaxIter | CriteriaTypes.Eps, 30, 0.001));

        // 最终校验：确保检测到的角点数量与期望完全一致
        return corners.Length == config.CornerCols * config.CornerRows;
    } 
    
    #endregion


    #region 棋盘格世界坐标构建

    /// <summary>
    /// 构建单份棋盘格角点世界坐标（3D）。
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    public List<Point3f> CreateWorldPoints(ChessboardConfig config)
    {
        var singlePattern = new List<Point3f>(config.CornerCols * config.CornerRows);
        for (int row = 0; row < config.CornerRows; row++)
            for (int col = 0; col < config.CornerCols; col++)
                singlePattern.Add(new Point3f(
                    col * (float)config.SquareSize,   // X：列方向
                    row * (float)config.SquareSize,   // Y：行方向
                    0f));                             // Z：平面标定板 → 0
        return singlePattern;
    }

    /// <summary>
    /// 构建所有图像对共享的棋盘格角点世界坐标（3D）。
    /// </summary>
    /// <param name="config">棋盘格配置（内角点数、格子物理尺寸）。</param>
    /// <param name="pairCount">有效图片对数量，决定生成几份相同的世界坐标模板。</param>
    /// <returns>
    /// 长度为 <paramref name="pairCount"/> 的列表，每个元素都是
    /// <c>PatternWidth × PatternHeight</c> 个 <see cref="Point3f"/> 角点。
    /// </returns>
    /// <remarks>
    /// 世界坐标系定义：
    /// <list type="bullet">
    ///   <item>Z = 0（所有角点在同一平面）</item>
    ///   <item>X 轴向右，Y 轴向下</item>
    ///   <item>原点位于棋盘格左上角第一个内角点</item>
    ///   <item>坐标单位为 <see cref="ChessboardConfig.SquareSize"/>（毫米）</item>
    /// </list>
    /// 因为每张标定图像对应完全相同的世界坐标模板，
    /// 所以只需构建一份然后复制 <paramref name="pairCount"/> 份。
    /// </remarks>
    public List<List<Point3f>> CreateObjectPoints(ChessboardConfig config, int pairCount)
    {
        // 构建单份世界坐标模板
        var singlePattern = CreateWorldPoints(config);

        // 为每张图像复制相同的世界坐标模板
        var result = new List<List<Point3f>>(pairCount);
        for (int i = 0; i < pairCount; i++)
            result.Add(new List<Point3f>(singlePattern));

        return result;
    }

    #endregion


    #region 标定结果参数读写

    /// <summary>
    /// 将标定结果写入 OpenCV YAML 文件。
    /// </summary>
    /// <param name="filePath">目标文件路径（扩展名建议 .yaml 或 .yml）。如果父目录不存在则自动创建。</param>
    /// <param name="result">要持久化的标定结果对象。</param>
    /// <remarks>
    /// <para>使用 OpenCV <c>FileStorage</c> 的 WRITE 模式写入，格式为标准 YAML 1.0。</para>
    /// <para>写入的关键字段：LeftCameraMatrix、RightCameraMatrix、LeftDistCoeffs、
    /// RightDistCoeffs、RotationMatrix、TranslationVector、ImageWidth、ImageHeight、
    /// SkippedPairCount、ReprojectionError。</para>
    /// <para>与 <see cref="LoadStereoCalibrationParams"/> 配对使用，保证读写往返一致性。</para>
    /// </remarks>
    public void SaveStereoCalibrationParams(string filePath, StereoCalibrationParams result)
    {
        // 确保目标目录存在
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 将 double[][] 转换为 OpenCV Mat，通过 FileStorage 写入 YAML
        using var leftCam = result.LeftCameraMatrix.ToMat();
        using var rightCam = result.RightCameraMatrix.ToMat();
        using var leftDist = result.LeftDistCoeffs.ToMat();
        using var rightDist = result.RightDistCoeffs.ToMat();
        using var R = result.RotationMatrix.ToMat();
        using var T = result.TranslationVector.ToMat();
        using var E = result.EssentialMatrix.ToMat();
        using var F = result.FundamentalMatrix.ToMat();

        // FileStorage.Write 自动以 YAML 格式序列化 Mat 数据
        using var fs = new FileStorage(filePath, FileStorage.Modes.Write);
        fs.Write("LeftCameraMatrix", leftCam);
        fs.Write("RightCameraMatrix", rightCam);
        fs.Write("LeftDistCoeffs", leftDist);
        fs.Write("RightDistCoeffs", rightDist);
        fs.Write("RotationMatrix", R);
        fs.Write("TranslationVector", T);
        fs.Write("EssentialMatrix", E);
        fs.Write("FundamentalMatrix", F);
        fs.Write("ImageWidth", result.ImageWidth);
        fs.Write("ImageHeight", result.ImageHeight);
        fs.Write("ReprojectionError", result.ReprojectionError);
        fs.Write("CornerCols", result.CornerCols);
        fs.Write("CornerRows", result.CornerRows);
        fs.Write("SquareSize", result.SquareSize);
        fs.Write("PixelSizeUm", result.PixelSizeUm);
    }

    /// <summary>
    /// 从 OpenCV YAML 文件加载标定结果。
    /// </summary>
    /// <param name="filePath">YAML 标定结果文件的完整路径。</param>
    /// <returns>从文件反序列化得到的标定结果对象。</returns>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> 指定的文件不存在时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// FileStorage 无法打开文件（文件损坏或格式不兼容）时抛出。
    /// </exception>
    /// <remarks>
    /// 读取顺序与 <see cref="SaveStereoCalibrationParams"/> 写入顺序严格对应，
    /// 确保数据往返一致。字段缺失或类型不匹配将导致 OpenCV 内部异常。
    /// </remarks>
    public StereoCalibrationParams LoadStereoCalibrationParams(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"标定文件未找到: {filePath}", filePath);

        using var fs = new FileStorage(filePath, FileStorage.Modes.Read);
        if (!fs.IsOpened())
            throw new InvalidOperationException($"无法打开标定文件: {filePath}");

        // 使用索引器按名称读取各 Mat 字段
        using var leftCam = fs["LeftCameraMatrix"].ReadMat();
        using var rightCam = fs["RightCameraMatrix"].ReadMat();
        using var leftDist = fs["LeftDistCoeffs"].ReadMat();
        using var rightDist = fs["RightDistCoeffs"].ReadMat();
        using var R = fs["RotationMatrix"].ReadMat();
        using var T = fs["TranslationVector"].ReadMat();
        using var E = fs["EssentialMatrix"].ReadMat();
        using var F = fs["FundamentalMatrix"].ReadMat();

        // 将 Mat 转换为 C# 基础类型后立即释放 Mat，避免内存泄漏
        return new StereoCalibrationParams
        {
            LeftCameraMatrix = leftCam.ToDouble2D(),
            RightCameraMatrix = rightCam.ToDouble2D(),
            LeftDistCoeffs = leftDist.ToDoubleArray(),
            RightDistCoeffs = rightDist.ToDoubleArray(),
            RotationMatrix = R.ToDouble2D(),
            TranslationVector = T.ToDoubleArray(),
            EssentialMatrix = E.ToDouble2D(),
            FundamentalMatrix = F.ToDouble2D(),
            ImageWidth = fs["ImageWidth"].ReadInt(),
            ImageHeight = fs["ImageHeight"].ReadInt(),
            ReprojectionError = fs["ReprojectionError"].ReadDouble(),
            CornerCols = fs["CornerCols"].ReadInt(),
            CornerRows = fs["CornerRows"].ReadInt(),
            SquareSize = fs["SquareSize"].ReadDouble(),
            PixelSizeUm = fs["PixelSizeUm"].ReadDouble(),
        };
    }

    /// <summary>
    /// 将单目标定结果写入 OpenCV YAML 文件。
    /// </summary>
    public void SaveCalibrationParams(string filePath, CalibrationParams result)
    {
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var camMat = result.CameraMatrix.ToMat();
        using var distCoeffs = result.DistCoeffs.ToMat();

        using var fs = new FileStorage(filePath, FileStorage.Modes.Write);
        fs.Write("CameraMatrix", camMat);
        fs.Write("DistCoeffs", distCoeffs);
        fs.Write("ImageWidth", result.ImageWidth);
        fs.Write("ImageHeight", result.ImageHeight);
        fs.Write("ReprojectionError", result.ReprojectionError);
        fs.Write("CornerCols", result.CornerCols);
        fs.Write("CornerRows", result.CornerRows);
        fs.Write("SquareSize", result.SquareSize);
        fs.Write("PixelSizeUm", result.PixelSizeUm);
    }

    /// <summary>
    /// 从 OpenCV YAML 文件加载单目标定结果。
    /// </summary>
    public CalibrationParams LoadCalibrationParams(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"标定文件未找到: {filePath}", filePath);

        using var fs = new FileStorage(filePath, FileStorage.Modes.Read);
        if (!fs.IsOpened())
            throw new InvalidOperationException($"无法打开标定文件: {filePath}");

        using var camMat = fs["CameraMatrix"].ReadMat();
        using var distCoeffs = fs["DistCoeffs"].ReadMat();

        return new CalibrationParams
        {
            CameraMatrix = camMat.ToDouble2D(),
            DistCoeffs = distCoeffs.ToDoubleArray(),
            ImageWidth = fs["ImageWidth"].ReadInt(),
            ImageHeight = fs["ImageHeight"].ReadInt(),
            ReprojectionError = fs["ReprojectionError"].ReadDouble(),
            CornerCols = fs["CornerCols"].ReadInt(),
            CornerRows = fs["CornerRows"].ReadInt(),
            SquareSize = fs["SquareSize"].ReadDouble(),
            PixelSizeUm = fs["PixelSizeUm"].ReadDouble(),
        };
    }
    
    #endregion
}
