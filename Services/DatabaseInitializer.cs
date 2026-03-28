
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;
using WPFStandardFramework.Data;

namespace WPFStandardFramework.Services
{
    /// <summary>
    /// 数据库初始化服务
    /// </summary>
    public class DatabaseInitializer : IDisposable
    {
        private readonly AppDbContext _dbContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;
        private bool _disposed;

        public DatabaseInitializer(
            AppDbContext dbContext,
            IConfiguration configuration)
        {
            _dbContext = dbContext;
            _configuration = configuration;
            _logger = Log.ForContext<DatabaseInitializer>();
        }

        /// <summary>
        /// 初始化数据库
        /// </summary>
        public async Task<bool> InitializeAsync(bool recreateOnError = false)
        {
            try
            {
                var dbType = _configuration["Database:DatabaseType"] ?? "Sqlite";
                _logger.Information("初始化数据库，类型: {DbType}", dbType);

                await EnsureDatabaseExistsAsync();
                await EnsureSchemaCreatedAsync();
                await SeedDataAsync();

                _logger.Information("数据库初始化完成");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "数据库初始化失败");
                if (recreateOnError)
                {
                    await _dbContext.Database.EnsureDeletedAsync();
                    return await InitializeAsync(false);
                }
                throw;
            }
        }

        /// <summary>
        /// 获取数据库信息
        /// </summary>
        public async Task<DatabaseInfo> GetDatabaseInfoAsync()
        {
            var info = new DatabaseInfo
            {
                DatabaseType = _configuration["Database:DatabaseType"] ?? "Sqlite",
                CanConnect = await _dbContext.Database.CanConnectAsync(),
                // IsInitialized = await _dbContext.CustomConfigs.AnyAsync()
            };

            try
            {
                var pendingMigrations = await _dbContext.Database.GetPendingMigrationsAsync();
                info.HasPendingMigrations = pendingMigrations.Any();
                info.PendingMigrationCount = pendingMigrations.Count();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "获取迁移信息失败");
            }

            return info;
        }

        private async Task EnsureSchemaCreatedAsync()
        {
            var pendingMigrations = await _dbContext.Database.GetPendingMigrationsAsync();
            if (pendingMigrations.Any())
            {
                await _dbContext.Database.MigrateAsync();
                _logger.Information("数据库迁移完成，迁移数量: {Count}", pendingMigrations.Count());
            }
            else
            {
                await _dbContext.Database.EnsureCreatedAsync();
                _logger.Information("数据库表结构已创建");
            }
        }

        private async Task SeedDataAsync()
        {

            // 仅在数据库未初始化时添加种子数据
            //此处添加需要的种子数据
         

            if (_dbContext.ChangeTracker.HasChanges())
            {
                await _dbContext.SaveChangesAsync();
                _logger.Information("种子数据保存完成");
            }
        }

   

        private async Task EnsureDatabaseExistsAsync()
        {
            var dbType = _configuration["Database:DatabaseType"] ?? "Sqlite";
            if (dbType == "Sqlite")
            {
                _logger.Debug("使用 SQLite 数据库，文件会自动创建");
                return;
            }

            var connectionString = _configuration["Database:SqlServerConnectionString"];
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.Warning("SQL Server 连接字符串为空，跳过数据库创建");
                return;
            }

            var dbNameMatch = System.Text.RegularExpressions.Regex.Match(
                connectionString, @"Database=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var dbName = dbNameMatch.Success ? dbNameMatch.Groups[1].Value : "Gb2Gb3TraceDb";

            var masterConn = System.Text.RegularExpressions.Regex.Replace(
                connectionString, @"Database=[^;]+;?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            masterConn = masterConn.TrimEnd(';');

            _logger.Debug("检查 SQL Server 数据库是否存在: {DbName}", dbName);

            var optionsBuilder = new DbContextOptionsBuilder<TestDbContext>();
            optionsBuilder.UseSqlServer(masterConn);
            using var tempContext = new TestDbContext(optionsBuilder.Options);

            try
            {
                var exists = await tempContext.Database
                    .SqlQueryRaw<bool>($"SELECT CAST(1 AS BIT) FROM sys.databases WHERE name = {{0}}", dbName)
                    .FirstOrDefaultAsync();

                if (!exists)
                {
                    _logger.Information("SQL Server 数据库不存在，正在创建: {DbName}", dbName);
                    await tempContext.Database.ExecuteSqlRawAsync($"CREATE DATABASE [{dbName}]");
                    _logger.Information("SQL Server 数据库已创建: {DbName}", dbName);
                }
                else
                {
                    _logger.Debug("SQL Server 数据库已存在: {DbName}", dbName);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "检查/创建 SQL Server 数据库失败");
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 用于测试数据库连接的临时 DbContext
    /// </summary>
    public class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }

    /// <summary>
    /// 数据库信息类
    /// </summary>
    public class DatabaseInfo
    {
        public string DatabaseType { get; set; } = string.Empty;
        public bool CanConnect { get; set; }
        public bool IsInitialized { get; set; }
        public bool HasPendingMigrations { get; set; }
        public int PendingMigrationCount { get; set; }
    }
}