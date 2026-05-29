using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 内存监控服务（帮助发现内存泄漏）
    /// </summary>
    public class MemoryMonitorService : IDisposable
    {
        private readonly ILogger<MemoryMonitorService> _logger;
        private readonly Timer _timer;
        private long _lastMemoryUsage;
        private int _gcCount;

        public MemoryMonitorService(ILogger<MemoryMonitorService> logger)
        {
            _logger = logger;
            _timer = new Timer(CheckMemory, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        private void CheckMemory(object? state)
        {
            try
            {
                // 获取当前内存使用
                var currentMemory = GC.GetTotalMemory(false);
                var currentGcCount = GC.CollectionCount(2);

                var memoryMB = currentMemory / 1024 / 1024;
                var memoryDelta = currentMemory - _lastMemoryUsage;
                var deltaMB = memoryDelta / 1024 / 1024;
                var gcDelta = currentGcCount - _gcCount;

                if (_lastMemoryUsage > 0)
                {
                    // 检测显著内存增长（超过100MB）
                    if (memoryDelta > 100 * 1024 * 1024)
                    {
                        _logger.LogWarning("⚠️ 检测到内存显著增长：+{DeltaMB}MB，当前使用：{CurrentMB}MB，GC次数：+{GcDelta}",
                            deltaMB, memoryMB, gcDelta);

                        // 记录详细内存信息
                        LogDetailedMemoryInfo();

                        // 触发完整GC
                        GC.Collect(2, GCCollectionMode.Forced, true);
                        GC.WaitForPendingFinalizers();

                        _logger.LogInformation("已触发完整垃圾回收");
                    }
                    else if (memoryMB > 500)
                    {
                        _logger.LogWarning("📊 内存使用较高：{CurrentMB}MB，建议检查资源释放", memoryMB);
                    }
                    else
                    {
                        _logger.LogDebug("内存使用正常：{CurrentMB}MB，变化：{DeltaMB}MB", memoryMB, deltaMB);
                    }
                }

                _lastMemoryUsage = currentMemory;
                _gcCount = currentGcCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "内存监控检查失败");
            }
        }

        private void LogDetailedMemoryInfo()
        {
            try
            {
                // 获取各代内存信息
                var gen0Collections = GC.CollectionCount(0);
                var gen1Collections = GC.CollectionCount(1);
                var gen2Collections = GC.CollectionCount(2);

                _logger.LogInformation("GC统计 - Gen0:{Gen0}, Gen1:{Gen1}, Gen2:{Gen2}",
                    gen0Collections, gen1Collections, gen2Collections);

                // 获取进程内存信息
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var workingSetMB = process.WorkingSet64 / 1024 / 1024;
                var privateMemoryMB = process.PrivateMemorySize64 / 1024 / 1024;

                _logger.LogInformation("进程内存 - 工作集:{WorkingSetMB}MB, 私有内存:{PrivateMemoryMB}MB",
                    workingSetMB, privateMemoryMB);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取详细内存信息失败");
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
