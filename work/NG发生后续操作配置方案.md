# NG 发生后续操作配置方案

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在系统设置中新增“单项 NG 后是否继续测试”选项，让现场可选择遇到单项 NG 后继续整轮检测，或立即停止本轮并等待处理。

**Architecture:** 保持当前 WPF/MVVM 小项目结构，不新增第二套检测引擎，不新增独立检测行为配置类。系统设置直接保存到 `DeviceSettings.ContinueTestingAfterNg`，`TestPageViewModel` 构造 `InspectionConfig` 时读取该选项，`InspectionEngine` 在单项判定 NG 后决定继续或停止。

**Tech Stack:** C#、WPF、CommunityToolkit.Mvvm、Microsoft.Extensions.Hosting、Serilog、JSON 设备配置、MVVM 绑定。

---

## 1. 背景

当前检测流程中，单项结果为 NG 时，`InspectionEngine` 会继续测试后续项目，直到方案全部项目检测结束后再进入保存弹窗，综合判定为 NG。

现场新增需求：

```text
系统设置新增 NG 发生后的后续操作选项。
用户可选择当项目测试结果是 NG 时，是否继续测试。
当发生 NG 且用户选择继续测试时，测试状态指示灯显示“测试中”，继续测试下一项目。
当发生 NG 且用户选择不继续测试时，停止测试，测试状态指示灯显示“NG”，日志输出区输出相应信息，等待用户下一步处理。
```

本方案把该类停止明确命名为“单项 NG 停止”，避免和整轮最终综合 NG、PLC 停止 `DT122`、急停 `DT123` 混淆。

---

## 2. 推荐行为

新增配置项：

```text
单项 NG 后继续测试
```

建议默认值：

```text
true
```

原因：

```text
默认继续测试可保持当前系统行为，避免升级后现场突然变成首个 NG 即停止。
需要首个 NG 停止的产线，可在系统设置中关闭该选项。
```

运行规则：

| 配置 | 单项 NG 后动作 | 测试状态指示灯 | 日志输出 |
|---|---|---|---|
| 继续测试 | 记录该项 NG，继续下一项目 | 测试中 | 第 X 项 NG，配置为继续测试，进入下一项 |
| 不继续测试 | 记录该项 NG，停止本轮，不测后续项目 | 单项NG | 第 X 项 NG，配置为停止测试，已停止本轮，等待用户处理 |

如果现场必须严格显示原需求里的“NG”两个字，UI 文案可显示为 `NG`；但代码状态名、日志和停止原因仍使用 `SingleItemNg` 语义。

---

## 3. 状态语义

这次“单项 NG 后停止”不是 PLC 停止信号 `DT122`，也不是急停 `DT123`。

建议新增独立业务状态：

```csharp
InspectionState.StoppedBySingleItemNg
TestUIState.SingleItemNgStopped
```

UI 显示：

```text
测试状态：单项NG
颜色：红色
输入区：保持禁用或按现有停止态规则处理，等待复位/终了
```

保留数据：

```text
已测项目保留检查结果和 OK/NG 判定。
触发停止的 NG 项保留检查结果和 NG 判定。
未测项目保持“未检测”。
不自动弹出“所有项目已检测完毕”的保存弹窗，因为本轮不是完整方案检测结束。
```

用户下一步处理：

```text
复位：清空界面检测结果、清内存断点、清 PLC 输出，回到待机/可启动。
终了：结束当前运行页，按已有终了流程返回。
```

---

## 4. 配置设计

### 4.1 直接扩展 DeviceSettings

文件：

```text
AppConfig/DeviceConfigs/DeviceSettings.cs
```

新增属性：

```csharp
/// <summary>
/// 单项 NG 后是否继续测试后续项目。
/// true：保持当前行为，继续测完整个方案；
/// false：首个 NG 后停止本轮，等待用户复位或终了。
/// </summary>
public bool ContinueTestingAfterNg { get; set; } = true;
```

不新增 `InspectionBehaviorConfig.cs`。当前只有一个行为选项，直接放在 `DeviceSettings` 更轻，`SystemSettingsViewModel.cs` 加载和保存时也更直观；等以后确实出现多项检测行为配置，再考虑拆分配置类。

保存到运行时配置：

```text
bin/Debug/net10.0-windows10.0.17763.0/设置/设备设置/DeviceSettings.json
```

示例 JSON：

```json
{
  "ContinueTestingAfterNg": true
}
```

兼容要求：

```text
旧 DeviceSettings.json 没有 ContinueTestingAfterNg 字段时，反序列化后必须自动使用默认值 true。
```

---

## 5. 系统设置界面

文件：

```text
ViewModels/SystemSettingsViewModel.cs
Views/SystemSettingsView.xaml
```

新增项放在“常规设置”最上面，不新增单独的“检测流程”分组：

```text
常规设置
  单项 NG 后继续测试
```

控件文案：

```text
单项 NG 后继续测试
说明：开启后遇到 NG 仍继续后续项目；关闭后首个 NG 停止本轮并显示单项NG。
```

控件建议：

```text
CheckBox 或项目现有复选框样式。
```

保存规则：

```text
点击“保存设置”时随 DeviceSettings 一起保存。
保存成功后写 Warning 审计日志：
[系统设置][审计] 单项 NG 后继续测试={ContinueTestingAfterNg}
```

---

## 6. 检测流程设计

### 6.1 扩展 InspectionConfig

文件：

```text
Services/InspectionEngine.cs
```

在 `InspectionConfig` 中新增：

```csharp
/// <summary>
/// 单项 NG 后是否继续测试后续项目。
/// true：继续测完整个方案；
/// false：首个 NG 后停止本轮，等待用户复位或终了。
/// </summary>
public bool ContinueTestingAfterNg { get; set; } = true;
```

### 6.2 TestPageViewModel 传入配置

文件：

```text
ViewModels/TestPageViewModel.cs
```

在加载方案并构造 `InspectionConfig` 时，从 `IDeviceSettingsService.LoadSettings()` 读取：

```csharp
ContinueTestingAfterNg = deviceSettings?.ContinueTestingAfterNg ?? true
```

日志：

```text
检测配置已更新：单项 NG 后继续测试=True/False
```

### 6.3 InspectionEngine 单项 NG 处理

文件：

```text
Services/InspectionEngine.cs
```

当前单项判定链路：

```text
READ? -> ParseMeasurement -> JudgeResult -> testPoint.Judgment -> StepCompleted -> AddFinishedResult
```

建议在单项完成并写入点位结果后判断：

```csharp
if (judgment == "NG" && !_config.ContinueTestingAfterNg)
{
    result.IsAborted = false;
    result.IsAllPassed = false;
    result.ErrorMessage = $"第 {i + 1} 项 {testPoint.Name} 判定 NG，已按系统设置停止本轮检测";
    result.TotalCount = _config.TestPoints.Count;
    result.PassCount = passCount;
    result.FailCount = failCount;
    result.StopReason = InspectionStopReason.SingleItemNg;
    result.StopPointIndex = i;
    result.StopPointName = testPoint.Name;
    result.EndTime = DateTime.Now;

    await ClearPlcOutputsAndRelayFlagAsync(_inspectionCts.Token).ConfigureAwait(false);
    await _plcDevice.ClearPcReadyAsync(CancellationToken.None).ConfigureAwait(false);
    await _plcDevice.WriteFinalResultAsync(false, i, CancellationToken.None).ConfigureAwait(false);

    SetState(InspectionState.StoppedBySingleItemNg);
    InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
    return result;
}
```

注意：

```text
这不是异常中止，不要显示“检测中止”弹窗。
这不是完整完成，不要进入保存弹窗。
必须保留当前 NG 项的 StepCompleted UI 回调，让表格显示该项结果。
必须清 DT130~DT185、DT302、DT234，避免继电器输出残留。
建议写 DT304=0、DT305=1，并写入 NG 项索引，方便 PLC 知道本件已有 NG。
```

---

## 7. 运行页状态设计

文件：

```text
ViewModels/TestPageViewModel.cs
Views/TestPageView.xaml
```

新增 UI 状态：

```csharp
SingleItemNgStopped
```

状态映射：

```text
InspectionState.StoppedBySingleItemNg -> TestUIState.SingleItemNgStopped
SensorStatusText = "单项NG"
```

状态颜色：

```text
SingleItemNgStopped 使用红色，和 NG 判定一致。
```

`OnInspectionCompleted` 处理：

```text
如果 Result 对应 StopReason.SingleItemNg：
1. 不调用 OnAllPinsTestedAsync。
2. 不弹保存弹窗。
3. 日志输出：
   第 X 项 {项目名} 判定 NG，系统设置为 NG 后停止，已停止本轮检测，等待复位或终了。
4. UI 状态保持单项NG。
```

建议 `InspectionResult` 增加停止原因字段：

```csharp
public InspectionStopReason StopReason { get; set; } = InspectionStopReason.None;
public int? StopPointIndex { get; set; }
public string StopPointName { get; set; } = string.Empty;
```

枚举：

```csharp
public enum InspectionStopReason
{
    None,
    SingleItemNg,
    PlcStop,
    Reset,
    EmergencyStop,
    Error,
    Canceled
}
```

如果不想增加枚举，也可以先用 `InspectionState.StoppedBySingleItemNg` 加 `ErrorMessage` 文案完成最小闭环，但枚举更利于后续日志和 UI 分支清晰。

---

## 8. PLC 输出和保存策略

### 8.1 ContinueTestingAfterNg=true

```text
保持现有行为。
每项写点位结果。
全部项目结束后写最终 DT304/DT305。
进入保存弹窗。
综合判定为 NG。
```

### 8.2 ContinueTestingAfterNg=false

```text
单项 NG 后立即停止本轮。
写当前点位结果为 NG。
写最终 DT304=0、DT305=1。
如果支持 NG 项目编号，则写当前项索引。
清 DT130~DT185。
清 DT302。
清 DT234。
不清 DT120 的策略沿用现有启动请求清理规则，若现场要求停止后释放启动请求，可在联调后补充。
不自动保存 CSV，因为本轮不是完整方案检测结束。
```

---

## 9. 任务拆分

### Task 1: 配置模型

**Files:**

- Modify: `AppConfig/DeviceConfigs/DeviceSettings.cs`

- [ ] **Step 1: DeviceSettings 增加 ContinueTestingAfterNg**

Expected:

```text
DeviceSettings 直接新增 ContinueTestingAfterNg，默认 true。
中文注释说明 true/false 行为。
```

- [ ] **Step 2: 验证旧配置兼容**

Expected:

```text
旧配置文件缺少 ContinueTestingAfterNg 字段时使用默认 true。
不新增 InspectionBehaviorConfig.cs，不引入额外配置层级。
```

### Task 2: 系统设置 UI

**Files:**

- Modify: `ViewModels/SystemSettingsViewModel.cs`
- Modify: `Views/SystemSettingsView.xaml`

- [ ] **Step 1: ViewModel 加载配置**

Expected:

```text
进入系统设置时加载 DeviceSettings.ContinueTestingAfterNg。
没有字段时默认 ContinueTestingAfterNg=true。
```

- [ ] **Step 2: View 在常规设置最上面新增选项**

Expected:

```text
在常规设置区域最上面显示“单项 NG 后继续测试”选项。
说明文案清楚解释开启/关闭结果。
```

- [ ] **Step 3: 保存时写入 DeviceSettings.json**

Expected:

```text
保存所有设置时包含 DeviceSettings.ContinueTestingAfterNg。
日志 Warning 记录该设置值。
```

### Task 3: 检测引擎

**Files:**

- Modify: `Services/InspectionEngine.cs`

- [ ] **Step 1: InspectionConfig 增加 ContinueTestingAfterNg**

Expected:

```text
默认 true，保持现有行为。
```

- [ ] **Step 2: 增加单项 NG 停止状态**

Expected:

```text
InspectionState 增加 StoppedBySingleItemNg。
必要时 InspectionResult 增加 StopReason/StopPointIndex/StopPointName。
```

- [ ] **Step 3: 单项 NG 后按配置分支**

Expected:

```text
ContinueTestingAfterNg=true：继续下一项。
ContinueTestingAfterNg=false：写当前 NG 结果、写最终 NG、清输出、SetState(StoppedBySingleItemNg)、触发 InspectionCompleted、return result。
```

### Task 4: 运行页状态

**Files:**

- Modify: `ViewModels/TestPageViewModel.cs`
- Modify: `Views/TestPageView.xaml`

- [ ] **Step 1: 读取设置传入 InspectionConfig**

Expected:

```text
LoadPlanItemsAsync 或构造 InspectionConfig 的位置读取 ContinueTestingAfterNg。
```

- [ ] **Step 2: 增加 SingleItemNgStopped UI 状态**

Expected:

```text
测试状态显示“单项NG”。
状态灯为红色。
日志输出“第 X 项 NG，系统设置为停止测试，等待复位或终了”。
```

- [ ] **Step 3: 单项 NG 停止时不弹保存弹窗**

Expected:

```text
OnInspectionCompleted 遇到 StopReason.SingleItemNg 时只记录日志并保持单项NG状态。
不调用 OnAllPinsTestedAsync。
```

### Task 5: 最小测试

**Files:**

- Modify: `Tests/MinimumLoopTests/Program.cs`

- [ ] **Step 1: 默认值测试**

Expected:

```text
new InspectionConfig().ContinueTestingAfterNg == true
```

- [ ] **Step 2: 状态枚举测试**

Expected:

```text
InspectionState.StoppedBySingleItemNg 存在。
InspectionStopReason.SingleItemNg 存在。
```

- [ ] **Step 3: 构建验证**

Run:

```powershell
dotnet build --no-restore
```

Expected:

```text
0 Error
```

- [ ] **Step 4: 最小测试验证**

Run:

```powershell
dotnet run --project Tests\MinimumLoopTests\MinimumLoopTests.csproj --no-restore
```

Expected:

```text
全部 PASS
```

---

## 10. 验收标准

### 场景 1：配置为继续测试

```text
系统设置勾选“单项 NG 后继续测试”。
触发某一项 NG。
测试状态保持“测试中”。
日志输出该项 NG 且继续下一项。
后续项目继续检测。
全部项目结束后弹保存确认。
综合判定为 NG。
```

### 场景 2：配置为不继续测试

```text
系统设置取消勾选“单项 NG 后继续测试”。
触发某一项 NG。
当前 NG 项显示测量结果和 NG 判定。
后续未测项目保持“未检测”。
测试状态显示“单项NG”。
日志输出该项 NG 且已停止本轮。
不弹保存确认。
PLC 输出区和 DT302 已清理。
DT304=0、DT305=1。
等待用户点击复位或终了。
```

### 场景 3：复位

```text
NG 停止后点击复位或 PLC 触发 DT121。
界面检测结果清空。
断点清空。
DT130~DT185、DT302、DT234、DT304、DT305 清理。
状态回到待机或可启动。
```

---

## 11. 不采用的方案

### 不采用：新增 InspectionBehaviorConfig.cs

原因：

```text
当前只有一个选项，单独建 InspectionBehaviorConfig 会增加文件和配置层级。
本项目规模不大，SystemSettingsViewModel 直接加载和保存 DeviceSettings.ContinueTestingAfterNg 更见名知义。
以后如果检测行为配置增加到多项，再拆分独立配置类也不迟。
```

### 不采用：把单项 NG 后停止复用为 DT122 停止

原因：

```text
DT122 是 PLC 停止/暂停信号，语义是可保留断点并等待再次启动。
单项 NG 后停止是业务判定导致的本轮终止，不应伪造成 PLC 停止信号。
复用 DT122 会混淆日志、断点和现场排查。
```

### 不采用：每个 NG 都弹窗询问继续或停止

原因：

```text
现场检测节拍会被弹窗打断。
用户需求是系统设置中的选项，而不是每次动态询问。
```

### 不采用：在 ViewModel 中直接决定跳过后续项

原因：

```text
逐项检测、判定和流程停止属于 InspectionEngine 职责。
ViewModel 只负责读取设置、传入配置、展示状态和日志。
```

---

## 12. 后续可选项

真实联调后如果 PLC 需要更明确的单项 NG 停止握手，可再补充：

```text
单项 NG 停止专用 PLC 地址。
NG 项目编号寄存器确认。
单项 NG 停止后 DT120 是否由上位机清零。
单项 NG 停止后是否允许保存部分检测记录。
```

这些不是当前最小实现范围。
