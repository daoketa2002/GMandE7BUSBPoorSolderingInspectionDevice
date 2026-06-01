using System;
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
    /// 独属于PLC 动作控制的WinForms UI封装类，用于在UI线程中更新界面
    /// </summary>
    public class TcpPLCMotionWinFormsUIModbusService : IDisposable
    {
        private readonly TcpClientPLCMotionService _modbusService;
        private readonly System.Windows.Forms.Control _uiControl; // UI控件引用，用于跨线程调用
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
        /// 初始化WinForms UI Modbus服务封装类
        /// </summary>
        /// <param name="modbusService">Modbus服务实例</param>
        /// <param name="uiControl">UI控件引用，用于跨线程调用</param>
        public TcpPLCMotionWinFormsUIModbusService(TcpClientPLCMotionService modbusService, System.Windows.Forms.Control uiControl)
        {
            _modbusService = modbusService ?? throw new ArgumentNullException(nameof(modbusService));
            _uiControl = uiControl ?? throw new ArgumentNullException(nameof(uiControl));

            // 订阅Modbus响应事件
            _modbusService.ModbusResponseReceived += OnModbusResponseReceived;
            _modbusService.OnNotification += OnNotificationReceived;

            // 订阅连接状态变更事件
            _modbusService.ConnectionStateChanged += OnConnectionStateChangedInternal;

            // 订阅详细连接状态变更事件
            _modbusService.DetailedConnectionStateChanged += OnDetailedConnectionStateChangedInternal;
        }

        /// <summary>
        /// 内部处理连接状态变更事件，确保在UI线程中触发
        /// </summary>
        /// <param name="isConnected">连接状态</param>
        private void OnConnectionStateChangedInternal(bool isConnected)
        {
            if (_uiControl.InvokeRequired)
            {
                _uiControl.Invoke(new Action<bool>(OnConnectionStateChangedInternal), isConnected);
            }
            else
            {
                OnConnectionStateChanged?.Invoke(isConnected);
            }
        }

        /// <summary>
        /// 内部处理详细连接状态变更事件，确保在UI线程中触发
        /// </summary>
        /// <param name="state">详细连接状态</param>
        private void OnDetailedConnectionStateChangedInternal(TcpClientPLCMotionService.ConnectionState state)
        {
            if (_uiControl.InvokeRequired)
            {
                _uiControl.Invoke(new Action<TcpClientPLCMotionService.ConnectionState>(OnDetailedConnectionStateChangedInternal), state);
            }
            else
            {
                OnDetailedConnectionStateChanged?.Invoke(state);
            }
        }

        /// <summary>
        /// 启动Modbus服务（线程安全）
        /// </summary>
        /// <returns>表示异步启动操作的任务</returns>
        public async Task StartAsync()
        {
            await _modbusService.StartAsync();
        }

        /// <summary>
        /// 停止Modbus服务（线程安全）
        /// </summary>
        /// <returns>表示异步停止操作的任务</returns>
        public async Task StopAsync()
        {
            // 停止所有轮询（新版）
            StopAllPolling();

            await _modbusService.StopAsync();
        }

        /// <summary>
        /// 执行Modbus读取操作（线程安全）
        /// </summary>
        /// <param name="functionCode">功能码</param>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="quantity">数量</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>Modbus响应对象</returns>
        public async Task<ModbusResponse?> ExecuteReadOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort quantity, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteReadOperationAsync(functionCode, unitId, startAddress, quantity, timeoutMs);
        }

        /// <summary>
        /// 执行Modbus写入操作（线程安全）
        /// </summary>
        /// <param name="functionCode">功能码</param>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="data">要写入的数据</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>Modbus响应对象</returns>
        public async Task<ModbusResponse?> ExecuteWriteOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort[] data, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteWriteOperationAsync(functionCode, unitId, startAddress, data, timeoutMs);
        }

        /// <summary>
        /// 发送命令到PLC（线程安全）
        /// </summary>
        /// <param name="command">要发送的命令字符串</param>
        /// <returns>表示异步发送操作的任务</returns>
        public async Task SendCommandAsync(string command)
        {
            await _modbusService.SendCommandAsync(command);
        }

        /// <summary>
        /// 执行需要响应的TCP操作（线程安全）
        /// </summary>
        /// <param name="command">要发送到PLC的命令字符串</param>
        /// <param name="expectedPrefix">期望响应消息的前缀</param>
        /// <param name="timeoutMs">等待响应的超时时间（毫秒）</param>
        /// <returns>如果收到匹配的响应返回true，否则返回false</returns>
        public async Task<bool> ExecuteOperationAsync(string command, string expectedPrefix, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteOperationAsync(command, expectedPrefix, timeoutMs);
        }

        /// <summary>
        /// 执行需要响应的TCP操作（线程安全）
        /// </summary>
        /// <param name="responseValidator">一个异步函数，接收响应字符串并返回布尔值</param>
        /// <param name="command">要发送到PLC的命令字符串</param>
        /// <param name="timeoutMs">等待响应的超时时间（毫秒）</param>
        /// <returns>如果验证通过返回true，否则返回false</returns>
        public async Task<bool> ExecuteOperationAsync(Func<string, Task<bool>> responseValidator, string command, int timeoutMs = 3000)
        {
            return await _modbusService.ExecuteOperationAsync(responseValidator, command, timeoutMs);
        }

        /// <summary>
        /// Modbus响应事件处理
        /// </summary>
        /// <param name="sender">事件发送者</param>
        /// <param name="response">Modbus响应对象</param>
        private void OnModbusResponseReceived(object sender, ModbusResponse response)
        {
            // 如果需要在UI上更新响应信息，可以在这里处理
            if (_uiControl.InvokeRequired)
            {
                _uiControl.Invoke(new Action<object, ModbusResponse>(OnModbusResponseReceived), sender, response);
            }
            else
            {
                // 在UI线程中执行更新操作
                // 例如：更新DataGridView、TextBox等UI控件
                Console.WriteLine($"收到Modbus响应: 功能码={response.FunctionCode}, 数据长度={response.Data?.Length ?? 0}");
            }
        }

        /// <summary>
        /// 通知事件处理
        /// </summary>
        /// <param name="sender">事件发送者</param>
        /// <param name="notification">通知对象</param>
        private void OnNotificationReceived(object sender, CommunicationNotification notification)
        {
            if (_uiControl.InvokeRequired)
            {
                _uiControl.Invoke(new Action<object, CommunicationNotification>(OnNotificationReceived), sender, notification);
            }
            else
            {
                // 在UI线程中更新通知信息
                // 例如：更新状态栏、日志文本框等
                Console.WriteLine($"[{notification.Type}] {notification.Message}");
            }
        }

        /// <summary>
        /// 解析Modbus响应数据为寄存器值数组
        /// </summary>
        /// <param name="response">Modbus响应对象</param>
        /// <returns>寄存器值数组</returns>

        public ushort[] ParseRegisters(ModbusResponse response)
        {
            if (response?.Data == null || response.IsError)
                return null;

            byte[] data = response.Data;
            if (data.Length % 2 != 0)
                return null; // 寄存器必须是偶数字节

            var registers = new ushort[data.Length / 2];
            for (int i = 0; i < registers.Length; i++)
            {
                // 大端字节序（高位在前）——与你的手动解析一致
                registers[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
            }
            return registers;
        }

        /// <summary>
        /// 解析Modbus响应数据为线圈状态数组
        /// </summary>
        /// <param name="response">Modbus响应对象</param>
        /// <returns>线圈状态数组</returns>
        public bool[] ParseCoils(ModbusResponse response)
        {
            if (response == null || response.IsError || response.Data == null)
                return new bool[0];

            return ModbusMessageHelper.ParseReadCoilsResponse(response.Data);
        }

        /// <summary>
        /// 获取服务连接状态
        /// </summary>
        public bool IsConnected => _modbusService.IsConnected;

        #region IDisposable接口实现
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // 👇 关键：取消事件订阅！
                _modbusService.ModbusResponseReceived -= OnModbusResponseReceived;
                _modbusService.OnNotification -= OnNotificationReceived;
                _modbusService.ConnectionStateChanged -= OnConnectionStateChangedInternal;
                _modbusService.DetailedConnectionStateChanged -= OnDetailedConnectionStateChangedInternal;

                // 停止轮询
                StopAllPolling();

                _disposed = true;
            }
        }

        #endregion

        #region 扩展
        /// <summary>
        /// 写入单个保持寄存器（功能码 0x06）
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">要写入的值</param>
        /// <param name="unitId">从站地址，默认为1</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>成功返回 true，否则 false</returns>
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
                                // ✅ 使用你新增的方法
                                response = await _modbusService.SendCustomModbusRequestAsync(
                                    config.RawRequestFrame!,
                                    timeoutMs: 2000);
                                // 注意：SendCustomModbusRequestAsync 返回的就是 ModbusResponse?
                                // 不需要再手动解析或构造！
                            }

                            if (response != null && !response.IsError)
                            {
                                Action updateAction = () => UpdateUIWithPollingResult(response, config);
                                if (_uiControl.InvokeRequired)
                                    _uiControl.Invoke(updateAction);
                                else
                                    updateAction();
                            }
                            else if (response?.IsError == true)
                            {
                                Console.WriteLine($"轮询 [{config.Key}] 错误: {response.ErrorCode}");
                            }
                            // 如果 response == null，说明超时或发送失败，可选择记录日志
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

        // 停止单个
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

        // 停止所有
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

        // 获取当前所有轮询状态（用于UI显示）
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
            // 示例：根据 config.DisplayName 决定更新哪个控件
            Console.WriteLine($"[{config.DisplayName}] 轮询结果: {BitConverter.ToString(response.Data)}");

            // 你可以在这里触发事件，让 Form 订阅并更新具体控件
            OnPollingDataReceived?.Invoke(this, new PollingDataEventArgs(config.Key, config.DisplayName, response));
        }

        #endregion
    }
}



