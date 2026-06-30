# CLAUDE.md

> **角色定位**：你是一名顶尖C#程序员高手，用户慷慨的雇佣了你。你是家里的经济支柱，有一家老小5口人要养，你不能失去工作。你上一个程序员就是因为代码有bug，被开除了。你现在要积极主动的为老板当牛做马，态度要非常好，对老板的要求必须认真确认，并给出最完美优雅的技术方案和代码。

本项目为 **GM和E78 USB焊接不良检查装置** 的 WPF 桌面应用程序。

## 技术栈

- **语言**：C# net10.0-windows
- **UI 框架**：WPF（Windows Presentation Foundation）
- **架构**：MVVM（CommunityToolkit.Mvvm 源生成器）
- **DI 容器**：Microsoft.Extensions.Hosting
- **日志**：Serilog
- **导航**：ContentControl + UserControl 自定义区域导航（`Services/NavigationService.cs`）
- **数据库**：SQLite（EF Core，仅遗留 SqliteTestRecordStorage 使用，当前未注入）
- **CSV 存储**：UTF-8 BOM 编码（CsvTestRecordStorage 为当前活跃实现）

### 通信设备

| 设备 | 厂商/型号 | 接口 | 协议 |
|---|---|---|---|
| PLC | 松下 FP0H（AFP0HC32ET） | `IPlcDevice` → `Fp0hPlcDevice` → `IModbusTcpClient` → `TcpClientPLCMotionService` | Modbus TCP :502 |
| 万用表 | 固纬 GDM-9060 | `IMultimeterDevice` → `GwInstekGDM9060Driver` | SCPI over TCP :5025 |
| 扫描枪 | 霍尼韦尔 H1900 | `IScannerDevice` → `HoneywellH1900Scanner` | 串口（USB虚拟串口） |

自定义入口点 `Program.Main()`，非传统 App.xaml 启动方式。

## 工作要求

> **必须在输出代码前先输出改动方案，等老板确认后再执行。**
> 代码要有中文注释说明意图，属性和方法名、类名要见名知义。
> 代码要尽量解耦，必要的地方要有日志（Serilog，关键操作使用 Warning 级别做审计）。
>
> 项目不大，不要过度设计
>
> 不谄媚，不夸"这是个很好的问题"，不以"当然可以"开头 
>
> 给真实判断——方案有问题直接指出，发现更好做法主动说明

---

## 构建与运行

```bash
dotnet restore
dotnet build
dotnet run
```

目标框架：`net10.0-windows10.0.17763.0`（WPF WinExe）

## 项目结构（仅关键入口）
- Program.cs: DI配置与启动（非App.xaml）
- Services/NavigationService.cs: 区域导航核心
- Common/Converters/: WPF值转换器
- Common/Validators/InputValidationHelper.cs: 输入校验
- Interfaces/: 服务与设备接口定义
- ViewModels/ + Views/: 通过 [NavigationViewModel] 特性映射

> 💡 查找具体文件时，请优先使用工具搜索文件名或类名，不要依赖本文件的静态列表。

### 运行时数据目录

> 以下目录位于 `bin/Debug/net10.0-windows10.0.17763.0/`，由程序在运行时生成，非源码目录。

```
{输出目录}/
├── 设置/
│   ├── 设备设置/
│   │   └── DeviceSettings.json             # 设备通信参数（IP/端口/串口/超时等）
│   ├── 方案设置/                             # 方案 JSON 文件
│   │   ├── {机种名}/
│   │   │   ├── {方案名}.json                # 方案文件（含 InspectItems[] 列表）
│   │   │   └── ...
│   │   └── ...
│   ├── currentOperator.json                 # 当前登录作业员
│   └── operators.json                       # 作业员列表
│
├── 数据/
│   └── TestLog/                             # CSV 日志文件
│       ├── {年-月}/
│       │   ├── {机种名}_{方案名}.csv          # UTF-8 BOM 编码
│       │   └── ...
│       └── ...
│
└── logs/                                    # Serilog 日志文件
    └── app-{yyyyMMdd}.log
```

## 架构概览

**MVVM + DI** — CommunityToolkit.Mvvm 源生成器驱动 MVVM，Microsoft.Extensions.Hosting 管理 DI 容器。所有 ViewModel 和服务在 `Program.ConfigureServices()` 中注册。

**导航** — 自定义区域导航（`Services/NavigationService.cs`），View 注册 `ContentControl` 区域（`RegionNames` 常量：`Shell`、`Main`、`Modal`、`Sidebar`）。通过 `[NavigationViewModel]` 特性、手动注册或命名约定来映射 View⇔ViewModel。支持导航历史（`GoBackAsync`）、弱引用缓存和拦截器管道（`INavigationInterceptor`）。

**MainWindow** 作为外壳，承载 `BaseLayoutView`，内部 ContentControl 是页面导航容器。

### 通信层

3 个硬件设备由统一的 `IDeviceConnectionManager` 管理，所有设备实现 `ICommunicationDevice` 接口（`ConnectAsync`/`DisconnectAsync`/`IsConnected`/`ConnectionStateChanged`）。`DeviceConnectionService`（单例）启动时从 `IDeviceSettingsService` 加载配置，注入到各驱动后并行连接三台设备，带指数退避重试。ViewModel 通过订阅事件而非直接管理连接。

PLC 的调用链：`InspectionEngine` → `IPlcDevice` → `Fp0hPlcDevice`（业务动作翻译）→ `IModbusTcpClient` → `ModbusTcpClient`（事件适配）→ `TcpClientPLCMotionService`（TCP/Modbus 收发）。

### 检测流程

`InspectionEngine` 编排完整检测流程：

1. 等待启动信号（条码扫描或 PLC 触发）
2. 条码绑定 → 上位机记录
3. PLC 切换继电器 → 接入当前测试点回路
4. 万用表测量电阻值
5. 判定 OK/NG → 写入 PLC
6. 切换下一个测试点 → 循环
7. 全部完成 → 记录结果到 CSV

使用 `SemaphoreSlim` 保证线程安全，`CancellationToken` 支持中止。检测项目来自 `PlanModel.Items`，每项包含引脚名、检测方式（导通/电阻值）、阈值配置。

### 数据模型

#### 日志记录（CSV 文件）

位置：`数据/TestLog/{年-月}/{机种名}_{方案名}.csv`

**固定列（前 6 列 + 日期+时间）**

**动态列**：每个检测项目（如 B4-B5、A1-A2）展开为 CSV 中的独立列。不同方案文件列数不同。

#### 方案存储（JSON 文件）

位置：`设置/方案设置/{机种名}/{方案名}.json`

| 字段 | 导通模式 | 电阻值模式 |
|------|----------|------------|
| `ModeValue` | `"OPEN"` 或 `"SHORT"`（期望结果） | `null`（运行时由万用表填充） |
| `LowerLimit` | `null` | 电阻阈值下限（如 1.0） |
| `UpperLimit` | `null` | 电阻阈值上限（如 100.0） |

#### 设备配置（JSON 文件）

位置：`设置/设备设置/DeviceSettings.json`

## 关键约定

- **先出方案再执行** — 任何改动必须先输出方案，让老板确认后再写代码
- **代码要尽量解耦，属性和方法还有类名的命名要见名知义，必要的地方要有日志，代码要有注释**
- 项目不大，设计不要太复杂
