using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
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

                var mainWindow = host.Services.GetRequiredService<MainWindow>();
                app.Run(mainWindow);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"应用程序启动失败: {ex.Message}",
                              "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Log.Fatal(ex, "应用程序启动失败");
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
                      .WriteTo.Debug(
                          outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                      .WriteTo.File("logs/app-.log",
                          rollingInterval: RollingInterval.Day,
                          retainedFileCountLimit: 7,
                          outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
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

            // === 基础设施服务 ===
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<IDeviceSettingsService, SettingsService>();
            services.AddSingleton<IOperatorStorageService, OperatorStorageService>();
            services.AddSingleton<IOperatorStateService, OperatorStateService>();

            // === 导航服务 === 
            services.AddSingleton<INavigationService, NavigationService>();

            // === 方案设定相关服务 === 
            services.AddSingleton<ICurrentPlanService, CurrentPlanService>();
            services.AddSingleton<IPlanStorageService, PlanStorageService>();

            // === 日志数据相关服务 ===
            services.AddSingleton<LogDataService>();

            // === 内存监控服务 === 
            services.AddSingleton<MemoryMonitorService>();

            services.AddSingleton<ILogDataService, LogDataService>();    // 日志数据服务（接口注入）
            services.AddSingleton<ICsvExportService, CsvExportService>(); // CSV导出服务（接口注入）

            // ⭐⭐⭐ ====== 新增：硬件驱动服务注册 ====== ⭐⭐⭐

            // 1. Modbus TCP PLC 服务（动作控制）
            // 同时注册具体类型和接口，确保两者可解析到同一实例
            services.AddSingleton<TcpClientPLCMotionService>();
            services.AddSingleton<ITcpClientPLCMotionService>(sp =>
                sp.GetRequiredService<TcpClientPLCMotionService>());
            // UI层封装
            services.AddSingleton<TcpPLCMotionWPFUIModbusService>();

            // 2. 固纬 GDM-9060 万用表驱动
            services.AddSingleton<GwInstekGDM9060Driver>();

            // 3. 霍尼韦尔 H1900 扫描枪驱动
            services.AddSingleton<HoneywellH1900Scanner>();

            // 4. 检测流程引擎
            services.AddSingleton<InspectionEngine>();

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
    }
}
