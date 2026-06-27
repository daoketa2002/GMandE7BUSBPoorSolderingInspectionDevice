# PLC 模块重构设计

日期：2026-06-27

## 背景

本项目当前 PLC 使用松下 FP0H（AFP0HC32ET）并通过 Modbus TCP 通信。现有 PLC 相关代码包含 `TcpClientPLCMotionService`、`ITcpClientPLCMotionService`、`PlcCommunicationAdapter`、`TcpPLCMotionWPFUIModbusService`、Modbus 报文工具、轮询模型以及旧 FINS 代码。

现状的主要问题不是 Modbus 能力不足，而是 PLC 对业务层存在两条入口：连接状态通过 `IPlcDevice` 和 `PlcCommunicationAdapter` 管理，检测流程又直接依赖 `ITcpClientPLCMotionService` 执行 Modbus 写入。随着后续可能增加 PLC 调试页、传感器轮询、报警读取、动作脚本和地址配置，这种入口分叉会继续增加维护成本。

参考项目中的 PLC 分层更完整，包含 UI 封装服务、通信接口、TCP 客户端服务、Modbus 报文工具和通知/报警/测试步骤模型。本项目不应直接照搬 UI 封装层，但应吸收其“业务入口、通信接口、协议工具、扩展模型”分层思想。

## 目标

1. 让业务层只依赖一个 PLC 入口：`IPlcDevice`。
2. 将 PLC 业务动作和 Modbus TCP 通信能力分离。
3. 保留后续扩展 PLC 调试、报警读取、传感器轮询、动作脚本和原始报文测试的空间。
4. 避免 `InspectionEngine`、ViewModel 直接散落功能码、线圈地址和寄存器地址。
5. 降低一次性重构风险，先新增新结构并迁移核心业务调用，再逐步清理旧入口。

## 非目标

1. 不在第一阶段重写完整 PLC 通信栈。
2. 不直接删除当前 `TcpClientPLCMotionService` 的成熟收发逻辑。
3. 不立即实现复杂 PLC 调试页、动作脚本引擎或通用轮询框架。
4. 不修改方案存储、CSV 存储、万用表和扫描枪业务逻辑。
5. 不在没有硬件联调的情况下调整实际 PLC 地址含义。

## 推荐架构

```text
ViewModel / InspectionEngine
        │
        ▼
IPlcDevice
        │
        ▼
Fp0hPlcDevice
        │
        ▼
IModbusTcpClient
        │
        ▼
ModbusTcpClient
        │
        ▼
ModbusTcpMessageHelper / ModbusResponse
```

辅助模型：

```text
PlcAddressMap
PlcOperationResult
PlcNotification
PlcAlarmState
ModbusTcpClientOptions
```

## 核心组件

`IPlcDevice`

业务层唯一依赖的 PLC 接口。它继承通用设备生命周期能力，并扩展 FP0H 检测业务动作，例如应用配置、读取启动信号、写入 Busy/OK/NG/Error、选择测试点、控制继电器等。

`Fp0hPlcDevice`

松下 FP0H 设备实现。它负责把业务动作翻译成 Modbus 操作，内部依赖 `IModbusTcpClient`。检测流程调用 `SetBusyAsync(true)`、`SetOkAsync(true)`、`SelectTestPointAsync(index)` 等方法，不直接关心 Modbus 功能码和地址。

`IModbusTcpClient`

通用 Modbus TCP 通信接口。它只关心连接、断开、读线圈、写单线圈、读保持寄存器、写单寄存器、写多寄存器、自定义请求和响应匹配，不包含具体检测业务含义。

`ModbusTcpClient`

底层 Modbus TCP 客户端实现。可复用或迁移当前 `TcpClientPLCMotionService` 中已验证的连接、接收循环、事务 ID 匹配、超时处理和基础健康检查逻辑。

`PlcAddressMap`

集中保存启动信号、OK、NG、Busy、Error、测试点选择寄存器、继电器基地址等 PLC 地址。第一阶段使用当前默认地址，后续可改为从配置读取。

`PlcNotification`

用于表达 PLC 通信通知，例如连接失败、超时、写入失败、重连成功、收到异常响应。普通流程使用 `Information` 日志，用户操作、断线、重连失败、配置拒绝等关键动作使用 `Warning` 级别审计。

`PlcAlarmState`

预留 PLC 报警状态模型。第一阶段可以只定义基础结构，不强制接入检测流程。后续硬件联调确认报警线圈或寄存器后再填充。

## 数据流

### 启动连接

1. `DeviceConnectionManager` 从 `IDeviceSettingsService` 读取 `DeviceSettings`。
2. 将 `FP0HCommunicationConfig` 应用到 `IPlcDevice`。
3. 调用 `IPlcDevice.ConnectAsync()`。
4. `Fp0hPlcDevice` 根据配置生成 `ModbusTcpClientOptions`。
5. `IModbusTcpClient` 建立 TCP 连接并执行必要的 Modbus 验证。
6. 连接状态通过 `ConnectionStateChanged` 返回给设备连接管理器和 UI。

### 检测流程

1. `InspectionEngine` 只依赖 `IPlcDevice`。
2. 检测开始时调用 `SetBusyAsync(true)`。
3. 每个测试点调用 `SelectTestPointAsync(index)` 或继电器控制方法。
4. 万用表完成测量后，检测流程调用 `SetOkAsync(true)` 或 `SetNgAsync(true)`。
5. 异常时调用 `SetErrorAsync(true)` 并记录审计日志。
6. 检测结束后清理 Busy、OK、NG、Error 等必要信号。

### 后续扩展

PLC 调试页可以直接依赖 `IModbusTcpClient` 或新增 `IPlcDiagnosticService`，但检测业务仍只依赖 `IPlcDevice`。传感器轮询可以新增 `IPlcPollingService`，内部依赖 `IPlcDevice` 或 `IModbusTcpClient`，不应把轮询逻辑塞回 ViewModel。

## 兼容策略

第一阶段不直接删除旧类。新增 `IPlcDevice` 业务能力和 `Fp0hPlcDevice` 后，先将 `InspectionEngine` 从 `ITcpClientPLCMotionService` 迁移到 `IPlcDevice`。确认编译和基础行为通过后，再逐步取消 `PlcCommunicationAdapter`、`TcpPLCMotionWPFUIModbusService` 和旧接口的注册。

`TcpClientPLCMotionService` 可以先作为旧实现保留，也可以在后续任务中拆出通用通信能力并重命名为 `ModbusTcpClient`。清理旧类必须在没有业务引用后进行。

## 错误处理与日志

1. PLC 连接失败、重连失败、写入失败、Modbus 异常响应使用 `Warning` 记录审计日志。
2. 底层网络异常、响应解析异常、事务 ID 匹配异常记录异常详情。
3. 业务方法返回 `PlcOperationResult` 或抛出受控异常的策略需要在实施阶段统一，不能在不同方法中混用。
4. 连接失败不应导致 UI 未处理异常，设备连接管理器应继续发布未连接状态。

## 验证计划

1. 编译检查：`dotnet build`。
2. 静态检查：确认 `InspectionEngine` 不再依赖 `ITcpClientPLCMotionService`。
3. 静态检查：确认 ViewModel 不直接使用 Modbus 功能码控制检测流程。
4. 无硬件环境时，至少验证 DI 可解析 `IPlcDevice` 和 `IModbusTcpClient`。
5. 有硬件环境时，依次验证 PLC 连接、启动信号读取、Busy/OK/NG/Error 写入、测试点选择和断线重连。

## 实施边界

第一阶段以统一 PLC 业务入口为主，不追求一次性清理全部遗留代码。所有删除旧类、归档 FINS、移除 WPF UI 封装服务的动作，都必须建立在编译和引用检查确认无业务依赖之后。
