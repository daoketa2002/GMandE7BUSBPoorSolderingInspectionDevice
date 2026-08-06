using GMScannerRecoveryService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ScannerRecoveryConstants.ServiceName;
});

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

builder.Services.AddSingleton<ScannerDeviceLocator>();
builder.Services.AddSingleton<PnpDeviceController>();
builder.Services.AddSingleton<ScannerRecoveryPipeServer>();
builder.Services.AddHostedService<ScannerRecoveryWorker>();

await builder.Build().RunAsync();
