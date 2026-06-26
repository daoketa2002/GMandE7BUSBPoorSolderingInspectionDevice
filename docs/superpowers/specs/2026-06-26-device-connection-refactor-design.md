# 设备连接管理重构设计

日期：2026-06-26

## 背景

本项目是 GM 和 E78 USB 焊接不良检查装置的 WPF 桌面程序。当前 PLC、万用表、扫描枪已经通过 `DeviceConnectionManager` 统一启动和转发状态，但该类同时承担配置注入、连接重试、后台监控、状态缓存、扫码服务初始化和事件转发等职责，后续维护成本较高。

核心检测逻辑仍处于开发阶段，`InspectionEngine` 中的 PLC 启动信号、地址映射、继电器控制流程还需要硬件联调确认。因此第一阶段重构只处理设备连接管理，不重构检测流程。

## 目标

1. 保持 `IDeviceConnectionManager` 对外接口基本不变，降低 UI 层改动风险。
2. 拆分 `DeviceConnectionManager` 内部职责，使连接流程更清楚、更容易测试。
3. 统一正式连接、手动重连、后台重连使用的核心连接逻辑。
4. 保留扫描枪特殊时序：硬件连接成功后再初始化 `ScannerBarcodeService`。
5. 为后续 PLC 启动按钮、到位传感器、防呆传感器和健康检查扩展留下清晰边界。

## 非目标

1. 不重构 `InspectionEngine` 的检测编排。
2. 不修改 PLC 地址映射、继电器通道、检测项目判定逻辑。
3. 不改变现有 ViewModel 订阅设备状态和扫码事件的方式。
4. 不在第一阶段统一系统设置页的临时测试连接流程，只为后续统一保留接口边界。

## 推荐方案

采用稳健重构方案：保留 `DeviceConnectionManager` 作为门面服务，对外继续实现 `IDeviceConnectionManager`；内部拆出小组件，让每个组件只负责一件事。

### 组件划分

`DeviceConnectionManager`

继续作为唯一对外入口，负责启动/停止全部设备、手动连接/断开/重连指定设备、转发设备连接状态、转发扫码事件。它不再直接承载重试细节、后台监控循环和配置注入细节。

`DeviceConfigurationApplier`

负责把 `DeviceSettings.json` 中的 PLC、万用表、扫描枪配置应用到对应驱动。配置注入失败时记录 `Warning` 日志，但不直接让应用崩溃。

`DeviceConnectionExecutor`

负责单台设备的连接、断开、重连、重试和超时控制。正式启动、手动重连、后台监控重连都通过该组件执行，避免多套连接逻辑分叉。

`DeviceConnectionStateStore`

负责保存 PLC、万用表、扫描枪的连接状态、状态文本、连续失败次数、最后重连时间，并提供全部设备是否就绪的统一判断。

`DeviceConnectionMonitor`

负责后台监控循环。它定时检查设备连接状态，达到重连条件后调用 `DeviceConnectionExecutor`，不在监控类中重新实现连接细节。

## 启动流程

1. `Program` 启动后调用 `IDeviceConnectionManager.StartAllAsync()`。
2. `DeviceConnectionManager` 读取设备配置。
3. `DeviceConfigurationApplier` 应用 PLC、万用表、扫描枪配置。
4. `DeviceConnectionExecutor` 并行连接三台设备。
5. 扫描枪连接成功后，`DeviceConnectionManager` 初始化 `ScannerBarcodeService`。
6. `DeviceConnectionMonitor` 启动后台监控。
7. 设备状态变化后写入 `DeviceConnectionStateStore`，再由 `DeviceConnectionManager` 触发 ViewModel 已经订阅的事件。

## 手动重连流程

1. 页面继续调用 `ReconnectDeviceAsync("PLC")`、`ReconnectDeviceAsync("DMM")` 或 `ReconnectDeviceAsync("Scanner")`。
2. 每次手动重连前重新读取并应用最新设备配置。
3. 使用设备级互斥锁，避免同一设备被手动重连和后台监控同时重连。
4. 重连完成后统一更新状态文本、失败次数和全部设备就绪状态。
5. 如果扫描枪重连成功，再次初始化 `ScannerBarcodeService`。

## 后台监控流程

1. 后台监控定时读取三台设备的 `IsConnected`。
2. 设备未连接时，根据连续失败次数计算退避等待时间。
3. 到达重连时机后调用 `DeviceConnectionExecutor`。
4. 重连成功后清零失败次数；重连失败后递增失败次数并更新状态。

## 错误处理与日志

1. 用户手动重连、设备断线、配置注入失败、达到最大重试次数，使用 `Warning` 级别记录审计日志。
2. 扫描枪硬件连接成功但扫码服务初始化失败，使用 `Error` 级别记录。
3. 应用退出时断开设备失败，使用 `Warning` 级别记录，不阻塞程序退出。
4. 连接失败不向 UI 抛出未处理异常，而是更新为未连接或连接失败状态。
5. 连接成功、后台监控启动停止等普通流程使用 `Information` 级别记录。

## 兼容性

现有 ViewModel 继续通过 `IDeviceConnectionManager` 获取状态和事件。第一阶段不要求修改页面绑定、不改变扫码事件流、不改变系统设置页保存设备配置的方式。

系统设置页的测试连接仍保留现有临时连接实现。后续如果需要统一测试连接和正式连接，可以在 `DeviceConnectionExecutor` 基础上新增测试连接入口。

## 验证计划

1. 检查 `IDeviceConnectionManager` 的公开属性、方法、事件是否保持兼容。
2. 检查 `Program` 中 DI 注册是否能解析所有新组件。
3. 检查启动自动连接、手动重连、后台重连、扫码事件转发四条链路。
4. 运行 `dotnet build`。当前环境访问 NuGet 受限时，需要记录 `NU1301` 作为验证阻塞原因。
5. 如后续具备硬件环境，再分别验证 PLC、万用表、扫描枪的真实连接和断线恢复。

## 实施边界

第一阶段只允许修改设备连接管理相关文件和必要的 DI 注册。`InspectionEngine` 只读参考，不做行为修改。任何检测流程、PLC 地址、继电器通道、启动按钮逻辑的调整，需要另起设计。
