# StereoTool 双目相机工具

.NET 10 / WPF / OpenCvSharp 桌面工具，支持棋盘格单目标定、双目标定和离线双目测距。

## 界面与运行逻辑

- 左侧导航切换单目标定、双目标定和双目测距，切换时保留各页数据。
- 标定页顶部按“导入 → 角点分析 → 标定”排列操作，并提示当前下一步；左侧选择参与图片，中间检查角点、校正图、误差和参数，右侧编辑设置并查看结果。
- 图片列表和设置侧栏可拖动分隔线调整宽度；预览初始适配显示区域，Ctrl + 滚轮缩放，侧栏内容超出高度时滚动查看。
- 已有图片时启用角点分析，至少 3 张/对分析成功且参与的样本才能标定。分析和求解期间锁定输入，角点分析支持取消。
- 测距页将原图、立体校正检查、视差与深度、完整标定参数分为标签；右侧集中显示 XYZ、对应点、输入摘要和数据导出。参数和左右图齐全后启用 SGBM，左右原图对应点齐全后启用手动测距。

运行职责：MainViewModel 只维护导航选择和发送请求，MainWindow 负责取得页面；三个工作流 ViewModel 管理输入、后台运算、结果失效和显示状态；独立类库中的 CalibrationService 执行标定，界面项目中的 DistanceService 执行立体校正与测距。图片数据集和参与勾选发生变化时，界面同步更新剩余数量、误差统计和操作提示。

## 标定操作

参考 MATLAB Camera Calibrator 和 Stereo Camera Calibrator 的操作顺序：

**导入图片 → 角点分析 → 标定 → 评估误差及校正图 → 排除并重新标定 → 导出。**

- [MATLAB 单目标定操作](https://www.mathworks.com/help/vision/ug/using-the-single-camera-calibrator-app.html)
- [MATLAB 双目标定操作](https://www.mathworks.com/help/vision/ug/using-the-stereo-camera-calibrator-app.html)

这里采用 OpenCV 针孔相机和棋盘格算法。参数文件为 OpenCV YAML，不能作为 MATLAB 会话文件使用。

### 单目标定

1. 设置棋盘格**内角点**列数、行数，以及每格真实尺寸（mm）。内角点数不是方格数。
2. 点击“导入图片”，选择一个图片文件夹。可以继续导入其他文件夹；相同路径不会重复添加。导入只添加图片，不自动检测角点。
3. 点击“角点分析”，在后台检测角点并显示处理数量。失败图片保留在列表中并显示原因，不参与求解。
4. 检查检测点及黄色原点标记，至少选中 3 张有效图片，再点击“标定 / 重新标定”。
5. 在“误差与筛选”检查逐图误差，在“去畸变检查”检查直线。可取消“参与”，或按阈值排除高误差图片，然后重新标定。
6. 确认后导出 YAML。重新勾选可恢复被排除的图片；“移除”仅移除数据集记录，不删除原图。

建议采集 10～20 张图片，覆盖画面不同位置和多种倾角。当前针孔模型默认估计 k1、k2，可选择估计 k3 和切向畸变 p1、p2；不估计非零轴倾斜系数。

### 双目标定

1. 分别点击“导入左图”和“导入右图”，各自选择对应文件夹，顺序不限。每次导入保留另一侧图片，并重新按名称配对，不自动检测角点。
2. 文件按名称配对：支持双目录同名图片、left01/right01、left_01/right_01。数字前导零被忽略。缺少对应图的记录会保留，重复编号歧义会报错，不使用错位顺序配对。
3. 点击“角点分析”，完成后检查左右图检测点，尤其确认黄色原点和棋盘格点的对应关系。
4. 选择内参方式：
   - 默认联合估计左右内参与双目外参。
   - 勾选“使用固定内参”，导入左右两份单目标定 YAML。该模式保持内参和畸变不变，仅求双目外参。图片尺寸必须与内参文件一致。
5. 标定后检查逐对重投影误差和双目校正预览。校正图的对应点应位于同一水平线上。
6. 排除问题图片对后重新标定，再导出 YAML，或点击“用于测距”直接传入测距页。

单目使用 CalibrateCamera 的逐图位姿计算误差。双目固定已求出的相机参数，再为每对图片拟合一个共同棋盘格位姿，计算左右重投影误差；不会用两个独立棋盘格位姿掩盖配对错误。

### 结果与状态

- 平均误差：检测点与投影点的二维欧氏距离均值，单位 px。
- “误差与筛选”的最大误差取自剩余勾选参与图片（双目为图片对）的列表平均重投影误差，与列表数值及排除阈值使用同一口径，单位 px。勾选或排除后立即按上一轮误差更新，重新标定后使用新的拟合误差；没有有效误差时显示“--”。
- “误差与筛选”同时显示剩余参与标定的图片数量（双目按对计数），随排除和手动勾选立即更新。“恢复全选”重新勾选全部角点分析成功的图片，待检测、检测失败或缺图记录不参与。
- RMS：OpenCV 标定返回的全局均方根重投影误差，与平均误差分开展示。
- 误差条按当前列表最大值统一缩放，不截断高于 3 px 的结果；排除记录淡化显示，保留上一轮误差供比较。
- 改变参与图片、格子尺寸或畸变模型后，旧参数立即失效，需重新标定。
- 导入图片后，当前数据集回到待分析状态，需先点击“角点分析”，再执行标定。
- 改变内角点行列数后，保留图片并清空检测缓存，需重新分析。
- 排除图片后保留上一轮误差，便于继续筛选；重新标定后更新。
- 加载参数仅用于查看、校正和测距，不恢复原始图片会话；旧文件没有记录的误差均值显示为未知。
- OpenCV 标定求解期间界面仍可响应。“取消分析”在当前图片检测完成后生效，不中断底层标定求解。

## 双目测距

1. 加载双目标定 YAML，或从双目标定页点击“用于测距”。
2. 导入左右图片，尺寸必须相同且与标定图片一致。
3. 打开“立体校正检查”：参数和左右图齐全后自动显示去畸变与极线校正结果。左右图并排显示，可共同缩放、平移；绿色线对应同一图像行，用于检查对应点高度。参考线只用于显示，不参与匹配。更换图片或参数后自动刷新，尺寸不符时清空预览并提示所需尺寸。
4. 确认后点击“SGBM 匹配”：采用与预览相同的校正映射 → 视差 → Q 矩阵重投影 → XYZ。
5. 点击左原图时，先把原图像素转换到校正坐标再查询 XYZ；深度伪彩图直接查询校正坐标。
6. “手动对应点测距”需要在左右原图选取同一个物理点，无需先执行 SGBM。程序先去畸变与校正，检查行差和正视差，再用 Q 计算 XYZ。
7. 保存视差和 XYZ 为浮点 TIFF。XYZ TIFF 是三通道坐标图。

测量坐标为校正左相机坐标系下的 **X、Y、Z（mm）**，其中 Z 为前向深度。无有效深度时显示“--”。更换图片或标定参数后，旧视差、XYZ 和测量值会清空。原图和视差/深度图的标记分别显示，避免将校正图选点用于原图手动测距。

## 示例数据

资料/calImage/left 和 right 中有棋盘格图片。使用这些图片时设置内角点 **9 × 6**、格子尺寸 **40 mm**，与应用初始格子尺寸 20 mm 区分。

标定平移和重建坐标的单位由格子尺寸决定；输入 mm 才输出 mm。相机内参中的 fx、fy 使用像素单位。参数文件保留 PixelSizeUm 元数据，以兼容已有 YAML 文件。

## 项目结构

- StereoTool.Calibration：独立的 .NET 10 标定服务类库，不引用 WPF 或界面项目。
- StereoTool.Calibration/Services/CalibrationService.cs：单目/双目标定及 YAML 读写。
- StereoTool.Calibration/Services/CalibrationService.Analysis.cs：图片配对、角点分析、误差拟合及 Mat 预览。
- StereoTool.Calibration/Models：棋盘格配置、检测记录和单目/双目标定参数。沿用现有可观察模型，保留属性变更通知，不依赖 WPF。
- StereoTool.Calibration/Utils/CvConvert.cs：C# 数组和 OpenCV Mat 转换。
- StereoTool/App.xaml.cs / MainWindow：依赖注入、启动和导航，通过项目引用调用标定服务。
- StereoTool/ViewModels / Views：单目、双目和测距工作流及 WPF 页面，支持 Ctrl+滚轮缩放。
- StereoTool/Services/DistanceService.cs：立体校正、SGBM 和三维重建。
- StereoTool/Models/StereoRectifyResult.cs：测距使用的校正矩阵和映射。

页面与 ViewModel 均为 Singleton，切换页面保留当前数据。

## 在其他项目调用标定服务

引用 StereoTool.Calibration/StereoTool.Calibration.csproj 后，可直接创建现有服务类：

~~~csharp
using StereoTool.Calibration.Models;
using StereoTool.Calibration.Services;

var service = new CalibrationService();
var config = new ChessboardConfig
{
    CornerCols = 9,
    CornerRows = 6,
    SquareSize = 40
};
var leftPaths = service.GetImagePaths(leftFolder);
var rightPaths = service.GetImagePaths(rightFolder);

var images = service.AnalyzeImages(leftPaths, config);
var mono = service.CameraCalibrate(config,
    images.Where(image => image.IsDetected && image.IsIncluded));
service.SaveCalibrationParams(monoFilePath, mono);

var pairs = service.AnalyzePairs(service.PairImages(leftPaths, rightPaths), config);
var stereo = service.StereoCalibrate(config,
    pairs.Where(pair => pair.IsDetected && pair.IsIncluded));
service.SaveStereoCalibrationParams(stereoFilePath, stereo);
~~~

示例中的文件夹和输出文件路径由调用方提供。分析方法支持 IProgress 和 CancellationToken；标定方法为同步计算，后台调度和异常处理由调用方负责。固定内参模式通过 StereoCalibrate 的 fixedLeft、fixedRight 参数传入两份单目结果。

服务返回现有标定模型和 OpenCV Mat；预览 Mat 由调用方释放，WPF 图像转换留在界面项目。类库使用 OpenCvSharp4 和 CommunityToolkit.Mvvm；OpenCV 本机运行库由宿主提供，当前 WPF 应用和控制台验证项目已引用 OpenCvSharp4.runtime.win。YAML 字段与原版本兼容。

## 构建与验证

在仓库根目录执行：

~~~powershell
dotnet build StereoTool.slnx
dotnet run --project tests/CalibrationServiceProbe/CalibrationServiceProbe.csproj -- .
dotnet run --project tests/CalibrationWorkflowProbe/CalibrationWorkflowProbe.csproj -- .
~~~

CalibrationServiceProbe 是仅引用标定类库的 .NET 10 控制台程序，不启用 WPF，使用真实图片验证独立调用及 YAML 往返，结果写入 .tmp/calibration-service-verification。

CalibrationWorkflowProbe 使用仓库图片及合成几何，检查配对、失败诊断、单目/双目求解、固定内参、YAML 兼容、误差筛选状态、后台调度、坐标转换与 SGBM。页面离屏预览和验证 YAML 写入 .tmp/calibration-verification，不覆盖示例文件。
