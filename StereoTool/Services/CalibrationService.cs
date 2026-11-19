using System.IO;
using OpenCvSharp;
using StereoTool.Models;
using StereoTool.Utils;

namespace StereoTool.Services;

/// <summary>
/// 单目与双目相机标定服务，基于 OpenCV 棋盘格标定算法实现。
/// </summary>
/// <remarks>图片分析和误差预览实现见 CalibrationService.Analysis.cs。</remarks>
public partial class CalibrationService
{
    #region 相机标定

    /// <summary>
    /// 双目标定
    /// </summary>
    /// <param name="chessBoard">棋盘格及畸变模型配置。</param>
    /// <param name="detectedImagePairs">参与拟合的有效图片对快照。</param>
    /// <param name="fixedLeft">可选固定左内参。</param>
    /// <param name="fixedRight">可选固定右内参，须和左内参同时提供。</param>
    /// <returns>相机标定参数与质量统计。</returns>
    /// <exception cref="InvalidOperationException">数据数量、尺寸或角点配置不满足标定条件。</exception>
    public StereoCalibrationParams StereoCalibrate(ChessboardConfig chessBoard, IEnumerable<DetectedImagePair> detectedImagePairs,
        CalibrationParams? fixedLeft = null, CalibrationParams? fixedRight = null)
    {
        var pairs = detectedImagePairs.ToList();
        ValidateDataset(chessBoard, pairs.Select(pair => pair.ImageSize),
            pairs.Select(pair => pair.LeftCorners.Length));
        if (pairs.Any(pair => pair.RightCorners.Length != chessBoard.CornerCols * chessBoard.CornerRows))
            throw new InvalidOperationException("右图角点数量与当前配置不同，请重新检测。");
        if ((fixedLeft == null) != (fixedRight == null))
            throw new InvalidOperationException("请同时导入左右单目标定文件。");
        var objectPoints = CreateObjectPoints(chessBoard, pairs.Count);

        var leftPoints = pairs.Select(p => p.LeftCorners.ToList()).ToList();
        var rightPoints = pairs.Select(p => p.RightCorners.ToList()).ToList();
        var imageSize = pairs[0].ImageSize;

        if (fixedLeft != null && (fixedLeft.ImageWidth != imageSize.Width || fixedLeft.ImageHeight != imageSize.Height ||
            fixedRight!.ImageWidth != imageSize.Width || fixedRight.ImageHeight != imageSize.Height))
            throw new InvalidOperationException("固定内参的图片尺寸与当前数据集不同。");

        var leftCameraMatrix = fixedLeft == null ? new double[3, 3] : (double[,])fixedLeft.CameraMatrix.Clone();
        var leftDistCoeffs = fixedLeft == null ? new double[5] : (double[])fixedLeft.DistCoeffs.Clone();
        var rightCameraMatrix = fixedRight == null ? new double[3, 3] : (double[,])fixedRight.CameraMatrix.Clone();
        var rightDistCoeffs = fixedRight == null ? new double[5] : (double[])fixedRight.DistCoeffs.Clone();
        var flags = CalibrationFlags.FixIntrinsic;
        if (fixedLeft == null)
        {
            var modelFlags = GetCalibrationFlags(chessBoard);
            Cv2.CalibrateCamera(objectPoints, leftPoints, imageSize, leftCameraMatrix, leftDistCoeffs, out _, out _, modelFlags);
            Cv2.CalibrateCamera(objectPoints, rightPoints, imageSize, rightCameraMatrix, rightDistCoeffs, out _, out _, modelFlags);
            flags = modelFlags | CalibrationFlags.UseIntrinsicGuess;
        }

        using var R = new Mat();
        using var T = new Mat();
        using var E = new Mat();
        using var F = new Mat();

        // 联合模式优化内外参；固定模式只求解双目外参。
        var rms = Cv2.StereoCalibrate(objectPoints,
            leftPoints, rightPoints, 
            leftCameraMatrix, leftDistCoeffs, 
            rightCameraMatrix, rightDistCoeffs, 
            imageSize,
            R, T, E, F, flags, new TermCriteria(CriteriaTypes.MaxIter | CriteriaTypes.Eps, 100, 1e-6));

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
            ImageCount = pairs.Count,
            UsedFixedIntrinsics = fixedLeft != null,
            EstimateK3 = chessBoard.EstimateK3,
            EstimateTangentialDistortion = chessBoard.EstimateTangentialDistortion,
        };

        UpdateStereoErrors(chessBoard, pairs, result);
        return result;
    }

    /// <summary>从有效图片快照执行单目标定，计算每幅图的投影点和平均误差。</summary>
    /// <param name="chessBoard">棋盘格及畸变配置。</param>
    /// <param name="detectedImages">参与标定的有效图片快照。</param>
    /// <returns>内参、畸变和质量统计。</returns>
    public CalibrationParams CameraCalibrate(ChessboardConfig chessBoard, IEnumerable<DetectedImage> detectedImages)
    { 
        var images = detectedImages.ToList();
        ValidateDataset(chessBoard, images.Select(image => image.ImageSize), images.Select(image => image.Corners.Length));
        var objectPoints = CreateObjectPoints(chessBoard, images.Count);

        var imagePoints = images.Select(p=>p.Corners.ToList()).ToList();
        var imageSize = images[0].ImageSize;

        var cameraMatrix = new double[3, 3];
        var distCoeffs = new double[5];
        var rms = Cv2.CalibrateCamera(
            objectPoints,
            imagePoints, 
            imageSize, 
            cameraMatrix, 
            distCoeffs, 
            out var rotations,
            out var translations,
            GetCalibrationFlags(chessBoard));

        for (var i = 0; i < images.Count; i++)
        {
            Cv2.ProjectPoints(objectPoints[i],
                [rotations[i].Item0, rotations[i].Item1, rotations[i].Item2],
                [translations[i].Item0, translations[i].Item1, translations[i].Item2],
                cameraMatrix, distCoeffs, out var projected, out _);
            images[i].ProjectedCorners = projected;
            images[i].MeanError = GetMeanError(images[i].Corners, projected);
        }

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
            MeanReprojectionError = images.Average(image => image.MeanError),
            ImageCount = images.Count,
            EstimateK3 = chessBoard.EstimateK3,
            EstimateTangentialDistortion = chessBoard.EstimateTangentialDistortion,
        };

        return result;
    }

    #endregion


    #region 角点检测

    /// <summary>按名称配对并返回检测成功的图片对；完整诊断使用 AnalyzePairs。</summary>
    /// <param name="leftImgPaths">左图路径。</param>
    /// <param name="rightImgPaths">右图路径。</param>
    /// <param name="config">棋盘格配置。</param>
    /// <returns>检测成功的图片对。</returns>
    public List<DetectedImagePair> DetectImagePairs(IEnumerable<string> leftImgPaths,
        IEnumerable<string> rightImgPaths, ChessboardConfig config)
    {
        return AnalyzePairs(PairImages(leftImgPaths, rightImgPaths), config)
            .Where(pair => pair.IsDetected).ToList();
    }

    /// <summary>返回检测成功的单目图片；完整诊断使用 AnalyzeImages。</summary>
    /// <param name="imgPaths">图片路径。</param>
    /// <param name="config">棋盘格配置。</param>
    /// <returns>检测成功的图片记录。</returns>
    public List<DetectedImage> DetectImages(IEnumerable<string> imgPaths, ChessboardConfig config)
    {
        return AnalyzeImages(imgPaths, config).Where(image => image.IsDetected).ToList();
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
        ValidateConfig(config);
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
    /// ReprojectionError，以及平均误差、图片数量和畸变模型选项。</para>
    /// <para>与 <see cref="LoadStereoCalibrationParams"/> 配对使用，保证读写往返一致性。</para>
    /// </remarks>
    public void SaveStereoCalibrationParams(string filePath, StereoCalibrationParams result)
    {
        // 确保目标目录存在
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 将托管数组转换为 OpenCV Mat，通过 FileStorage 写入 YAML
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
        fs.Write("MeanReprojectionError", result.MeanReprojectionError);
        fs.Write("ImageCount", result.ImageCount);
        fs.Write("UsedFixedIntrinsics", result.UsedFixedIntrinsics ? 1 : 0);
        fs.Write("EstimateK3", result.EstimateK3 ? 1 : 0);
        fs.Write("EstimateTangentialDistortion", result.EstimateTangentialDistortion ? 1 : 0);
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
        using var leftCam = fs["LeftCameraMatrix"]!.ReadMat();
        using var rightCam = fs["RightCameraMatrix"]!.ReadMat();
        using var leftDist = fs["LeftDistCoeffs"]!.ReadMat();
        using var rightDist = fs["RightDistCoeffs"]!.ReadMat();
        using var R = fs["RotationMatrix"]!.ReadMat();
        using var T = fs["TranslationVector"]!.ReadMat();
        using var E = fs["EssentialMatrix"]!.ReadMat();
        using var F = fs["FundamentalMatrix"]!.ReadMat();

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
            ImageWidth = fs["ImageWidth"]!.ReadInt(),
            ImageHeight = fs["ImageHeight"]!.ReadInt(),
            ReprojectionError = fs["ReprojectionError"]!.ReadDouble(),
            MeanReprojectionError = ReadOptionalDouble(fs, "MeanReprojectionError", double.NaN),
            ImageCount = (int)ReadOptionalDouble(fs, "ImageCount", 0),
            UsedFixedIntrinsics = ReadOptionalDouble(fs, "UsedFixedIntrinsics", 0) != 0,
            EstimateK3 = ReadOptionalDouble(fs, "EstimateK3", 1) != 0,
            EstimateTangentialDistortion = ReadOptionalDouble(fs, "EstimateTangentialDistortion", 1) != 0,
            CornerCols = fs["CornerCols"]!.ReadInt(),
            CornerRows = fs["CornerRows"]!.ReadInt(),
            SquareSize = fs["SquareSize"]!.ReadDouble(),
            PixelSizeUm = fs["PixelSizeUm"]!.ReadDouble(),
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
        fs.Write("MeanReprojectionError", result.MeanReprojectionError);
        fs.Write("ImageCount", result.ImageCount);
        fs.Write("EstimateK3", result.EstimateK3 ? 1 : 0);
        fs.Write("EstimateTangentialDistortion", result.EstimateTangentialDistortion ? 1 : 0);
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
        using var cameraNode = fs["CameraMatrix"];
        if (cameraNode == null || cameraNode.Empty)
            throw new InvalidOperationException("请选择单目标定 YAML，双目标定文件不能作为单目内参导入。");

        using var camMat = fs["CameraMatrix"]!.ReadMat();
        using var distCoeffs = fs["DistCoeffs"]!.ReadMat();

        return new CalibrationParams
        {
            CameraMatrix = camMat.ToDouble2D(),
            DistCoeffs = distCoeffs.ToDoubleArray(),
            ImageWidth = fs["ImageWidth"]!.ReadInt(),
            ImageHeight = fs["ImageHeight"]!.ReadInt(),
            ReprojectionError = fs["ReprojectionError"]!.ReadDouble(),
            MeanReprojectionError = ReadOptionalDouble(fs, "MeanReprojectionError", double.NaN),
            ImageCount = (int)ReadOptionalDouble(fs, "ImageCount", 0),
            EstimateK3 = ReadOptionalDouble(fs, "EstimateK3", 1) != 0,
            EstimateTangentialDistortion = ReadOptionalDouble(fs, "EstimateTangentialDistortion", 1) != 0,
            CornerCols = fs["CornerCols"]!.ReadInt(),
            CornerRows = fs["CornerRows"]!.ReadInt(),
            SquareSize = fs["SquareSize"]!.ReadDouble(),
            PixelSizeUm = fs["PixelSizeUm"]!.ReadDouble(),
        };
    }
    
    #endregion

    /// <summary>读取可选诊断字段，兼容旧版 YAML。</summary>
    /// <param name="storage">已打开的文件。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="fallback">旧文件缺少字段时的值。</param>
    /// <returns>读取值或默认值。</returns>
    private static double ReadOptionalDouble(FileStorage storage, string name, double fallback)
    {
        using var node = storage[name];
        return node == null || node.Empty ? fallback : node.ReadDouble();
    }
}