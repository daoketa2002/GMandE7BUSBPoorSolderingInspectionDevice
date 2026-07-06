# TestPageView 复位后旧检测日志继续输出修复方案

## 1. 问题现象

半实物联调时，在 `TestPageView` 点击复位后，界面检测列表已经清空，但日志输出区仍然继续出现检测流程日志，看起来像检测还在后台继续执行。

这个问题不能只按“清空界面列表”处理。复位语义应当是：

```text
收到 DT121=1 或点击复位按钮
-> 中止当前检测
-> 清空界面结果和内存断点
-> 清 PLC 输出区
-> 清 DT121
-> 等待下一次启动
```

## 2. 真实原因判断

当前复位流程已经调用了 `_inspectionEngine.Stop()`，但 `Stop()` 只取消检测引擎内部的 `CancellationTokenSource`，没有等待旧检测任务真正退出。

同时，运行页把检测引擎日志直接接到 `AddLog`：

```csharp
_inspectionEngine.LogMessage += (s, msg) => AddLog(msg);
```

这条日志路径没有判断 `_isResetting` 或 `_ignoreInspectionCallbacksUntilNextStart`。所以复位后即使 `StepStarted`、`StepCompleted`、`InspectionCompleted` 回调被挡住，旧检测流程内部晚到的 `LogMessage` 仍然会继续写入日志输出区。

另外，`RunInspectionAsync()` 在等待后台检测任务返回后，还会统一处理 `InspectionResult`，如果旧任务在复位后才返回，也可能继续弹出“检测中止”提示或写入中止日志。

## 3. 真实模式是否适配

适配真实模式。

本方案不绑定 `SemiPhysicalDebug`，也不只针对上位机复位按钮。真实模式和半实物模式最终都会进入同一个复位流程：

```text
真实模式：
实体按钮 / PLC 置 DT121=1
-> PLC 轮询读到复位信号
-> UpdateUiStateFromPlcInputsAsync()
-> ExecuteResetFlowAsync()

半实物按钮：
上位机写 DT121=1
-> 主动调用 ExecuteResetFlowAsync()
```

因此修复点放在 `ExecuteResetFlowAsync()`、`RunInspectionAsync()` 和检测引擎日志订阅处，真实模式同样生效。

## 4. 为什么 Fake 模式不明显

Fake 模式不是天然没有这个漏洞，而是不容易暴露：

- Fake PLC 和 Fake 万用表没有真实网络通讯耗时。
- Fake 不会长时间等待真实 `DT302`。
- Fake 的 `READ?` 不会被真实 TCP 或仪器状态阻塞。
- 整个检测循环通常很快结束，复位和旧任务收尾不容易撞在一起。

真实模式和半实物模式有 PLC 轮询、DT302 等待、万用表 TCP 读写等耗时步骤，所以复位时更容易看到旧检测日志晚到。

## 5. 最小改动方案

### 5.1 屏蔽复位后的旧检测日志

把匿名日志订阅改为命名方法：

```csharp
_inspectionEngine.LogMessage += OnInspectionLogMessage;
```

新增方法：

```csharp
private void OnInspectionLogMessage(object? sender, string message)
{
    if (_ignoreInspectionCallbacksUntilNextStart || _isResetting)
    {
        _logger.LogDebug("[复位流程] 已忽略旧检测日志：{Message}", message);
        return;
    }

    AddLog(message);
}
```

退出页面或释放时要对应取消订阅。

### 5.2 给运行任务增加本轮序号

在 `TestPageViewModel` 增加一个轻量字段：

```csharp
private int _inspectionRunVersion;
```

每次启动检测前递增，复位时也递增。`RunInspectionAsync()` 保存启动时的版本号，后台检测返回后先判断版本是否仍然匹配：

```csharp
int runVersion = ++_inspectionRunVersion;

var result = await Task.Run(...);

if (runVersion != _inspectionRunVersion || _ignoreInspectionCallbacksUntilNextStart)
{
    _logger.LogWarning("[复位流程][审计] 旧检测任务已返回但被丢弃，RunVersion={RunVersion}, Current={CurrentVersion}");
    return;
}
```

这样旧检测任务即使在复位后返回，也不会再弹中止提示、写旧状态或影响 UI。

### 5.3 复位时等待检测引擎停止

在 `InspectionEngine` 增加一个很小的等待方法，不新建服务：

```csharp
public async Task StopAndWaitAsync(TimeSpan timeout, CancellationToken ct = default)
{
    Stop();

    var deadline = DateTime.UtcNow + timeout;
    while (_isRunning && DateTime.UtcNow < deadline)
    {
        await Task.Delay(50, ct).ConfigureAwait(false);
    }

    if (_isRunning)
    {
        _logger.LogWarning("[检测流程][审计] 已请求停止检测，但等待 {TimeoutMs}ms 后仍未完全退出", timeout.TotalMilliseconds);
    }
}
```

`ExecuteResetFlowAsync()` 中用它替换只调用 `Stop()` 的逻辑：

```csharp
await _inspectionEngine.StopAndWaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
```

等待时间不宜过长，避免复位按钮卡住。超时后仍继续安全清理 PLC 输出和 UI 状态，并保留 Warning 日志用于现场排查。

### 5.4 在硬件耗时步骤后补取消检查

在 `InspectionEngine.RunInspectionAsync()` 的关键硬件调用后补：

```csharp
_inspectionCts.Token.ThrowIfCancellationRequested();
```

建议位置：

- 写入当前测试点后。
- 等待 `DT302` 后。
- 切换万用表模式后。
- `READ?` 返回后、判定前。
- 单项清理后、进入下一项前。

目的不是改变流程，而是让复位取消信号更快生效，避免旧流程继续判定、写结果或进入下一项。

## 6. 不采用的方案

### 不新建第二套 PLC 监控服务

当前项目不大，复位信号已经由 `TestPageViewModel` 轮询处理。新增后台监控服务会带来第二个 PLC 信号 owner，反而增加现场排查难度。

### 不把问题修成半实物专用逻辑

真实模式、半实物按钮、Fake 调试最终都可能进入同一套复位流程。修复应放在共用链路，而不是只判断 `SemiPhysicalDebug`。

### 不只清日志输出区

清日志只能掩盖现象，旧检测任务仍可能继续写 PLC、读万用表或触发旧 UI 处理。必须从任务取消和旧回调隔离处修。

## 7. 验证方式

### 7.1 构建验证

```powershell
dotnet build --no-restore
```

期望：

```text
0 Error
```

### 7.2 最小测试验证

```powershell
dotnet run --project Tests\MinimumLoopTests\MinimumLoopTests.csproj --no-restore
```

期望：

```text
所有最小测试通过
```

### 7.3 半实物/真实模式手工验证

操作：

```text
1. 启动一轮检测。
2. 在检测过程中点击复位，或由 PLC 置 DT121=1。
3. 观察检测列表和日志输出区。
```

期望：

```text
检测列表清空。
状态回到待机或可启动。
日志区只出现复位收口相关日志。
不再继续出现旧检测项“正在检测”“读取万用表”“判定 OK/NG”等日志。
不再弹出上一轮检测的“检测中止”提示。
```

## 8. 文件范围

只建议修改：

```text
ViewModels/TestPageViewModel.cs
Services/InspectionEngine.cs
```

不新增服务，不新增复杂抽象，不调整设备接口。
