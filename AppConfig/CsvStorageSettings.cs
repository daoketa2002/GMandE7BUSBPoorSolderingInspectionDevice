// ============================================================
// 文件: AppConfig/CsvStorageSettings.cs
// 描述: CSV 存储配置模型 —— 从 appsettings.json 的 CsvStorage 节点绑定
// ============================================================

using Microsoft.Extensions.Configuration;
using System;

namespace GMandE7BUSBPoorSolderingInspectionDevice.AppConfig
{
    /// <summary>
    /// CSV 存储配置类，用于从 appsettings.json 等配置源中绑定 CSV 存储相关设置。
    /// </summary>
    public class CsvStorageSettings
    {
        private readonly IConfiguration _configuration;

        /// <summary>
        /// 构造函数 —— 注入 IConfiguration 以读取配置
        /// </summary>
        public CsvStorageSettings(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        /// <summary>
        /// CSV 文件存储根路径
        /// "Default" 或空字符串 → 使用应用程序基目录
        /// 其他值 → 使用指定的自定义路径
        /// </summary>
        public string RootPath =>
            _configuration["CsvStorage:RootPath"] ?? "Default";

        /// <summary>
        /// 单个CSV文件最大数据行数（不含表头），超出后自动创建分卷文件
        /// 默认 50000 行
        /// </summary>
        public int MaxRowsPerFile
        {
            get
            {
                var value = _configuration["CsvStorage:MaxRowsPerFile"];
                if (int.TryParse(value, out int result) && result > 0)
                    return result;
                return 50000;
            }
        }

        /// <summary>
        /// 获取实际存储根路径（解析 "Default" 为程序基目录）
        /// </summary>
        public string GetEffectiveRootPath()
        {
            if (string.IsNullOrEmpty(RootPath) ||
                RootPath.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
            return RootPath;
        }
    }
}