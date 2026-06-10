# GM和E78 USB焊接不良检查装置系统

基于 WPF + MVVM 的上位机自动化检测系统，用于 GM/E78 型号 USB 连接器焊接不良的电阻检测。

---

## 目录结构

```
GMandE7BUSBPoorSolderingInspectionDevice/
├── App.xaml / App.xaml.cs              # 应用程序入口，Fluent 主题加载
├── Program.cs                          # 主机配置 (Generic Host)，DI容器，数据库初始化
├── AssemblyInfo.cs                     # 程序集信息
├── .gitattributes / .gitignore         # Git配置
├── LICENSE.txt                         # 许可证
├── README.md                           # ← 本文件
│
├── AppConfig/                          # 配置管理
│   ├── appsettings.json                # 主配置文件 (连接串、Serilog、应用设置)
│   ├── ApplicationSettings.cs          # 应用设置模型
│   └── DatabaseSettings.cs             # 数据库设置
│
├── Common/                             # 公共组件
│   ├── Converters/                     # WPF 值转换器 (7个)
│   │   ├── BoolInverterConverter.cs            # bool取反
│   │   ├── BoolToTextConverter.cs              # bool→文本
│   │   ├── BoolToVisibilityConverter.cs        # bool→可见性
│   │   ├── ConnectionStatusToBackgroundConverter.cs  # 连接状态→背景色
│   │   ├── DataSourceToVisibilityConverter.cs  # 数据源→可见性
│   │   ├── JudgmentToBackgroundConverter.cs    # 判定结果→背景色
│   │   └── SortDirectionConverter.cs           # 排序方向转换
│   └── Navigation/                     # 区域化导航框架
│       ├── RegionNames.cs              # 导航区域常量 (Main / Shell / Modal / Sidebar)
│       ├── NavigationViewModelAttribute.cs     # 视图→ViewModel 特性绑定
│       ├── NavigationHistoryEntry.cs           # 导航历史条目
│       ├── NavigationEventArgs.cs              # 导航事件参数
│       ├── NavigationStateChangedEventArgs.cs  # 导航状态变更参数
│       └── NavigationCanceledException.cs      # 导航取消异常
│
├── Data/                               # 数据层
│   └── AppDbContext.cs                 # EF Core 数据库上下文 (SQLite)
│
├── Devices/                            # 硬件驱动层
│   ├── GwInstekGDM9060Driver.cs        # 固纬 GDM-9060 万用表驱动 (SCPI over TCP)
│   └── HoneywellH1900Scanner.cs        # 霍尼韦尔 H1900 扫描枪驱动 (USB虚拟串口)
│
├── Interfaces/                         # 接口定义 (10个)
│   ├── INavigationService.cs           # 导航服务接口
│   ├── INavigationAware.cs             # 导航生命周期感知
│   ├── INavigationInterceptor.cs       # 导航拦截器
│   ├── INotificationService.cs         # 消息通知服务
│   ├── IStatefulViewModel.cs           # ViewModel状态保存/恢复
│   ├── IParameterizedView.cs           # 参数化视图
│   ├── ISettingsService.cs             # 设置读写服务
│   ├── IAppSettingsService.cs          # 应用设置服务
│   ├── ITcpClientPLCMotionService.cs   # PLC Modbus TCP 客户端接口
│   └── ITcpServerPLCMotionService.cs   # PLC Modbus TCP 服务端接口 (预留)
│
├── Models/                             # 数据模型
│   ├── BarcodeReceivedEventArgs.cs     # 条码接收事件
│   ├── CommunicationNotification.cs    # 通信通知模型
│   ├── HealthCheckMode.cs              # 心跳检测模式枚举
│   ├── NotificationType.cs             # 通知类型枚举
│   ├── OperatorModel.cs                # 操作员模型
│   ├── TestItemModel.cs                # 测试项目数据模型
│   ├── PLC动作控制/                     # PLC动作控制相关
│   │   ├── AlarmState.cs               # 报警状态
│   │   ├── PollingDataEventArgs.cs     # 轮询数据事件
│   │   ├── PollingTaskConfig.cs        # 轮询任务配置
│   │   ├── ResponseMatchType.cs        # 响应匹配类型
│   │   └── TcpTestStep.cs              # TCP测试步骤
│   └── TCP报文相关/                     # Modbus TCP 协议
│       ├── ModbusMessageHelper.cs       # Modbus报文解析
│       ├── ModbusRequest.cs             # Modbus请求
│       ├── ModbusResponse.cs            # Modbus响应
│       ├── ModbusRtuMessageHelper.cs    # Modbus RTU 报文
│       └── ModbusTcpMessageHelper.cs    # Modbus TCP 报文生成/解析
│
├── PLC通讯模块/                         # PLC通信 (来自其他项目的遗留代码)
│   ├── FinsTcpUtil.cs                  # 欧姆龙 FINS/TCP 协议实现 (当前项目未使用)
│   └── PlcConnectionState.cs           # PLC连接状态枚举
│
├── Services/                           # 服务实现层
│   ├── NavigationService.cs            # 核心导航服务 (区域注册、缓存、拦截器)
│   ├── NotificationService.cs          # 消息通知服务
│   ├── SettingsService.cs              # JSON设置读写服务
│   ├── ConfigManagerService.cs         # 配置热更新管理
│   ├── DatabaseInitializer.cs          # 数据库初始化 (种子数据)
│   ├── MemoryMonitorService.cs         # 内存使用监控
│   ├── InspectionEngine.cs             # ★ 检测流程引擎 (核心业务逻辑)
│   └── TcpModbus/                      # Modbus TCP 通信
│       ├── TcpClientPLCMotionService.cs         # Modbus TCP 客户端 (连接/读写/心跳/重连)
│       └── TcpPLCMotionWPFUIModbusService.cs   # WPF UI层封装 (线程安全)
│
├── ViewModels/                         # 视图模型 (MVVM)
│   ├── MainMenuViewModel.cs            # 主菜单VM (导航按钮、作业员选择、密码验证)
│   ├── MainViewModel.cs                # [空存根，待实现]
│   ├── MainWindowViewModel.cs          # [空存根，待实现]
│   ├── TestPageViewModel.cs            # ★ 测试页面VM (硬件连接、检测控制、统计)
│   ├── OperatorSelectionViewModel.cs   # 作业员选择对话框VM
│   ├── PasswordDialogViewModel.cs      # 密码验证对话框VM
│   └── ExitConfirmViewModel.cs         # 退出确认对话框VM
│
├── Views/                              # WPF视图 (XAML)
│   ├── MainWindow.xaml/.cs             # ★ 主窗口 (ContentControl区域导航宿主)
│   ├── MainMenuView.xaml/.cs           # 主菜单页 (运行界面/数据提取/系统设置)
│   ├── TestPageView.xaml/.cs           # ★ 测试运行页 (状态面板/DataGrid/日志)
│   ├── BaseLayoutView.xaml/.cs         # 基础布局页
│   ├── OperatorSelectionDialog.xaml/.cs # 作业员选择对话框
│   ├── PasswordDialog.xaml/.cs         # 密码验证对话框
│   └── ExitConfirmDialog.xaml/.cs      # 退出确认对话框
│
└── 设置相关类/                          # 设置模型 (来自其他项目的遗留代码)
    └── ApplicationSettings.cs           # TcpServer/TcpClient/Scanner/PLC通用设置
```

---

## 技术栈

| 层 | 技术 |
|---|---|
| **运行时** | .NET 10 (net10.0-windows10.0.17763.0) |
| **UI 框架** | WPF + Fluent Theme |
| **MVVM** | CommunityToolkit.Mvvm (源生成器) |
| **DI 容器** | Microsoft.Extensions.Hosting |
| **日志** | Serilog (Debug输出 + 文件滚动日志) |
| **数据库** | SQLite (EF Core) |
| **PLC 通信** | Modbus TCP (松下 FP0H AFP0HC32ET) |
| **万用表通信** | SCPI over TCP Socket (固纬 GDM-9060) |
| **扫描枪通信** | System.IO.Ports (霍尼韦尔 H1900, USB虚拟串口) |
| **导航** | 自定义区域化导航 (ContentControl + UserControl) |

---

## 系统架构

```
┌─────────────────────────────────────────────────────────┐
│                    WPF 上位机 (指挥官)                     │
│  ┌─────────┐  ┌──────────┐  ┌──────────────────────┐              │
│  │ 导航服务       │    │ 检测引擎           │  │   万用表驱动 (SCPI)                        │              │
│  │ContentCtrl     │    │Inspection          │  │ GwInstekGDM9060Driver                       │            │
│  │+Region         │     │Engine             │  │ TCP: 192.168.1.4:5025                       │           │
│  └─────────┘  └──────────┘  └──────────────────────┘              │
│                      │                      │            │
│              ┌───────┴───────┐               │                                                │
│              │ Modbus TCP                   │               │ LAN口                                            │
│              │ 客户端服务                   │               │ SCPI指令                                           │
│              └───────┬───────┘               │                                                    │
└──────────────────────┼───────────────────────┼────────────┘
                       │                       │
              ┌────────▼────────┐    ┌─────────▼─────────┐
              │  松下 FP0H PLC                   │    │ 固纬 GDM-9060                       │
              │  (IO扩展板+安全)                 │    │ 万用表                              │
              │                                  │    │                                     │
              │ 输入: 启动按钮                    │    │ 测量: 电阻值                       │
              │ 输出: 继电器阵列                 │    │ 返回: 数字量                        │
              │ 输出: 绿灯/红灯                  │    └───────────────────┘
              └─────────────────┘
```

---

## 关键类说明

### InspectionEngine.cs — 核心检测引擎

| 特性 | 说明 |
|---|---|
| 状态枚举 | `Idle → Initializing → Testing → CompletedPass/CompletedFail → Aborted/Error` |
| 测试条件 | `Open`(开路检查>阈值), `Short`(短路检查<阈值), `Resistance`(电阻区间检查) |
| PLC交互 | 通过 Modbus 0x05 写线圈、0x06 写寄存器 |
| 万用表交互 | `CONF:RES` 切换模式 → `MEAS?` 读数 |
| 线程安全 | `SemaphoreSlim` 防止重入，`CancellationToken` 支持中止 |

### TcpClientPLCMotionService.cs — Modbus TCP 客户端

| 特性 | 说明 |
|---|---|
| 协议 | Modbus TCP (MBAP头 + PDU) |
| 功能码 | 0x01(读线圈), 0x03(读寄存器), 0x05(写线圈), 0x06(写寄存器), 0x0F(写多线圈), 0x10(写多寄存器) |
| 事务匹配 | 事务ID + `ConcurrentDictionary<ushort, TaskCompletionSource>` |
| 断线重连 | 指数退避重连，最大 N 次 |
| 心跳检测 | CommandResponse / DataActivity / StatusQuery 三种模式 |

### GwInstekGDM9060Driver.cs — 万用表驱动

| 特性 | 说明 |
|---|---|
| 协议 | SCPI over TCP (端口 5025) |
| 验证 | 连接后发 `*IDN?` 确认设备身份 |
| 测量 | `CONF:RES` → 等 100ms 稳定 → `MEAS?` → 解析返回数值 |
| 容错 | 正则提取数值、连接丢失自动重连 |

### NavigationService.cs — 区域化导航

| 特性 | 说明 |
|---|---|
| 区域注册 | `ContentControl` → `WeakReference` 区域字典 |
| 视图创建 | DI容器创建 + 弱引用缓存 (可配Transient/Cached) |
| ViewModel绑定 | 特性 `[NavigationViewModel]` / 手动映射 / 命名约定 |
| 生命周期 | `INavigationAware.OnNavigatedToAsync` / `OnNavigatedFromAsync` |
| 拦截器 | `INavigationInterceptor` 管道 |
| 历史 | Stack<NavigationHistoryEntry>，支持 GoBack |

---

## 快速开始

### 环境要求

- Windows 10/11 x64
- .NET 10 SDK
- Visual Studio 2022 或 JetBrains Rider

### 编译运行

```bash
# 还原依赖
dotnet restore

# 编译 (Debug)
dotnet build

# 运行
dotnet run
```

### 硬件连接

| 设备 | 接口 | 默认地址 |
|---|---|---|
| 松下 FP0H PLC | Ethernet (Modbus TCP) | 192.168.1.x (端口 502) |
| 固纬 GDM-9060 | LAN (SCPI) | 192.168.1.4:5025 |
| 霍尼韦尔 H1900 | USB 虚拟串口 | COM9 (115200-8-N-1) |

### 配置文件

编辑 `AppConfig/appsettings.json` 配置数据库连接、日志级别等。

---

## 项目状态

### ✅ 已完成

- [x] WPF MVVM 基础框架 (DI, 导航, 通知)
- [x] Modbus TCP 完整协议栈 (读写线圈/寄存器、心跳、重连)
- [x] 万用表 SCPI 驱动 (完整、稳定)
- [x] 扫描枪串口驱动
- [x] 检测流程引擎 `InspectionEngine`
- [x] 测试页面 UI (状态面板、DataGrid、日志)
- [x] 作业员选择/密码验证对话框
- [x] Serilog 日志系统

### ⚠️ 待完善

- [ ] **PLC 握手逻辑** — InspectionEngine 当前由 UI 按钮触发，需改为监听 PLC M100 标志位自动触发
- [ ] **ExecuteWriteOperationAsync 不支持 0x05(写单线圈)** — InspectionEngine 调用会失败
- [ ] XAML 引用但未实现的转换器: `JudgmentToForegroundConverter`, `BoolToConnectionColorConverter`, `TestStatusToColorConverter`
- [ ] `MainViewModel.cs` / `MainWindowViewModel.cs` 为空存根
- [ ] 主窗口标题仍为旧项目名 "Gb2Gb3TraceSystem"
- [ ] `FinsTcpUtil.cs` 为欧姆龙 FINS 协议（遗留代码，当前项目使用 Modbus，不相关）
- [ ] `设置相关类/ApplicationSettings.cs` 中的设置模型需要与新系统对齐
- [ ] 数据提取页面未实现
- [ ] 系统设置页面未实现
- [ ] InspectionConfig 的 10 个默认测试点为硬编码示例，需从数据库/配置文件加载
- [ ] 数据库种子数据为空

---

## 许可证

见 [LICENSE.txt](./LICENSE.txt)
