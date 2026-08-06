namespace GMScannerRecoveryService;

/// <summary>扫描枪恢复服务固定协议常量。</summary>
internal static class ScannerRecoveryConstants
{
    public const string ServiceName = "GMScannerRecoveryService";
    public const string PipeName = "GM.ScannerRecovery.v1";
    public const string RestartCommand = "RestartScanner";
    public const string ExpectedVidPid = "VID_0C2E&PID_0914";
    public const int MaxComPortNumber = 256;
    public const int DeviceReappearTimeoutSeconds = 15;
    public const int DisableEnableGapMilliseconds = 2000;
}
