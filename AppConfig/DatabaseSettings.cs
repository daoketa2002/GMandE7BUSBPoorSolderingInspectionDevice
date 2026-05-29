using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.AppConfig
{
 
    /// <summary>
    /// 数据库配置类，用于从 appsettings.json 等配置源中绑定数据库相关设置。
    /// 支持在运行时动态修改配置
    /// </summary>
    public class DatabaseSettings
    {
        private readonly IConfiguration _configuration;

        public DatabaseSettings(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        /// <summary>
        /// 数据库类型: "Sqlite" 或 "SqlServer"
        /// </summary>
        public string DatabaseType => _configuration["Database:DatabaseType"] ?? "Sqlite";

        /// <summary>
        /// SQLite 连接字符串
        /// </summary>
        public string SqliteConnectionString =>
            _configuration["Database:SqliteConnectionString"] ?? "Data Source=app.db";

        /// <summary>
        /// SQL Server 连接字符串
        /// </summary>
        public string SqlServerConnectionString =>
            _configuration["Database:SqlServerConnectionString"] ?? string.Empty;

        /// <summary>
        /// 获取当前使用的连接字符串
        /// </summary>
        public string GetCurrentConnectionString()
        {
            return DatabaseType == "SqlServer" ? SqlServerConnectionString : SqliteConnectionString;
        }

        /// <summary>
        /// 静态方法：从配置中获取连接字符串（兼容现有代码）
        /// </summary>
        public static string GetConnectionString(IConfiguration configuration)
        {
            var dbType = configuration["Database:DatabaseType"];
            if (dbType == "SqlServer")
            {
                return configuration["Database:SqlServerConnectionString"]
                    ?? "Server=localhost;Database=UnnamedSoftwareDb;User Id=sa;Password=yourpassword;TrustServerCertificate=True;MultipleActiveResultSets=true";
            }
            else
            {
                return configuration["Database:SqliteConnectionString"]
                    ?? "Data Source=app.db";
            }
        }

        /// <summary>
        /// 静态方法：获取数据库类型
        /// </summary>
        public static string GetDatabaseType(IConfiguration configuration)
        {
            return configuration["Database:DatabaseType"] ?? "Sqlite";
        }
    }
}
