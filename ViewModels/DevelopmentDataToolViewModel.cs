using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.Development;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Serilog;
using System;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class DevelopmentDataToolViewModel : ObservableObject, INavigationAware
    {
        private readonly TestDataSeeder _testDataSeeder;
        private readonly CsvStoragePathManager _pathManager;
        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly Serilog.ILogger _logger = Log.ForContext<DevelopmentDataToolViewModel>();

        public DevelopmentDataToolViewModel(
            TestDataSeeder testDataSeeder,
            CsvStoragePathManager pathManager,
            INavigationService navigationService,
            INotificationService notificationService)
        {
            _testDataSeeder = testDataSeeder ?? throw new ArgumentNullException(nameof(testDataSeeder));
            _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));

            CurrentMonth = DateTime.Now.ToString("yyyy-MM");
            StartMonth = DateTime.Now.AddMonths(-6).ToString("yyyy-MM");
            TestLogRootPath = _pathManager.GetTestLogRootPath();
        }

        [ObservableProperty]
        private string _currentMonth = string.Empty;

        [ObservableProperty]
        private string _machineType = "ZZTEST-索引机种A";

        [ObservableProperty]
        private int _planCount = 3;

        [ObservableProperty]
        private int _recordCount = 10000;

        [ObservableProperty]
        private string _startMonth = string.Empty;

        [ObservableProperty]
        private int _monthCount = 7;

        [ObservableProperty]
        private int _recordsPerMonth = 5000;

        [ObservableProperty]
        private string _testLogRootPath = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateNormalDataCommand))]
        [NotifyCanExecuteChangedFor(nameof(GenerateBulkMonthDataCommand))]
        [NotifyCanExecuteChangedFor(nameof(GenerateHistoryDataCommand))]
        [NotifyCanExecuteChangedFor(nameof(RebuildCurrentMonthIndexCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClearDevDataCommand))]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusText = "等待操作";

        [ObservableProperty]
        private int _progress;

        [ObservableProperty]
        private int _progressMaximum = 100;

        [ObservableProperty]
        private string _lastResult = "尚未执行";

        private bool CanExecuteToolCommand() => !IsBusy;

        [RelayCommand(CanExecute = nameof(CanExecuteToolCommand))]
        private async Task GenerateNormalDataAsync()
        {
            await RunToolAsync(
                "正在生成正常链路数据...",
                100,
                progress => _testDataSeeder.SeedThroughNormalSaveAsync(MachineType, 100, progress));
        }

        [RelayCommand(CanExecute = nameof(CanExecuteToolCommand))]
        private async Task GenerateBulkMonthDataAsync()
        {
            await RunToolAsync(
                "正在生成单月性能数据...",
                Math.Max(1, RecordCount),
                progress => _testDataSeeder.SeedBulkMonthAsync(CurrentMonth, MachineType, PlanCount, RecordCount, progress));
        }

        [RelayCommand(CanExecute = nameof(CanExecuteToolCommand))]
        private async Task GenerateHistoryDataAsync()
        {
            await RunToolAsync(
                "正在生成跨月份历史数据...",
                Math.Max(1, MonthCount) * Math.Max(1, RecordsPerMonth),
                progress => _testDataSeeder.SeedHistoryAsync(StartMonth, MonthCount, MachineType, RecordsPerMonth, progress));
        }

        [RelayCommand(CanExecute = nameof(CanExecuteToolCommand))]
        private async Task RebuildCurrentMonthIndexAsync()
        {
            await RunToolAsync(
                "正在重建索引...",
                1,
                async progress =>
                {
                    await _testDataSeeder.RebuildMonthIndexAsync(CurrentMonth);
                    progress.Report(1);
                });
        }

        [RelayCommand(CanExecute = nameof(CanExecuteToolCommand))]
        private async Task ClearDevDataAsync()
        {
            var confirmed = await _notificationService.ConfirmAsync(
                "将删除 TestLog 下所有 ZZTEST-*.csv，并重建受影响月份索引。确认继续？",
                "清理 ZZTEST 测试数据");
            if (!confirmed)
                return;

            await RunToolAsync(
                "正在清理 ZZTEST 数据...",
                1,
                async progress =>
                {
                    await _testDataSeeder.ClearDevTestDataAsync();
                    progress.Report(1);
                });
        }

        [RelayCommand]
        private async Task NavigateBackAsync()
        {
            await _navigationService.NavigateToAsync<MainMenuView>("Main", null);
        }

        public Task OnNavigatedToAsync(object? parameter = null)
        {
            TestLogRootPath = _pathManager.GetTestLogRootPath();
            _logger.Information("[用户操作] 进入开发测试数据工具");
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync() => Task.CompletedTask;

        public Task<bool> CanNavigateFromAsync() => Task.FromResult(!IsBusy);

        private async Task RunToolAsync(string runningStatus, int maximum, Func<IProgress<int>, Task> action)
        {
            try
            {
                IsBusy = true;
                StatusText = runningStatus;
                Progress = 0;
                ProgressMaximum = Math.Max(1, maximum);

                var progress = new Progress<int>(value => Progress = Math.Clamp(value, 0, ProgressMaximum));
                await action(progress);

                StatusText = "完成";
                LastResult = $"{runningStatus.Replace("正在", string.Empty).Replace("...", string.Empty)}完成：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            }
            catch (Exception ex)
            {
                StatusText = "失败";
                LastResult = ex.Message;
                _logger.Error(ex, "[ZZTEST] 工具执行失败");
                await _notificationService.ShowErrorAsync($"执行失败：{ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
