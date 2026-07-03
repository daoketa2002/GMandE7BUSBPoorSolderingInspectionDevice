using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Logging;
using GMandE7BUSBPoorSolderingInspectionDevice.Data;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Plc;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice
{
    /// <summary>
    /// 应用程序的主入口类，负责配置依赖注入容器、初始化数据库并启动 WPF 主窗口。
    /// 使用 .NET 通用主机（Generic Host）实现现代化的 WPF 应用架构。
    /// </summary>
    public class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        /// <param name="args">命令行参数（当前未使用，但保留以支持未来扩展）。</param>
        [STAThread]
        public static void Main(string[] args)
        {
            // 注册代码页编码提供程序，使 .NET Core 支持 GB2312/GBK 等传统编码
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var app = new Application();

            try
            {
                LoadFluentTheme(app);
                var host = CreateHostBuilder(args).Build();

                using (var scope = host.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    InitializeDatabase(db);
                }

                var mainWindow = host.Services.GetRequiredService<MainWindow>();

                // ⭐ 启动设备连接管理器（后台自动连接PLC、万用表、扫描枪）
                var deviceManager = host.Services.GetRequiredService<IDeviceConnectionManager>();

                var startupLogger = Log.ForContext<Program>();
                startupLogger.Information("[系统启动] 开始初始化设备连接");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await deviceManager.StartAllAsync();
                        startupLogger.Information("[系统启动] 设备连接管理器启动完成");
                    }
                    catch (Exception ex)
                    {
                        startupLogger.Error(ex, "[系统启动] 设备连接管理器启动失败: {Message}", ex.Message);
                    }
                });

                app.Run(mainWindow);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"应用程序启动失败: {ex.Message}",
                              "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Log.ForContext<Program>().Fatal(ex, "[系统启动] 应用程序启动失败");
            }
        }


        private static void LoadFluentTheme(Application app)
        {
            // 创建一个新的 ResourceDictionary 并加载 Fluent 主题
            var fluentResource = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
            };

            // 合并到 Application 的资源中
            app.Resources.MergedDictionaries.Add(fluentResource);


            // 可选：强制使用 Light 或 Dark 模式（避免 Known Issue 中提到的 System 模式崩溃）
            // 注意：.NET 10 Preview 4 中 System 模式有 Bug，建议显式指定
            // 你可以根据系统设置动态选择，但为稳定性先固定一个

            // app.ThemeMode = ThemeMode.Light;// 或 "Dark" ,实验性API

        }
        

        /// <summary>
        /// 创建并配置 .NET 通用主机（IHostBuilder），包括：
        /// - 应用配置（appsettings.json 等）
        /// - 服务注册（DI 容器）
        /// - Serilog 日志集成
        /// </summary>
        /// <param name="args">命令行参数，用于支持环境切换或配置覆盖。</param>
        /// <returns>已配置的 IHostBuilder 实例。</returns>
        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    // 默认已加载 appsettings.json、环境变量、命令行参数等
                    // 显式加载 appsettings.Development.json，用于开发阶段配置（如 Fake 硬件开关）。
                    // 生产机不携带该文件，config.AddJsonFile 使用 optional: true 静默跳过。
                    config.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
                })
                .ConfigureServices((context, services) =>
                {
                    ConfigureServices(services, context.Configuration);
                })
              .UseSerilog((context, configuration) =>
              {
                  configuration
                      .MinimumLevel.Information()
                      .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning) // 减少EF日志
                      .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                      .Enrich.FromLogContext()
                      .Enrich.With<SourceContextShortNameEnricher>()
                      .WriteTo.Debug(
                          outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{SourceContextShortName}] {Message:lj}{NewLine}{Exception}")
                      .WriteTo.File("logs/app-.log",
                          rollingInterval: RollingInterval.Day,
                          retainedFileCountLimit: 7,
                          outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContextShortName}] {Message:lj}{NewLine}{Exception}");
              });


        /// <summary>
        /// 向依赖注入容器注册应用程序所需的所有服务，包括：
        /// - 数据库上下文（AppDbContext）
        /// - 业务逻辑服务（如 IUserService）
        /// - 日志服务
        /// - ViewModel 和 View（窗口）
        /// </summary>
        /// <param name="services">IServiceCollection 实例，用于注册服务。</param>
        /// <param name="configuration">应用程序配置对象，用于读取连接字符串等设置。</param>

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            // === 配置管理服务 ===
            services.AddSingleton<ConfigManagerService>();

            // === 数据库配置 ===
            services.AddSingleton<DatabaseSettings>();

            // === 数据库初始化服务 ===
            services.AddScoped<DatabaseInitializer>();

            // === 数据库上下文 ===
            services.AddDbContext<AppDbContext>();

            // === 数据库上下文工厂 ===（替代直接注入 DbContext，避免跨线程问题）（保留，EF Core 仍需要）
            services.AddDbContextFactory<AppDbContext>((sp, options) =>
            {
                var dbSettings = sp.GetRequiredService<DatabaseSettings>();
                var connectionString = dbSettings.SqliteConnectionString;
                options.UseSqlite(connectionString);
            }, ServiceLifetime.Scoped);

            // === 基础设施服务 ===
            services.AddSingleton<INotificationService, NotificationService>();

            // 作业员服务
            services.AddSingleton<IOperatorStorageService, OperatorStorageService>();
            services.AddSingleton<IOperatorStateService, OperatorStateService>();

            // ══════════════════════════════════════════════════════════
            // CSV 存储服务注册 + ITestRecordStorage 注入
            // ══════════════════════════════════════════════════════════

            // CSV 存储配置
            services.AddSingleton<CsvStorageSettings>();

            // CSV 路径管理器
            services.AddSingleton<CsvStoragePathManager>();

            // ⭐ 核心改动：ITestRecordStorage → CSV 实现
            services.AddSingleton<ITestRecordStorage, CsvTestRecordStorage>();

            //// 保留旧的 SQLite 实现（不注入接口，仅供需要时手动解析）
            //services.AddSingleton<SqliteTestRecordStorage>();

            // CSV导出服务
            services.AddSingleton<ICsvExportService, CsvExportService>(); 

            // ══════════════════════════════════════════════════════════


            // === 导航服务 === 
            services.AddSingleton<INavigationService, NavigationService>();

            // === 方案设定相关服务 === 
            services.AddSingleton<IPlanStorageService, PlanStorageService>();

            // === 内存监控服务 === 
            services.AddSingleton<MemoryMonitorService>();


            // ⭐⭐⭐ ====== 新增：硬件驱动服务注册 ====== ⭐⭐⭐

            services.AddSingleton<IDeviceSettingsService, DeviceSettingsService>();

            // 1. Modbus TCP PLC 服务（动作控制）
            services.AddSingleton<TcpClientPLCMotionService>();

            // ⭐ 新增：IModbusTcpClient 实现（包装现有 TcpClientPLCMotionService）
            services.AddSingleton<IModbusTcpClient, ModbusTcpClient>();

            // ⭐ 新增：Fp0hPlcDevice 作为 IPlcDevice 的新实现
            services.AddSingleton<Fp0hPlcDevice>();

            // 2. 固纬 GDM-9060 万用表驱动
            services.AddSingleton<GwInstekGDM9060Driver>();

            // ⭐ Fake 硬件开关：从配置读取 Hardware:UseFakeInspectionHardware。
            // appsettings.json 不写该字段 → GetValue<bool> 返回 false → 默认注册真实设备。
            // appsettings.Development.json 可写入 {"Hardware": {"UseFakeInspectionHardware": true}} 启用 Fake。
            bool useFake = configuration.GetValue<bool>("Hardware:UseFakeInspectionHardware");
            if (useFake)
            {
                services.AddSingleton<FakeInspectionHardware>();
                services.AddSingleton<IPlcDevice>(sp => sp.GetRequiredService<FakeInspectionHardware>());
                services.AddSingleton<IMultimeterDevice>(sp => sp.GetRequiredService<FakeInspectionHardware>());
            }
            else
            {
                services.AddSingleton<IPlcDevice>(sp => sp.GetRequiredService<Fp0hPlcDevice>());
                services.AddSingleton<IMultimeterDevice>(sp => sp.GetRequiredService<GwInstekGDM9060Driver>());
            }

            // 3. 霍尼韦尔 H1900 扫描枪驱动
            services.AddSingleton<HoneywellH1900Scanner>();
            // ⭐ 注册为 IScannerDevice（不冲突，因为接口不同）
            services.AddSingleton<IScannerDevice>(sp =>
                sp.GetRequiredService<HoneywellH1900Scanner>());

            // 4. 检测流程引擎
            services.AddSingleton<InspectionEngine>();

            // 5. 扫描枪条码服务（保留，因为 DeviceConnectionManager 依赖它转发扫码事件）
            services.AddSingleton<IScannerBarcodeService, ScannerBarcodeService>();

            // 6. 设备连接管理器（统一管理 PLC、万用表、扫描枪的连接/重连/状态发布）
            services.AddSingleton<IDeviceConnectionManager, DeviceConnectionService>();

            // ⭐⭐⭐ ====== 硬件驱动服务注册结束 ====== ⭐⭐⭐

            // === ViewModels ===
            services.AddTransient<MainViewModel>();
            services.AddTransient<MainMenuViewModel>();
            services.AddTransient<ExitConfirmViewModel>();
            services.AddTransient<TestPageViewModel>();
            services.AddTransient<OperatorSettingsViewModel>();
            services.AddTransient<PlanSettingViewModel>();
            services.AddTransient<PlanEditViewModel>();
            services.AddTransient<LogDataViewModel>();
            services.AddTransient<SystemSettingsViewModel>();

            // === Views ===
            services.AddTransient<MainWindow>();
            services.AddTransient<MainMenuView>();
            services.AddTransient<ExitConfirmDialog>();
            services.AddTransient<TestPageView>();
            services.AddTransient<OperatorSettingsView>();
            services.AddTransient<PlanSettingView>();
            services.AddTransient<PlanEditView>();
            services.AddTransient<LogDataView>();
            services.AddTransient<SystemSettingsView>();

            // === 其他服务 ===
            services.AddLogging();
        }


        /// <summary>
        /// 初始化数据库（智能处理，永不自动删除生产数据）
        /// </summary>
        private static void InitializeDatabase(AppDbContext db)
        {
            try
            {
                var logger = Log.ForContext<Program>();
                logger.Information("[系统启动] 开始初始化数据库");

#if DEBUG
                // ========== DEBUG 模式：开发环境，可以删库重建 ==========
                const bool RECREATE_DATABASE_ON_EACH_RUN = true;

                if (RECREATE_DATABASE_ON_EACH_RUN)
                {
                    logger.Information("[系统启动] 【开发模式】正在删除旧数据库");
                    db.Database.EnsureDeleted();
                    logger.Information("[系统启动] 旧数据库已删除");
                }

                // 创建数据库和表
                logger.Information("[系统启动] 【开发模式】正在创建数据库表结构");
                db.Database.EnsureCreated();
                logger.Information("[系统启动] 数据库表结构创建完成");

                // 初始化种子数据
                InitializeSeedData(db);

                logger.Information("[系统启动] 【开发模式】数据库初始化完成");
#else
                // ========== RELEASE 模式：生产环境，绝对不能删库 ==========

                // 1. 检查是否有待执行的迁移
                var pendingMigrations = db.Database.GetPendingMigrations().ToList();

                if (pendingMigrations.Any())
                {
                    logger.Information("[系统启动] 【生产模式】执行数据库迁移，共 {Count} 个", pendingMigrations.Count);
                    db.Database.Migrate();
                    logger.Information("[系统启动] 迁移执行完成");

                    // 迁移后初始化种子数据
                    InitializeSeedData(db);
                    logger.Information("[系统启动] 数据库初始化完成");
                    return;
                }

                // 2. 没有迁移时，直接尝试创建表（如果表已存在，EnsureCreated 不会做任何事）
                logger.Information("[系统启动] 【生产模式】检查/创建数据库表结构");
                var created = db.Database.EnsureCreated();
                logger.Information("[系统启动] EnsureCreated 执行结果: {Created}", created);

                // 3. 初始化种子数据
                InitializeSeedData(db);

                logger.Information("[系统启动] 【生产模式】数据库初始化完成");
#endif
            }
            catch (Exception ex)
            {
                Log.ForContext<Program>().Error(ex, "[系统启动] 数据库初始化失败");

#if DEBUG
                // DEBUG 模式下抛出异常，让开发者看到问题
                throw;
#else
                // RELEASE 模式下显示友好错误，但不中断启动
                MessageBox.Show(
                    $"数据库初始化失败: {ex.Message}\n\n程序将以默认配置运行，请检查数据库设置。",
                    "数据库错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
#endif
            }
        }


        /// <summary>
        /// 初始化种子数据
        /// </summary>
        private static void InitializeSeedData(AppDbContext db)
        {
            try
            {
                // 此处添加初始化配置
                if (db.LogRecords.Any()) return; // 已有数据则跳过

                var random = new Random();
                var records = new List<LogRecord>();
                var planNames = new[] { "测试1" };
                var seriesList = new[] { "GM5", "E78" };
                var serialPrefix = "SN-TEST";

                for (int i = 1; i <= 30; i++)
                {
                    var series = seriesList[i % 2];          // 交替 GM5 / E78
                    var planName = planNames[i % planNames.Length];
                    var isNg = i % 5 == 0;                    // 每5条1条NG

                    var record = new LogRecord
                    {
                        Timestamp = DateTime.Now.AddDays(-random.Next(0, 7))
                                            .AddHours(-random.Next(0, 24)),
                        Series = series,
                        SerialNumber = $"{serialPrefix}-{i:D4}",
                        PlanName = planName,
                        Operator = $"操作员{i % 3 + 1}",
                        FinalResult = isNg ? "NG" : "OK",
                        CreatedAt = DateTime.Now,
                        PinResults = new List<PinResult>
                        {
                            new() { PinName = "A1-A2", Result = isNg ? "OPEN" : "SHORT" }
                        }
                    };
                    records.Add(record);
                }

                db.LogRecords.AddRange(records);
                db.SaveChanges();
                Log.ForContext<Program>().Information("[系统启动] 种子数据初始化完成");
            }
            catch (Exception ex)
            {
                Log.ForContext<Program>().Error(ex, "[系统启动] 种子数据初始化失败");
            }
        }


    }
}
