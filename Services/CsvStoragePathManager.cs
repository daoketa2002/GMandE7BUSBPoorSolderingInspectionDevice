// ============================================================
// 文件: Services/CsvStoragePathManager.cs
// 修改: 支持运行时动态切换存储路径
// 修复: 移除 SanitizeFileName 中对下划线的替换，解决方案名含下划线时查询失败的问题
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// CSV 存储路径管理器
    /// 支持运行时动态切换存储路径（从 CsvStorageSettings 实时读取）
    /// </summary>
    public class CsvStoragePathManager
    {
        private readonly CsvStorageSettings _settings;
        private readonly ILogger<CsvStoragePathManager> _logger;
        private readonly int _maxRowsPerFile;

        private const string DATA_LOG_FOLDER_NAME = "DataLog";

        /// <summary>
        /// 构造函数
        /// </summary>
        public CsvStoragePathManager(
            CsvStorageSettings settings,
            ILogger<CsvStoragePathManager> logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _maxRowsPerFile = _settings.MaxRowsPerFile;

            // 启动时确保默认路径存在
            var rootPath = GetDataLogRootPath();
            EnsureDirectoryExistsCore(rootPath);

            _logger.LogInformation(
                "CsvStoragePathManager 初始化完成 - 根路径: {RootPath}, 分卷阈值: {MaxRows}",
                rootPath, _maxRowsPerFile);
        }

        /// <summary>
        /// 单个正式 CSV 文件最大数据行数。
        /// </summary>
        public int MaxRowsPerFile => _maxRowsPerFile;

        /// <summary>
        /// 根据用户选择的存储根目录，生成实际 DataLog 目录。
        /// </summary>
        public static string BuildDataLogRootPath(string rootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            return Path.Combine(rootPath, DATA_LOG_FOLDER_NAME);
        }

        /// <summary>
        /// 获取当前实际使用的 CSV 日志根目录。
        /// </summary>
        public string GetDataLogRootPath()
        {
            var effectiveRoot = _settings.GetEffectiveRootPath();
            return BuildDataLogRootPath(effectiveRoot);
        }

        /// <summary>
        /// 获取指定年月的文件夹路径
        /// </summary>
        public string GetMonthFolderPath(DateTime date)
        {
            string yearMonth = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return Path.Combine(GetDataLogRootPath(), yearMonth);
        }

        /// <summary>
        /// 获取当前月份的文件夹路径
        /// </summary>
        public string GetCurrentMonthFolderPath()
        {
            return GetMonthFolderPath(DateTime.Now);
        }

        /// <summary>
        /// 确保指定年月的文件夹存在
        /// </summary>
        public void EnsureMonthDirectoryExists(DateTime date)
        {
            var folderPath = GetMonthFolderPath(date);
            EnsureDirectoryExistsCore(folderPath);
        }

        /// <summary>
        /// 生成基础文件名（不含扩展名和分卷后缀）
        /// </summary>
        public string GetBaseFileName(string machineType, string planName)
        {
            var safeMachineType = SanitizeFileName(machineType);
            var safePlanName = SanitizeFileName(planName);
            return $"{safeMachineType}_{safePlanName}";
        }

        /// <summary>
        /// 获取可写入的 CSV 文件完整路径（默认当前月份）
        /// </summary>
        public string GetAvailableCsvFilePath(string machineType, string planName)
        {
            return GetAvailableCsvFilePath(machineType, planName, DateTime.Now);
        }

        /// <summary>
        /// 获取指定月份下可写入的 CSV 文件路径
        /// </summary>
        public string GetAvailableCsvFilePath(string machineType, string planName, DateTime date)
        {
            // 每次实时从配置读取路径，确保动态切换立即生效
            EnsureMonthDirectoryExists(date);

            var folderPath = GetMonthFolderPath(date);
            var baseFileName = GetBaseFileName(machineType, planName);

            var existingFiles = GetExistingFiles(baseFileName, folderPath);

            if (existingFiles.Count == 0)
            {
                return Path.Combine(folderPath, $"{baseFileName}.csv");
            }

            var lastFile = existingFiles.OrderByDescending(f => f.Number).First();
            int rowCount = CountDataRows(lastFile.FullPath);

            if (rowCount < _maxRowsPerFile)
            {
                _logger.LogDebug(
                    "复用现有文件: {FileName} (当前 {Rows}/{Max} 行)",
                    Path.GetFileName(lastFile.FullPath), rowCount, _maxRowsPerFile);
                return lastFile.FullPath;
            }

            int newFileNumber = lastFile.Number + 1;
            string newFilePath = newFileNumber == 0
                ? Path.Combine(folderPath, $"{baseFileName}.csv")
                : Path.Combine(folderPath, $"{baseFileName}({newFileNumber}).csv");

            _logger.LogInformation(
                "文件已满（{Rows}行），创建新分卷: {FileName}",
                rowCount, Path.GetFileName(newFilePath));

            return newFilePath;
        }

        /// <summary>
        /// 统计 CSV 文件的数据行数（不含表头）
        /// </summary>
        public int CountDataRows(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return 0;

                int lineCount = 0;
                bool isFirstLine = true;

                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    while (reader.ReadLine() != null)
                    {
                        if (isFirstLine)
                        {
                            isFirstLine = false;
                            continue;
                        }
                        lineCount++;
                    }
                }

                return lineCount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "统计文件行数失败: {FilePath}", filePath);
                return 0;
            }
        }

        /// <summary>
        /// 对文件名进行安全处理
        /// 仅过滤非法文件名字符，保留下划线以便文件名解析
        /// </summary>
        public string SanitizeFileName(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "Unknown";

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new StringBuilder();
            foreach (char c in input)
            {
                if (Array.IndexOf(invalidChars, c) >= 0)
                    sanitized.Append('-');
                // ⭐ 修复：移除对下划线的替换，保留下划线
                // 原因：方案名可能包含下划线（如 Scheme_V1），替换为短横线会导致搜索模式与文件名不匹配
                else
                    sanitized.Append(c);
            }
            return sanitized.ToString().TrimEnd(' ', '.');
        }

        #region 私有方法

        private void EnsureDirectoryExistsCore(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                _logger.LogDebug("已创建目录: {Directory}", directoryPath);
            }
        }

        private List<PlanFileInfo> GetExistingFiles(string baseFileName, string directory)
        {
            var files = new List<PlanFileInfo>();

            try
            {
                string searchPattern = $"{baseFileName}*.csv";
                var allFiles = Directory.GetFiles(directory, searchPattern);

                foreach (var filePath in allFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(filePath);

                    if (fileName == baseFileName)
                    {
                        files.Add(new PlanFileInfo { FullPath = filePath, Number = 0 });
                    }
                    else if (fileName.StartsWith(baseFileName + "(") && fileName.EndsWith(")"))
                    {
                        string numberPart = fileName.Substring(
                            baseFileName.Length + 1,
                            fileName.Length - baseFileName.Length - 2);

                        if (int.TryParse(numberPart, out int number))
                        {
                            files.Add(new PlanFileInfo { FullPath = filePath, Number = number });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "搜索文件失败: {Directory}/{BaseName}", directory, baseFileName);
            }

            return files;
        }

        private class PlanFileInfo
        {
            public string FullPath { get; set; } = string.Empty;
            public int Number { get; set; }
        }

        #endregion
    }
}
