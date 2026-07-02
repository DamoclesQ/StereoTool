using OpenCvSharp;

namespace StereoTool.Models;

/// <summary>
/// 视差图生成结果，包含 CV_32F 视差图和对应的 Q 重投影矩阵。
/// 调用方可用 Q 矩阵执行 ReprojectImageTo3D 获得 XYZ 空间坐标。
/// 实现 IDisposable，用完后需释放内部 Mat 对象。
/// </summary>
public class DisparityResult : IDisposable
{
    /// <summary>
    /// 构造函数，接收视差图和 Q 矩阵。
    /// </summary>
    /// <param name="disparity">CV_32F 视差图（实际视差值）</param>
    /// <param name="q">4×4 双精度重投影矩阵</param>
    public DisparityResult(Mat disparity, Mat q)
    {
        Disparity = disparity;
        Q = q;
    }

    /// <summary>CV_32F 全图视差（实际值，已除以16）</summary>
    public Mat Disparity { get; init; }

    /// <summary>4×4 重投影矩阵 Q，用于 ReprojectImageTo3D 反算空间坐标</summary>
    public Mat Q { get; init; }

    /// <summary>释放内部 Mat 对象</summary>
    public void Dispose()
    {
        Disparity?.Dispose();
        Q?.Dispose();
    }
}
