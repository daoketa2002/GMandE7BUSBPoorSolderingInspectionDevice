using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Logging;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace GMandE7BUSBPoorSolderingInspectionDevice
{
    /// <summary>
    /// 应用程序的主入口类，负责配置依赖注入容器并启动 WPF 主窗口。
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
                RegisterGlobalExceptionHandlers(app);

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


        /// <summary>
        /// 注册进程级异常兜底。只在主入口注册一次，记录异常后保留框架默认终止策略。
        /// </summary>
        private static void RegisterGlobalExceptionHandlers(Application app)
        {
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>记录 UI 线程未处理异常，不将致命异常伪装成已处理。</summary>
        private static void OnDispatcherUnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs e)
        {
            Log.ForContext<Program>().Error(
                e.Exception,
                "[全局异常][UI线程] Dispatcher 未处理异常");
            e.Handled = false;
        }

        /// <summary>记录 AppDomain 未处理异常，保留进程终止信息。</summary>
        private static void OnAppDomainUnhandledException(
            object? sender,
            UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                Log.ForContext<Program>().Fatal(
                    exception,
                    "[全局异常][AppDomain] IsTerminating={IsTerminating}",
                    e.IsTerminating);
            }
            else
            {
                Log.ForContext<Program>().Fatal(
                    "[全局异常][AppDomain] IsTerminating={IsTerminating}, ExceptionObject={ExceptionObject}",
                    e.IsTerminating,
                    e.ExceptionObject);
            }
        }

        /// <summary>记录未观察任务异常并标记已观察，避免后台异常重复升级。</summary>
        private static void OnUnobservedTaskException(
            object? sender,
            UnobservedTaskExceptionEventArgs e)
        {
            Log.ForContext<Program>().Error(
                e.Exception,
                "[全局异常][未观察Task] 后台任务异常");
            e.SetObserved();
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
                  string runMode = ApplicationRunModeResolver.Resolve(context.Configuration);

                  configuration
                      .MinimumLevel.Information()
                      .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                      .Enrich.FromLogContext()
                      .Enrich.WithProperty("RunMode", runMode)
                      .Enrich.With<SourceContextShortNameEnricher>()
                      .WriteTo.Debug(
                          outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [RunMode={RunMode}] [{SourceContextShortName}] {Message:lj}{NewLine}{Exception}")
                      .WriteTo.File("logs/app-.log",
                          rollingInterval: RollingInterval.Day,
                          fileSizeLimitBytes: 10 * 1024 * 1024,
                          rollOnFileSizeLimit: true,
                          retainedFileCountLimit: 30,
                          shared: true,
                          outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [RunMode={RunMode}] [{SourceContextShortName}] {Message:lj}{NewLine}{Exception}");
              });


        /// <summary>
        /// 向依赖注入容器注册应用程序所需的所有服务，包括：
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

            // === 基础设施服务 ===
            services.AddSingleton<IDialogCoordinator, DialogCoordinator>();
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

            // === 导航服务 === 
            services.AddSingleton<INavigationService, NavigationService>();

            // === 方案设定相关服务 === 
            services.AddSingleton<IPlanStorageService, PlanStorageService>();
            services.AddSingleton<ISeriesMachineStorageService, SeriesMachineStorageService>();
            services.AddSingleton<IReferenceSelectionStateService, ReferenceSelectionStateService>();

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

            // 7. 独立无状态临时测试器
            services.AddSingleton<IPlcConnectionTester, PlcConnectionTester>();
            services.AddSingleton<IDmmConnectionTester, DmmConnectionTester>();

            // ⭐⭐⭐ ====== 硬件驱动服务注册结束 ====== ⭐⭐⭐

            // === ViewModels ===
            services.AddTransient<MainViewModel>();
            services.AddTransient<MainMenuViewModel>();
            services.AddTransient<ExitConfirmViewModel>();
            services.AddTransient<TestPageViewModel>();
            services.AddTransient<OperatorSelectionDialogViewModel>();
            services.AddTransient<SeriesMachineSelectionDialogViewModel>();
            services.AddTransient<PlanSettingViewModel>();
            services.AddTransient<PlanEditViewModel>();
            services.AddTransient<SystemSettingsViewModel>();

            // === Views ===
            services.AddTransient<MainWindow>();
            services.AddTransient<MainMenuView>();
            services.AddTransient<ExitConfirmDialog>();
            services.AddTransient<TestPageView>();
            services.AddTransient<OperatorSelectionDialog>();
            services.AddTransient<SeriesMachineSelectionDialog>();
            services.AddTransient<PlanSettingView>();
            services.AddTransient<PlanEditView>();
            services.AddTransient<SystemSettingsView>();

            // === 其他服务 ===
            services.AddLogging();
        }


    }
}
