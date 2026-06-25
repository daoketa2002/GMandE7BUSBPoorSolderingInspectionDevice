# CLAUDE.md

> **角色定位**：你是本项目的 C# 程序员。老板雇佣了你，你是家里的经济支柱，一家老小靠你养。上一任程序员因为 bug 被开除了，你必须认真对待每一行代码。对老板的要求要反复确认，给出最完美的技术方案和代码，绝不容许任何疏忽。

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
| PLC | 松下 FP0H（AFP0HC32ET） | `IPlcDevice` → `PlcCommunicationAdapter` → `TcpClientPLCMotionService` | Modbus TCP :502 |
| 万用表 | 固纬 GDM-9060 | `IMultimeterDevice` → `GwInstekGDM9060Driver` | SCPI over TCP :5025 |
| 扫描枪 | 霍尼韦尔 H1900 | `IScannerDevice` → `HoneywellH1900Scanner` | 串口（USB虚拟串口） |

自定义入口点 `Program.Main()`，非传统 App.xaml 启动方式。

## 工作要求

> **必须在输出代码前先输出改动方案，等老板确认后再执行。**
> 代码要有中文注释说明意图，属性和方法名、类名要见名知义。
> 代码要尽量解耦，必要的地方要有日志（Serilog，关键操作使用 Warning 级别做审计）。

---

## 构建与运行

```bash
dotnet restore
dotnet build
dotnet run
```

目标框架：`net10.0-windows10.0.17763.0`（WPF WinExe）

## 程序目录结构

```
{程序目录}/
├── Common/
│   ├── Behaviors/
│   │   └── NumericTextBoxBehavior.cs    # 数字输入拦截附加行为
│   ├── Converters/                       # 12 个 WPF 值转换器
│   └── Validators/
│       └── InputValidationHelper.cs      # 通用输入校验工具类
│
├── Models/                              # 数据模型
│   ├── PlanModel.cs                     # 方案模型（MachineType/PlanName/Items）
│   ├── LogRecord.cs                     # 检测记录模型（主记录）
│   ├── PinResult.cs                     # Pin 检测结果明细
│   ├── TestItemModel.cs                 # 运行界面 DataGrid 行绑定
│   └── OperatorModel.cs                 # 作业员模型
│
├── ViewModels/                          # 12 个 ViewModel
│   ├── MainMenuViewModel.cs            # 主菜单
│   ├── TestPageViewModel.cs            # 运行界面
│   ├── PlanSettingViewModel.cs         # 方案设定
│   ├── PlanEditViewModel.cs            # 方案编辑
│   ├── LogDataViewModel.cs             # 日志数据
│   ├── SystemSettingsViewModel.cs      # 系统设置
│   ├── OperatorSettingsViewModel.cs    # 作业员设定
│   ├── PasswordDialogViewModel.cs      # 密码弹窗
│   └── ...
│
├── Views/                               # 对应的 XAML 视图
│   ├── MainMenuView.xaml
│   ├── TestPageView.xaml
│   ├── PlanSettingView.xaml
│   ├── PlanEditView.xaml
│   ├── LogDataView.xaml
│   ├── SystemSettingsView.xaml
│   ├── OperatorSettingsView.xaml
│   └── PasswordDialog.xaml
│
├── Services/                            # 服务层
│   ├── NavigationService.cs            # 区域导航
│   ├── InspectionEngine.cs             # 检测流程引擎
│   ├── PlanStorageService.cs           # 方案 JSON 文件 CRUD
│   ├── CsvTestRecordStorage.cs         # CSV 日志存储（当前活跃）
│   └── SqliteTestRecordStorage.cs      # SQLite 日志存储（遗留）
│
├── 设置/
│   ├── 设备设置/
│   │   └── DeviceSettings.json
│   ├── 方案设置/                         # 方案 JSON 文件
│   │   ├── T998248391/
│   │   │   ├── 方案A.json
│   │   │   └── 方案B.json
│   │   └── ...
│   ├── currentOperator.json             # 当前作业员
│   └── operators.json                   # 作业员列表
│
├── 数据/
│   └── TestLog/                         # CSV 日志文件
│       ├── 2026-06/
│       │   ├── T998248391_方案A.csv
│       │   └── ...
│       └── ...
│
├── appsettings.json
└── GMandE7BUSBPoorSolderingInspectionDevice.exe
```

## 架构概览

**MVVM + DI** — CommunityToolkit.Mvvm 源生成器驱动 MVVM，Microsoft.Extensions.Hosting 管理 DI 容器。所有 ViewModel 和服务在 `Program.ConfigureServices()` 中注册。

**导航** — 自定义区域导航（`Services/NavigationService.cs`），View 注册 `ContentControl` 区域（`RegionNames` 常量：`Shell`、`Main`、`Modal`、`Sidebar`）。通过 `[NavigationViewModel]` 特性、手动注册或命名约定来映射 View⇔ViewModel。支持导航历史（`GoBackAsync`）、弱引用缓存和拦截器管道（`INavigationInterceptor`）。

**MainWindow** 作为外壳，承载 `BaseLayoutView`，内部 ContentControl 是页面导航容器。

### 页面导航流程

```
MainMenuView  (主菜单)
│
├── TestPageView          (运行界面)  ← 直接进入
├── OperatorSettingsView  (作业员设定) ← 直接进入
├── PlanSettingView       (方案设定)   ← 需要密码 (📋 蓝色 #3498DB)
│   └── PlanEditView      (方案编辑)   ← 已密码验证过，直接进入
├── LogDataView           (日志数据)   ← 直接进入
├── SystemSettingsView    (系统设置)   ← 需要密码 (⚙ 橙色 #E67E22)
└── PLC通信测试            (开发中)
```

密码弹窗根据入口不同显示不同文案和强调色（`PasswordDialogContext` 枚举控制）。

### 通信层

3 个硬件设备由统一的 `IDeviceConnectionManager` 管理，所有设备实现 `ICommunicationDevice` 接口（`ConnectAsync`/`DisconnectAsync`/`IsConnected`/`ConnectionStateChanged`）。`DeviceConnectionManager`（单例）启动时从 `IDeviceSettingsService` 加载配置，注入到各驱动后并行连接三台设备，带指数退避重试。ViewModel 通过订阅事件而非直接管理连接。

`PlcCommunicationAdapter` 适配 `TcpClientPLCMotionService`（`Action<bool>` 委托）到 `ICommunicationDevice` 接口（`EventHandler<bool>`）。

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

**固定列（前 6 列 + 日期时间）：**
```
序号,机种名称,序列号,方案名称,检查者,综合判定,B4-B5,B5-B6,...,日期,时间
1,T998248391,xxxxxxxx,方案A,张三,OK,2.5,OPEN,...,2026年06月25日,14时30分00秒
```

**动态列**：每个检测项目（如 B4-B5、A1-A2）展开为 CSV 中的独立列。不同方案文件列数不同。

#### 方案存储（JSON 文件）

位置：`设置/方案设置/{机种名}/{方案名}.json`

```json
{
  "MachineType": "T998248391",
  "PlanName": "方案A",
  "CreatedTime": "2026-06-24T16:15:08.4373085+08:00",
  "LastModifiedTime": "2026-06-24T16:23:06.5969716+08:00",
  "InspectItems": [
    {
      "Id": "bf8e917a-d964-4b28-9e69-e9406cac90c3",
      "Name": "A1-A2",
      "Order": 1,
      "CheckMode": "导通",
      "LowerLimit": null,
      "UpperLimit": null,
      "ModeValue": "OPEN"
    },
    {
      "Id": "086e83a7-e9de-490e-a32c-d80db7689ab8",
      "Name": "A3-A4",
      "Order": 2,
      "CheckMode": "电阻值",
      "LowerLimit": 1.0,
      "UpperLimit": 100.0,
      "ModeValue": null
    }
  ]
}
```

| 字段 | 导通模式 | 电阻值模式 |
|------|----------|------------|
| `ModeValue` | `"OPEN"` 或 `"SHORT"`（期望结果） | `null`（运行时由万用表填充） |
| `LowerLimit` | `null` | 电阻阈值下限（如 1.0） |
| `UpperLimit` | `null` | 电阻阈值上限（如 100.0） |

#### 设备配置（JSON 文件）

位置：`设置/设备设置/DeviceSettings.json`

```json
{
  "fP0HCommunication": {
    "ipAddress": "192.168.1.3",
    "port": 502,
    "slaveId": 1,
    "receiveTimeoutMs": 5000,
    "sendTimeoutMs": 5000,
    "reconnectDelayMs": 2000,
    "maxReconnectAttempts": 12,
    "healthCheckMode": 3,
    "healthCheckIntervalSeconds": 5
  },
  "scannerSerialCommunication": {
    "serialNumber": "COM8",
    "baudRate": 9600,
    "parity": "None",
    "dataBits": 8,
    "stopBits": "1",
    "flowControl": "None",
    "healthCheckMode": 1,
    "healthCheckIntervalSeconds": 5,
    "lastDataTimeoutSeconds": 30
  },
  "gdM9060Communication": {
    "ipAddress": "192.168.1.4",
    "port": 5025,
    "receiveTimeoutMs": 5000,
    "sendTimeoutMs": 5000,
    "healthCheckMode": 0,
    "healthCheckIntervalSeconds": 5,
    "lastDataTimeoutSeconds": 30
  },
  "isPlcCommunicationTestEnabled": false
}
```

#### 核心模型关系

```
PlanModel                      LogRecord
 ├── MachineType (机种名)        ├── Timestamp (检测时间)
 ├── PlanName   (方案名)         ├── SerialNumber (序列号)
 └── Items[]   (检测项目)        ├── FinalResult (OK/NG)
     ├── CheckMode (导通/电阻值)  ├── Operator (作业员)
     ├── LowerLimit               └── PinResults[] (检测明细)
     ├── UpperLimit                    ├── PinName (如 A1-A2)
     └── ModeValue (OPEN/SHORT/实测值) └── Result (测量值/判定)
```

- **方案存储**：JSON 文件，`{方案根目录}/{机种名}/{方案名}.json`，`IPlanStorageService` 处理 CRUD
- **日志存储**：CSV 文件，`CsvTestRecordStorage`（当前注入） / `SqliteTestRecordStorage`（遗留）
- **作业员管理**：`IOperatorStateService`（当前作业员） + `IOperatorStorageService`（JSON 持久化）

### View / ViewModel 映射（10 个页面）

| 视图（View） | ViewModel | 密码保护 | 功能说明 |
|---|---|---|---|
| `MainMenuView` | `MainMenuViewModel` | 无 | 主菜单导航入口 |
| `TestPageView` | `TestPageViewModel` | 无 | 运行界面——检测主流程 |
| `PlanSettingView` | `PlanSettingViewModel` | ✅ 密码（📋 蓝） | 方案列表浏览/选择 |
| `PlanEditView` | `PlanEditViewModel` | 无（已验过密码） | 方案新增/编辑 |
| `LogDataView` | `LogDataViewModel` | 无 | CSV 日志检索/分页/导出 |
| `SystemSettingsView` | `SystemSettingsViewModel` | ✅ 密码（⚙ 橙） | 设备通信参数配置 |
| `OperatorSettingsView` | `OperatorSettingsViewModel` | 无 | 作业员增删改管理 |
| `PasswordDialog` | `PasswordDialogViewModel` | — | 密码验证弹窗本身 |
| `ExitConfirmDialog` | `ExitConfirmViewModel` | 无 | 退出确认弹窗 |
| `BaseLayoutView` | （代码后置） | 无 | 外壳容器，无独立 VM |

### WPF 值转换器（12 个）

全部位于 `Common/Converters/`：

| 转换器 | 用途 |
|--------|------|
| `BoolToVisibilityConverter` | bool → Visibility.Visible/Collapsed |
| `BoolInverterConverter` | bool 取反 |
| `BoolToConnectionColorConverter` | 连接状态 → 颜色 |
| `BoolToOpacityConverter` | bool → 透明度 |
| `BoolToTextConverter` | bool → 文字 |
| `ConnectionStatusToBackgroundConverter` | 连接状态 → 背景色 |
| `DataSourceToVisibilityConverter` | 数据源 → 可见性 |
| `IntPlusOneConverter` | 索引从 0→1 显示 |
| `JudgmentToBackgroundConverter` | OK=绿色/NG=红色 背景 |
| `JudgmentToForegroundConverter` | OK=绿色/NG=红色 前景 |
| `SortDirectionConverter` | 排序方向 |
| `TestStatusToColorConverter` | 测试状态 → 颜色 |

## 配置

- `appsettings.json` — 数据库连接串、Serilog 设置、`CsvStorage`（根路径/最大行数/编码）、`PlanStorage`（每个机种最大方案数）
- `DeviceSettings.json`（`设置/设备设置/`）— 各设备通信参数（IP、端口、串口、超时等）
- 数据库：`app.db`（SQLite，仓库根目录）。DEBUG 模式下 `Program.cs` 设置 `RECREATE_DATABASE_ON_EACH_RUN = true`，每次启动重建并填充种子数据

### 密码验证（PasswordDialog）

验证密码入口有两处，使用 `PasswordDialogContext` 枚举区分：

| 入口 | 上下文 | 图标 | 强调色 | 说明文字 |
|------|--------|------|--------|----------|
| 方案设定 | `PlanSettings` | 📋 | 蓝色 `#3498DB` | 需要管理员权限才能访问方案设定 |
| 系统设置 | `SystemSettings` | ⚙ | 橙色 `#E67E22` | 需要管理员权限才能访问系统设置 |

密码硬编码（中控台后续会改为可配置），审计日志使用 `Warning` 级别记录验证时间和结果。

---

## 输入校验与限制规范

### 公共校验组件

| 组件 | 路径 | 说明 |
|------|------|------|
| `NumericTextBoxBehavior` | `Common/Behaviors/NumericTextBoxBehavior.cs` | WPF 附加行为，拦截非数字字符输入。键盘 `PreviewTextInput` + 粘贴 `DataObject.Pasting` 双通道。支持 `AllowDecimal`、`AllowNegative`、`MinValue`、`MaxValue` |
| `InputValidationHelper` | `Common/Validators/InputValidationHelper.cs` | 静态校验工具类，提供 IP/端口/串口/引脚/电阻值/超时/心跳等格式和范围校验方法 |

### 方案编辑页（PlanEditView + PlanEditViewModel）

| # | 校验项 | 触发时机 | 限制规则 |
|---|--------|----------|----------|
| 1 | 引脚格式 | `SavePlanAsync` | 正则 `^[AB]\d+$`（大写A/B + 数字） |
| 2 | 电阻值下限范围 | `SavePlanAsync` | 0 ~ 99,999,999 Ω，排除 NaN/∞/负数 |
| 3 | 电阻值上限范围 | `SavePlanAsync` | 同上 |
| 4 | 下限≤上限 | `SavePlanAsync` | 下限值不能大于上限值 |
| 5 | 项目数量上限 | `AddItem` | 最多 50 项 |
| 6 | 必填项 | `SavePlanAsync` | 机种名、方案名、至少一个项目 |
| 7 | 数值输入拦截 | XAML `NumericTextBoxBehavior` | 下限/上限 TextBox 仅允许数字+小数点（0~99,999,999） |
| 8 | 方案名称长度 | XAML `MaxLength="50"` | 最长 50 字符 |
| 9 | CheckMode 联动 | `OnCheckModeChanged` | 导通→清空上下限；电阻→清空 ModeValue |

### 系统设置页（SystemSettingsView + SystemSettingsViewModel）

| # | 字段 | 所属设备 | 限制规则 |
|---|------|----------|----------|
| 10 | `IpAddress` | PLC / 万用表 | 合法 IPv4 格式（xxx.xxx.xxx.xxx，每段0-255） |
| 11 | `Port` | PLC / 万用表 | 1 ~ 65535 |
| 12 | `SlaveId` | PLC | 1 ~ 247（Modbus 标准） |
| 13 | `ReceiveTimeoutMs` | PLC / 万用表 | 50 ~ 120,000 ms |
| 14 | `SendTimeoutMs` | PLC / 万用表 | 50 ~ 120,000 ms |
| 15 | `ReconnectDelayMs` | PLC | 100 ~ 60,000 ms |
| 16 | `MaxReconnectAttempts` | PLC | 1 ~ 100 次 |
| 17 | `HealthCheckIntervalSeconds` | PLC / 扫描枪 / 万用表 | 1 ~ 3,600 秒 |
| 18 | `SerialNumber`（串口号） | 扫描枪 | 正则 `^COM\d+$`，数字 1~256 |
| 19 | `LastDataTimeoutSeconds` | 扫描枪 / 万用表 | 3 ~ 3,600 秒 |

以上全部在 `ValidateAllConfigs()` 中统一校验，保存时触发。

### 运行测试页（TestPageView + TestPageViewModel）

| # | 校验项 | 触发时机 | 限制规则 |
|---|--------|----------|----------|
| 20 | 机种名长度 | XAML `MaxLength="50"` | 最长 50 字符 |
| 21 | 序列号长度 | XAML `MaxLength="50"` | 最长 50 字符 |
| 22 | 方案名有效性 | `OnSchemeNameChanged` | 必须存在于下拉列表中，否则红色边框+ToolTip 提示 |
| 23 | 检测中禁止扫码 | `OnScannerBarcodeParsed` | Testing/PendingSave 状态忽略扫码输入 |

### 日志查询页（LogDataViewModel）

| # | 校验项 | 触发时机 | 限制规则 |
|---|--------|----------|----------|
| 24 | 日期区间 | `SearchAsync` | 开始日期不能晚于结束日期 |

### 作业员设定页（OperatorSettingsView + OperatorSettingsViewModel）

| # | 校验项 | 触发时机 | 限制规则 |
|---|--------|----------|----------|
| 25 | 作业员名长度 | XAML `MaxLength="20"` | 最长 20 字符 |
| 26 | 重名检查 | `AddOrUpdateAsync` | 忽略大小写+去空格匹配，重名弹确认框 |
| 27 | 自动去空格 | `AddOrUpdateAsync` | 首尾空格自动去除 |
| 28 | 非空检查 | `AddOrUpdateAsync` | 空或空白弹 Warning |

### 密码验证（PasswordDialogViewModel）

| # | 审计事件 | 日志级别 | 说明 |
|---|----------|----------|------|
| 29 | 密码验证成功 | `Warning` | 记录操作时间 |
| 30 | 密码验证失败 | `Warning` | 记录操作时间 |

### Bug 修复记录

| # | 文件 | 修复内容 |
|---|------|----------|
| 31 | `InspectionEngine.cs:552` | `TestPointConfig.CheckMode` 默认值英文 `"Continuity"` → 中文 `"导通"`（与 `CheckModeConstants` 一致） |
| 32 | `InspectionEngine.cs:198` | 日志中硬编码 `"Resistance"` → `CheckModeConstants.Resistance`（保持一致性） |

## 关键约定

- **先出方案再执行** — 任何改动必须先输出方案，让老板确认后再写代码
- **中文 UI/注释** — 用户界面和代码注释使用中文。这是一个中文工业检测系统
- **ObservableProperty** — 全项目 ViewModel 和 Model 使用 CommunityToolkit.Mvvm 源生成器（例如 `[ObservableProperty]` 私有字段）
- **异步模式** — 服务使用 `async`/`await` + `CancellationToken`。导航生命周期：`INavigationAware.OnNavigatedToAsync` / `OnNavigatedFromAsync`
- **Dispatcher** — 所有 UI 绑定状态更新通过 `Application.Current.Dispatcher.Invoke()` 路由（见 `DeviceConnectionManager` 事件处理）
- **文件范围命名空间** — 使用 .NET 文件范围命名空间
- **日志记录** — 所有用户操作、校验拒绝和设备事件均使用 Serilog 记录。安全审计使用 `Warning` 级别
- **密码弹窗** — 使用 `PasswordDialogContext` 枚举区分入口上下文，不同上下文使用不同图标/标题/说明/强调色
- **CSV 编码** — 日志文件使用 UTF-8 BOM 编码，确保中文正常显示

## 遗留/未使用代码

- `FinsTcpUtil.cs`（欧姆龙 FINS 协议）— 未使用，当前 PLC 使用 Modbus TCP
- `ITcpServerPLCMotionService` — 预留的服务器模式，未实现
- `MainViewModel.cs` / `MainWindowViewModel.cs` — 空桩代码
- `SqliteTestRecordStorage` — 已注册但未通过 `ITestRecordStorage` 注入；CSV 实现为活跃使用
