namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>扫描枪异常帧被完整丢弃时发布的事件参数。</summary>
public sealed class ScannerFrameRejectedEventArgs : EventArgs
{
    public int TotalBytes { get; }
    public string Reason { get; }
    public DateTime TimestampUtc { get; }
    public string HexPreview { get; }

    public ScannerFrameRejectedEventArgs(int totalBytes, string reason, string hexPreview)
    {
        TotalBytes = totalBytes;
        Reason = reason;
        HexPreview = hexPreview;
        TimestampUtc = DateTime.UtcNow;
    }
}
