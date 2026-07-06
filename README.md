# 双目相机工具 (StereoTool)

基于 **OpenCV + WPF** 的双目立体视觉桌面应用，提供相机标定与立体测距一体化工作流。

## ✨ 功能特性

| 模块 | 说明 |
|------|------|
| **单目标定** | 棋盘格角点亚像素检测 + `CalibrateCamera` 求解内参矩阵与畸变系数 |
| **双目标定** | `StereoCalibrate` 联合标定，解算双相机外参（R/T/E/F） |
| **双目测距** | SGBM 半全局立体匹配 → 视差图 → `ReprojectImageTo3D` 生成 XYZ 空间坐标 |
| **视差图可视化** | 视差图归一化 + 伪彩色渲染（Jet colormap），支持伪彩色开关切换 |
| **即时测距** | 缓存 XYZ 点云，点击图像任意位置实时读取距离值 |
| **YAML 持久化** | 标定结果通过 OpenCV `FileStorage` 读写为标准 YAML 格式 |

## 🖥️ 界面预览

- 左侧导航面板：**双目测距** / **双目标定** / **单目标定** 三个功能入口
- 测距页面：TabControl 切换原始双目图像与视差图，支持左右分栏拖拽调整比例
- 标定页面：棋盘格配置、图片导入、角点检测、标定执行、结果展示

## 🛠️ 技术栈

| 技术 | 版本 | 用途 |
|------|------|------|
| .NET | 10.0 (net10.0-windows) | 运行时框架 |
| WPF | — | 桌面 UI 框架 |
| [OpenCvSharp4](https://github.com/shimat/opencvsharp) | 4.13.0 | 计算机视觉（标定、SGBM、重投影） |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MVVM 架构（命令、消息、IoC） |
| [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/) | 10.0.9 | 依赖注入容器 |
| [Extended.Wpf.Toolkit](https://github.com/xceedsoftware/wpftoolkit) | 5.1.2 | WPF 扩展控件 |
| [Microsoft.Xaml.Behaviors.Wpf](https://github.com/microsoft/XamlBehaviorsWpf) | 1.1.142 | XAML 行为支持 |

## 📁 项目结构

```
StereoTool/
├── Models/                     # 数据模型
│   ├── CalibrationParams.cs        # 单目标定结果
│   ├── StereoCalibrationParams.cs  # 双目标定结果
│   ├── StereoRectifyResult.cs      # 双目校正参数（remap 映射 + Q 矩阵）
│   ├── ChessboardConfig.cs         # 棋盘格标定板配置
│   ├── DetectedImage.cs            # 单图角点检测结果
│   └── DetectedImagePair.cs        # 图像对角点检测结果
├── Services/                   # 业务逻辑
│   ├── CalibrationService.cs       # 标定服务（角点检测 / 单目&双目标定 / YAML读写）
│   └── DistanceService.cs          # 测距服务（SGBM立体匹配 / 视差图生成 / XYZ计算）
├── ViewModels/                 # MVVM ViewModel 层
│   ├── MainViewModel.cs            # 主窗口导航状态
│   ├── CalibrationViewModel.cs     # 单目标定页面逻辑
│   ├── StereoCalibrationViewModel.cs # 双目标定页面逻辑
│   └── DistanceViewModel.cs        # 测距页面逻辑（视差图显示 / 伪彩色 / 点击测距）
├── Views/                      # WPF 页面
│   ├── MainWindow.xaml             # 主窗口（侧边导航 + Frame 内容区）
│   ├── CalibrationPage.xaml        # 单目标定页
│   ├── StereoCalibrationPage.xaml  # 双目标定页
│   └── DistancePage.xaml           # 测距页（双目图像 / 视差图 Tab 切换）
├── Helpers/                    # 值转换器等辅助类
├── Utils/                      # 工具类（Mat ↔ double[] 转换）
├── Resources/                  # 字体、图片等静态资源
├── App.xaml.cs                 # 应用入口（DI 容器配置）
└── StereoTool.csproj           # 项目文件
```

## 📖 使用说明

### 1. 相机标定

1. 进入**双目标定**（或**单目标定**）页面
2. 配置棋盘格参数（内角点列数/行数、格子物理尺寸）
3. 导入左右相机拍摄的棋盘格图片
4. 点击**角点检测**，系统自动识别并精化棋盘格角点
5. 点击**执行标定**，系统依次完成：
   - 单目标定 → 获得内参矩阵和畸变系数
   - （双目标定）立体标定 → 获得外参 R/T/E/F
6. 标定结果可**保存为 YAML** 文件，下次启动直接加载

### 2. 双目测距

1. 加载已标定好的 YAML 参数文件
2. 加载左右相机拍摄的待测图像对
3. 系统自动执行：
   - `Remap` 立体校正
   - `StereoSGBM` 视差计算
   - `ReprojectImageTo3D` 生成 XYZ 空间坐标
4. 在左图（或视差图）上**点击任意像素点**，右侧显示该点的三维坐标和距离
5. 支持**伪彩色开关**：视差图可在灰度/彩色之间切换

## 📐 核心算法流程

```
┌──────────────┐    ┌──────────────┐
│   Left Image │    │  Right Image │
└──────┬───────┘    └──────┬───────┘
       │                   │
       ▼                   ▼
  ┌─────────┐         ┌─────────┐
  │  Remap  │         │  Remap  │    ← 立体校正（StereoRectify）
  └────┬────┘         └────┬────┘
       │                   │
       └────────┬──────────┘
                ▼
        ┌──────────────┐
        │  StereoSGBM  │              ← 半全局立体匹配
        └──────┬───────┘
               ▼
        ┌──────────────┐
        │  Disparity   │              ← 视差图 (CV_32F)
        └──────┬───────┘
               ▼
        ┌──────────────┐
        │ ReprojectTo3D│              ← Q 矩阵重投影
        └──────┬───────┘
               ▼
        ┌──────────────┐
        │   XYZ Map    │              ← 空间坐标缓存
        └──────┬───────┘
               ▼
        ┌──────────────┐
        │ Click → Dist │              ← 点击即得距离
        └──────────────┘
```

## 🏗️ 架构设计

- **MVVM 模式**：View ↔ ViewModel ↔ Service，数据绑定驱动 UI 更新
- **DI 容器**：`Microsoft.Extensions.DependencyInjection` 统一管理依赖，Service 以 Singleton 注册
- **CommunityToolkit.Mvvm**：`[ObservableProperty]` / `[RelayCommand]` 减少样板代码
- **标定-测距解耦**：标定参数通过 YAML 文件传递，`StereoRectifyResult` 缓存校正映射避免重复计算

---

> 💡 标定用棋盘格图片示例位于 `资料/calImage/` 目录下。
