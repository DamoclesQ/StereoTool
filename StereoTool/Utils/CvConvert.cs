using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace StereoTool.Utils;

/// <summary>
/// C# 常规数据类型与 OpenCV Mat 之间的高性能互转工具类。
/// 基础类型（double/float/int/byte）使用 Marshal.Copy 走快速路径；
/// 结构体类型（Point2f/Point3f/Vec3d）使用 Mat.Set / Mat.At&lt;&gt;。
/// </summary>
public static partial class CvConvert
{
    // ═══════════════════════════════════════════════════════════════════
    //  double[] ↔ Mat (CV_64FC1)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将一维双精度数组转换为 Mat（CV_64FC1）。
    /// </summary>
    /// <param name="array">源数组，不可为 null。</param>
    /// <param name="asColumn">true 生成 N×1 列向量，false 生成 1×N 行向量。</param>
    /// <returns>CV_64FC1 Mat。</returns>
    /// <exception cref="ArgumentNullException">array 为 null 时抛出。</exception>
    public static Mat ToMat(this double[] array, bool asColumn = true)
    {
        if (array == null)
            throw new ArgumentNullException(nameof(array));

        int rows = asColumn ? array.Length : 1;
        int cols = asColumn ? 1 : array.Length;

        Mat mat = new(rows, cols, MatType.CV_64FC1);
        // 使用 Marshal.Copy 直接写入 Mat 非托管内存，避免逐元素 Set 的 marshaling 开销
        Marshal.Copy(array, 0, mat.Data, array.Length);

        return mat;
    }

    /// <summary>
    /// 将 CV_64FC1 Mat 按行优先顺序展平为一维 double[]。
    /// </summary>
    /// <param name="mat">CV_64FC1 Mat，不可为 null。</param>
    /// <returns>长度为 Rows × Cols 的一维数组。</returns>
    /// <exception cref="ArgumentException">Mat 类型不是 CV_64FC1 时抛出。</exception>
    public static double[] ToDoubleArray(this Mat mat)
    {
        if (mat.Type() != MatType.CV_64FC1)
            throw new ArgumentException("Mat 必须为 CV_64FC1");

        int total = mat.Rows * mat.Cols;
        double[] result = new double[total];
        Marshal.Copy(mat.Data, result, 0, total);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  double[,] ↔ Mat (CV_64FC1)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将二维双精度数组转换为 Mat（CV_64FC1），保持行列形状。
    /// </summary>
    /// <param name="array">源二维数组，不可为 null。</param>
    /// <returns>等尺寸的 CV_64FC1 Mat。</returns>
    /// <exception cref="ArgumentNullException">array 为 null 时抛出。</exception>
    public static Mat ToMat(this double[,] array)
    {
        if (array == null)
            throw new ArgumentNullException(nameof(array));

        int rows = array.GetLength(0);
        int cols = array.GetLength(1);
        int total = rows * cols;

        Mat mat = new(rows, cols, MatType.CV_64FC1);

        // double[,] 在 .NET 中按行优先连续存储，先展平再一次性拷贝
        double[] flat = new double[total];
        Buffer.BlockCopy(array, 0, flat, 0, total * sizeof(double));
        Marshal.Copy(flat, 0, mat.Data, total);

        return mat;
    }

    /// <summary>
    /// 将 CV_64FC1 Mat 转换为二维 double[,]。
    /// </summary>
    /// <param name="mat">CV_64FC1 Mat，不可为 null。</param>
    /// <returns>等尺寸的 double[,]。</returns>
    /// <exception cref="ArgumentException">Mat 类型不是 CV_64FC1 时抛出。</exception>
    public static double[,] ToDouble2D(this Mat mat)
    {
        if (mat.Type() != MatType.CV_64FC1)
            throw new ArgumentException("Mat 必须为 CV_64FC1");

        int rows = mat.Rows;
        int cols = mat.Cols;
        int total = rows * cols;

        double[] flat = new double[total];
        Marshal.Copy(mat.Data, flat, 0, total);

        double[,] result = new double[rows, cols];
        Buffer.BlockCopy(flat, 0, result, 0, total * sizeof(double));

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  double[][] ↔ Mat (CV_64FC1) — 锯齿数组
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将二维锯齿数组转换为 Mat（CV_64FC1）。
    /// </summary>
    /// <param name="array">二维锯齿数组，所有行长度必须一致。</param>
    /// <returns>等尺寸的 CV_64FC1 Mat。</returns>
    /// <exception cref="ArgumentNullException">array 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">行长度不一致时抛出。</exception>
    public static Mat ToMat(this double[][] array)
    {
        if (array == null)
            throw new ArgumentNullException(nameof(array));

        int rows = array.Length;
        int cols = rows > 0 ? array[0].Length : 0;

        // 校验所有行长度一致
        for (int r = 1; r < rows; r++)
        {
            if (array[r].Length != cols)
                throw new ArgumentException("锯齿数组所有行的长度必须一致");
        }

        Mat mat = new(rows, cols, MatType.CV_64FC1);

        for (int r = 0; r < rows; r++)
        {
            // 逐行 Marshal.Copy：目标地址 = 基址 + 行偏移（字节）
            Marshal.Copy(array[r], 0, mat.Data + r * cols * sizeof(double), cols);
        }

        return mat;
    }

    /// <summary>
    /// 将 CV_64FC1 Mat 转换为二维锯齿数组 double[][]。
    /// </summary>
    /// <param name="mat">CV_64FC1 Mat，不可为 null。</param>
    /// <returns>长度为 Rows 的锯齿数组，每行长度为 Cols。</returns>
    /// <exception cref="ArgumentException">Mat 类型不是 CV_64FC1 时抛出。</exception>
    public static double[][] ToJaggedDouble2D(this Mat mat)
    {
        if (mat.Type() != MatType.CV_64FC1)
            throw new ArgumentException("Mat 必须为 CV_64FC1");

        int rows = mat.Rows;
        int cols = mat.Cols;

        double[][] result = new double[rows][];

        for (int r = 0; r < rows; r++)
        {
            result[r] = new double[cols];
            Marshal.Copy(mat.Data + r * cols * sizeof(double), result[r], 0, cols);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  float[] ↔ Mat (CV_32FC1)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将一维单精度数组转换为 Mat（CV_32FC1）。
    /// </summary>
    /// <param name="array">源数组，不可为 null。</param>
    /// <param name="asColumn">true 生成 N×1 列向量，false 生成 1×N 行向量。</param>
    /// <returns>CV_32FC1 Mat。</returns>
    /// <exception cref="ArgumentNullException">array 为 null 时抛出。</exception>
    public static Mat ToMat(this float[] array, bool asColumn = true)
    {
        if (array == null)
            throw new ArgumentNullException(nameof(array));

        int rows = asColumn ? array.Length : 1;
        int cols = asColumn ? 1 : array.Length;

        Mat mat = new(rows, cols, MatType.CV_32FC1);
        Marshal.Copy(array, 0, mat.Data, array.Length);

        return mat;
    }

    /// <summary>
    /// 将 CV_32FC1 Mat 按行优先顺序展平为一维 float[]。
    /// </summary>
    /// <param name="mat">CV_32FC1 Mat，不可为 null。</param>
    /// <returns>长度为 Rows × Cols 的一维数组。</returns>
    /// <exception cref="ArgumentException">Mat 类型不是 CV_32FC1 时抛出。</exception>
    public static float[] ToFloatArray(this Mat mat)
    {
        if (mat.Type() != MatType.CV_32FC1)
            throw new ArgumentException("Mat 必须为 CV_32FC1");

        int total = mat.Rows * mat.Cols;
        float[] result = new float[total];
        Marshal.Copy(mat.Data, result, 0, total);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  float[,] ↔ Mat (CV_32FC1)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将二维单精度数组转换为 Mat（CV_32FC1），保持行列形状。
    /// </summary>
    /// <param name="array">源二维数组，不可为 null。</param>
    /// <returns>等尺寸的 CV_32FC1 Mat。</returns>
    /// <exception cref="ArgumentNullException">array 为 null 时抛出。</exception>
    public static Mat ToMat(this float[,] array)
    {
        if (array == null)
            throw new ArgumentNullException(nameof(array));

        int rows = array.GetLength(0);
        int cols = array.GetLength(1);
        int total = rows * cols;

        Mat mat = new(rows, cols, MatType.CV_32FC1);

        float[] flat = new float[total];
        Buffer.BlockCopy(array, 0, flat, 0, total * sizeof(float));
        Marshal.Copy(flat, 0, mat.Data, total);

        return mat;
    }

    /// <summary>
    /// 将 CV_32FC1 Mat 转换为二维 float[,]。
    /// </summary>
    /// <param name="mat">CV_32FC1 Mat，不可为 null。</param>
    /// <returns>等尺寸的 float[,]。</returns>
    /// <exception cref="ArgumentException">Mat 类型不是 CV_32FC1 时抛出。</exception>
    public static float[,] ToFloat2D(this Mat mat)
    {
        if (mat.Type() != MatType.CV_32FC1)
            throw new ArgumentException("Mat 必须为 CV_32FC1");

        int rows = mat.Rows;
        int cols = mat.Cols;
        int total = rows * cols;

        float[] flat = new float[total];
        Marshal.Copy(mat.Data, flat, 0, total);

        float[,] result = new float[rows, cols];
        Buffer.BlockCopy(flat, 0, result, 0, total * sizeof(float));

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  int[] ↔ Mat (CV_32SC1)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将一维整型数组转换为 Mat（CV_32SC1）。
    /// </summary>
    /// <param name="array">源数组，不可为 null。</param>
    /// <param name="asColumn">true 生成 N×1 列向量，false 生成 1×N 行向量。</param>
    /// <returns>CV_32SC1 Mat。</returns>
    /// <exception cref="ArgumentNullException">array 为 null 时抛出。</exception>
    public static Mat ToMat(this int[] array, bool asColumn = true)
    {
        if (array == null)
            throw new ArgumentNullException(nameof(array));

        int rows = asColumn ? array.Length : 1;
        int cols = asColumn ? 1 : array.Length;

        Mat mat = new(rows, cols, MatType.CV_32SC1);
        Marshal.Copy(array, 0, mat.Data, array.Length);

        return mat;
    }

    /// <summary>
    /// 将 CV_32SC1 Mat 按行优先顺序展平为一维 int[]。
    /// </summary>
    /// <param name="mat">CV_32SC1 Mat，不可为 null。</param>
    /// <returns>长度为 Rows × Cols 的一维数组。</returns>
    /// <exception cref="ArgumentException">Mat 类型不是 CV_32SC1 时抛出。</exception>
    public static int[] ToIntArray(this Mat mat)
    {
        if (mat.Type() != MatType.CV_32SC1)
            throw new ArgumentException("Mat 必须为 CV_32SC1");

        int total = mat.Rows * mat.Cols;
        int[] result = new int[total];
        Marshal.Copy(mat.Data, result, 0, total);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  byte[] ↔ Mat (CV_8UC1)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将一维字节数组转换为 Mat（CV_8UC1）。
    /// </summary>
    /// <param name="array">源数组，不可为 null。</param>
    /// <param name="asColumn">true 生成 N×1 列向量，false 生成 1×N 行向量。</param>
    /// <returns>CV_8UC1 Mat。</returns>
    /// <exception cref="ArgumentNullException">array 为 null 时抛出。</exception>
    public static Mat ToMat(this byte[] array, bool asColumn = true)
    {
        if (array == null)
            throw new ArgumentNullException(nameof(array));

        int rows = asColumn ? array.Length : 1;
        int cols = asColumn ? 1 : array.Length;

        Mat mat = new(rows, cols, MatType.CV_8UC1);
        Marshal.Copy(array, 0, mat.Data, array.Length);

        return mat;
    }

    /// <summary>
    /// 将 CV_8UC1 Mat 按行优先顺序展平为一维 byte[]。
    /// </summary>
    /// <param name="mat">CV_8UC1 Mat，不可为 null。</param>
    /// <returns>长度为 Rows × Cols 的一维数组。</returns>
    /// <exception cref="ArgumentException">Mat 类型不是 CV_8UC1 时抛出。</exception>
    public static byte[] ToByteArray(this Mat mat)
    {
        if (mat.Type() != MatType.CV_8UC1)
            throw new ArgumentException("Mat 必须为 CV_8UC1");

        int total = mat.Rows * mat.Cols;
        byte[] result = new byte[total];
        Marshal.Copy(mat.Data, result, 0, total);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Point2f[] / List<Point2f> ↔ Mat (CV_32FC2)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将 <see cref="Point2f"/> 数组转换为 N×1 单列 Mat（CV_32FC2）。
    /// </summary>
    /// <param name="points">2D 点坐标数组，不可为 null。</param>
    /// <returns>N×1 的 CV_32FC2 Mat。</returns>
    /// <exception cref="ArgumentNullException">points 为 null 时抛出。</exception>
    public static Mat ToMat(this Point2f[] points)
    {
        if (points == null)
            throw new ArgumentNullException(nameof(points));

        Mat mat = new(points.Length, 1, MatType.CV_32FC2);

        for (int i = 0; i < points.Length; i++)
        {
            mat.Set(i, 0, points[i]);
        }

        return mat;
    }

    /// <summary>
    /// 将 <see cref="Point2f"/> 列表转换为 N×1 单列 Mat（CV_32FC2）。
    /// </summary>
    /// <param name="points">2D 点坐标列表，不可为 null。</param>
    /// <returns>N×1 的 CV_32FC2 Mat。</returns>
    /// <exception cref="ArgumentNullException">points 为 null 时抛出。</exception>
    public static Mat ToMat(this List<Point2f> points)
    {
        if (points == null)
            throw new ArgumentNullException(nameof(points));

        Mat mat = new(points.Count, 1, MatType.CV_32FC2);

        for (int i = 0; i < points.Count; i++)
        {
            mat.Set(i, 0, points[i]);
        }

        return mat;
    }

    /// <summary>
    /// 将 CV_32FC2 单列 Mat 转换为 <see cref="Point2f"/> 列表。
    /// </summary>
    /// <param name="mat">N×1 的 CV_32FC2 Mat，不可为 null。</param>
    /// <returns>包含 N 个 Point2f 的列表。</returns>
    /// <exception cref="ArgumentException">
    /// Mat 类型不是 CV_32FC2 或不是单列（Cols != 1）时抛出。
    /// </exception>
    public static List<Point2f> ToPoint2fList(this Mat mat)
    {
        if (mat.Type() != MatType.CV_32FC2)
            throw new ArgumentException("Mat 必须为 CV_32FC2（单列 Point2f）");
        if (mat.Cols != 1)
            throw new ArgumentException("Mat 必须为单列（Cols == 1）");

        List<Point2f> list = new(mat.Rows);

        var height = mat.Rows;
        for (int i = 0; i < height; i++)
        {
            list.Add(mat.At<Point2f>(i, 0));
        }

        return list;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Point3f[] / List<Point3f> ↔ Mat (CV_32FC3)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将 <see cref="Point3f"/> 列表转换为 N×1 单列 Mat（CV_32FC3）。
    /// </summary>
    /// <param name="points">3D 点坐标列表，不可为 null。</param>
    /// <returns>N×1 的 CV_32FC3 Mat。</returns>
    /// <exception cref="ArgumentNullException">points 为 null 时抛出。</exception>
    public static Mat ToMat(this List<Point3f> points)
    {
        if (points == null)
            throw new ArgumentNullException(nameof(points));

        Mat mat = new(points.Count, 1, MatType.CV_32FC3);

        for (int i = 0; i < points.Count; i++)
        {
            mat.Set(i, 0, points[i]);
        }

        return mat;
    }

    /// <summary>
    /// 将 CV_32FC3 单列 Mat 转换为 <see cref="Point3f"/> 列表。
    /// </summary>
    /// <param name="mat">N×1 的 CV_32FC3 Mat，不可为 null。</param>
    /// <returns>包含 N 个 Point3f 的列表。</returns>
    /// <exception cref="ArgumentException">
    /// Mat 类型不是 CV_32FC3 或不是单列（Cols != 1）时抛出。
    /// </exception>
    public static List<Point3f> ToPoint3fList(this Mat mat)
    {
        if (mat.Type() != MatType.CV_32FC3)
            throw new ArgumentException("Mat 必须为 CV_32FC3（单列 Point3f）");
        if (mat.Cols != 1)
            throw new ArgumentException("Mat 必须为单列（Cols == 1）");

        List<Point3f> list = new(mat.Rows);

        var height = mat.Rows;
        for (int i = 0; i < height; i++)
        {
            list.Add(mat.At<Point3f>(i, 0));
        }

        return list;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Vec3d[] ↔ Mat (CV_64FC3)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将 <see cref="Vec3d"/> 数组转换为 N×1 单列 Mat（CV_64FC3）。
    /// </summary>
    /// <param name="vectors">Vec3d 数组，不可为 null。</param>
    /// <returns>N×1 的 CV_64FC3 Mat。</returns>
    /// <exception cref="ArgumentNullException">vectors 为 null 时抛出。</exception>
    public static Mat ToMat(this Vec3d[] vectors)
    {
        if (vectors == null)
            throw new ArgumentNullException(nameof(vectors));

        Mat mat = new(vectors.Length, 1, MatType.CV_64FC3);

        for (int i = 0; i < vectors.Length; i++)
        {
            mat.Set(i, 0, vectors[i]);
        }

        return mat;
    }

    /// <summary>
    /// 将 CV_64FC3 单列 Mat 转换为 <see cref="Vec3d"/> 数组。
    /// </summary>
    /// <param name="mat">N×1 的 CV_64FC3 Mat，不可为 null。</param>
    /// <returns>长度为 N 的 Vec3d 数组。</returns>
    /// <exception cref="ArgumentException">
    /// Mat 类型不是 CV_64FC3 或不是单列（Cols != 1）时抛出。
    /// </exception>
    public static Vec3d[] ToVec3dArray(this Mat mat)
    {
        if (mat.Type() != MatType.CV_64FC3)
            throw new ArgumentException("Mat 必须为 CV_64FC3（单列 Vec3d）");
        if (mat.Cols != 1)
            throw new ArgumentException("Mat 必须为单列（Cols == 1）");

        var height = mat.Rows;
        Vec3d[] result = new Vec3d[height];
        for (int i = 0; i < height; i++)
        {
            result[i] = mat.At<Vec3d>(i, 0);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  List<List<Point2f>> → List<Mat>
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将二维 <see cref="Point2f"/> 列表的列表转换为 Mat 列表。
    /// 通常用于为 OpenCV 标定 API 准备多幅图像的角点数据。
    /// </summary>
    /// <param name="src">二维 Point2f 列表，不可为 null。</param>
    /// <returns>每个子列表对应一个 N×1 的 CV_32FC2 Mat。</returns>
    /// <exception cref="ArgumentNullException">src 为 null 时抛出。</exception>
    public static List<Mat> ToMatList(this List<List<Point2f>> src)
    {
        if (src == null)
            throw new ArgumentNullException(nameof(src));

        return src.Select(p => p.ToMat()).ToList();
    }
}
