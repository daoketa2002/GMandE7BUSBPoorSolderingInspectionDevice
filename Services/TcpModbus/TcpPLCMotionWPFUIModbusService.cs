using System;
using System.Windows;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus
{
    /// <summary>
    /// Modbus PLC 通信的 WPF UI 层封装。
    /// 负责将底层 TcpClientPLCMotionService 的事件转发到 UI 线程，
    /// 并提供轮询等便利功能。
    /// </summary>
    public class TcpPLCMotionWPFUIModbusService : IDisposable
    {
        #region 字段

        private readonly TcpClientPLCMotionService _modbusService;
        private readonly ILogger<TcpPLCMotionWPFUIModbusService> _logger;
        private readonly Dispatcher _dispatcher;
        private bool _disposed = false;
        private readonly Dictionary<string, (PollingTaskConfig Config, CancellationTokenSource Cts)> _pollingTasks = new();
        private readonly object _pollingLock = new();

        #endregion

        #region 事件

        /// <summary>轮询数据接收事件（在 UI 线程触发）</summary>
        public event EventHandler<PollingDataEventArgs>? OnPollingDataReceived;

        /// <summary>连接状态变更事件（在 UI 线程触发）</summary>
        public event Action<bool>? OnConnectionStateChanged;

        /// <summary>详细连接状态变更事件（在 UI 线程触发）</summary>
        public event Action<TcpClientPLCMotionService.ConnectionState>? OnDetailedConnectionStateChanged;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化 WPF UI Modbus 服务封装类。
        /// 从 Application.Current 获取 UI 线程的 Dispatcher，
        /// 避免将 Dispatcher 作为 DI 参数（DI 容器无法天然提供）。
        /// </summary>
        public TcpPLCMotionWPFUIModbusService(
            TcpClientPLCMotionService modbusService,
            ILogger<TcpPLCMotionWPFUIModbusService> logger)
        {
            _modbusService = modbusService ?? throw new ArgumentNullException(nameof(modbusService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ✅ WPF 方式：从 Application.Current 获取 Dispatcher，不依赖 DI
            _dispatcher = Application.Current?.Dispatcher
                ?? throw new InvalidOperationException(
                    "TcpPLCMotionWPFUIModbusService 必须在 WPF Application 启动后创建");

            // 订阅底层服务事件
            _modbusService.ModbusResponseReceived += OnModbusResponseReceived;
            _modbusService.OnNotification += OnNotificationReceived;
            _modbusService.ConnectionStateChanged += OnConnectionStateChangedInternal;
            _modbusService.DetailedConnectionStateChanged += OnDetailedConnectionStateChangedInternal;

            _logger.LogDebug("TcpPLCMotionWPFUIModbusService 已初始化");
        }

        #endregion

        #region 跨线程调度

        /// <summary>在 UI 线程上同步执行操作</summary>
        private void RunOnUIThread(Action action)
        {
            if (_dispatcher.CheckAccess())
                action();
            else
                _dispatcher.Invoke(action);
        }

        /// <summary>在 UI 线程上异步执行操作</summary>
        private async Task RunOnUIThreadAsync(Action action)
        {
            if (_dispatcher.CheckAccess())
                action();
            else
                await _dispatcher.InvokeAsync(action);
        }

        #endregion

        #region 连接状态事件（转发到 UI 线程）

        private void OnConnectionStateChangedInternal(bool isConnected)
        {
            RunOnUIThread(() => OnConnectionStateChanged?.Invoke(isConnected));
        }

        private void OnDetailedConnectionStateChangedInternal(TcpClientPLCMotionService.ConnectionState state)
        {
            RunOnUIThread(() => OnDetailedConnectionStateChanged?.Invoke(state));
        }

        #endregion

        #region 基础操作（透明代理到底层服务）

        public async Task StartAsync() => await _modbusService.StartAsync();

        public async Task StopAsync()
        {
            StopAllPolling();
            await _modbusService.StopAsync();
        }

        public async Task<ModbusResponse?> ExecuteReadOperationAsync(
            byte functionCode, byte unitId, ushort startAddress, ushort quantity, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteReadOperationAsync(functionCode, unitId, startAddress, quantity, timeoutMs);
        }

        public async Task<ModbusResponse?> ExecuteWriteOperationAsync(
            byte functionCode, byte unitId, ushort startAddress, ushort[] data, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteWriteOperationAsync(functionCode, unitId, startAddress, data, timeoutMs);
        }

        public async Task SendCommandAsync(string command)
        {
            await _modbusService.SendCommandAsync(command);
        }

        public async Task<bool> ExecuteOperationAsync(string command, string expectedPrefix, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteOperationAsync(command, expectedPrefix, timeoutMs);
        }

        public async Task<bool> ExecuteOperationAsync(
            Func<string, Task<bool>> responseValidator, string command, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteOperationAsync(responseValidator, command, timeoutMs);
        }

        #endregion

        #region 底层事件处理（UI 线程）

        private void OnModbusResponseReceived(object? sender, ModbusResponse response)
        {
            RunOnUIThread(() =>
            {
                _logger.LogDebug("收到 Modbus 响应: FC={FunctionCode}, 数据长度={DataLength}",
                    response.FunctionCode, response.Data?.Length ?? 0);
            });
        }

        private void OnNotificationReceived(object? sender, CommunicationNotification notification)
        {
            RunOnUIThread(() =>
            {
                _logger.LogDebug("[{NotificationType}] {Message}", notification.Type, notification.Message);
            });
        }

        #endregion

        #region 数据解析

        public ushort[]? ParseRegisters(ModbusResponse response)
        {
            if (response?.Data == null || response.IsError)
                return null;

            byte[] data = response.Data;
            if (data.Length % 2 != 0)
                return null;

            var registers = new ushort[data.Length / 2];
            for (int i = 0; i < registers.Length; i++)
            {
                registers[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
            }
            return registers;
        }

        public bool[] ParseCoils(ModbusResponse response)
        {
            if (response == null || response.IsError || response.Data == null)
                return Array.Empty<bool>();

            return ModbusMessageHelper.ParseReadCoilsResponse(response.Data);
        }

        #endregion

        #region 属性

        public bool IsConnected => _modbusService.IsConnected;

        #endregion

        #region 便利方法

        /// <summary>
        /// 写单个保持寄存器（0x06 功能码封装）
        /// </summary>
        public async Task<bool> WriteSingleRegisterAsync(
            ushort address, ushort value, byte unitId = 1, int timeoutMs = 5000)
        {
            var response = await ExecuteWriteOperationAsync(
                functionCode: 0x06,
                unitId: unitId,
                startAddress: address,
                data: new ushort[] { value },
                timeoutMs: timeoutMs);

            return response != null && !response.IsError;
        }

        #endregion

        #region 轮询功能

        /// <summary>
        /// 启动后台轮询任务，定时读取 PLC 寄存器并在 UI 线程触发事件
        /// </summary>
        public async Task StartPollingAsync(PollingTaskConfig config)
        {
            config.Validate();

            lock (_pollingLock)
            {
                if (_pollingTasks.TryGetValue(config.Key, out var existing))
                {
                    existing.Cts.Cancel();
                    _pollingTasks.Remove(config.Key);
                }

                var cts = new CancellationTokenSource();
                _pollingTasks[config.Key] = (config, cts);

                _ = Task.Run(async () =>
                {
                    while (!cts.Token.IsCancellationRequested && _modbusService.IsConnected)
                    {
                        try
                        {
                            ModbusResponse? response = null;

                            if (config.UsesRegisterMode)
                            {
                                response = await _modbusService.ExecuteReadOperationAsync(
                                    config.FunctionCode!.Value,
                                    config.UnitId,
                                    config.StartAddress,
                                    config.Quantity,
                                    timeoutMs: 2000);
                            }
                            else if (config.UsesRawFrameMode)
                            {
                                response = await _modbusService.SendCustomModbusRequestAsync(
                                    config.RawRequestFrame!,
                                    timeoutMs: 2000);
                            }

                            if (response != null && !response.IsError)
                            {
                                // WPF: 切换到 UI 线程触发事件
                                await _dispatcher.InvokeAsync(() =>
                                {
                                    _logger.LogDebug("[{DisplayName}] 轮询结果: {Data}",
                                        config.DisplayName, BitConverter.ToString(response.Data!));
                                    OnPollingDataReceived?.Invoke(this,
                                        new PollingDataEventArgs(config.Key, config.DisplayName, response));
                                });
                            }
                            else if (response?.IsError == true)
                            {
                                _logger.LogWarning("轮询 [{Key}] Modbus 错误: {ErrorCode}",
                                    config.Key, response.ErrorCode);
                            }
                            else
                            {
                                _logger.LogDebug("轮询 [{Key}] 超时或无响应", config.Key);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "轮询 [{Key}] 异常", config.Key);
                            if (!_modbusService.IsConnected) break;
                        }

                        try
                        {
                            await Task.Delay(config.IntervalMs, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    lock (_pollingLock)
                    {
                        _pollingTasks.Remove(config.Key);
                    }
                }, cts.Token);
            }
        }

        public void StopPolling(string key)
        {
            lock (_pollingLock)
            {
                if (_pollingTasks.TryGetValue(key, out var task))
                {
                    task.Cts.Cancel();
                    _pollingTasks.Remove(key);
                }
            }
        }

        public void StopAllPolling()
        {
            lock (_pollingLock)
            {
                foreach (var (_, cts) in _pollingTasks.Values)
                {
                    cts.Cancel();
                }
                _pollingTasks.Clear();
            }
        }

        public IReadOnlyDictionary<string, PollingTaskConfig> GetActivePollingConfigs()
        {
            lock (_pollingLock)
            {
                return _pollingTasks.ToDictionary(kv => kv.Key, kv => kv.Value.Config);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _modbusService.ModbusResponseReceived -= OnModbusResponseReceived;
                _modbusService.OnNotification -= OnNotificationReceived;
                _modbusService.ConnectionStateChanged -= OnConnectionStateChangedInternal;
                _modbusService.DetailedConnectionStateChanged -= OnDetailedConnectionStateChangedInternal;

                StopAllPolling();
            }

            _disposed = true;
        }

        #endregion
    }
}
