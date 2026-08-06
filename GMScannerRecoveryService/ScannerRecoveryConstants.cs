namespace GMScannerRecoveryService;

/// <summary>扫描枪恢复服务固定协议常量。</summary>
internal static class ScannerRecoveryConstants
{
    public const string ServiceName = "GMScannerRecoveryService";
    public const string PipeName = "GM.ScannerRecovery.v1";
    public const string RestartCommand = "RestartScanner";
    // 生产现场允许更换不同型号的 Honeywell 扫描枪，因此只固定厂商 VID，不限制具体 PID。
    public const string ExpectedVendorId = "VID_0C2E";
    public const int MaxComPortNumber = 256;
    public const int DeviceReappearTimeoutSeconds = 15;
    public const int DisableEnableGapMilliseconds = 2000;
    public const int PostEnableStabilizationMilliseconds = 2000;
}
