/// <summary>PLC 运行输出清理结果汇总</summary>
public sealed record RunOutputCleanupResult(
    bool StartCleared,
    bool PcReadyCleared,
    bool RelayCleared,
    bool PinsCleared,
    bool FinalResultCleared)
{
    public bool AllSucceeded =>
        StartCleared
        && PcReadyCleared
        && RelayCleared
        && PinsCleared
        && FinalResultCleared;
}
