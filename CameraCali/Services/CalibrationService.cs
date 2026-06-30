using System.IO;
using CameraCali.Models;
using OpenCvSharp;

namespace CameraCali.Services;

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

    // ═══════════════════════════════════════════════════════════════════
    // 公有方法
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从原始图像路径列表执行完整的双目相机标定流水线。
    /// </summary>
    /// <param name="imagePaths">
    /// 所有标定图片的完整路径集合。必须成对出现（偶数张），
    /// 左右各一张为一对。支持 .bmp / .jpg / .png / .tiff 等常见格式。
    /// </param>
    /// <param name="config">棋盘格标定板配置参数（内角点数、格子物理尺寸等）。</param>
    /// <param name="progress">
    /// 进度报告器（0–100）。0→30% 为角点检测阶段，30→50% 为左相机单目标定，
    /// 50→70% 为右相机单目标定，70→100% 为双目立体标定。可传 null 忽略。
    /// </param>
    /// <param name="ct">协作取消令牌，用于在标定过程中取消操作。</param>
    /// <returns>包含左右相机内参、畸变系数及双目外参的完整标定结果。</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="imagePaths"/> 为空或数量为奇数时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 有效图片对不足 3 对时抛出——立体标定至少需要 3 对有效图片。
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// 通过 <paramref name="ct"/> 取消操作时抛出。
    /// </exception>
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

        // 在后台线程中执行耗时计算，避免阻塞 UI
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            // ── 阶段 1：分离左右图像 ──
            // SeparateStereoImages 优先按文件名关键词匹配，失败则交替分配
            var (leftPaths, rightPaths) = SeparateStereoImages(paths);
            int totalPairs = leftPaths.Count;

            // ── 阶段 2：角点检测（逐个处理，跳过失败对） ──
            // 静默跳过机制：左右任一图像检测失败则整对跳过，不中断流水线
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
                    // 以第一对成功检测的图片尺寸作为所有图像的参考尺寸
                    if (imageSize == default)
                        imageSize = sz;
                }
                // 跳过失败对：左右任一图像角点检测失败则整对跳过，不中断整个标定流程

                // 角点检测阶段：0% → 30%
                progress?.Report((int)(30.0 * (i + 1) / totalPairs));
            }

            int validPairs = leftCornersList.Count;
            int skippedPairs = totalPairs - validPairs;

            // OpenCV 立体标定至少需要 3 对有效图片才能求解外参
            if (validPairs < 3)
                throw new InvalidOperationException(
                    $"标定失败：{totalPairs} 对图片中仅 {validPairs} 对检测到棋盘格角点，" +
                    $"至少需要 3 对有效图片。请检查图片质量或棋盘格配置。");

            // ── 阶段 3：构建棋盘格世界坐标（基于有效配对数量） ──
            // 世界坐标系：Z=0 平面，X 向右，Y 向下
            var objectPointsList = BuildObjectPointsList(config, validPairs);

            // ── 阶段 4-5：单目标定 + 双目立体标定 ──
            // 以单位矩阵初始化内参，零向量初始化畸变系数
            var leftCam = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var leftDist = Mat.Zeros(5, 1, MatType.CV_64FC1).ToMat();
            var rightCam = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var rightDist = Mat.Zeros(5, 1, MatType.CV_64FC1).ToMat();

            // 将点集转换为 Mat 数组以匹配 OpenCvSharp4 API
            // CalibrateCamera 要求：objectPoints → Mat[]（每张图一个 Mat，N×1×CV_32FC3）
            //                       imagePoints  → Mat[]（每张图一个 Mat，N×1×CV_32FC2）
            var objMats = objectPointsList.Select(pts => Point3fListToMat(pts)).ToArray();
            var leftImgMats = leftCornersList.Select(pts => Point2fArrayToMat(pts)).ToArray();
            var rightImgMats = rightCornersList.Select(pts => Point2fArrayToMat(pts)).ToArray();

            // 左相机单目标定（CalibrateCamera 返回 RMS 重投影误差）
            double leftRms = Cv2.CalibrateCamera(objMats, leftImgMats, imageSize, leftCam, leftDist,
                out var rvecsL, out var tvecsL);
            progress?.Report(50);

            // 右相机单目标定
            double rightRms = Cv2.CalibrateCamera(objMats, rightImgMats, imageSize, rightCam, rightDist,
                out var rvecsR, out var tvecsR);

            // 左右相机平均 RMS 重投影误差（像素）
            // 使用 RMS 方式的均方根平均，比简单平均更合理
            double reprojectionError = Math.Sqrt((leftRms * leftRms + rightRms * rightRms) / 2.0);
            progress?.Report(70);

            // 清理单目标定产生的中间结果（旋转/平移向量），不再需要
            foreach (var rv in rvecsL) rv.Dispose();
            foreach (var tv in tvecsL) tv.Dispose();
            foreach (var rv in rvecsR) rv.Dispose();
            foreach (var tv in tvecsR) tv.Dispose();

            // ── 双目立体标定 ──
            // CalibrationFlags.FixIntrinsic：固定已完成单目标定的内参和畸变系数，
            // 只优化外参 R、T（这是标准做法，避免内外参耦合优化导致不稳定）
            using var R = new Mat();
            using var T = new Mat();
            using var E = new Mat();   // 本质矩阵
            using var F = new Mat();   // 基础矩阵
            // 将 Mat[] 显式转换为 InputArray（OpenCvSharp4 StereoCalibrate 的 API 要求）
            Cv2.StereoCalibrate(
                objMats.Select(m => (InputArray)m),
                leftImgMats.Select(m => (InputArray)m),
                rightImgMats.Select(m => (InputArray)m),
                leftCam, leftDist, rightCam, rightDist,
                imageSize, R, T, E, F,
                flags: CalibrationFlags.FixIntrinsic);
            progress?.Report(100);

            // ── 阶段 6：封装结果 ──
            // 将 OpenCV Mat 转换为 C# 基础数据类型，方便 JSON/内存传递
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

            // 释放所有临时 Mat 对象
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

    /// <summary>
    /// 从已缓存的角点检测结果执行双目标定（跳过角点检测阶段）。
    /// </summary>
    /// <param name="pairs">
    /// 已通过角点检测的有效图片对列表。每对必须包含完整的
    /// <see cref="CachedStereoPair.LeftCorners"/> 和 <see cref="CachedStereoPair.RightCorners"/>。
    /// </param>
    /// <param name="config">棋盘格标定板配置参数。</param>
    /// <param name="progress">进度报告器（0→40→60→100）。可传 null 忽略。</param>
    /// <param name="ct">协作取消令牌。</param>
    /// <returns>包含左右相机内参、畸变系数及双目外参的完整标定结果。</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="pairs"/> 数量不足 3 对时抛出。
    /// </exception>
    /// <remarks>
    /// 此方法是 <see cref="CalibrateAsync"/> 的缓存版本——角点检测已由 ViewModel 的
    /// "检测角点" 按钮前置完成，此处直接复用检测结果进行单目和立体标定，
    /// 避免重复计算。使用场景：用户导入图像→检测角点→筛选有效对→开始标定。
    /// </remarks>
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

            // ── 阶段 1：从缓存提取角点 + 图像尺寸 ──
            // 直接复用前期角点检测的结果，跳过图像 I/O 和 FindChessboardCorners
            var leftCornersList = pairs.Select(p => p.LeftCorners).ToList();
            var rightCornersList = pairs.Select(p => p.RightCorners).ToList();
            Size imageSize = pairs[0].ImageSize;

            // ── 阶段 2：构建棋盘格世界坐标 ──
            var objectPointsList = BuildObjectPointsList(config, validPairs);

            // ── 阶段 3-4：单目标定 ──
            var leftCam = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var leftDist = Mat.Zeros(5, 1, MatType.CV_64FC1).ToMat();
            var rightCam = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var rightDist = Mat.Zeros(5, 1, MatType.CV_64FC1).ToMat();

            var objMats = objectPointsList.Select(pts => Point3fListToMat(pts)).ToArray();
            var leftImgMats = leftCornersList.Select(pts => Point2fArrayToMat(pts)).ToArray();
            var rightImgMats = rightCornersList.Select(pts => Point2fArrayToMat(pts)).ToArray();

            // 左相机单目标定（40%）
            double leftRms = Cv2.CalibrateCamera(objMats, leftImgMats, imageSize,
                leftCam, leftDist, out var rvecsL, out var tvecsL);
            progress?.Report(40);

            ct.ThrowIfCancellationRequested();
            // 右相机单目标定（60%）
            double rightRms = Cv2.CalibrateCamera(objMats, rightImgMats, imageSize,
                rightCam, rightDist, out var rvecsR, out var tvecsR);
            progress?.Report(60);

            // 左右相机平均 RMS 重投影误差
            double reprojectionError = Math.Sqrt(
                (leftRms * leftRms + rightRms * rightRms) / 2.0);

            // 清理单目标定的中间结果
            foreach (var rv in rvecsL) rv.Dispose();
            foreach (var tv in tvecsL) tv.Dispose();
            foreach (var rv in rvecsR) rv.Dispose();
            foreach (var tv in tvecsR) tv.Dispose();

            // ── 阶段 5：双目立体标定 ──
            // FixIntrinsic：固定单目标定结果，仅优化外参 R、T
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

            // ── 阶段 6：封装结果 ──
            // SkippedPairCount 固定为 0——缓存路径的图片对已经通过角点检测筛选
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

            // 释放所有临时 Mat 对象
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
    /// <para>与 <see cref="LoadCalibrationResult"/> 配对使用，保证读写往返一致性。</para>
    /// </remarks>
    public void SaveCalibrationResult(string filePath, CalibrationResult result)
    {
        // 确保目标目录存在
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

        // FileStorage.Write 自动以 YAML 格式序列化 Mat 数据
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
    /// 读取顺序与 <see cref="SaveCalibrationResult"/> 写入顺序严格对应，
    /// 确保数据往返一致。字段缺失或类型不匹配将导致 OpenCV 内部异常。
    /// </remarks>
    public CalibrationResult LoadCalibrationResult(string filePath)
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

        // 将 Mat 转换为 C# 基础类型后立即释放 Mat，避免内存泄漏
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
    /// </summary>
    /// <param name="paths">已按名称排序的图像路径列表。</param>
    /// <returns>包含左图路径和右图路径的元组，两列表长度相等。</returns>
    /// <exception cref="ArgumentException">
    /// 无法将图像平均分配为左右两组时抛出。
    /// </exception>
    /// <remarks>
    /// <para><b>策略 1（关键词匹配）：</b>扫描文件名中是否包含
    /// "left"/"right"（英文）或 "左侧"/"左相机"/"右侧"/"右相机"（中文）
    /// 等关键词，同时满足以下条件才采纳：
    /// <list type="bullet">
    ///   <item>左、右匹配均非空</item>
    ///   <item>匹配到的总数等于输入总数（无遗漏）</item>
    ///   <item>左右数量相等</item>
    /// </list>
    /// </para>
    /// <para><b>策略 2（交替分配，回退）：</b>当关键词匹配不满足上述条件时，
    /// 将已排序的路径按奇偶索引交替分配：偶数索引 → 左，奇数索引 → 右。</para>
    /// </remarks>
    private static (List<string> Left, List<string> Right) SeparateStereoImages(List<string> paths)
    {
        paths.Sort(StringComparer.OrdinalIgnoreCase);

        // 关键词列表：支持中英文文件命名习惯
        string[] leftKw = ["left", "_l.", "_l_", "左侧", "左相机"];
        string[] rightKw = ["right", "_r.", "_r_", "右侧", "右相机"];

        // 本地函数：检查文件名（不含扩展名）是否包含任一关键词
        bool ContainsAny(string path, string[] keywords) =>
            keywords.Any(k => Path.GetFileNameWithoutExtension(path)
                .Contains(k, StringComparison.OrdinalIgnoreCase));

        var leftMatches = paths.Where(p => ContainsAny(p, leftKw)).ToList();
        var rightMatches = paths.Where(p => ContainsAny(p, rightKw)).ToList();

        // 关键词匹配策略：必须满足全覆盖、非空、数量相等三个条件
        if (leftMatches.Count > 0 && rightMatches.Count > 0
            && leftMatches.Count + rightMatches.Count == paths.Count
            && leftMatches.Count == rightMatches.Count)
        {
            return (leftMatches, rightMatches);
        }

        // ── 回退策略：交替分配（简单可靠，不依赖文件命名） ──
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
    private static List<List<Point3f>> BuildObjectPointsList(ChessboardConfig config, int pairCount)
    {
        // 构建单份世界坐标模板
        var singlePattern = new List<Point3f>(config.PatternWidth * config.PatternHeight);
        for (int row = 0; row < config.PatternHeight; row++)
            for (int col = 0; col < config.PatternWidth; col++)
                singlePattern.Add(new Point3f(
                    col * (float)config.SquareSize,   // X：列方向
                    row * (float)config.SquareSize,   // Y：行方向
                    0f));                             // Z：平面标定板 → 0

        // 为每张图像复制相同的世界坐标模板
        var result = new List<List<Point3f>>(pairCount);
        for (int i = 0; i < pairCount; i++)
            result.Add(new List<Point3f>(singlePattern));

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 公有方法 — 角点检测（供 ViewModel 调用）
    // ═══════════════════════════════════════════════════════════════════

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
        if (!Cv2.FindChessboardCorners(gray, new Size(config.PatternWidth, config.PatternHeight),
                out corners, ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage))
            return false;

        // 亚像素精化：在 11×11 窗口内迭代优化角点位置
        // CriteriaTypes.Eps | MaxIter：同时满足精度 ≤0.001 或 30 次迭代时停止
        Cv2.CornerSubPix(gray, corners, new Size(11, 11), new Size(-1, -1),
            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.001));

        // 最终校验：确保检测到的角点数量与期望完全一致
        return corners.Length == config.PatternWidth * config.PatternHeight;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 私有方法 — Mat ↔ 数组转换
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将 <see cref="Point3f"/> 列表转换为单列 Mat（CV_32FC3）。
    /// 用于 OpenCV 标定 API 的 objectPoints 参数。
    /// </summary>
    /// <param name="points">3D 角点世界坐标列表。</param>
    /// <returns>N×1 的单列 Mat，每个元素为一个 Point3f（3 通道 float）。</returns>
    private static Mat Point3fListToMat(List<Point3f> points)
    {
        var mat = new Mat(points.Count, 1, MatType.CV_32FC3);
        for (int i = 0; i < points.Count; i++)
            mat.Set(i, 0, points[i]);
        return mat;
    }

    /// <summary>
    /// 将 <see cref="Point2f"/> 数组转换为单列 Mat（CV_32FC2）。
    /// 用于 OpenCV 标定 API 的 imagePoints 参数。
    /// </summary>
    /// <param name="points">2D 角点图像坐标数组。</param>
    /// <returns>N×1 的单列 Mat，每个元素为一个 Point2f（2 通道 float）。</returns>
    private static Mat Point2fArrayToMat(Point2f[] points)
    {
        var mat = new Mat(points.Length, 1, MatType.CV_32FC2);
        for (int i = 0; i < points.Length; i++)
            mat.Set(i, 0, points[i]);
        return mat;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 私有方法 — Mat ↔ 数组转换（通用）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将双精度 Mat 按行优先顺序展平为一维 <c>double[]</c>。
    /// </summary>
    /// <param name="mat">任意行×列的双精度 Mat（CV_64FC1）。</param>
    /// <returns>长度为 Rows × Cols 的一维数组。</returns>
    private static double[] MatToArray1D(Mat mat)
    {
        var result = new double[mat.Rows * mat.Cols];
        int idx = 0;
        for (int r = 0; r < mat.Rows; r++)
            for (int c = 0; c < mat.Cols; c++)
                result[idx++] = mat.Get<double>(r, c);
        return result;
    }

    /// <summary>
    /// 将双精度 Mat 转换为锯齿数组 <c>double[][]</c>（每行一个子数组）。
    /// </summary>
    /// <param name="mat">任意行×列的双精度 Mat（CV_64FC1）。</param>
    /// <returns>长度为 Rows 的二维锯齿数组，每行长度为 Cols。</returns>
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

    /// <summary>
    /// 将二维锯齿数组 <c>double[][]</c> 转换回双精度 Mat。
    /// </summary>
    /// <param name="array">二维锯齿数组，所有行的长度必须一致。</param>
    /// <returns>等尺寸的 CV_64FC1 Mat。</returns>
    /// <remarks>
    /// 此方法标记为 <c>internal</c>，供 <see cref="DistanceService"/> 在立体校正时
    /// 将 <see cref="CalibrationResult"/> 中的相机参数恢复为 OpenCV Mat 格式。
    /// </remarks>
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

    /// <summary>
    /// 将一维 <c>double[]</c> 转换为列向量 Mat（N×1, CV_64FC1）。
    /// </summary>
    /// <param name="array">一维双精度数组。</param>
    /// <returns>N×1 列向量 Mat。</returns>
    /// <remarks>
    /// 标记为 <c>internal</c> 原因同 <see cref="Array2DToMat(double[][])"/>。
    /// </remarks>
    internal static Mat Array1DToMat(double[] array)
    {
        var mat = new Mat(array.Length, 1, MatType.CV_64FC1);
        for (int i = 0; i < array.Length; i++)
            mat.Set(i, 0, array[i]);
        return mat;
    }
}
