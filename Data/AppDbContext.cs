using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Diagnostics;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Data
{
    public class AppDbContext : DbContext
    {
        
        /// <summary>
        /// 检测主记录表
        /// </summary>
        public DbSet<LogRecord> LogRecords => Set<LogRecord>();

        /// <summary>
        /// Pin结果明细表
        /// </summary>
        public DbSet<PinResult> PinResults => Set<PinResult>();

        // 数据库设定
        private readonly DatabaseSettings _databaseSettings;
        private static readonly ILogger _logger = Log.ForContext<AppDbContext>();

        /// <summary>
        /// 构造函数 - 使用 DatabaseSettings
        /// </summary>
        public AppDbContext(DatabaseSettings databaseSettings)
        {
            _databaseSettings = databaseSettings ?? throw new ArgumentNullException(nameof(databaseSettings));
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (optionsBuilder.IsConfigured)
                return;

            var dbType = _databaseSettings.DatabaseType;
            var connectionString = _databaseSettings.GetCurrentConnectionString();

            _logger.Debug("数据库配置 - 类型: {DbType}, 连接字符串: {ConnectionString}", dbType,
                dbType == "SqlServer" ? connectionString : "Data Source=app.db");

            if (dbType == "SqlServer")
            {
                optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
                {
                    sqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(30),
                        errorNumbersToAdd: null);
                    sqlOptions.CommandTimeout(60);
                });
            }
            else
            {
                optionsBuilder.UseSqlite(connectionString);
            }

#if DEBUG
            optionsBuilder
                .EnableSensitiveDataLogging(false)
                .LogTo(message =>
                {
                    if (message.Contains("Executed") || message.Contains("Command"))
                    {
                        _logger.Debug("[EF Core] {Message}", message);
                    }
                });
#else
            optionsBuilder.EnableDetailedErrors(false);
#endif
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
           
        }

    }
}