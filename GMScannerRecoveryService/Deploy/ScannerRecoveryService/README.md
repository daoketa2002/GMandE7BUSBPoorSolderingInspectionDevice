# GMScannerRecoveryService

该服务以管理员权限运行，用于在扫描枪普通串口重连无效时重启指定 USB 串行设备。

## 开发运行

在管理员 PowerShell 中执行：

```powershell
dotnet run --project .\GMScannerRecoveryService\GMScannerRecoveryService.csproj
```

## 发布

```powershell
dotnet publish .\GMScannerRecoveryService\GMScannerRecoveryService.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o .\publish\RecoveryService
```

将发布目录中的服务程序复制到 `Install-Service.cmd` 所在目录，再以管理员身份运行安装脚本。

服务只接受本机命名管道 `GM.ScannerRecovery.v1` 的 `RestartScanner` 命令，并且只允许动态定位到 `VID_0C2E&PID_0914` 的 COM 设备。
