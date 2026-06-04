using System;
using System.Windows;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus
{
    /// <summary>
    /// 独属于PLC动作控制的WPF UI封装类，用于在UI线程中更新界面
    /// </summary>
    public class TcpPLCMotionWPFUIModbusService : IDisposable
    {
        private readonly TcpClientPLCMotionService _modbusService;
        private readonly Dispatcher _dispatcher;
        private bool _disposed = false;
        private readonly Dictionary<string, (PollingTaskConfig Config, CancellationTokenSource Cts)> _pollingTasks = new();
        private readonly object _pollingLock = new();

        // 可选：定义事件供外部订阅
        public event EventHandler<PollingDataEventArgs>? OnPollingDataReceived;

        /// <summary>
        /// 连接状态变更事件 - 由底层服务触发，经由UI层转发到UI线程
        /// </summary>
        public event Action<bool>? OnConnectionStateChanged;

        /// <summary>
        /// 详细连接状态变更事件 - 提供更详细的连接状态信息
        /// </summary>
        public event Action<TcpClientPLCMotionService.ConnectionState>? OnDetailedConnectionStateChanged;

        /// <summary>
        /// 初始化WPF UI Modbus服务封装类
        /// </summary>
        /// <param name="modbusService">Modbus服务实例</param>
        /// <param name="dispatcher">UI线程的Dispatcher，用于跨线程调用</param>
        public TcpPLCMotionWPFUIModbusService(TcpClientPLCMotionService modbusService, Dispatcher dispatcher)
        {
            _modbusService = modbusService ?? throw new ArgumentNullException(nameof(modbusService));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            // 订阅Modbus响应事件
            _modbusService.ModbusResponseReceived += OnModbusResponseReceived;
            _modbusService.OnNotification += OnNotificationReceived;

            // 订阅连接状态变更事件
            _modbusService.ConnectionStateChanged += OnConnectionStateChangedInternal;

            // 订阅详细连接状态变更事件
            _modbusService.DetailedConnectionStateChanged += OnDetailedConnectionStateChangedInternal;
        }

        #region 跨线程调度辅助方法

        /// <summary>
        /// 在UI线程上执行操作
        /// </summary>
        private void RunOnUIThread(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                // 已经在UI线程，直接执行
                action();
            }
            else
            {
                // 切换到UI线程执行
                _dispatcher.Invoke(action);
            }
        }

        /// <summary>
        /// 异步在UI线程上执行操作
        /// </summary>
        private async Task RunOnUIThreadAsync(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                await _dispatcher.InvokeAsync(action);
            }
        }

        #endregion

        #region 连接状态事件处理

        /// <summary>
        /// 内部处理连接状态变更事件，确保在UI线程中触发
        /// </summary>
        private void OnConnectionStateChangedInternal(bool isConnected)
        {
            RunOnUIThread(() => OnConnectionStateChanged?.Invoke(isConnected));
        }

        /// <summary>
        /// 内部处理详细连接状态变更事件，确保在UI线程中触发
        /// </summary>
        private void OnDetailedConnectionStateChangedInternal(TcpClientPLCMotionService.ConnectionState state)
        {
            RunOnUIThread(() => OnDetailedConnectionStateChanged?.Invoke(state));
        }

        #endregion

        #region 基础操作

        /// <summary>
        /// 启动Modbus服务（线程安全）
        /// </summary>
        public async Task StartAsync()
        {
            await _modbusService.StartAsync();
        }

        /// <summary>
        /// 停止Modbus服务（线程安全）
        /// </summary>
        public async Task StopAsync()
        {
            StopAllPolling();
            await _modbusService.StopAsync();
        }

        public async Task<ModbusResponse?> ExecuteReadOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort quantity, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteReadOperationAsync(functionCode, unitId, startAddress, quantity, timeoutMs);
        }

        public async Task<ModbusResponse?> ExecuteWriteOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort[] data, int timeoutMs = 3000)
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

        public async Task<bool> ExecuteOperationAsync(Func<string, Task<bool>> responseValidator, string command, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteOperationAsync(responseValidator, command, timeoutMs);
        }

        #endregion

        #region 事件处理

        private void OnModbusResponseReceived(object sender, ModbusResponse response)
        {
            RunOnUIThread(() =>
            {
                Console.WriteLine($"收到Modbus响应: 功能码={response.FunctionCode}, 数据长度={response.Data?.Length ?? 0}");
            });
        }

        private void OnNotificationReceived(object sender, CommunicationNotification notification)
        {
            RunOnUIThread(() =>
            {
                Console.WriteLine($"[{notification.Type}] {notification.Message}");
            });
        }

        #endregion

        #region 数据解析

        public ushort[] ParseRegisters(ModbusResponse response)
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
                return new bool[0];

            return ModbusMessageHelper.ParseReadCoilsResponse(response.Data);
        }

        #endregion

        #region 属性

        public bool IsConnected => _modbusService.IsConnected;

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _modbusService.ModbusResponseReceived -= OnModbusResponseReceived;
                _modbusService.OnNotification -= OnNotificationReceived;
                _modbusService.ConnectionStateChanged -= OnConnectionStateChangedInternal;
                _modbusService.DetailedConnectionStateChanged -= OnDetailedConnectionStateChangedInternal;

                StopAllPolling();
                _disposed = true;
            }
        }

        #endregion

        #region 扩展功能

        public async Task<bool> WriteSingleRegisterAsync(ushort address, ushort value, byte unitId = 1, int timeoutMs = 5000)
        {
            var response = await ExecuteWriteOperationAsync(
                functionCode: 0x06,
                unitId: unitId,
                startAddress: address,
                data: new ushort[] { value },
                timeoutMs: timeoutMs
            );

            return response != null && !response.IsError;
        }

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
                                    config.FunctionCode.Value,
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
                                // ✅ WPF方式：使用Dispatcher切换到UI线程
                                await _dispatcher.InvokeAsync(() => UpdateUIWithPollingResult(response, config));
                            }
                            else if (response?.IsError == true)
                            {
                                Console.WriteLine($"轮询 [{config.Key}] 错误: {response.ErrorCode}");
                            }
                            else if (response == null)
                            {
                                Console.WriteLine($"轮询 [{config.Key}] 超时或无响应");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"轮询 [{config.Key}] 异常: {ex.Message}");
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
                return _pollingTasks.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Config);
            }
        }

        private void UpdateUIWithPollingResult(ModbusResponse response, PollingTaskConfig config)
        {
            Console.WriteLine($"[{config.DisplayName}] 轮询结果: {BitConverter.ToString(response.Data)}");
            OnPollingDataReceived?.Invoke(this, new PollingDataEventArgs(config.Key, config.DisplayName, response));
        }

        #endregion
    }
}