# CLAUDE.md

本项目为 GM和E78 USB焊接不良检查装置 的 WPF 桌面应用程序。
技术栈：C# net10.0-windows + WPF + MVVM（CommunityToolkit.Mvvm）+ DI（Microsoft.Extensions.Hosting）
自定义入口点 `Program.Main()`，非传统 App.xaml 启动方式。

## 构建与运行

```bash
dotnet restore
dotnet build
dotnet run
```

目标框架：`net10.0-windows10.0.17763.0`（WPF WinExe）

## 架构概览

**MVVM + DI** — CommunityToolkit.Mvvm 源生成器驱动 MVVM，Microsoft.Extensions.Hosting 管理 DI 容器。所有 ViewModel 和服务在 `Program.ConfigureServices()` 中注册。

**导航** — 自定义区域导航（`Services/NavigationService.cs`），View 注册 `ContentControl` 区域（`RegionNames` 常量：`Shell`、`Main`、`Modal`、`Sidebar`）。通过 `[NavigationViewModel]` 特性、手动注册或命名约定来映射 View⇔ViewModel。支持导航历史（`GoBackAsync`）、弱引用缓存和拦截器管道（`INavigationInterceptor`）。

**MainWindow** 作为外壳，承载 `BaseLayoutView`，内部 ContentControl 是页面导航容器。

### 通信层（当前分支：`InputValidation`）

3 个硬件设备由统一的 `IDeviceConnectionManager` 管理：

| 设备 | 接口 | 驱动 | 协议 |
|---|---|---|---|
| PLC（松下 FP0H） | `IPlcDevice` | `PlcCommunicationAdapter` → `TcpClientPLCMotionService` | Modbus TCP :502 |
| 万用表（固纬 GDM-9060） | `IMultimeterDevice` | `GwInstekGDM9060Driver` | SCPI over TCP :5025 |
| 扫描枪（霍尼韦尔 H1900） | `IScannerDevice` | `HoneywellH1900Scanner` | 串口（USB虚拟串口） |

所有设备实现 `ICommunicationDevice`（`ConnectAsync`/`DisconnectAsync`/`IsConnected`/`ConnectionStateChanged`）。`DeviceConnectionManager`（单例）启动时从 `IDeviceSettingsService` 加载配置，注入到各驱动后并行连接三台设备，带指数退避重试。ViewModel 通过订阅事件而非直接管理连接。

`PlcCommunicationAdapter` 适配 `TcpClientPLCMotionService`（`Action<bool>` 委托）到 `ICommunicationDevice` 接口（`EventHandler<bool>`）。

### 检测流程

`InspectionEngine` 编排完整检测流程：PLC 继电器切换 → 万用表测量 → 判定 → 下一引脚 → 保存。使用 `SemaphoreSlim` 保证线程安全，`CancellationToken` 支持中止。检测项目来自 `PlanModel.InspectItems`，每项包含引脚名、检测方式（导通/电阻值）、阈值配置。

### 数据模型

- **日志记录**：`LogRecord`（主检测记录）→ `PinResult`（每引脚详情），CSV 文件存储（`CsvTestRecordStorage` 为当前注入实现，`SqliteTestRecordStorage` 为遗留实现）
- **方案存储**：JSON 文件，目录结构 `{方案根目录}/{机种名}/{方案名}.json`。`PlanModel` 含 `MachineType`、`PlanName`、`List<PlanItem>`。`IPlanStorageService` 处理 CRUD
- **检测记录存储**：`ITestRecordStorage` 接口，双实现 — `CsvTestRecordStorage`（活跃，当前注入）和 `SqliteTestRecordStorage`（遗留）。CSV 使用 `CsvStoragePathManager` + UTF-8 BOM
- **作业员管理**：`IOperatorStateService`（当前作业员、登录登出）+ `IOperatorStorageService`（JSON 文件持久化）

### ViewModel

12 个 ViewModel。关键：
- `TestPageViewModel` — 运行页面；订阅 `IDeviceConnectionManager` 事件，实时显示连接状态，通过 `InspectionEngine` 控制检测。`TestUIState` 枚举 `Ready → CanStart → Testing → PendingSave`
- `PlanSettingViewModel` / `PlanEditViewModel` — 管理检测方案（机种→方案层次）
- `LogDataViewModel` — 读取 CSV 日志，动态列生成，复合搜索
- `SystemSettingsViewModel` — 设备通信参数（IP、端口、波特率）

### WPF 值转换器

位于 `Common/Converters/`。关键：`JudgmentToBackgroundConverter`（OK=绿色，NG=红色）、`JudgmentToForegroundConverter`、`ConnectionStatusToBackgroundConverter`、`TestStatusToColorConverter`。

## 配置

- `appsettings.json` — 数据库连接串、Serilog 设置、`CsvStorage`（根路径/最大行数/编码）、`PlanStorage`（每个机种最大方案数）
- `DeviceSettings.json`（`AppConfig/DeviceConfigs/`）— 各设备通信参数
- 数据库：`app.db`（SQLite，仓库根目录）。DEBUG 模式下 `Program.cs` 设置 `RECREATE_DATABASE_ON_EACH_RUN = true`，每次启动重建数据库并填充种子数据

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

- **中文 UI/注释** — 用户界面和代码注释使用中文。这是一个中文工业检测系统
- **ObservableProperty** — 全项目 ViewModel 和 Model 使用 CommunityToolkit.Mvvm 源生成器（例如 `[ObservableProperty]` 私有字段）
- **异步模式** — 服务使用 `async`/`await` + `CancellationToken`。导航生命周期：`INavigationAware.OnNavigatedToAsync` / `OnNavigatedFromAsync`
- **Dispatcher** — 所有 UI 绑定状态更新通过 `Application.Current.Dispatcher.Invoke()` 路由（见 `DeviceConnectionManager` 事件处理）
- **文件范围命名空间** — 使用 .NET 文件范围命名空间
- **日志记录** — 所有用户操作、校验拒绝和设备事件均使用 Serilog 记录日志。安全审计使用 `Warning` 级别

## 遗留/未使用代码

- `FinsTcpUtil.cs`（欧姆龙 FINS 协议）— 未使用，当前 PLC 使用 Modbus TCP
- `ITcpServerPLCMotionService` — 预留的服务器模式，未实现
- `MainViewModel.cs` / `MainWindowViewModel.cs` — 空桩代码
- `SqliteTestRecordStorage` — 已注册但未通过 `ITestRecordStorage` 注入；CSV 实现为活跃使用
