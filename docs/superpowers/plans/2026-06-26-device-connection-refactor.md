# Device Connection Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor device connection management so PLC, multimeter, and scanner connection lifecycle code is split into focused components while keeping `IDeviceConnectionManager` compatible.

**Architecture:** Keep `DeviceConnectionManager` as the public facade used by ViewModels. Move configuration application, connection retry execution, connection state storage, and background reconnect monitoring into small services under `Services/DeviceConnections`. Leave `InspectionEngine` behavior unchanged.

**Tech Stack:** C# 13, .NET 10 WPF, Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Logging, Serilog-backed logging, existing device interfaces.

---

## File Structure

- Create: `Services/DeviceConnections/DeviceTypeNames.cs`  
  Holds canonical device type strings used by the manager, executor, monitor, and state store.

- Create: `Services/DeviceConnections/DeviceConnectionRetryOptions.cs`  
  Holds retry and monitor timing constants that are currently embedded in `DeviceConnectionManager`.

- Create: `Services/DeviceConnections/DeviceConnectionStateStore.cs`  
  Owns connection booleans, status text, failure counts, and last reconnect attempt timestamps.

- Create: `Services/DeviceConnections/DeviceConfigurationApplier.cs`  
  Applies `DeviceSettings` to PLC, DMM, and scanner drivers.

- Create: `Services/DeviceConnections/DeviceConnectionExecutor.cs`  
  Owns one-device connect/disconnect/reconnect execution and retry behavior.

- Create: `Services/DeviceConnections/DeviceConnectionMonitor.cs`  
  Owns the background monitor loop and calls the executor when a device should reconnect.

- Modify: `Services/DeviceConnectionManager.cs`  
  Keep the public facade and event forwarding, delegate internal work to the new components.

- Modify: `Program.cs`  
  Register the new singleton components in DI before `IDeviceConnectionManager`.

- Do not modify: `Services/InspectionEngine.cs`  
  The detection flow, PLC address map, and relay logic stay unchanged in this phase.

---

### Task 1: Add Shared Device Names And Retry Options

**Files:**
- Create: `Services/DeviceConnections/DeviceTypeNames.cs`
- Create: `Services/DeviceConnections/DeviceConnectionRetryOptions.cs`

- [ ] **Step 1: Create canonical device names**

Create `Services/DeviceConnections/DeviceTypeNames.cs`:

```csharp
namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 设备类型名称常量，避免连接管理代码中散落硬编码字符串。
/// </summary>
public static class DeviceTypeNames
{
    public const string Plc = "PLC";
    public const string Dmm = "DMM";
    public const string Scanner = "Scanner";

    public static bool IsKnown(string deviceType) =>
        deviceType is Plc or Dmm or Scanner;
}
```

- [ ] **Step 2: Create retry option constants**

Create `Services/DeviceConnections/DeviceConnectionRetryOptions.cs`:

```csharp
namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 设备连接与后台重连的时间参数，集中放置便于联调时调整。
/// </summary>
public sealed class DeviceConnectionRetryOptions
{
    public int ReconnectBaseDelayMs { get; init; } = 2000;
    public int ReconnectMaxDelayMs { get; init; } = 30000;
    public int MaxReconnectAttempts { get; init; } = 12;
    public int MonitorIntervalMs { get; init; } = 5000;
    public int InitialReconnectIntervalMs { get; init; } = 3000;
    public int BackoffReconnectIntervalMs { get; init; } = 5000;
    public int MaxReconnectIntervalMs { get; init; } = 30000;
    public int CleanDisconnectDelayMs { get; init; } = 300;
}
```

- [ ] **Step 3: Verify new files are included by SDK project**

Run:

```powershell
dotnet build --no-restore
```

Expected: Either compilation reaches C# build, or fails because packages were not restored. A NuGet `NU1301` restore error is an environment limitation, not a code failure for this step.

- [ ] **Step 4: Commit**

Run:

```powershell
git add Services/DeviceConnections/DeviceTypeNames.cs Services/DeviceConnections/DeviceConnectionRetryOptions.cs
git commit -m "refactor: add device connection shared settings"
```

Expected: Commit succeeds. If Git index write permission is denied, record the exact permission error and continue without claiming a commit.

---

### Task 2: Add Device Connection State Store

**Files:**
- Create: `Services/DeviceConnections/DeviceConnectionStateStore.cs`

- [ ] **Step 1: Implement state storage**

Create `Services/DeviceConnections/DeviceConnectionStateStore.cs`:

```csharp
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 集中保存三台设备的连接状态、状态文本和重连统计。
/// </summary>
public sealed class DeviceConnectionStateStore
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, DeviceState> _states = new()
    {
        [DeviceTypeNames.Plc] = new DeviceState(),
        [DeviceTypeNames.Dmm] = new DeviceState(),
        [DeviceTypeNames.Scanner] = new DeviceState()
    };

    public bool IsPlcConnected => IsConnected(DeviceTypeNames.Plc);
    public bool IsDmmConnected => IsConnected(DeviceTypeNames.Dmm);
    public bool IsScannerConnected => IsConnected(DeviceTypeNames.Scanner);
    public bool AreAllDevicesReady => IsPlcConnected && IsDmmConnected && IsScannerConnected;

    public string PlcStatusText => GetStatusText(DeviceTypeNames.Plc);
    public string DmmStatusText => GetStatusText(DeviceTypeNames.Dmm);
    public string ScannerStatusText => GetStatusText(DeviceTypeNames.Scanner);

    public bool IsConnected(string deviceType)
    {
        lock (_syncRoot)
        {
            return _states[deviceType].IsConnected;
        }
    }

    public string GetStatusText(string deviceType)
    {
        lock (_syncRoot)
        {
            return _states[deviceType].StatusText;
        }
    }

    public void SetConnecting(string deviceType)
    {
        lock (_syncRoot)
        {
            _states[deviceType].StatusText = "连接中...";
        }
    }

    public DeviceConnectionStateChangedEventArgs UpdateConnectionState(string deviceType, bool isConnected)
    {
        lock (_syncRoot)
        {
            var state = _states[deviceType];
            state.IsConnected = isConnected;
            state.StatusText = isConnected ? "已连接" : "未连接";

            if (isConnected)
            {
                state.ConsecutiveFailures = 0;
            }

            return new DeviceConnectionStateChangedEventArgs(deviceType, isConnected, state.StatusText);
        }
    }

    public int GetFailureCount(string deviceType)
    {
        lock (_syncRoot)
        {
            return _states[deviceType].ConsecutiveFailures;
        }
    }

    public void IncrementFailure(string deviceType)
    {
        lock (_syncRoot)
        {
            _states[deviceType].ConsecutiveFailures++;
        }
    }

    public bool ShouldAttemptReconnect(string deviceType, DeviceConnectionRetryOptions options, DateTime now)
    {
        lock (_syncRoot)
        {
            var state = _states[deviceType];
            var cooldownMs = Math.Min(
                options.InitialReconnectIntervalMs + state.ConsecutiveFailures * options.BackoffReconnectIntervalMs,
                options.MaxReconnectIntervalMs);

            if ((now - state.LastReconnectAttempt).TotalMilliseconds < cooldownMs)
            {
                return false;
            }

            state.LastReconnectAttempt = now;
            return true;
        }
    }

    private sealed class DeviceState
    {
        public bool IsConnected { get; set; }
        public string StatusText { get; set; } = "未连接";
        public int ConsecutiveFailures { get; set; }
        public DateTime LastReconnectAttempt { get; set; } = DateTime.MinValue;
    }
}
```

- [ ] **Step 2: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: No C# errors from the new store. If restore blocks the build, record the restore error.

- [ ] **Step 3: Commit**

Run:

```powershell
git add Services/DeviceConnections/DeviceConnectionStateStore.cs
git commit -m "refactor: add device connection state store"
```

Expected: Commit succeeds, or Git permission denial is recorded.

---

### Task 3: Add Configuration Applier

**Files:**
- Create: `Services/DeviceConnections/DeviceConfigurationApplier.cs`

- [ ] **Step 1: Implement device configuration application**

Create `Services/DeviceConnections/DeviceConfigurationApplier.cs`:

```csharp
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 将设备设置应用到具体硬件驱动，隔离配置模型与连接流程。
/// </summary>
public sealed class DeviceConfigurationApplier
{
    private readonly ILogger<DeviceConfigurationApplier> _logger;

    public DeviceConfigurationApplier(ILogger<DeviceConfigurationApplier> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Apply(DeviceSettings settings, IPlcDevice plcDevice, IMultimeterDevice dmmDevice, IScannerDevice scannerDevice)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplyPlc(settings, plcDevice);
        ApplyDmm(settings, dmmDevice);
        ApplyScanner(settings, scannerDevice);
    }

    private void ApplyPlc(DeviceSettings settings, IPlcDevice plcDevice)
    {
        if (settings.FP0HCommunication is null || plcDevice is not PlcCommunicationAdapter plcAdapter)
        {
            _logger.LogWarning("PLC配置注入失败，Settings={HasSettings}, Device={DeviceType}",
                settings.FP0HCommunication is not null, plcDevice.GetType().Name);
            return;
        }

        plcAdapter.ApplyConfig(settings.FP0HCommunication);
        _logger.LogDebug("已注入PLC配置: {Host}:{Port}",
            settings.FP0HCommunication.IpAddress, settings.FP0HCommunication.Port);
    }

    private void ApplyDmm(DeviceSettings settings, IMultimeterDevice dmmDevice)
    {
        if (settings.GDM9060Communication is null || dmmDevice is not GwInstekGDM9060Driver dmmDriver)
        {
            _logger.LogWarning("万用表配置注入失败，Settings={HasSettings}, Device={DeviceType}",
                settings.GDM9060Communication is not null, dmmDevice.GetType().Name);
            return;
        }

        dmmDriver.Host = settings.GDM9060Communication.IpAddress;
        dmmDriver.Port = settings.GDM9060Communication.Port;
        dmmDriver.TimeoutMs = settings.GDM9060Communication.ReceiveTimeoutMs;
        _logger.LogDebug("已注入万用表配置: {Host}:{Port}", dmmDriver.Host, dmmDriver.Port);
    }

    private void ApplyScanner(DeviceSettings settings, IScannerDevice scannerDevice)
    {
        if (settings.ScannerSerialCommunication is null || scannerDevice is not HoneywellH1900Scanner scanner)
        {
            _logger.LogWarning("扫描枪配置注入失败，Settings={HasSettings}, Device={DeviceType}",
                settings.ScannerSerialCommunication is not null, scannerDevice.GetType().Name);
            return;
        }

        scanner.PortName = settings.ScannerSerialCommunication.SerialNumber;
        scanner.BaudRate = settings.ScannerSerialCommunication.BaudRate;
        _logger.LogInformation("已注入扫描枪配置: Port={Port}, BaudRate={BaudRate}",
            scanner.PortName, scanner.BaudRate);
    }
}
```

- [ ] **Step 2: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: The new class compiles with existing device namespaces.

- [ ] **Step 3: Commit**

Run:

```powershell
git add Services/DeviceConnections/DeviceConfigurationApplier.cs
git commit -m "refactor: extract device configuration applier"
```

Expected: Commit succeeds, or Git permission denial is recorded.

---

### Task 4: Add Connection Executor

**Files:**
- Create: `Services/DeviceConnections/DeviceConnectionExecutor.cs`

- [ ] **Step 1: Implement executor**

Create `Services/DeviceConnections/DeviceConnectionExecutor.cs`:

```csharp
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 执行单台设备的连接、断开和带重试连接，供启动、手动重连和后台监控复用。
/// </summary>
public sealed class DeviceConnectionExecutor : IDisposable
{
    private readonly ILogger<DeviceConnectionExecutor> _logger;
    private readonly DeviceConnectionRetryOptions _options;
    private readonly SemaphoreSlim _plcLock = new(1, 1);
    private readonly SemaphoreSlim _dmmLock = new(1, 1);
    private readonly SemaphoreSlim _scannerLock = new(1, 1);

    public DeviceConnectionExecutor(
        ILogger<DeviceConnectionExecutor> logger,
        DeviceConnectionRetryOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<bool> ConnectWithRetryAsync(
        ICommunicationDevice device,
        string deviceType,
        CancellationToken ct)
    {
        var deviceLock = GetLock(deviceType);
        _logger.LogInformation("[{DeviceType}] 开始连接流程", deviceType);

        await deviceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (device.IsConnected)
            {
                _logger.LogInformation("[{DeviceType}] 硬件报告已连接", deviceType);
                return true;
            }

            for (var attempt = 1; attempt <= _options.MaxReconnectAttempts && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    LogAttempt(device, deviceType, attempt);
                    await device.DisconnectAsync().ConfigureAwait(false);
                    await Task.Delay(_options.CleanDisconnectDelayMs, ct).ConfigureAwait(false);

                    var connected = await device.ConnectAsync(ct).ConfigureAwait(false);
                    if (connected)
                    {
                        _logger.LogInformation("[{DeviceType}] 连接成功", deviceType);
                        return true;
                    }

                    _logger.LogWarning("[{DeviceType}] ConnectAsync 返回 false，尝试 {Attempt}/{MaxAttempts}",
                        deviceType, attempt, _options.MaxReconnectAttempts);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[{DeviceType}] 连接被取消", deviceType);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{DeviceType}] 连接异常，尝试 {Attempt}/{MaxAttempts}",
                        deviceType, attempt, _options.MaxReconnectAttempts);
                }

                if (attempt < _options.MaxReconnectAttempts)
                {
                    var delay = Math.Min(
                        _options.ReconnectBaseDelayMs * (int)Math.Pow(2, attempt - 1),
                        _options.ReconnectMaxDelayMs);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }

            _logger.LogWarning("[{DeviceType}] 达到最大重试次数，连接失败", deviceType);
            return false;
        }
        finally
        {
            deviceLock.Release();
            _logger.LogInformation("[{DeviceType}] 连接流程结束", deviceType);
        }
    }

    public async Task DisconnectAsync(ICommunicationDevice device, string deviceType)
    {
        var deviceLock = GetLock(deviceType);
        await deviceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await device.DisconnectAsync().ConfigureAwait(false);
            _logger.LogInformation("[{DeviceType}] 已断开连接", deviceType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{DeviceType}] 断开连接失败", deviceType);
        }
        finally
        {
            deviceLock.Release();
        }
    }

    private SemaphoreSlim GetLock(string deviceType) => deviceType switch
    {
        DeviceTypeNames.Plc => _plcLock,
        DeviceTypeNames.Dmm => _dmmLock,
        DeviceTypeNames.Scanner => _scannerLock,
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, "未知设备类型")
    };

    private void LogAttempt(ICommunicationDevice device, string deviceType, int attempt)
    {
        _logger.LogInformation("[{DeviceType}] 连接尝试 {Attempt}/{MaxAttempts}",
            deviceType, attempt, _options.MaxReconnectAttempts);

        if (deviceType == DeviceTypeNames.Scanner && device is HoneywellH1900Scanner scanner)
        {
            _logger.LogInformation("[扫描枪] 当前配置 - 端口:{Port}, 波特率:{BaudRate}",
                scanner.PortName, scanner.BaudRate);
            try
            {
                var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
                _logger.LogInformation("[扫描枪] 系统可用串口: {Ports}", string.Join(", ", availablePorts));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[扫描枪] 无法枚举系统串口");
            }
        }
    }

    public void Dispose()
    {
        _plcLock.Dispose();
        _dmmLock.Dispose();
        _scannerLock.Dispose();
    }
}
```

- [ ] **Step 2: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: Executor compiles and does not require UI references.

- [ ] **Step 3: Commit**

Run:

```powershell
git add Services/DeviceConnections/DeviceConnectionExecutor.cs
git commit -m "refactor: extract device connection executor"
```

Expected: Commit succeeds, or Git permission denial is recorded.

---

### Task 5: Add Background Monitor

**Files:**
- Create: `Services/DeviceConnections/DeviceConnectionMonitor.cs`

- [ ] **Step 1: Implement monitor**

Create `Services/DeviceConnections/DeviceConnectionMonitor.cs`:

```csharp
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 后台监控设备断线并触发重连，连接细节委托给 DeviceConnectionExecutor。
/// </summary>
public sealed class DeviceConnectionMonitor : IDisposable
{
    private readonly ILogger<DeviceConnectionMonitor> _logger;
    private readonly DeviceConnectionRetryOptions _options;
    private readonly DeviceConnectionExecutor _executor;
    private readonly DeviceConnectionStateStore _stateStore;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public DeviceConnectionMonitor(
        ILogger<DeviceConnectionMonitor> logger,
        DeviceConnectionRetryOptions options,
        DeviceConnectionExecutor executor,
        DeviceConnectionStateStore stateStore)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public void Start(
        IReadOnlyDictionary<string, ICommunicationDevice> devices,
        Func<string, bool, Task> onStateChangedAsync,
        Func<Task> onScannerConnectedAsync)
    {
        if (_monitorCts is not null)
        {
            return;
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(devices, onStateChangedAsync, onScannerConnectedAsync, _monitorCts.Token));
        _logger.LogInformation("后台设备监控已启动，检查间隔 {Interval}ms", _options.MonitorIntervalMs);
    }

    public void Stop()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        if (_monitorTask is not null)
        {
            try
            {
                _monitorTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "等待监控线程结束时出现异常");
            }

            _monitorTask = null;
        }

        _logger.LogInformation("后台设备监控已停止");
    }

    private async Task MonitorLoopAsync(
        IReadOnlyDictionary<string, ICommunicationDevice> devices,
        Func<string, bool, Task> onStateChangedAsync,
        Func<Task> onScannerConnectedAsync,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.MonitorIntervalMs, ct).ConfigureAwait(false);

                foreach (var pair in devices)
                {
                    await CheckAndReconnectAsync(pair.Key, pair.Value, onStateChangedAsync, onScannerConnectedAsync, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("后台监控被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "后台监控异常");
        }
    }

    private async Task CheckAndReconnectAsync(
        string deviceType,
        ICommunicationDevice device,
        Func<string, bool, Task> onStateChangedAsync,
        Func<Task> onScannerConnectedAsync,
        CancellationToken ct)
    {
        if (device.IsConnected)
        {
            return;
        }

        if (!_stateStore.ShouldAttemptReconnect(deviceType, _options, DateTime.Now))
        {
            return;
        }

        _stateStore.SetConnecting(deviceType);
        _logger.LogWarning("后台监控检测到 {DeviceType} 未连接，开始重连", deviceType);

        var connected = await _executor.ConnectWithRetryAsync(device, deviceType, ct).ConfigureAwait(false);
        await onStateChangedAsync(deviceType, connected).ConfigureAwait(false);

        if (connected && deviceType == DeviceTypeNames.Scanner)
        {
            await onScannerConnectedAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
```

- [ ] **Step 2: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: Monitor compiles and depends only on interfaces plus new connection components.

- [ ] **Step 3: Commit**

Run:

```powershell
git add Services/DeviceConnections/DeviceConnectionMonitor.cs
git commit -m "refactor: extract device connection monitor"
```

Expected: Commit succeeds, or Git permission denial is recorded.

---

### Task 6: Refactor DeviceConnectionManager Facade

**Files:**
- Modify: `Services/DeviceConnectionManager.cs`

- [ ] **Step 1: Replace internal fields**

In `Services/DeviceConnectionManager.cs`, add:

```csharp
using GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;
```

Replace duplicated state dictionaries, retry constants, locks, and monitor fields with:

```csharp
private readonly DeviceConfigurationApplier _configurationApplier;
private readonly DeviceConnectionExecutor _connectionExecutor;
private readonly DeviceConnectionStateStore _stateStore;
private readonly DeviceConnectionMonitor _connectionMonitor;
private CancellationTokenSource? _connectionCts;
private bool _isDisposed;
```

- [ ] **Step 2: Update constructor dependencies**

Change the constructor signature to include the new components:

```csharp
public DeviceConnectionManager(
    ILogger<DeviceConnectionManager> logger,
    IPlcDevice plcDevice,
    IMultimeterDevice dmmDevice,
    IScannerDevice scannerDevice,
    IScannerBarcodeService scannerBarcodeService,
    IDeviceSettingsService settingsService,
    DeviceConfigurationApplier configurationApplier,
    DeviceConnectionExecutor connectionExecutor,
    DeviceConnectionStateStore stateStore,
    DeviceConnectionMonitor connectionMonitor)
```

Assign them with null checks:

```csharp
_configurationApplier = configurationApplier ?? throw new ArgumentNullException(nameof(configurationApplier));
_connectionExecutor = connectionExecutor ?? throw new ArgumentNullException(nameof(connectionExecutor));
_stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
_connectionMonitor = connectionMonitor ?? throw new ArgumentNullException(nameof(connectionMonitor));
```

- [ ] **Step 3: Delegate public state properties**

Replace property bodies with:

```csharp
public bool IsPlcConnected => _stateStore.IsPlcConnected;
public bool IsDmmConnected => _stateStore.IsDmmConnected;
public bool IsScannerConnected => _stateStore.IsScannerConnected;
public bool AreAllDevicesReady => _stateStore.AreAllDevicesReady;
public string PlcStatusText => _stateStore.PlcStatusText;
public string DmmStatusText => _stateStore.DmmStatusText;
public string ScannerStatusText => _stateStore.ScannerStatusText;
```

- [ ] **Step 4: Rewrite StartAllAsync**

Use this structure:

```csharp
public async Task StartAllAsync()
{
    _logger.LogInformation("========== 开始自动连接所有设备 ==========");
    _connectionCts = new CancellationTokenSource();

    var settings = _settingsService.LoadSettings();
    _configurationApplier.Apply(settings, _plcDevice, _dmmDevice, _scannerDevice);

    var ct = _connectionCts.Token;
    var plcTask = ConnectAndPublishAsync(_plcDevice, DeviceTypeNames.Plc, ct);
    var dmmTask = ConnectAndPublishAsync(_dmmDevice, DeviceTypeNames.Dmm, ct);
    var scannerTask = ConnectAndPublishAsync(_scannerDevice, DeviceTypeNames.Scanner, ct);

    await Task.WhenAll(plcTask, dmmTask, scannerTask).ConfigureAwait(false);

    if (_stateStore.IsScannerConnected)
    {
        await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);
    }

    _connectionMonitor.Start(CreateDeviceMap(), PublishStateChangeAsync, InitializeScannerBarcodeServiceAsync);
    _logger.LogInformation("========== 设备连接管理器启动完成 ==========");
}
```

- [ ] **Step 5: Add helper methods inside DeviceConnectionManager**

Add:

```csharp
private IReadOnlyDictionary<string, ICommunicationDevice> CreateDeviceMap() => new Dictionary<string, ICommunicationDevice>
{
    [DeviceTypeNames.Plc] = _plcDevice,
    [DeviceTypeNames.Dmm] = _dmmDevice,
    [DeviceTypeNames.Scanner] = _scannerDevice
};

private async Task ConnectAndPublishAsync(ICommunicationDevice device, string deviceType, CancellationToken ct)
{
    var connected = await _connectionExecutor.ConnectWithRetryAsync(device, deviceType, ct).ConfigureAwait(false);
    await PublishStateChangeAsync(deviceType, connected).ConfigureAwait(false);
}

private Task PublishStateChangeAsync(string deviceType, bool connected)
{
    var args = _stateStore.UpdateConnectionState(deviceType, connected);
    PublishOnUiThread(deviceType, args);
    CheckAllDevicesReady();
    return Task.CompletedTask;
}

private async Task InitializeScannerBarcodeServiceAsync()
{
    try
    {
        await _scannerBarcodeService.InitializeAsync().ConfigureAwait(false);
        _logger.LogInformation("ScannerBarcodeService 初始化完成");
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "ScannerBarcodeService 初始化失败");
    }
}
```

- [ ] **Step 6: Keep UI event forwarding centralized**

Add:

```csharp
private void PublishOnUiThread(string deviceType, DeviceConnectionStateChangedEventArgs args)
{
    Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        switch (deviceType)
        {
            case DeviceTypeNames.Plc:
                PlcConnectionStateChanged?.Invoke(this, args);
                break;
            case DeviceTypeNames.Dmm:
                DmmConnectionStateChanged?.Invoke(this, args);
                break;
            case DeviceTypeNames.Scanner:
                ScannerConnectionStateChanged?.Invoke(this, args);
                break;
        }
    });
}
```

- [ ] **Step 7: Rewrite manual connect, reconnect, disconnect methods**

Use this shape for `ReconnectDeviceAsync`:

```csharp
public async Task ReconnectDeviceAsync(string deviceType)
{
    if (!DeviceTypeNames.IsKnown(deviceType))
    {
        _logger.LogWarning("未知设备类型: {DeviceType}", deviceType);
        return;
    }

    _logger.LogWarning("用户手动重连设备: {DeviceType}", deviceType);
    var settings = _settingsService.LoadSettings();
    _configurationApplier.Apply(settings, _plcDevice, _dmmDevice, _scannerDevice);

    _stateStore.SetConnecting(deviceType);
    var device = GetDevice(deviceType);
    await _connectionExecutor.DisconnectAsync(device, deviceType).ConfigureAwait(false);
    await ConnectAndPublishAsync(device, deviceType, _connectionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);

    if (deviceType == DeviceTypeNames.Scanner && _stateStore.IsScannerConnected)
    {
        await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);
    }
}
```

Add:

```csharp
private ICommunicationDevice GetDevice(string deviceType) => deviceType switch
{
    DeviceTypeNames.Plc => _plcDevice,
    DeviceTypeNames.Dmm => _dmmDevice,
    DeviceTypeNames.Scanner => _scannerDevice,
    _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, "未知设备类型")
};
```

Adapt `ConnectDeviceAsync` to skip when `_stateStore.IsConnected(deviceType)` is true, then call `ConnectAndPublishAsync`. Adapt `DisconnectDeviceAsync` to call `_connectionExecutor.DisconnectAsync`, then `PublishStateChangeAsync(deviceType, false)`.

- [ ] **Step 8: Simplify hardware event subscription**

Keep `SubscribeToHardwareEvents`, but make each device call `PublishStateChangeAsync`:

```csharp
_plcDevice.ConnectionStateChanged += async (sender, connected) =>
    await PublishStateChangeAsync(DeviceTypeNames.Plc, connected).ConfigureAwait(false);

_dmmDevice.ConnectionStateChanged += async (sender, connected) =>
    await PublishStateChangeAsync(DeviceTypeNames.Dmm, connected).ConfigureAwait(false);

_scannerDevice.ConnectionStateChanged += async (sender, connected) =>
    await PublishStateChangeAsync(DeviceTypeNames.Scanner, connected).ConfigureAwait(false);
```

Keep barcode forwarding through `Application.Current.Dispatcher.BeginInvoke`.

- [ ] **Step 9: Replace all-ready check and remove obsolete private methods**

Replace `CheckAllDevicesReady` with:

```csharp
private void CheckAllDevicesReady()
{
    var allReady = _stateStore.AreAllDevicesReady;
    AllDevicesReadyChanged?.Invoke(this, allReady);
    _logger.LogDebug("设备就绪状态变更: AllReady={AllReady} (PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner})",
        allReady,
        _stateStore.IsPlcConnected,
        _stateStore.IsDmmConnected,
        _stateStore.IsScannerConnected);
}
```

Remove these old private members from `DeviceConnectionManager` after their callers have been replaced:

```text
ApplyConfigurationToDevices
ConnectDeviceWithRetryAsync
UpdateConnectionState
RaisePlcStateChanged
RaiseDmmStateChanged
RaiseScannerStateChanged
SetStatusText
StartBackgroundMonitor
StopBackgroundMonitor
MonitorDevicesLoopAsync
CheckAndReconnectDeviceAsync
```

Expected: `DeviceConnectionManager` no longer owns configuration injection, retry loops, connection status dictionaries, reconnect timestamp dictionaries, or background monitor loop code.

- [ ] **Step 10: Rewrite StopAllAsync and Dispose**

`StopAllAsync` should stop monitor, cancel token, disconnect three devices, publish false states, and dispose token:

```csharp
public async Task StopAllAsync()
{
    _logger.LogInformation("正在停止所有设备连接...");
    _connectionMonitor.Stop();
    _connectionCts?.Cancel();

    await Task.WhenAll(
        _connectionExecutor.DisconnectAsync(_plcDevice, DeviceTypeNames.Plc),
        _connectionExecutor.DisconnectAsync(_dmmDevice, DeviceTypeNames.Dmm),
        _connectionExecutor.DisconnectAsync(_scannerDevice, DeviceTypeNames.Scanner)
    ).ConfigureAwait(false);

    await PublishStateChangeAsync(DeviceTypeNames.Plc, false).ConfigureAwait(false);
    await PublishStateChangeAsync(DeviceTypeNames.Dmm, false).ConfigureAwait(false);
    await PublishStateChangeAsync(DeviceTypeNames.Scanner, false).ConfigureAwait(false);

    _connectionCts?.Dispose();
    _connectionCts = null;
    _logger.LogInformation("所有设备连接已停止");
}
```

`Dispose` should call `_connectionMonitor.Stop()`, cancel/dispose `_connectionCts`, and not dispose DI-owned singleton dependencies except objects owned by the manager itself.

- [ ] **Step 11: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: `DeviceConnectionManager` compiles and `IDeviceConnectionManager` public surface remains unchanged.

- [ ] **Step 12: Commit**

Run:

```powershell
git add Services/DeviceConnectionManager.cs
git commit -m "refactor: simplify device connection manager facade"
```

Expected: Commit succeeds, or Git permission denial is recorded.

---

### Task 7: Register New Components In DI

**Files:**
- Modify: `Program.cs`

- [ ] **Step 1: Add namespace**

In `Program.cs`, add:

```csharp
using GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;
```

- [ ] **Step 2: Register connection components**

Before `services.AddSingleton<IDeviceConnectionManager, DeviceConnectionManager>();`, add:

```csharp
services.AddSingleton<DeviceConnectionRetryOptions>();
services.AddSingleton<DeviceConnectionStateStore>();
services.AddSingleton<DeviceConfigurationApplier>();
services.AddSingleton<DeviceConnectionExecutor>();
services.AddSingleton<DeviceConnectionMonitor>();
```

- [ ] **Step 3: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: DI registrations compile. If packages are missing and restore is blocked, record `NU1301`.

- [ ] **Step 4: Commit**

Run:

```powershell
git add Program.cs
git commit -m "refactor: register device connection components"
```

Expected: Commit succeeds, or Git permission denial is recorded.

---

### Task 8: Final Verification

**Files:**
- Verify: `Interfaces/IDeviceConnectionManager.cs`
- Verify: `ViewModels/TestPageViewModel.cs`
- Verify: `ViewModels/PlanSettingViewModel.cs`
- Verify: `ViewModels/PlanEditViewModel.cs`
- Verify: `ViewModels/LogDataViewModel.cs`
- Verify: `ViewModels/SystemSettingsViewModel.cs`
- Verify: `Services/InspectionEngine.cs`

- [ ] **Step 1: Confirm public interface compatibility**

Run:

```powershell
rg -n "IDeviceConnectionManager|ReconnectDeviceAsync|ConnectDeviceAsync|DisconnectDeviceAsync|BarcodeScanned|PlcConnectionStateChanged|DmmConnectionStateChanged|ScannerConnectionStateChanged" Interfaces ViewModels Services Program.cs
```

Expected: Existing ViewModels still use the same `IDeviceConnectionManager` members. No ViewModel should be changed for this refactor unless a compile error proves a signature mismatch.

- [ ] **Step 2: Confirm InspectionEngine is untouched**

Run:

```powershell
git diff -- Services/InspectionEngine.cs
```

Expected: No diff output.

- [ ] **Step 3: Run full build**

Run:

```powershell
dotnet build
```

Expected: Build succeeds. If NuGet access is blocked, expected blocking error is `NU1301` for `https://api.nuget.org/v3/index.json`; record it and run `dotnet build --no-restore` after dependencies are available.

- [ ] **Step 4: Review changed files**

Run:

```powershell
git diff --stat
git diff -- Services/DeviceConnectionManager.cs Program.cs Services/DeviceConnections
```

Expected: Diffs are limited to device connection components and DI registration. `SystemSettingsViewModel` and `InspectionEngine` should not be modified.

- [ ] **Step 5: Final commit**

Run:

```powershell
git add Services/DeviceConnections Services/DeviceConnectionManager.cs Program.cs
git commit -m "refactor: split device connection management"
```

Expected: Commit succeeds if previous task commits were skipped. If all task commits already succeeded, this step should report no staged changes.
